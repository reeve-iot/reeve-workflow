using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Execution;

internal sealed class WorkflowRunHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource lost = new();
    private readonly CancellationTokenSource stop = new();
    private readonly ILogger logger;
    private readonly TimeProvider clock;
    private Task loop = Task.CompletedTask;
    private int stopped;

    private WorkflowRunHeartbeat(ILogger logger, TimeProvider clock)
    {
        this.logger = logger;
        this.clock = clock;
    }

    public CancellationToken Token => lost.Token;

    public WorkflowRunOwnershipLoss? RowLoss { get; private set; }

    public bool BudgetLost { get; private set; }

    public bool StoppedTheRun => RowLoss is not null || BudgetLost;

    public static WorkflowRunHeartbeat? Start(
        TimeSpan? interval, IWorkflowRunRecoveryStore? renewals, IWorkflowRunStateStore? rows,
        Guid? stateExecutionId, string? replicaId, WorkflowRunLease? lease, ILogger logger, TimeProvider? clock = null)
    {
        if (interval is not { } every || every <= TimeSpan.Zero)
            return null;

        var row = renewals is not null && rows is not null && stateExecutionId is not null && !string.IsNullOrEmpty(replicaId)
            ? (renewals, rows, stateExecutionId.Value, replicaId)
            : ((IWorkflowRunRecoveryStore, IWorkflowRunStateStore, Guid, string)?)null;
        var budget = lease is { HoldsClusterBudget: true } ? lease : null;
        if (row is null && budget is null)
            return null;

        var heartbeat = new WorkflowRunHeartbeat(logger, clock ?? TimeProvider.System);
        heartbeat.loop = heartbeat.BeatAsync(every, row, budget);
        return heartbeat;
    }

    private async Task BeatAsync(
        TimeSpan every, (IWorkflowRunRecoveryStore Renewals, IWorkflowRunStateStore Rows, Guid Id, string Replica)? row,
        WorkflowRunLease? lease)
    {
        while (true)
        {
            try
            {
                await Task.Delay(every, clock, stop.Token).ConfigureAwait(false);
                if (row is { } r && !await RenewRowAsync(r.Renewals, r.Id, r.Replica).ConfigureAwait(false))
                {
                    var loss = await WorkflowRunOwnership.ExplainAsync(
                        r.Rows, r.Id, expectedStatus: null, r.Replica, logger, stop.Token).ConfigureAwait(false);
                    Lose(loss, budgetLost: false);
                    return;
                }

                if (lease is not null && !await RenewBudgetAsync(lease).ConfigureAwait(false))
                {
                    Lose(null, budgetLost: true);
                    return;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<bool> RenewRowAsync(IWorkflowRunRecoveryStore renewals, Guid id, string replicaId)
    {
        try
        {
            return await renewals.RenewClaimAsync(id, replicaId, stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not renew run state row {ExecutionId}; the next beat tries again.", id);
            return true;
        }
    }

    private async Task<bool> RenewBudgetAsync(WorkflowRunLease lease)
    {
        try
        {
            return await lease.RenewAsync(stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not re-assert the cluster budget of run {RunId}; the next beat tries again.", lease.RunId);
            return true;
        }
    }

    private void Lose(WorkflowRunOwnershipLoss? rowLoss, bool budgetLost)
    {
        if (Volatile.Read(ref stopped) != 0)
            return;
        RowLoss = rowLoss;
        BudgetLost = budgetLost;
        _ = lost.CancelAsync();
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;
        await stop.CancelAsync().ConfigureAwait(false);
        try { await loop.ConfigureAwait(false); }
        catch (Exception ex) { logger.LogDebug(ex, "The heartbeat ended with an error after it was stopped."); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        stop.Dispose();
    }
}
