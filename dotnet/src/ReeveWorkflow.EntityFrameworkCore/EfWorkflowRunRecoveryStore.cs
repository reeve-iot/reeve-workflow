using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The query that finds runs whose replica died, over the workflow context's own state table.</summary>
/// <remarks>On PostgreSQL the whole batch is claimed by one statement: the candidates are locked with
/// <c>FOR UPDATE SKIP LOCKED</c> and taken in the same statement, so two sweeps running together divide the
/// backlog instead of racing over the head of it. Everywhere else it is a read and then a statement per row:
/// the candidates are read once, and each is taken by a compare-and-swap on the version it was read at. Every
/// writer of this table increments that version, so the swap is what makes two sweeps over one row end with
/// exactly one owner; the age is re-asserted beside it, the status is not. The portable path stays because it
/// is the only one SQLite and SQL Server can run.
///
/// Both paths read <c>CURRENT_TIMESTAMP</c> first, which every relational provider answers: a row's
/// <c>last_transition_utc</c> was written by whichever replica held it, and comparing it against the sweeping
/// replica's own clock made "abandoned" mean something different on every machine.
///
/// A claim made in a replica's name also takes a reclaimable row that names no replica, whatever its age: every
/// replica that sweeps writes its name on every row it holds, so a row without one is a row a stopping replica
/// handed over, and nobody is running it. A claim in no name reads rows by their age alone.</remarks>
public sealed class EfWorkflowRunRecoveryStore(IServiceScopeFactory scopeFactory) : IWorkflowRunRecoveryStore
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private string? claimSql;

    private string? claimOrReleasedSql;

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
        WorkflowRunRecoveryClaim request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(request), request.BatchSize, "A sweep takes at least one row.");

        var running = nameof(WorkflowRunStateStatus.Running);
        var sleeping = nameof(WorkflowRunStateStatus.Sleeping);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var now = await DatabaseNowAsync(db, cancellationToken).ConfigureAwait(false);
        var abandonedBefore = now - request.AbandonedAfter;
        var takesReleased = request.ClaimedByReplicaId is not null;

        if (PostgresStatements.Applies(db.Database))
            return await ClaimWithSqlAsync(db, request, now, abandonedBefore, running, sleeping, cancellationToken)
                .ConfigureAwait(false);

        var candidates = await db.WorkflowExecutionStates
            .AsNoTracking()
            .Where(state => state.LastTransitionUtc < abandonedBefore
                            || (takesReleased && state.OwnedByReplicaId == null))
            .Where(state => state.Status == running
                            || (state.Status == sleeping
                                && state.WaitUntilUtc != null && state.WaitUntilUtc.Value <= now))
            .OrderBy(state => state.LastTransitionUtc)
            .Take(request.BatchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var claimed = new List<WorkflowRunStateRow>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var seenVersion = candidate.Version;

            var affected = await db.WorkflowExecutionStates
                .Where(state => state.ExecutionId == candidate.ExecutionId
                                && state.Version == seenVersion
                                && (state.LastTransitionUtc < abandonedBefore
                                    || (takesReleased && state.OwnedByReplicaId == null)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(state => state.OwnedByReplicaId, request.ClaimedByReplicaId)
                    .SetProperty(state => state.LastTransitionUtc, now)
                    .SetProperty(state => state.Version, state => state.Version + 1),
                    cancellationToken)
                .ConfigureAwait(false);

            if (affected == 0)
                continue;

            claimed.Add(RowOf(candidate, request.ClaimedByReplicaId, now));
        }

        return claimed;
    }

    private async Task<IReadOnlyList<WorkflowRunStateRow>> ClaimWithSqlAsync(
        WorkflowDbContext db, WorkflowRunRecoveryClaim request, DateTimeOffset now, DateTimeOffset abandonedBefore,
        string running, string sleeping, CancellationToken cancellationToken)
    {
        var table = PostgresStatements.Table<WorkflowExecutionState>(db.Model);
        var statement = request.ClaimedByReplicaId is null
            ? claimSql ??= ClaimStatement(table, takesReleased: false)
            : claimOrReleasedSql ??= ClaimStatement(table, takesReleased: true);

        var claimed = await PostgresStatements.RunAsync(
                db.Database,
                statement,
                [abandonedBefore, running, sleeping, now, request.BatchSize, request.ClaimedByReplicaId],
                async (command, token) =>
                {
                    var rows = new List<(DateTimeOffset AgedAt, WorkflowRunStateRow Row)>();
                    await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        rows.Add((
                            reader.GetFieldValue<DateTimeOffset>(9),
                            new WorkflowRunStateRow
                            {
                                ExecutionId = reader.GetGuid(0),
                                WorkflowId = reader.GetGuid(1),
                                Status = ParseStatus(reader.GetString(2), reader.GetGuid(0)),
                                WorkflowVersion = reader.GetInt32(3),
                                CurrentBlockPath = reader.GetString(4),
                                VariablesJson = reader.GetString(5),
                                WaitUntilUtc = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                                PinnedVersionsJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                                ParentExecutionId = reader.IsDBNull(8) ? null : reader.GetGuid(8),
                                StartedAtUtc = reader.GetFieldValue<DateTimeOffset>(10),
                                OwnedByReplicaId = request.ClaimedByReplicaId,
                                LastTransitionUtc = now
                            }));
                    }

                    return rows;
                },
                cancellationToken)
            .ConfigureAwait(false);

        return [.. claimed.OrderBy(row => row.AgedAt).ThenBy(row => row.Row.ExecutionId).Select(row => row.Row)];
    }

    private static string ClaimStatement(PostgresStatements.MappedTable table, bool takesReleased)
    {
        string Column(string property) => table.Column(property);

        var executionId = Column(nameof(WorkflowExecutionState.ExecutionId));
        var lastTransition = Column(nameof(WorkflowExecutionState.LastTransitionUtc));
        var status = Column(nameof(WorkflowExecutionState.Status));
        var waitUntil = Column(nameof(WorkflowExecutionState.WaitUntilUtc));
        var version = Column(nameof(WorkflowExecutionState.Version));
        var startedAt = Column(nameof(WorkflowExecutionState.StartedAtUtc));
        var owner = Column(nameof(WorkflowExecutionState.OwnedByReplicaId));
        var orReleased = takesReleased ? $" OR {owner} IS NULL" : string.Empty;
        var orClaimedReleased = takesReleased ? $" OR claimed.{owner} IS NULL" : string.Empty;

        return $"""
            WITH candidates AS (
                SELECT {executionId}, {lastTransition}
                FROM {table.Name}
                WHERE ({lastTransition} < $1{orReleased})
                  AND ({status} = $2
                       OR ({status} = $3 AND {waitUntil} IS NOT NULL AND {waitUntil} <= $4))
                ORDER BY {lastTransition}
                LIMIT $5
                FOR UPDATE SKIP LOCKED
            )
            UPDATE {table.Name} AS claimed
            SET {owner} = $6,
                {lastTransition} = $4,
                {version} = claimed.{version} + 1
            FROM candidates
            WHERE claimed.{executionId} = candidates.{executionId}
              AND (claimed.{lastTransition} < $1{orClaimedReleased})
            RETURNING claimed.{executionId}, claimed.{Column(nameof(WorkflowExecutionState.WorkflowId))},
                claimed.{status}, claimed.{Column(nameof(WorkflowExecutionState.WorkflowVersion))},
                claimed.{Column(nameof(WorkflowExecutionState.CurrentBlockPath))},
                claimed.{Column(nameof(WorkflowExecutionState.VariablesJson))}, claimed.{waitUntil},
                claimed.{Column(nameof(WorkflowExecutionState.PinnedVersionsJson))},
                claimed.{Column(nameof(WorkflowExecutionState.ParentExecutionId))},
                candidates.{lastTransition}, claimed.{startedAt}
            """;
    }

    private static WorkflowRunStateStatus ParseStatus(string status, Guid executionId) =>
        Enum.TryParse<WorkflowRunStateStatus>(status, ignoreCase: false, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Run state row {executionId} holds status '{status}', which is not a "
                + $"{nameof(WorkflowRunStateStatus)}.");

    /// <inheritdoc />
    public async Task<bool> RenewClaimAsync(
        Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var now = await DatabaseNowAsync(db, cancellationToken).ConfigureAwait(false);

        var affected = await db.WorkflowExecutionStates
            .Where(state => state.ExecutionId == executionId
                            && state.OwnedByReplicaId == claimedByReplicaId
                            && state.Status != nameof(WorkflowRunStateStatus.Completed)
                            && state.Status != nameof(WorkflowRunStateStatus.Failed)
                            && state.Status != nameof(WorkflowRunStateStatus.Cancelled))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.LastTransitionUtc, now)
                .SetProperty(state => state.Version, state => state.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);

        return affected == 1;
    }

    private static async Task<DateTimeOffset> DatabaseNowAsync(
        WorkflowDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<DateTimeOffset>($"SELECT CURRENT_TIMESTAMP AS \"Value\"")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0].ToUniversalTime()
            : throw new InvalidOperationException(
                $"CURRENT_TIMESTAMP answered with {rows.Count} values rather than one, so the sweep has no "
                + "clock to decide what is abandoned. Fill Seams.RunRecoveryStore with a store written for "
                + "this provider.");
    }

    private static WorkflowRunStateRow RowOf(WorkflowExecutionState state, string? owner, DateTimeOffset claimedAt) =>
        new()
        {
            ExecutionId = state.ExecutionId,
            WorkflowId = state.WorkflowId,
            Status = ParseStatus(state.Status, state.ExecutionId),
            WorkflowVersion = state.WorkflowVersion,
            CurrentBlockPath = state.CurrentBlockPath,
            VariablesJson = state.VariablesJson,
            WaitUntilUtc = state.WaitUntilUtc,
            OwnedByReplicaId = owner,
            LastTransitionUtc = claimedAt,
            PinnedVersionsJson = state.PinnedVersionsJson,
            ParentExecutionId = state.ParentExecutionId,
            StartedAtUtc = state.StartedAtUtc
        };
}
