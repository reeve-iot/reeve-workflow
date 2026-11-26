namespace ReeveWorkflow.Core.Execution;

/// <summary>The write before a root block that may not run twice did not land, so the block was not run: the row
/// still names an earlier block, and a recovery of a replica that died inside this one would repeat it without
/// asking.</summary>
/// <remarks>Only where a recovery could re-enter the run: a root run on a host whose sweep re-enters
/// (<see cref="WorkflowEngineSettings.RecoveryReentersRuns"/>), and not
/// under <see cref="RunStateWriteModeKind.Heartbeat"/>, whose recovery asks about every block after the checkpoint. The
/// write is tried three times first. The run ends as a failure, which a run-level retry policy retries as any other.</remarks>
public sealed class WorkflowCheckpointNotWrittenException(Guid stateExecutionId, string nodeId)
    : Exception($"checkpoint_not_written: the run's row could not be written before block '{nodeId}', which may not "
        + "run twice, so it was not run.")
{
    public Guid StateExecutionId { get; } = stateExecutionId;

    public string NodeId { get; } = nodeId;
}
