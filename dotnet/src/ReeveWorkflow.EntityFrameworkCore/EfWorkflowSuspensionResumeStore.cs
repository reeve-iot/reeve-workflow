using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The three questions a waker asks of the suspension table: which parked runs are ready, taking
/// one, and putting one back. All over the same rows <see cref="EfWorkflowSuspensionStore"/> writes.</summary>
/// <remarks>Taking a row is one UPDATE with the parked status in its WHERE clause, and the count it answers
/// with is what decides the race: two wakers over one row both run it, one updates a row and one updates
/// none. No lock hint, no election, and correct with any number of replicas.</remarks>
public sealed class EfWorkflowSuspensionResumeStore(
    IServiceScopeFactory scopeFactory, IReadOnlyList<WorkflowExpressionNamespace> expressionNamespaces)
    : IWorkflowSuspensionResumeStore
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly ParkedWaitKeys parkedKeys = new(expressionNamespaces);

    private string? resolveSql;

    private sealed record WakeProbeRow(DateTimeOffset AsOfUtc, DateTimeOffset? NextTimeoutAtUtc,
        bool HasTemplateOrSubWorkflow, bool HasAbandonedTake);

    private const string WakeProbeSql = """
        SELECT CURRENT_TIMESTAMP AS "AsOfUtc",
               (SELECT MIN(timeout_at_utc) FROM automation.workflow_suspensions WHERE status = 'Suspended') AS "NextTimeoutAtUtc",
               EXISTS (SELECT 1 FROM automation.workflow_suspensions WHERE status = 'Suspended' AND kind IN (1, 3)) AS "HasTemplateOrSubWorkflow",
               CASE WHEN {0} THEN EXISTS (SELECT 1 FROM automation.workflow_suspensions s
                   JOIN automation.workflow_execution_logs l ON l.id = s.execution_log_id
                   WHERE s.status IN ('Resumed', 'TimedOut') AND s.resolved_at_utc <= CURRENT_TIMESTAMP - make_interval(secs => {1})
                     AND s.resolved_at_utc >= COALESCE(CAST({2} AS timestamptz), '-infinity')
                     AND (CAST({2} AS timestamptz) IS NULL OR s.resolved_at_utc > CAST({2} AS timestamptz)
                          OR (s.resolved_at_utc = CAST({2} AS timestamptz) AND s.id > CAST({3} AS uuid)))
                     AND l.status = 0
                     AND NOT EXISTS (SELECT 1 FROM automation.workflow_suspensions later
                         WHERE later.execution_log_id = s.execution_log_id AND later.suspended_at_utc > s.suspended_at_utc))
                   ELSE FALSE END AS "HasAbandonedTake"
        """;

    /// <inheritdoc />
    public async Task<WorkflowSuspensionWakeProbe?> ProbeWakeAsync(
        TimeSpan? abandonedAfter, WorkflowAbandonedTakeCursor abandonedAfterCursor, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        if (!PostgresStatements.Applies(db.Database))
            return null;
        var rows = await db.Database.SqlQueryRaw<WakeProbeRow>(WakeProbeSql,
                abandonedAfter is not null,
                abandonedAfter?.TotalSeconds ?? 0,
                (object?)abandonedAfterCursor.AfterTakenAtUtc ?? DBNull.Value,
                (object?)abandonedAfterCursor.AfterSuspensionId ?? DBNull.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var row = rows.Single();
        return new WorkflowSuspensionWakeProbe(row.AsOfUtc.ToUniversalTime(), row.NextTimeoutAtUtc?.ToUniversalTime(),
            row.HasTemplateOrSubWorkflow, abandonedAfter is not null && row.HasAbandonedTake);
    }

    /// <inheritdoc />
    /// <remarks>By position: every pass reads and sorts the rows up to the page it wants, and two
    /// rows taken before the page step it over two more. The waker reads by <see cref="WorkflowSuspensionScanCursor"/>.</remarks>
    public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
        ScanAsync(new WorkflowSuspensionScanCursor(afterPosition), byKey: false, limit, null, cancellationToken);

    /// <inheritdoc />
    /// <remarks>By key: the page starts after the last row the previous one handed out, along an index that holds
    /// only parked template rows, so a pass costs the same at the ten-thousandth row as at the first. A row parked
    /// meanwhile with an earlier instant than the cursor — a replica whose clock is behind — is read once the
    /// pages wrap, as a position that drifted over it would have been.</remarks>
    public Task<WorkflowSuspensionScan> ScanAsync(
        WorkflowSuspensionScanCursor after, int limit, CancellationToken cancellationToken) =>
        ScanAsync(after, limit, null, cancellationToken);

    /// <inheritdoc />
    public Task<WorkflowSuspensionScan> ScanAsync(
        WorkflowSuspensionScanCursor after, int limit, IReadOnlyCollection<WorkflowSuspensionKind>? kinds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(after);
        return ScanAsync(after, byKey: true, limit, kinds, cancellationToken);
    }

    private async Task<WorkflowSuspensionScan> ScanAsync(
        WorkflowSuspensionScanCursor after, bool byKey, int limit, IReadOnlyCollection<WorkflowSuspensionKind>? kinds,
        CancellationToken cancellationToken)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A pass takes at least one row.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var now = await DatabaseNowAsync(db, cancellationToken).ConfigureAwait(false);

        var due = await Candidates(
                Parked(db, kinds)
                    .Where(row => row.TimeoutAtUtc <= now)
                    .OrderBy(row => row.TimeoutAtUtc)
                    .Take(limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var room = limit - due.Count;
        var speculative = new List<WorkflowSuspensionCandidate>();
        var next = after;

        if (room > 0 && (kinds is null
                || kinds.Contains(WorkflowSuspensionKind.WaitTemplate)
                || kinds.Contains(WorkflowSuspensionKind.SubWorkflow)))
        {
            var templates = Parked(db, kinds)
                .Where(row => (row.Kind == WorkflowSuspensionKind.WaitTemplate
                               || row.Kind == WorkflowSuspensionKind.SubWorkflow)
                              && row.TimeoutAtUtc > now);

            if (byKey && after is { AfterSuspendedAtUtc: { } at, AfterSuspensionId: { } id })
            {
                templates = templates.Where(row => row.SuspendedAtUtc >= at
                                                   && (row.SuspendedAtUtc > at || row.Id.CompareTo(id) > 0));
            }

            templates = templates.OrderBy(row => row.SuspendedAtUtc).ThenBy(row => row.Id);
            if (!byKey)
                templates = templates.Skip(after.Position);

            speculative = await Candidates(templates.Take(room))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            next = speculative.Count < room
                ? WorkflowSuspensionScanCursor.Start
                : !byKey
                    ? new WorkflowSuspensionScanCursor(after.Position + speculative.Count)
                    : new WorkflowSuspensionScanCursor(0)
                    {
                        AfterSuspendedAtUtc = speculative[^1].SuspendedAtUtc,
                        AfterSuspensionId = speculative[^1].SuspensionId
                    };
        }

        return new WorkflowSuspensionScan(now, [.. due, .. speculative], next.Position) { NextCursor = next };
    }

    /// <inheritdoc />
    public async Task<ResumeContext?> TryResolveAsync(
        Guid suspensionId,
        WorkflowSuspensionStatus status,
        string outcomeJson,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        if (PostgresStatements.Applies(db.Database))
            return await ResolveWithSqlAsync(db, suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken)
                .ConfigureAwait(false);

        var taken = await db.WorkflowSuspensions
            .Where(row => row.Id == suspensionId && row.Status == WorkflowSuspensionStatus.Suspended)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(s => s.Status, status)
                    .SetProperty(s => s.OutcomeJson, outcomeJson)
                    .SetProperty(s => s.ResolvedAtUtc, resolvedAtUtc)
                    .SetProperty(s => s.TakeCount, s => s.TakeCount + 1),
                cancellationToken)
            .ConfigureAwait(false);

        if (taken == 0)
            return null;

        await db.WorkflowSuspensionDependencyKeys.Where(key => key.SuspensionId == suspensionId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.WorkflowSuspensions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == suspensionId, cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new ResumeContext
            {
                SuspensionId = row.Id,
                ExecutionLogId = row.ExecutionLogId,
                WorkflowId = row.WorkflowId,
                BlockId = row.BlockId,
                Kind = row.Kind,
                OutcomeJson = outcomeJson,
                StatePayloadJson = row.StatePayloadJson,
                SuspendedAtUtc = row.SuspendedAtUtc
            };
    }

    private async Task<ResumeContext?> ResolveWithSqlAsync(
        WorkflowDbContext db, Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
        DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken)
    {
        var table = PostgresStatements.Table<WorkflowSuspension>(db.Model);
        var statement = resolveSql ??= ResolveStatement(table, PostgresStatements.Table<WorkflowSuspensionDependencyKey>(db.Model));

        return await PostgresStatements.RunAsync(
                db.Database,
                statement,
                [
                    table.ToProvider(nameof(WorkflowSuspension.Status), status),
                    outcomeJson,
                    resolvedAtUtc,
                    suspensionId,
                    table.ToProvider(nameof(WorkflowSuspension.Status), WorkflowSuspensionStatus.Suspended)
                ],
                async (command, token) =>
                {
                    await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                        return null;

                    return new ResumeContext
                    {
                        SuspensionId = reader.GetGuid(0),
                        ExecutionLogId = reader.GetGuid(1),
                        WorkflowId = reader.GetGuid(2),
                        BlockId = reader.GetString(3),
                        Kind = (WorkflowSuspensionKind)table.FromProvider(nameof(WorkflowSuspension.Kind), reader.GetValue(4))!,
                        OutcomeJson = outcomeJson,
                        StatePayloadJson = reader.GetString(5),
                        SuspendedAtUtc = reader.GetFieldValue<DateTimeOffset>(6)
                    };
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ResolveStatement(PostgresStatements.MappedTable table, PostgresStatements.MappedTable keys)
    {
        string Column(string property) => table.Column(property);

        return $"""
            WITH taken AS (
            UPDATE {table.Name}
            SET {Column(nameof(WorkflowSuspension.Status))} = $1,
                {Column(nameof(WorkflowSuspension.OutcomeJson))} = $2,
                {Column(nameof(WorkflowSuspension.ResolvedAtUtc))} = $3,
                {Column(nameof(WorkflowSuspension.TakeCount))} = {Column(nameof(WorkflowSuspension.TakeCount))} + 1
            WHERE {Column(nameof(WorkflowSuspension.Id))} = $4
              AND {Column(nameof(WorkflowSuspension.Status))} = $5
            RETURNING {Column(nameof(WorkflowSuspension.Id))}, {Column(nameof(WorkflowSuspension.ExecutionLogId))},
                {Column(nameof(WorkflowSuspension.WorkflowId))}, {Column(nameof(WorkflowSuspension.BlockId))},
                {Column(nameof(WorkflowSuspension.Kind))}, {Column(nameof(WorkflowSuspension.StatePayloadJson))},
                {Column(nameof(WorkflowSuspension.SuspendedAtUtc))}
            ), cleared AS (
            DELETE FROM {keys.Name}
            WHERE {keys.Column(nameof(WorkflowSuspensionDependencyKey.SuspensionId))} IN (SELECT {Column(nameof(WorkflowSuspension.Id))} FROM taken)
            )
            SELECT * FROM taken
            """;
    }

    /// <inheritdoc />
    public Task<bool> ReleaseAsync(
        Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
        ReleaseCoreAsync(suspensionId, takenAs, cancellationToken);

    private async Task<bool> ReleaseCoreAsync(
        Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var config = await db.WorkflowSuspensions.AsNoTracking().Where(row => row.Id == suspensionId)
            .Select(row => new { row.Kind, row.ConfigJson }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (config is null)
            return false;

        var keys = parkedKeys.Of(config.Kind, config.ConfigJson);

        if (PostgresStatements.Applies(db.Database))
            return await ReleaseWithSqlAsync(db, suspensionId, takenAs, keys, cancellationToken).ConfigureAwait(false);

        var released = await db.WorkflowSuspensions
            .Where(row => row.Id == suspensionId && row.Status == takenAs)
            .Where(row => db.WorkflowExecutionLogs.Any(log =>
                log.Id == row.ExecutionLogId && log.Status == WorkflowExecutionStatus.Running))
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(s => s.Status, WorkflowSuspensionStatus.Suspended)
                    .SetProperty(s => s.OutcomeJson, (string?)null)
                    .SetProperty(s => s.ResolvedAtUtc, (DateTimeOffset?)null),
                cancellationToken)
            .ConfigureAwait(false);

        if (released > 0)
        {
            await db.WorkflowSuspensionDependencyKeys.Where(key => key.SuspensionId == suspensionId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.WorkflowSuspensionDependencyKeys.AddRange(keys.Select(key => new WorkflowSuspensionDependencyKey(suspensionId, key)));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return released > 0;
    }

    private static async Task<bool> ReleaseWithSqlAsync(
        WorkflowDbContext db, Guid suspensionId, WorkflowSuspensionStatus takenAs, IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        var table = PostgresStatements.Table<WorkflowSuspension>(db.Model);
        var keyTable = PostgresStatements.Table<WorkflowSuspensionDependencyKey>(db.Model);
        var logs = PostgresStatements.Table<WorkflowExecutionLog>(db.Model);
        string Column(string property) => table.Column(property);

        var statement = $"""
            WITH released AS (
            UPDATE {table.Name}
            SET {Column(nameof(WorkflowSuspension.Status))} = $1,
                {Column(nameof(WorkflowSuspension.OutcomeJson))} = NULL,
                {Column(nameof(WorkflowSuspension.ResolvedAtUtc))} = NULL
            WHERE {Column(nameof(WorkflowSuspension.Id))} = $2
              AND {Column(nameof(WorkflowSuspension.Status))} = $3
              AND EXISTS (SELECT 1 FROM {logs.Name} AS log
                  WHERE log.{logs.Column(nameof(WorkflowExecutionLog.Id))} = {table.Name}.{Column(nameof(WorkflowSuspension.ExecutionLogId))}
                    AND log.{logs.Column(nameof(WorkflowExecutionLog.Status))} = $4)
            RETURNING {Column(nameof(WorkflowSuspension.Id))}
            ), keyed AS (
            INSERT INTO {keyTable.Name} ({keyTable.Column(nameof(WorkflowSuspensionDependencyKey.SuspensionId))}, {keyTable.Column(nameof(WorkflowSuspensionDependencyKey.DependencyKey))})
            SELECT released.{Column(nameof(WorkflowSuspension.Id))}, wanted.dependency_key
            FROM released CROSS JOIN unnest($5::text[]) AS wanted(dependency_key)
            ON CONFLICT DO NOTHING
            )
            SELECT count(*) FROM released
            """;

        var released = await PostgresStatements.RunAsync(
            db.Database,
            statement,
            [
                table.ToProvider(nameof(WorkflowSuspension.Status), WorkflowSuspensionStatus.Suspended),
                suspensionId,
                table.ToProvider(nameof(WorkflowSuspension.Status), takenAs),
                (int)WorkflowExecutionStatus.Running,
                keys.ToArray()
            ],
            async (command, token) => Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);
        return released > 0;
    }

    /// <inheritdoc />
    /// <remarks>Two conditions besides the age. The run's journal entry is still running, so a run that ended is not
    /// offered; and no later wait of the same run exists, so a run that was re-entered and has parked again since
    /// is not taken back to the wait before. A run somebody is resuming right now — a long segment, or a slow load —
    /// is offered anyway, because nothing here can see that; the resume's own swap refuses it.
    /// <para>Read in the order the waits were taken, from the cursor, over an index of the taken waits alone. Every
    /// cursor a look hands back is at or before the instant it read to, a cutoff ago; so a take whose commit trailed
    /// the instant it was stamped with by less than the cutoff is still read, and one that trailed by more only by a
    /// look from the start, which is what a waker's first look after it starts is.</para></remarks>
    public async Task<WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(
        WorkflowAbandonedTakeCursor after, TimeSpan abandonedAfter, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A pass takes at least one row.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var now = await DatabaseNowAsync(db, cancellationToken).ConfigureAwait(false);
        var takenBefore = now - abandonedAfter;

        var taken = db.WorkflowSuspensions
            .AsNoTracking()
            .Where(row => (row.Status == WorkflowSuspensionStatus.Resumed || row.Status == WorkflowSuspensionStatus.TimedOut)
                          && row.ResolvedAtUtc != null
                          && row.ResolvedAtUtc <= takenBefore);

        if (after.AfterTakenAtUtc is { } at)
        {
            taken = after.AfterSuspensionId is { } id
                ? taken.Where(row => row.ResolvedAtUtc >= at && (row.ResolvedAtUtc > at || row.Id.CompareTo(id) > 0))
                : taken.Where(row => row.ResolvedAtUtc > at);
        }

        var rows = await taken
            .Where(row => db.WorkflowExecutionLogs.Any(log =>
                log.Id == row.ExecutionLogId && log.Status == WorkflowExecutionStatus.Running))
            .Where(row => !db.WorkflowSuspensions.Any(later =>
                later.ExecutionLogId == row.ExecutionLogId && later.SuspendedAtUtc > row.SuspendedAtUtc))
            .OrderBy(row => row.ResolvedAtUtc)
            .ThenBy(row => row.Id)
            .Take(limit)
            .Select(row => new
            {
                row.Id, row.ExecutionLogId, row.WorkflowId, row.BlockId, row.Kind, row.ConfigJson,
                row.StatePayloadJson, row.SuspendedAtUtc, row.TimeoutAtUtc, row.Status, row.OutcomeJson, row.TakeCount,
                row.ResolvedAtUtc
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidates = rows
            .Select(row => new WorkflowSuspensionCandidate(
                row.Id, row.ExecutionLogId, row.WorkflowId, row.BlockId, row.Kind, row.ConfigJson,
                row.StatePayloadJson, row.SuspendedAtUtc, row.TimeoutAtUtc)
            {
                Status = row.Status,
                OutcomeJson = row.OutcomeJson,
                TakeCount = row.TakeCount,
                TakenAtUtc = row.ResolvedAtUtc
            })
            .ToList();

        return new WorkflowAbandonedTakeScan(candidates, NextAbandonedTakeCursor(after, candidates, limit, takenBefore));
    }

    private static WorkflowAbandonedTakeCursor NextAbandonedTakeCursor(
        WorkflowAbandonedTakeCursor after, List<WorkflowSuspensionCandidate> candidates, int limit, DateTimeOffset readTo)
    {
        if (candidates.Count == limit)
            return new WorkflowAbandonedTakeCursor
            {
                AfterTakenAtUtc = candidates[^1].TakenAtUtc, AfterSuspensionId = candidates[^1].SuspensionId
            };

        return after.AfterTakenAtUtc is { } at && at >= readTo
            ? after
            : new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = readTo };
    }

    /// <inheritdoc />
    /// <remarks>The take count is the fence and the re-stamp is the back-off: a takeover of a run that turns out
    /// to be still resuming is not offered again until the cutoff has passed once more.</remarks>
    public async Task<ResumeContext?> TryRetakeAsync(
        WorkflowSuspensionCandidate abandoned, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(abandoned);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var now = await DatabaseNowAsync(db, cancellationToken).ConfigureAwait(false);
        var retaken = await db.WorkflowSuspensions
            .Where(row => row.Id == abandoned.SuspensionId
                          && row.Status == abandoned.Status
                          && row.TakeCount == abandoned.TakeCount)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(s => s.ResolvedAtUtc, now)
                    .SetProperty(s => s.TakeCount, s => s.TakeCount + 1),
                cancellationToken)
            .ConfigureAwait(false);

        if (retaken == 0)
            return null;

        var row = await db.WorkflowSuspensions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == abandoned.SuspensionId, cancellationToken)
            .ConfigureAwait(false);

        return row?.OutcomeJson is null
            ? null
            : new ResumeContext
            {
                SuspensionId = row.Id,
                ExecutionLogId = row.ExecutionLogId,
                WorkflowId = row.WorkflowId,
                BlockId = row.BlockId,
                Kind = row.Kind,
                OutcomeJson = row.OutcomeJson,
                StatePayloadJson = row.StatePayloadJson,
                SuspendedAtUtc = row.SuspendedAtUtc
            };
    }

    /// <inheritdoc />
    /// <remarks>By primary key where the wake named the suspension, and along the execution's own index where it
    /// named the run. A wake carrying both is matched on both: a pair that does not belong together names no row,
    /// and answering with either half would resume a run nobody asked about. A run that has forked is parked on
    /// more than one wait at once, and an execution id names none of them in particular — such a read answers
    /// with no row rather than guessing, because a trigger's wake resolves the row it is handed without asking a
    /// condition, and a guess would fire one branch on the other's event and strand them both.</remarks>
    public async Task<WorkflowParkedRowRead?> GetParkedAsync(
        Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken)
    {
        if (suspensionId is null && executionLogId is null)
        {
            throw new ArgumentException(
                "A read of one parked row names neither a suspension nor an execution, so there is no row to read.");
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var now = await DatabaseNowAsync(db, cancellationToken).ConfigureAwait(false);

        var parked = Parked(db, null);
        if (suspensionId is { } id)
            parked = parked.Where(row => row.Id == id);
        if (executionLogId is { } log)
            parked = parked.Where(row => row.ExecutionLogId == log);

        var rows = await Candidates(parked
                .Where(candidate => db.WorkflowExecutionLogs.Any(log =>
                    log.Id == candidate.ExecutionLogId && log.Status == WorkflowExecutionStatus.Running))
                .Take(2))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 1)
            return new WorkflowParkedRowRead(now, rows[0]);

        return new WorkflowParkedRowRead(now, null)
        {
            NoLongerWaiting = await NoLongerWaitingAsync(db, suspensionId, executionLogId, cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private static async Task<bool> NoLongerWaitingAsync(
        WorkflowDbContext db, Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken)
    {
        if (suspensionId is { } id)
        {
            var named = db.WorkflowSuspensions.AsNoTracking().Where(row => row.Id == id);
            if (executionLogId is { } pairedWith)
                named = named.Where(row => row.ExecutionLogId == pairedWith);

            return await named
                .AnyAsync(row => row.Status != WorkflowSuspensionStatus.Suspended
                                 || !db.WorkflowExecutionLogs.Any(log =>
                                     log.Id == row.ExecutionLogId && log.Status == WorkflowExecutionStatus.Running),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await db.WorkflowExecutionLogs
            .AnyAsync(log => log.Id == executionLogId && log.Status != WorkflowExecutionStatus.Running, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>By key along <c>ix_workflow_suspensions_parked_speculative</c>, whose filter
    /// <see cref="ParkedWaitKinds"/> and this page are built from together, so an index build costs the same at
    /// the ten-thousandth parked wait as at the first. A wait parked meanwhile with an earlier instant than the
    /// cursor — a replica whose clock is behind — is read once the pages wrap, as the waker's own page reads it.
    ///
    /// The kinds are the ones a host can wake, whatever it asks for: a sleep ends at its own deadline, so an
    /// index entry for one is an entry nothing ever hits, and reading it would cost this page its index besides.
    ///
    /// Only the five columns a wait is read as are selected. The state payload is the run's whole checkpoint,
    /// and a page of fifty of those, read every time a host rebuilds its index, is a state dump.
    ///
    /// It does not ask what <see cref="GetParkedAsync"/> asks — that the row's run is still running. That test
    /// is a join, and a join is what would cost this page the index scan it exists for. The asymmetry is
    /// bounded: a wake published for a row whose run ended is refused by the read — a trigger's counted lost at
    /// once, any other held, tried and dropped — which costs at most a hold slot and a read, and never a run.</remarks>
    public async Task<WorkflowParkedWaitScan> ScanParkedWaitsAsync(
        WorkflowSuspensionScanCursor after, int limit, IReadOnlyCollection<WorkflowSuspensionKind>? kinds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var wanted = kinds is null ? null : ParkedWaitKinds.Wakeable.Where(kinds.Contains).ToList();
        if (wanted is { Count: 0 })
            return new WorkflowParkedWaitScan([], WorkflowSuspensionScanCursor.Start);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var parked = db.WorkflowSuspensions
            .AsNoTracking()
            .Where(row => row.Status == WorkflowSuspensionStatus.Suspended
                          && (row.Kind == WorkflowSuspensionKind.WaitForTrigger
                              || row.Kind == WorkflowSuspensionKind.WaitTemplate
                              || row.Kind == WorkflowSuspensionKind.SubWorkflow
                              || row.Kind == WorkflowSuspensionKind.Predicate));

        if (wanted is not null)
            parked = parked.Where(row => wanted.Contains(row.Kind));

        if (after is { AfterSuspendedAtUtc: { } at, AfterSuspensionId: { } id })
        {
            parked = parked.Where(row => row.SuspendedAtUtc >= at
                                         && (row.SuspendedAtUtc > at || row.Id.CompareTo(id) > 0));
        }

        var page = await parked
            .OrderBy(row => row.SuspendedAtUtc)
            .ThenBy(row => row.Id)
            .Take(limit)
            .Select(row => new ParkedWaitRow(row.Id, row.ExecutionLogId, row.Kind, row.ConfigJson, row.SuspendedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var waits = page
            .Select(row => new WorkflowParkedWait(
                row.Id,
                row.ExecutionLogId,
                row.Kind,
                parkedKeys.Of(row.Kind, row.ConfigJson),
                row.SuspendedAtUtc))
            .ToList();

        var next = page.Count < limit
            ? WorkflowSuspensionScanCursor.Start
            : new WorkflowSuspensionScanCursor(0)
            {
                AfterSuspendedAtUtc = page[^1].SuspendedAtUtc,
                AfterSuspensionId = page[^1].Id
            };

        return new WorkflowParkedWaitScan(waits, next);
    }

    /// <inheritdoc />
    /// <remarks>Read through the key index alone: the waits holding a key row for the key asked for, then every key
    /// row of that page, so each wait comes back with all it reads. Every writer keys a wait when it parks and again
    /// when it is put back, so a keyed-kind wait with no key row reads nothing a fire could change. The cursor follows
    /// the rows read, not the rows answered, so a page can hold fewer than <paramref name="limit"/> waits and not be
    /// the last.</remarks>
    public async Task<WorkflowParkedWaitScan> ScanParkedWaitsByDependencyKeyAsync(
        string dependencyKey, WorkflowSuspensionScanCursor after, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(dependencyKey);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var kinds = ParkedWaitKinds.Keyed.ToArray();
        var keyed = db.WorkflowSuspensions.AsNoTracking()
            .Where(row => row.Status == WorkflowSuspensionStatus.Suspended && kinds.Contains(row.Kind)
                          && db.WorkflowSuspensionDependencyKeys.Any(key =>
                              key.SuspensionId == row.Id && key.DependencyKey == dependencyKey));
        if (after is { AfterSuspendedAtUtc: { } at, AfterSuspensionId: { } id })
            keyed = keyed.Where(row => row.SuspendedAtUtc >= at && (row.SuspendedAtUtc > at || row.Id.CompareTo(id) > 0));

        var read = await keyed
            .OrderBy(row => row.SuspendedAtUtc).ThenBy(row => row.Id).Take(limit)
            .Select(row => new KeyedPageRow(row.Id, row.ExecutionLogId, row.Kind, row.SuspendedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var readIds = read.Select(row => row.Id).ToArray();
        var keysOf = (await db.WorkflowSuspensionDependencyKeys.AsNoTracking()
                .Where(key => readIds.Contains(key.SuspensionId))
                .Select(key => new { key.SuspensionId, key.DependencyKey })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(key => key.SuspensionId)
            .ToDictionary(group => group.Key, group => group.Select(key => key.DependencyKey).ToHashSet(StringComparer.Ordinal));

        var waits = new List<WorkflowParkedWait>();
        foreach (var row in read)
        {
            var keys = keysOf.GetValueOrDefault(row.Id, [dependencyKey]);
            if (keys.Contains(dependencyKey))
                waits.Add(new WorkflowParkedWait(row.Id, row.ExecutionLogId, row.Kind, keys, row.SuspendedAtUtc));
        }

        var next = read.Count < limit ? WorkflowSuspensionScanCursor.Start : new WorkflowSuspensionScanCursor(0)
        {
            AfterSuspendedAtUtc = read[^1].SuspendedAtUtc,
            AfterSuspensionId = read[^1].Id
        };
        return new WorkflowParkedWaitScan(waits, next);
    }

    private sealed record ParkedWaitRow(
        Guid Id, Guid ExecutionLogId, WorkflowSuspensionKind Kind, string ConfigJson, DateTimeOffset SuspendedAtUtc);

    private sealed record KeyedPageRow(
        Guid Id, Guid ExecutionLogId, WorkflowSuspensionKind Kind, DateTimeOffset SuspendedAtUtc);

    private static IQueryable<WorkflowSuspension> Parked(
        WorkflowDbContext db, IReadOnlyCollection<WorkflowSuspensionKind>? kinds)
    {
        var parked = db.WorkflowSuspensions
            .AsNoTracking()
            .Where(row => row.Status == WorkflowSuspensionStatus.Suspended);

        if (kinds is null)
            return parked;

        var wanted = kinds.Distinct().ToList();
        return parked.Where(row => wanted.Contains(row.Kind));
    }

    private static IQueryable<WorkflowSuspensionCandidate> Candidates(IQueryable<WorkflowSuspension> rows) =>
        rows
            .Select(row => new WorkflowSuspensionCandidate(
                row.Id,
                row.ExecutionLogId,
                row.WorkflowId,
                row.BlockId,
                row.Kind,
                row.ConfigJson,
                row.StatePayloadJson,
                row.SuspendedAtUtc,
                row.TimeoutAtUtc));

    private static async Task<DateTimeOffset> DatabaseNowAsync(
        WorkflowDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<DateTimeOffset>($"SELECT CURRENT_TIMESTAMP AS \"Value\"")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0].ToUniversalTime()
            : throw new InvalidOperationException(
                $"CURRENT_TIMESTAMP answered with {rows.Count} values rather than one, so the waker has no "
                + "clock to decide what is ready. Fill Seams.SuspensionResumeStore with a store written for "
                + "this provider.");
    }
}
