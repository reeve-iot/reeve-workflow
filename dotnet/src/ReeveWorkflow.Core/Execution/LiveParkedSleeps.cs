using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal sealed class LiveParkedSleeps(IWorkflowParkedSleepStore table, IWorkflowRunStateStore? stateStore, ILogger logger)
    : IWorkflowParkedSleepStore
{
    public async Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        if (!await table.HasParkedSleepAsync(workflowId, cancellationToken).ConfigureAwait(false))
            return false;

        return stateStore is null
            || (await ListParkedSleepsAsync(workflowId, cancellationToken).ConfigureAwait(false)).Count > 0;
    }

    public async Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var listed = await table.ListParkedSleepsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (stateStore is null)
            return listed;

        var live = new List<WorkflowParkedSleep>(listed.Count);
        foreach (var sleep in listed)
        {
            var state = await StateOfAsync(sleep, cancellationToken).ConfigureAwait(false);
            if (sleep.Status == WorkflowSuspensionStatus.Suspended
                    ? state is not (WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed or WorkflowRunStateStatus.Cancelled)
                    : state is WorkflowRunStateStatus.Suspended)
                live.Add(sleep);
        }

        return live;
    }

    public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken) =>
        table.CancelParkedSleepAsync(suspensionId, cancellationToken);

    public Task<bool> HasRunStartedSinceAsync(
        Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        table.HasRunStartedSinceAsync(workflowId, exceptExecutionLogId, sinceUtc, cancellationToken);

    private async Task<WorkflowRunStateStatus?> StateOfAsync(WorkflowParkedSleep sleep, CancellationToken cancellationToken)
    {
        if (WorkflowDurableSleep.StateExecutionIdOf(sleep.StatePayloadJson) is not { } stateExecutionId)
            return null;

        try
        {
            return (await stateStore!.GetAsync(stateExecutionId, cancellationToken).ConfigureAwait(false))?.Status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read run state row {StateExecutionId} of parked run {ExecutionLogId}.",
                stateExecutionId, sleep.ExecutionLogId);
            return sleep.Status == WorkflowSuspensionStatus.Suspended ? null : WorkflowRunStateStatus.Suspended;
        }
    }
}
