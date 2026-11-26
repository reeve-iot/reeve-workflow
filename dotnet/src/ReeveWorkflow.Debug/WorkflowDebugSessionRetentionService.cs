using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug;

/// <summary>Hourly sweep of debug sessions past the retention window, blobs and rows alike. Status is not in the
/// predicate: a session abandoned mid-debug never reaches a terminal one, and would otherwise never expire.</summary>
public sealed class WorkflowDebugSessionRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowDebugOptions> options,
    TimeProvider clock,
    IWorkflowLeaderGate leader,
    IWorkflowDebugBlobStore blobs,
    ILogger<WorkflowDebugSessionRetentionService> logger) : BackgroundService
{
    public const string LeaderLockKey = "WorkflowDebugSessionRetention_Leader";

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly int retentionDays = Math.Clamp(options.Value.SnapshotRetentionDays, 1, 365);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Workflow debug session retention starting. RetentionDays={RetentionDays}, OrphanedTimeoutSeconds={OrphanedTimeoutSeconds}.",
            options.Value.SnapshotRetentionDays,
            options.Value.OrphanedSessionTimeoutSeconds);

        await RunSweepTickAsync(stoppingToken);

        using var timer = new PeriodicTimer(CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSweepTickAsync(stoppingToken);
        }
    }

    /// <summary>Gate acquisition sits outside SweepAsync's try/catch; an escaping fault there would trip
    /// BackgroundServiceExceptionBehavior.StopHost and tear the host down.</summary>
    public async Task RunSweepTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepUnderLeaderAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Workflow debug session retention: tick failed; will retry next interval.");
        }
    }

    /// <summary>Runs one sweep only where the gate says this replica leads. Every replica hosts this service and
    /// the deletes are tracked RemoveRange plus SaveChanges, so ungated they race the same rows.</summary>
    public Task SweepUnderLeaderAsync(CancellationToken cancellationToken)
        => leader.RunUnderLeaderAsync(LeaderLockKey, SweepAsync, cancellationToken);

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

            var cutoff = clock.GetUtcNow().AddDays(-retentionDays);

            var expired = await db.WorkflowDebugSessions
                .Include(s => s.Snapshots)
                .Where(s => s.LastActivityUtc < cutoff)
                .ToListAsync(cancellationToken);

            if (expired.Count == 0)
            {
                logger.LogDebug("Workflow debug session retention: no expired sessions found.");
                return;
            }

            foreach (var session in expired)
            {
                foreach (var snapshot in session.Snapshots)
                {
                    await blobs.DeleteAsync($"{session.Id}/snapshots/{snapshot.Id}", cancellationToken)
                        .ConfigureAwait(false);
                }

                await blobs.DeleteAsync(session.Id.ToString(), cancellationToken).ConfigureAwait(false);
            }

            db.WorkflowDebugSessions.RemoveRange(expired);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Workflow debug session retention: deleted {Count} expired debug session(s) (cutoff={Cutoff:O}).",
                expired.Count,
                cutoff);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Workflow debug session retention: sweep failed.");
        }
    }
}
