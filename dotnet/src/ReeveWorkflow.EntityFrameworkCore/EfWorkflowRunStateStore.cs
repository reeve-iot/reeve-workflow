using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Where a run is, durably, in the workflow context's own state table.</summary>
/// <remarks>Every transition is a single compare-and-swap <c>UPDATE … WHERE status = @from</c>: zero rows
/// affected means the swap lost, which is an answer the caller acts on and must never retry. A scope is opened
/// per call rather than held, so this can be a singleton the engine keeps for the life of the process while its
/// context stays as short-lived as any other.</remarks>
public sealed class EfWorkflowRunStateStore(IServiceScopeFactory scopeFactory, TimeProvider? clock = null)
    : IWorkflowRunStateStore
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    private TransitionStatements? transitionSql;

    /// <inheritdoc />
    public async Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var executionId = Guid.NewGuid();
        var entity = new WorkflowExecutionState(
            executionId,
            request.WorkflowId,
            request.WorkflowVersion,
            nameof(WorkflowRunStateStatus.Running),
            request.VariablesJson,
            request.TriggerSource,
            request.OwnedByReplicaId,
            clock.GetUtcNow(),
            request.PinnedVersionsJson,
            request.ParentExecutionId);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        db.WorkflowExecutionStates.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return executionId;
    }

    /// <inheritdoc />
    public async Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var row = await db.WorkflowExecutionStates
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ExecutionId == executionId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null) return null;

        return new WorkflowRunStateRow
        {
            ExecutionId = row.ExecutionId,
            WorkflowId = row.WorkflowId,
            Status = ParseStatus(row.Status, row.ExecutionId),
            WorkflowVersion = row.WorkflowVersion,
            CurrentBlockPath = row.CurrentBlockPath,
            VariablesJson = row.VariablesJson,
            WaitUntilUtc = row.WaitUntilUtc,
            WaitForEventKey = row.WaitForEventKey,
            OwnedByReplicaId = row.OwnedByReplicaId,
            LastTransitionUtc = row.LastTransitionUtc,
            Version = row.Version,
            PinnedVersionsJson = row.PinnedVersionsJson,
            ParentExecutionId = row.ParentExecutionId,
            FailureReason = row.FailureReason,
            StartedAtUtc = row.StartedAtUtc,
            CompletedAtUtc = row.CompletedAtUtc
        };
    }

    /// <inheritdoc />
    /// <remarks>Keyed on <c>started_at_utc</c> and not offset from the top: a list read while runs are
    /// starting would otherwise show one run twice and skip another, and an offset deep into a workflow's
    /// history costs the database every row before it. The index this rides is
    /// <c>ix_workflow_executions_state_workflow_started</c>.
    ///
    /// Runs that started in the same instant — PostgreSQL keeps microseconds, so a fan-out opening children in
    /// a loop makes them — are ordered by their id and the cursor counts how many of that instant a caller has
    /// already been handed. That count is the only offset here and it is bounded by the size of one instant's
    /// group, where paging on the instant alone would have to hand back a whole group at once however large it
    /// was, and paging on the instant with a key would have to compare that key in the WHERE, which not every
    /// provider translates for a uuid.
    ///
    /// The page reads the nine columns a summary carries and not the row: <c>variables_json</c> is the run's
    /// whole checkpoint, and fifty of those crossing the wire is what this member exists not to do.</remarks>
    public async Task<WorkflowRunStatePage> ListByWorkflowAsync(
        Guid workflowId, WorkflowRunStatePageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit < 1 || request.Limit > WorkflowRunStatePageRequest.MaxLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.Limit,
                $"A page holds between 1 and {WorkflowRunStatePageRequest.MaxLimit} rows.");
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var query = db.WorkflowExecutionStates.AsNoTracking().Where(e => e.WorkflowId == workflowId);

        if (request.Statuses is { Count: > 0 } statuses)
        {
            var names = statuses.Select(status => status.ToString()).Distinct(StringComparer.Ordinal).ToArray();
            query = query.Where(e => names.Contains(e.Status));
        }

        var cursor = WorkflowRunPageCursor.Read(request.After);
        if (cursor is { } from)
        {
            var edge = from.StartedAtUtc;
            query = query.Where(e => e.StartedAtUtc <= edge);
        }

        var taken = await query
            .OrderByDescending(e => e.StartedAtUtc)
            .ThenByDescending(e => e.ExecutionId)
            .Skip(cursor?.Delivered ?? 0)
            .Take(request.Limit + 1)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (taken.Count <= request.Limit)
            return new WorkflowRunStatePage([.. taken.Select(Summarise)], null);

        var rows = taken.Take(request.Limit).ToList();
        var last = rows[^1].StartedAtUtc;

        var delivered = rows.Count(row => row.StartedAtUtc == last);
        if (cursor is { } previous && previous.StartedAtUtc == last)
            delivered += previous.Delivered;

        return new WorkflowRunStatePage(
            [.. rows.Select(Summarise)], WorkflowRunPageCursor.Write(last, delivered));
    }

    private static readonly System.Linq.Expressions.Expression<Func<WorkflowExecutionState, RunListRow>> Projection =
        row => new RunListRow(
            row.ExecutionId, row.WorkflowId, row.Status, row.WorkflowVersion, row.CurrentBlockPath,
            row.OwnedByReplicaId, row.StartedAtUtc, row.CompletedAtUtc, row.FailureReason);

    private sealed record RunListRow(
        Guid ExecutionId, Guid WorkflowId, string Status, int WorkflowVersion, string CurrentBlockPath,
        string? OwnedByReplicaId, DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc,
        string? FailureReason);

    private static WorkflowRunStateSummary Summarise(RunListRow row) => new()
    {
        ExecutionId = row.ExecutionId,
        WorkflowId = row.WorkflowId,
        Status = ParseStatus(row.Status, row.ExecutionId),
        WorkflowVersion = row.WorkflowVersion,
        CurrentBlockPath = row.CurrentBlockPath,
        OwnedByReplicaId = row.OwnedByReplicaId,
        StartedAtUtc = row.StartedAtUtc,
        CompletedAtUtc = row.CompletedAtUtc,
        FailureReason = row.FailureReason
    };

    /// <inheritdoc />
    public async Task<bool> TransitionAsync(
        WorkflowRunStateTransition request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = clock.GetUtcNow();
        var fromStatus = request.FromStatus.ToString();
        var toStatus = request.ToStatus.ToString();
        var expectedOwner = request.ExpectedOwnedByReplicaId;
        var expectedKey = request.ExpectedWaitForEventKey;
        var expectedVersion = request.ExpectedVersion;
        var expectedPath = request.ExpectedCurrentBlockPath;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        if (PostgresStatements.Applies(db.Database))
            return await TransitionWithSqlAsync(db, request, fromStatus, toStatus, now, cancellationToken).ConfigureAwait(false);

        var affected = await db.WorkflowExecutionStates
            .Where(e => e.ExecutionId == request.ExecutionId
                        && e.Status == fromStatus
                        && (expectedOwner == null || e.OwnedByReplicaId == expectedOwner)
                        && (expectedKey == null || e.WaitForEventKey == expectedKey)
                        && (expectedVersion == null || e.Version == expectedVersion)
                        && (expectedPath == null || e.CurrentBlockPath == expectedPath))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.Status, toStatus)
                .SetProperty(e => e.CurrentBlockPath, request.CurrentBlockPath)
                .SetProperty(e => e.VariablesJson, request.VariablesJson)
                .SetProperty(e => e.WaitUntilUtc, request.WaitUntilUtc)
                .SetProperty(e => e.WaitForEventKey, request.WaitForEventKey)
                .SetProperty(e => e.OwnedByReplicaId, request.OwnedByReplicaId)
                .SetProperty(e => e.LastTransitionUtc, now)
                .SetProperty(e => e.Version, e => e.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    private async Task<bool> TransitionWithSqlAsync(
        WorkflowDbContext db, WorkflowRunStateTransition request, string fromStatus, string toStatus,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var statements = transitionSql ??= TransitionStatements.For(db.Model);

        List<object?> parameters =
        [
            toStatus, request.CurrentBlockPath, request.VariablesJson, request.WaitUntilUtc, request.WaitForEventKey,
            request.OwnedByReplicaId, now, request.ExecutionId, fromStatus
        ];
        var guards = TransitionGuards.None;
        if (request.ExpectedOwnedByReplicaId is { } expectedOwner)
        {
            guards |= TransitionGuards.Owner;
            parameters.Add(expectedOwner);
        }
        if (request.ExpectedCurrentBlockPath is { } expectedPath)
        {
            guards |= TransitionGuards.Node;
            parameters.Add(expectedPath);
        }
        if (request.ExpectedWaitForEventKey is { } expectedKey)
        {
            guards |= TransitionGuards.WaitKey;
            parameters.Add(expectedKey);
        }
        if (request.ExpectedVersion is { } expectedVersion)
        {
            guards |= TransitionGuards.Version;
            parameters.Add(expectedVersion);
        }

        var affected = await PostgresStatements.RunAsync(
                db.Database, statements.Guarded(guards), [.. parameters],
                (command, token) => command.ExecuteNonQueryAsync(token), cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    [Flags]
    private enum TransitionGuards { None = 0, Owner = 1, Node = 2, WaitKey = 4, Version = 8 }

    private sealed class TransitionStatements(string update, string owner, string node, string waitKey, string version)
    {
        private readonly string?[] guarded = new string?[16];

        public string Guarded(TransitionGuards guards)
        {
            if (this.guarded[(int)guards] is { } cached)
                return cached;

            var sql = update;
            var position = 9;
            if (guards.HasFlag(TransitionGuards.Owner)) sql += Clause(owner, ++position);
            if (guards.HasFlag(TransitionGuards.Node)) sql += Clause(node, ++position);
            if (guards.HasFlag(TransitionGuards.WaitKey)) sql += Clause(waitKey, ++position);
            if (guards.HasFlag(TransitionGuards.Version)) sql += Clause(version, ++position);

            return this.guarded[(int)guards] = sql;
        }

        private static string Clause(string column, int position) => $"\n  AND {column} = ${position}";

        public static TransitionStatements For(IModel model)
        {
            var table = PostgresStatements.Table<WorkflowExecutionState>(model);
            string Column(string property) => table.Column(property);

            var update = $"""
                UPDATE {table.Name}
                SET {Column(nameof(WorkflowExecutionState.Status))} = $1,
                    {Column(nameof(WorkflowExecutionState.CurrentBlockPath))} = $2,
                    {Column(nameof(WorkflowExecutionState.VariablesJson))} = CAST($3 AS {table.StoreType(nameof(WorkflowExecutionState.VariablesJson))}),
                    {Column(nameof(WorkflowExecutionState.WaitUntilUtc))} = $4,
                    {Column(nameof(WorkflowExecutionState.WaitForEventKey))} = $5,
                    {Column(nameof(WorkflowExecutionState.OwnedByReplicaId))} = $6,
                    {Column(nameof(WorkflowExecutionState.LastTransitionUtc))} = $7,
                    {Column(nameof(WorkflowExecutionState.Version))} = {Column(nameof(WorkflowExecutionState.Version))} + 1
                WHERE {Column(nameof(WorkflowExecutionState.ExecutionId))} = $8
                  AND {Column(nameof(WorkflowExecutionState.Status))} = $9
                """;

            return new TransitionStatements(
                update,
                Column(nameof(WorkflowExecutionState.OwnedByReplicaId)),
                Column(nameof(WorkflowExecutionState.CurrentBlockPath)),
                Column(nameof(WorkflowExecutionState.WaitForEventKey)),
                Column(nameof(WorkflowExecutionState.Version)));
        }
    }

    /// <inheritdoc />
    public Task<bool> CompleteAsync(
        Guid executionId,
        WorkflowRunStateStatus terminalStatus,
        string? failureReason,
        CancellationToken cancellationToken) =>
        CompleteWhereAsync(executionId, owned: false, null, terminalStatus, failureReason, cancellationToken);

    /// <inheritdoc />
    /// <remarks>One conditional write, where the interface's default reads the row first.</remarks>
    public Task<bool> CompleteIfOwnedAsync(
        Guid executionId,
        string? ownedByReplicaId,
        WorkflowRunStateStatus terminalStatus,
        string? failureReason,
        CancellationToken cancellationToken) =>
        CompleteWhereAsync(executionId, owned: true, ownedByReplicaId, terminalStatus, failureReason, cancellationToken);

    private async Task<bool> CompleteWhereAsync(
        Guid executionId,
        bool owned,
        string? ownedByReplicaId,
        WorkflowRunStateStatus terminalStatus,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var toStatus = terminalStatus.ToString();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var rows = db.WorkflowExecutionStates
            .Where(e => e.ExecutionId == executionId
                        && e.Status != nameof(WorkflowRunStateStatus.Completed)
                        && e.Status != nameof(WorkflowRunStateStatus.Failed)
                        && e.Status != nameof(WorkflowRunStateStatus.Cancelled));
        if (owned)
            rows = ownedByReplicaId is null
                ? rows.Where(e => e.OwnedByReplicaId == null)
                : rows.Where(e => e.OwnedByReplicaId == ownedByReplicaId);

        var affected = await rows
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(e => e.Status, toStatus)
                .SetProperty(e => e.CompletedAtUtc, now)
                .SetProperty(e => e.LastTransitionUtc, now)
                .SetProperty(e => e.FailureReason, failureReason)
                .SetProperty(e => e.Version, e => e.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return affected > 0;
    }

    private static WorkflowRunStateStatus ParseStatus(string status, Guid executionId) =>
        Enum.TryParse<WorkflowRunStateStatus>(status, ignoreCase: false, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Run state row {executionId} holds status '{status}', which is not a "
                + $"{nameof(WorkflowRunStateStatus)}.");
}
