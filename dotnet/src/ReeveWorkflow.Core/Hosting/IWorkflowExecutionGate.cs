namespace ReeveWorkflow.Core.Hosting;

/// <summary>A host-level stop on starting work at all: the host may be running in a degraded mode where
/// workflows still load and still answer queries, but nothing is allowed to execute.</summary>
public interface IWorkflowExecutionGate
{
    /// <summary>Read once per run attempt, so a host that flips this mid-process stops the next run rather
    /// than the one already walking its graph.</summary>
    bool IsExecutionSuspended { get; }
}
