using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.History;

/// <summary>Deletes superseded workflow versions. A version goes only if no run that has not ended pins it in its
/// manifest when the sweep reads the pins, and if, in the statement that deletes it, no workflow names it current or
/// draft and no run references it: a run's history would cascade away with its version.</summary>
/// <remarks>A sweep lists every workflow's candidates by the rules of <see cref="WorkflowVersionRetentionOptions"/>,
/// then reads the manifests of the runs that have not ended, once, then deletes workflow by workflow, each in a
/// transaction of its own, so a fault rolls back that workflow's deletes alone. The transaction locks the workflow's
/// row, as each editor write takes it first, then the candidates' rows, then deletes. On PostgreSQL the workflow's
/// lock is <c>FOR NO KEY UPDATE</c>, the lock an editor's update takes, and the candidates' is <c>FOR UPDATE</c>,
/// which waits for a run whose opening insert references one and has not committed. On SQLite the transaction holds
/// the database's write lock from its start. Nothing here moves <c>workflows.revision</c>.</remarks>
public sealed class WorkflowVersionRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowRetentionOptions> options,
    TimeProvider clock,
    IWorkflowLeaderGate leader,
    ILogger<WorkflowVersionRetentionService> logger) : BackgroundService
{
    public const string LeaderLockKey = "WorkflowVersionRetention_Leader";

    private readonly int maxVersions = Math.Clamp(options.Value.Versions.MaxVersionsPerWorkflow, 1, 10_000);
    private readonly int retentionDays = Math.Clamp(options.Value.Versions.RetentionDays, 1, 36_500);
    private readonly TimeSpan sweepInterval =
        TimeSpan.FromHours(Math.Clamp(options.Value.Versions.SweepIntervalHours, 1, 8_760));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Workflow version retention starting. MaxVersions={MaxVersions}, RetentionDays={RetentionDays}, SweepIntervalHours={IntervalHours}.",
            maxVersions,
            retentionDays,
            sweepInterval.TotalHours);

        await RunCleanupTickAsync(stoppingToken);

        using var timer = new PeriodicTimer(sweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunCleanupTickAsync(stoppingToken);
        }
    }

    /// <summary>Gate acquisition sits outside CleanupOldVersionsAsync's try/catch, so a transient fault there
    /// must be swallowed here or it tears the host down via StopHost.</summary>
    public async Task RunCleanupTickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CleanupUnderLeaderAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Workflow version retention tick failed; will retry next interval.");
        }
    }

    public Task CleanupUnderLeaderAsync(CancellationToken cancellationToken)
        => leader.RunUnderLeaderAsync(LeaderLockKey, CleanupOldVersionsAsync, cancellationToken);

    private async Task CleanupOldVersionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cutoffDate = clock.GetUtcNow().AddDays(-retentionDays);

            List<Guid> workflowIds;
            using (var scope = scopeFactory.CreateScope())
            {
                workflowIds = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows
                    .Select(w => w.Id)
                    .ToListAsync(cancellationToken);
            }

            var listed = new List<(Guid WorkflowId, List<Guid> Candidates)>();
            foreach (var workflowId in workflowIds)
            {
                await OneWorkflowAsync(workflowId, async () =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
                    var candidates = await CandidatesAsync(db, workflowId, cutoffDate, cancellationToken);
                    if (candidates.Count > 0)
                        listed.Add((workflowId, candidates));
                }, cancellationToken);
            }

            if (listed.Count == 0)
                return;

            var pinned = await PinnedAsync(cancellationToken);
            int totalDeleted = 0;

            foreach (var (workflowId, candidates) in listed)
            {
                var unpinned = candidates.Where(id => !pinned.Contains(id)).ToList();
                if (unpinned.Count > 0)
                {
                    await OneWorkflowAsync(workflowId,
                        async () => totalDeleted += await DeleteAsync(workflowId, unpinned, cancellationToken), cancellationToken);
                }
            }

            if (totalDeleted > 0)
            {
                logger.LogInformation("Deleted {Count} old workflow version(s).", totalDeleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while cleaning up old workflow versions.");
        }
    }

    private async Task OneWorkflowAsync(Guid workflowId, Func<Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workflow version retention skipped workflow {WorkflowId} this sweep.", workflowId);
        }
    }

    private async Task<int> DeleteAsync(Guid workflowId, List<Guid> candidates, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, workflowId, candidates, cancellationToken);

        var deleted = await db.WorkflowVersions
            .Where(v => v.WorkflowId == workflowId && candidates.Contains(v.Id)
                && !db.Workflows.Any(w => w.CurrentVersionId == v.Id || w.DraftVersionId == v.Id)
                && !db.WorkflowExecutionLogs.Any(e => e.WorkflowVersionId == v.Id))
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private async Task<HashSet<Guid>> PinnedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var manifests = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionStates
            .Where(s => s.PinnedVersionsJson != null
                && s.Status != nameof(WorkflowRunStateStatus.Completed)
                && s.Status != nameof(WorkflowRunStateStatus.Failed)
                && s.Status != nameof(WorkflowRunStateStatus.Cancelled))
            .Select(s => s.PinnedVersionsJson!)
            .ToListAsync(cancellationToken);

        var pinned = new HashSet<Guid>();
        foreach (var manifest in manifests)
        {
            if (JsonSerializer.Deserialize<Dictionary<Guid, Guid>>(manifest) is { } read)
                pinned.UnionWith(read.Values);
        }

        return pinned;
    }

    private async Task<List<Guid>> CandidatesAsync(
        WorkflowDbContext db, Guid workflowId, DateTimeOffset cutoffDate, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows
            .Where(w => w.Id == workflowId)
            .Select(w => new { w.CurrentVersionId, w.DraftVersionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (workflow == null) return [];

        var versions = await db.WorkflowVersions
            .Where(v => v.WorkflowId == workflowId)
            .Where(v => v.Id != workflow.CurrentVersionId && v.Id != workflow.DraftVersionId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new { v.Id, v.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        var allExecutionVersionIds = await db.WorkflowExecutionLogs
            .Where(e => e.WorkflowId == workflowId)
            .Select(e => e.WorkflowVersionId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var candidates = new List<Guid>();
        for (int i = 0; i < versions.Count; i++)
        {
            var v = versions[i];

            if (allExecutionVersionIds.Contains(v.Id))
            {
                continue;
            }

            if (i >= maxVersions || v.CreatedAtUtc < cutoffDate)
            {
                candidates.Add(v.Id);
            }
        }

        return candidates;
    }

    private static async Task LockAsync(
        WorkflowDbContext db, Guid workflowId, List<Guid> candidates, CancellationToken cancellationToken)
    {
        if (!PostgresStatements.Applies(db.Database))
            return;

        var workflows = PostgresStatements.Table<Workflow>(db.Model);
        var versions = PostgresStatements.Table<WorkflowVersion>(db.Model);
        var workflowRow = "SELECT 1 FROM " + workflows.Name + " WHERE " + workflows.Column("Id") + " = {0} FOR NO KEY UPDATE";
        var candidateRows = "SELECT 1 FROM " + versions.Name + " WHERE " + versions.Column("Id") + " = ANY({0}) FOR UPDATE";
        await db.Database.ExecuteSqlRawAsync(workflowRow, [workflowId], cancellationToken);
        await db.Database.ExecuteSqlRawAsync(candidateRows, [candidates.ToArray()], cancellationToken);
    }
}
