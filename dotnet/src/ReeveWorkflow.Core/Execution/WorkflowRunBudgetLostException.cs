namespace ReeveWorkflow.Core.Execution;

/// <summary>Thrown when a live run's hold on its workflow's cluster budget — the lock of a one-permit mode, or a
/// held slot of an N-permit one — lapsed and could not be taken back because the budget is spent elsewhere.</summary>
/// <remarks>The run stops rather than exceed a budget another replica is now inside. Its row is handed over where
/// the host has a recovery sweep, so the run is replayed once the budget allows, and ended where it has none.</remarks>
public sealed class WorkflowRunBudgetLostException(Guid workflowId, string mode)
    : Exception(
        $"{WorkflowRunHandover.LeaseLostReason}: the cluster {(mode is "Single" or "Restart" ? "lock" : "slot")} this "
        + $"run of workflow {workflowId} held in {mode} mode lapsed, and the budget is held elsewhere now; the run stops "
        + "rather than exceed it.")
{
    public Guid WorkflowId { get; } = workflowId;

    /// <summary>The workflow's mode when the run was admitted, which is what said how many may run at once.</summary>
    public string Mode { get; } = mode;
}
