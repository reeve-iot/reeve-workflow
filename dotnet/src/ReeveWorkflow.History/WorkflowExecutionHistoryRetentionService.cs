using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.History;

/// <summary>Sweeps aged-out run history. Only finished runs are eligible — an unfinished one is live engine
/// state, not history, and deleting it would take the run with it.</summary>
public sealed class WorkflowExecutionHistoryRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowRetentionOptions> options,
    TimeProvider clock,
    IWorkflowLeaderGate leader,
    ILogger<WorkflowExecutionHistoryRetentionService> logger) : BackgroundService
{
    public const string LeaderLockKey = "WorkflowExecutionHistoryRetention_Leader";

    public static readonly IReadOnlyList<WorkflowExecutionStatus> TerminalLogStatuses =
    [
        WorkflowExecutionStatus.Succeeded,
        WorkflowExecutionStatus.Failed,
        WorkflowExecutionStatus.Cancelled,
        WorkflowExecutionStatus.TimedOut
    ];

    public static readonly IReadOnlyList<string> TerminalStateStatuses = ["Completed", "Failed", "Cancelled"];

    private readonly WorkflowExecutionHistoryRetentionOptions retention = options.Value.ExecutionHistory;

    private readonly TimeSpan sweepBudget = TimeSpan.FromSeconds(Math.Clamp(options.Value.ExecutionHistory.MaxSweepSeconds, 1, 3_600));

    private volatile bool backlogLeft;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromHours(Math.Clamp(retention.SweepIntervalHours, 1, 168));

        logger.LogInformation(
            "Workflow execution history retention starting. RetentionDays={RetentionDays}, SweepIntervalHours={IntervalHours}, MaxSweepSeconds={SweepSeconds}.",
            retention.RetentionDays,
            interval.TotalHours,
            sweepBudget.TotalSeconds);

        await RunSweepTickAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(backlogLeft ? sweepBudget : interval, clock, stoppingToken);
            await RunSweepTickAsync(stoppingToken);
        }
    }

    /// <summary>Gate acquisition sits outside SweepAsync's try/catch; an escaping fault there would trip
    /// BackgroundServiceExceptionBehavior.StopHost and tear the host down.</summary>
    public async Task RunSweepTickAsync(CancellationToken cancellationToken)
    {
        backlogLeft = false;
        try
        {
            await SweepUnderLeaderAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Workflow execution history retention tick failed; will retry next interval.");
        }
    }

    public Task SweepUnderLeaderAsync(CancellationToken cancellationToken)
        => leader.RunUnderLeaderAsync(LeaderLockKey, SweepAsync, cancellationToken);

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

            var cutoff = clock.GetUtcNow().AddDays(-Math.Clamp(retention.RetentionDays, 1, 3650));

            var (logsDeleted, logsLeft) = await DeleteExpiredExecutionLogsAsync(db, cutoff, cancellationToken);
            var (statesDeleted, statesLeft) = await DeleteExpiredExecutionStatesAsync(db, cutoff, cancellationToken);
            backlogLeft = logsLeft || statesLeft;

            if (logsDeleted == 0 && statesDeleted == 0)
            {
                logger.LogDebug("Workflow execution history retention: nothing expired (cutoff={Cutoff:O}).", cutoff);
                return;
            }

            logger.LogInformation(
                "Workflow execution history retention: deleted {LogCount} execution log(s) and {StateCount} terminal state row(s) (cutoff={Cutoff:O}); more expired: {BacklogLeft}.",
                logsDeleted,
                statesDeleted,
                cutoff,
                backlogLeft);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Workflow execution history retention sweep failed.");
        }
    }

    /// <summary>Ids of finished runs that aged out. The coalesce keeps a terminal row whose completion stamp
    /// was never written from becoming immortal.</summary>
    public static Task<List<Guid>> SelectExpiredExecutionLogIdsAsync(
        WorkflowDbContext db, DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
        => db.WorkflowExecutionLogs
            .Where(log => TerminalLogStatuses.Contains(log.Status)
                && (log.CompletedAtUtc ?? log.StartedAtUtc) < cutoff)
            .OrderBy(log => log.CompletedAtUtc ?? log.StartedAtUtc)
            .Select(log => log.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    /// <summary>Execution ids of terminal distributed-state rows that aged out, oldest first.</summary>
    public static Task<List<Guid>> SelectExpiredExecutionStateIdsAsync(
        WorkflowDbContext db, DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
        => db.WorkflowExecutionStates
            .Where(state => TerminalStateStatuses.Contains(state.Status)
                && (state.CompletedAtUtc ?? state.LastTransitionUtc) < cutoff)
            .OrderBy(state => state.CompletedAtUtc ?? state.LastTransitionUtc)
            .Select(state => state.ExecutionId)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

    private async Task<(int Deleted, bool Left)> DeleteExpiredExecutionLogsAsync(
        WorkflowDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
        => await DeleteInBatchesAsync(
            batchSize => SelectExpiredExecutionLogIdsAsync(db, cutoff, batchSize, cancellationToken),
            ids => db.WorkflowExecutionLogs.Where(log => ids.Contains(log.Id)).ExecuteDeleteAsync(cancellationToken),
            cancellationToken);

    private async Task<(int Deleted, bool Left)> DeleteExpiredExecutionStatesAsync(
        WorkflowDbContext db, DateTimeOffset cutoff, CancellationToken cancellationToken)
        => await DeleteInBatchesAsync(
            batchSize => SelectExpiredExecutionStateIdsAsync(db, cutoff, batchSize, cancellationToken),
            ids => db.WorkflowExecutionStates.Where(state => ids.Contains(state.ExecutionId)).ExecuteDeleteAsync(cancellationToken),
            cancellationToken);

    private async Task<(int Deleted, bool Left)> DeleteInBatchesAsync(
        Func<int, Task<List<Guid>>> selectBatch,
        Func<List<Guid>, Task<int>> deleteBatch,
        CancellationToken cancellationToken)
    {
        var batchSize = Math.Clamp(retention.DeleteBatchSize, 50, 50_000);
        var ceiling = Math.Max(batchSize, retention.MaxDeletesPerSweep);
        var started = clock.GetTimestamp();
        var total = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (total >= ceiling || clock.GetElapsedTime(started) >= sweepBudget)
            {
                return (total, true);
            }

            var ids = await selectBatch(Math.Min(batchSize, ceiling - total));
            if (ids.Count == 0)
            {
                break;
            }

            var deleted = await deleteBatch(ids);
            if (deleted == 0)
            {
                break;
            }

            total += deleted;
        }

        return (total, false);
    }
}
