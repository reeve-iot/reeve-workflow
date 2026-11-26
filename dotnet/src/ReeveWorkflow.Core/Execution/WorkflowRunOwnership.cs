using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Execution;

internal static class WorkflowRunOwnership
{
    public static async Task<WorkflowRunOwnershipLoss> ExplainAsync(
        IWorkflowRunStateStore store, Guid stateExecutionId, WorkflowRunStateStatus? expectedStatus, string? replicaId,
        ILogger logger, CancellationToken cancellationToken)
    {
        WorkflowRunStateRow? row;
        try
        {
            row = await store.GetAsync(stateExecutionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read back run state row {ExecutionId} to say why it was lost.", stateExecutionId);
            return WorkflowRunOwnershipLoss.Unknown;
        }

        if (row is null) return WorkflowRunOwnershipLoss.RowMissing;
        if (row.Status is WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed or WorkflowRunStateStatus.Cancelled)
            return WorkflowRunOwnershipLoss.StatusMoved;
        if (row.OwnedByReplicaId != replicaId)
            return row.OwnedByReplicaId is null ? WorkflowRunOwnershipLoss.Released : WorkflowRunOwnershipLoss.ReclaimedByPeer;
        return expectedStatus is { } expected && row.Status != expected
            ? WorkflowRunOwnershipLoss.StatusMoved
            : WorkflowRunOwnershipLoss.Unknown;
    }

    public static async Task<bool?> StillHeldAsync(
        IWorkflowRunStateStore store, Guid stateExecutionId, string? replicaId, ILogger logger)
    {
        try
        {
            var row = await store.GetAsync(stateExecutionId, CancellationToken.None).ConfigureAwait(false);
            return row is { Status: WorkflowRunStateStatus.Running } && row.OwnedByReplicaId == replicaId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read run state row {ExecutionId} to learn whether it is still this replica's.",
                stateExecutionId);
            return null;
        }
    }
}
