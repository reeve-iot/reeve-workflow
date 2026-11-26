using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Leaving a run for a peer instead of ending it: its row stays reclaimable and loses this replica's
/// name, so the recovery sweep on whichever replica reaches it first replays it.</summary>
public static class WorkflowRunHandover
{
    /// <summary>What the journal entry of a run its host stopped starts with, so a history can tell a deploy
    /// from an operator's cancel.</summary>
    public const string StoppedReason = "handed_over_on_stop";

    /// <summary>What the journal entry of a run that lost its cluster lease starts with.</summary>
    public const string LeaseLostReason = "handed_over_lease_lost";

    internal static bool StoppedByHost(bool handOverOnStop, CancellationToken hostStopping, CancellationToken caller) =>
        handOverOnStop && hostStopping.IsCancellationRequested && caller.IsCancellationRequested;

    internal static string JournalMessage(string reason) => reason == LeaseLostReason
        ? $"{reason}: another replica holds this workflow's run budget now; the run stopped and its state row was "
          + "released for a recovery sweep to carry on once the budget allows."
        : $"{reason}: the host stopped while this run was in flight; its state row was released for a peer's "
          + "recovery sweep to carry on.";

    internal static string JournalMessageNotReleased(string reason, bool rowEnded) =>
        (reason == LeaseLostReason
            ? $"{reason}: another replica holds this workflow's run budget now, and the run stopped; "
            : $"{reason}: the host stopped while this run was in flight; ")
        + (rowEnded
            ? "its state row had already ended, so no recovery sweep replays it."
            : "its state row could not be released, and a recovery sweep replays it once it ages out.");

    internal static async Task<bool> ReleaseAsync(
        IWorkflowRunStateStore store, Guid stateExecutionId, string? replicaId, ILogger logger) =>
        await ReleasedAsync(store, stateExecutionId, replicaId, logger).ConfigureAwait(false) is not null;

    internal static async Task<WorkflowRunStateRow?> ReleasedAsync(
        IWorkflowRunStateStore store, Guid stateExecutionId, string? replicaId, ILogger logger)
    {
        try
        {
            var row = await store.GetAsync(stateExecutionId, CancellationToken.None).ConfigureAwait(false);
            if (row is null
                || row.OwnedByReplicaId != replicaId
                || row.Status is not (WorkflowRunStateStatus.Running or WorkflowRunStateStatus.Sleeping))
                return null;

            return await store.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = stateExecutionId,
                FromStatus = row.Status,
                ToStatus = row.Status,
                CurrentBlockPath = row.CurrentBlockPath,
                VariablesJson = row.VariablesJson,
                WaitUntilUtc = row.WaitUntilUtc,
                OwnedByReplicaId = null,
                ExpectedOwnedByReplicaId = replicaId
            }, CancellationToken.None).ConfigureAwait(false) ? row : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not release run state row {StateExecutionId} for a peer; it is recovered once it ages out.",
                stateExecutionId);
            return null;
        }
    }
}
