using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Where a parked run waits, in the workflow context's own table. A host that writes nothing gets
/// this, and its runs survive a restart;</summary>
/// <remarks>Nothing in-process is registered for resumption: a timer per suspension is a timer per replica
/// over one row, and what actually moves a parked run is <see cref="WorkflowSuspensionWaker"/>'s pass, which
/// is the same on one replica and on ten.</remarks>
public sealed class EfWorkflowSuspensionStore(
    IServiceScopeFactory scopeFactory, IReadOnlyList<WorkflowExpressionNamespace> expressionNamespaces,
    WorkflowSuspensionOptions? options = null)
    : IWorkflowSuspensionStore, IWorkflowParkedSleepStore
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly WorkflowSuspensionOptions options = options ?? new WorkflowSuspensionOptions();

    private readonly ParkedWaitKeys parkedKeys = new(expressionNamespaces);

    /// <inheritdoc />
    public async Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var bytes = Encoding.UTF8.GetByteCount(request.StatePayloadJson);
        if (bytes > options.MaxStatePayloadBytes)
            throw new SuspendPayloadTooLargeException(bytes);

        var suspension = new WorkflowSuspension(
            Guid.NewGuid(),
            request.ExecutionLogId,
            request.WorkflowId,
            request.BlockId,
            request.Kind,
            request.ConfigJson,
            request.StatePayloadJson,
            request.SuspendedAtUtc,
            request.TimeoutAtUtc);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        db.WorkflowSuspensions.Add(suspension);
        foreach (var key in parkedKeys.Of(request.Kind, request.ConfigJson))
            db.WorkflowSuspensionDependencyKeys.Add(new WorkflowSuspensionDependencyKey(suspension.Id, key));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return suspension;
    }

    /// <inheritdoc />
    public async Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        await db.WorkflowSuspensionDependencyKeys
            .Where(key => db.WorkflowSuspensions.Any(row => row.Id == key.SuspensionId && row.ExecutionLogId == executionLogId
                && row.Status == WorkflowSuspensionStatus.Suspended))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        await db.WorkflowSuspensions
            .Where(row => row.ExecutionLogId == executionLogId && row.Status == WorkflowSuspensionStatus.Suspended)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(s => s.Status, WorkflowSuspensionStatus.Cancelled)
                    .SetProperty(s => s.ResolvedAtUtc, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>No index serves the workflow id: the status leads the one that exists. The rows read are this
    /// workflow's sleeps still parked or taken whose run is running, which are few; the newest per run is picked
    /// here rather than in SQL, where a provider such as SQLite cannot compare the instants.</remarks>
    public async Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        return (await ParkedSleepIdsAsync(db, workflowId, cancellationToken).ConfigureAwait(false)).Count > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(
        Guid workflowId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var ids = await ParkedSleepIdsAsync(db, workflowId, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
            return [];

        return await db.WorkflowSuspensions
            .AsNoTracking()
            .Where(row => ids.Contains(row.Id))
            .Select(row => new WorkflowParkedSleep(
                row.Id, row.ExecutionLogId, row.BlockId, row.Status, row.StatePayloadJson, row.SuspendedAtUtc))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var swapped = await db.WorkflowSuspensions
            .Where(row => row.Id == suspensionId
                && row.Kind == WorkflowSuspensionKind.Sleep
                && (row.Status == WorkflowSuspensionStatus.Suspended
                    || row.Status == WorkflowSuspensionStatus.Resumed
                    || row.Status == WorkflowSuspensionStatus.TimedOut))
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(s => s.Status, WorkflowSuspensionStatus.Cancelled)
                    .SetProperty(s => s.ResolvedAtUtc, DateTimeOffset.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        return swapped > 0;
    }

    /// <inheritdoc />
    /// <remarks>Compares instants in SQL, which SQLite cannot translate; it is asked whenever a Restart sleep resumes.</remarks>
    public async Task<bool> HasRunStartedSinceAsync(
        Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        return await db.WorkflowExecutionLogs
            .AsNoTracking()
            .AnyAsync(log => log.WorkflowId == workflowId && log.Id != exceptExecutionLogId && log.StartedAtUtc > sinceUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<List<Guid>> ParkedSleepIdsAsync(
        WorkflowDbContext db, Guid workflowId, CancellationToken cancellationToken)
    {
        var sleeps = await db.WorkflowSuspensions
            .AsNoTracking()
            .Where(row => row.WorkflowId == workflowId
                && row.Kind == WorkflowSuspensionKind.Sleep
                && (row.Status == WorkflowSuspensionStatus.Suspended
                    || row.Status == WorkflowSuspensionStatus.Resumed
                    || row.Status == WorkflowSuspensionStatus.TimedOut)
                && db.WorkflowExecutionLogs.Any(log =>
                    log.Id == row.ExecutionLogId && log.Status == WorkflowExecutionStatus.Running))
            .Select(row => new { row.Id, row.ExecutionLogId, row.Status, row.SuspendedAtUtc })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var taken = sleeps.Where(row => row.Status != WorkflowSuspensionStatus.Suspended).ToList();
        if (taken.Count == 0)
            return sleeps.Where(row => row.Status == WorkflowSuspensionStatus.Suspended).Select(row => row.Id).ToList();

        var runIds = taken.Select(row => row.ExecutionLogId).Distinct().ToList();
        var newest = (await db.WorkflowSuspensions
                .AsNoTracking()
                .Where(row => runIds.Contains(row.ExecutionLogId))
                .Select(row => new { row.Id, row.ExecutionLogId, row.SuspendedAtUtc })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(row => row.ExecutionLogId)
            .Select(run => run.MaxBy(row => row.SuspendedAtUtc)!.Id)
            .ToHashSet();

        return sleeps
            .Where(row => row.Status == WorkflowSuspensionStatus.Suspended || newest.Contains(row.Id))
            .Select(row => row.Id)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>Nothing to do: the waker's pass finds the row, and it finds it on whichever replica is up.</remarks>
    public void RegisterForResumption(WorkflowSuspension suspension) { }

    /// <inheritdoc />
    /// <remarks>Nothing to do either, for the same reason — <see cref="CancelByExecutionAsync"/> is what
    /// stops the row being found.</remarks>
    public void TearDownByExecution(Guid executionLogId) { }
}

/// <summary>What this package will park, and how much of a run it will hold.</summary>
public sealed class WorkflowSuspensionOptions
{
    /// <summary>The cap on one parked run's state payload. Past it the run fails at the wait, which is louder
    /// than a run whose middle is silently lost.</summary>
    public int MaxStatePayloadBytes { get; set; } = 256 * 1024;
}
