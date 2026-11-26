namespace ReeveWorkflow.Core.Execution;

/// <summary>Per-execution debug hook registered in WorkflowDebugSessionService; the engine calls
/// OnNodeAsync at the top of each node dispatch (after the null check).</summary>
public interface IWorkflowDebugHook
{
    /// <summary>Called at the top of the per-node dispatch loop. If the node is a breakpoint (or a pending
    /// step command), awaits a TaskCompletionSource until the UI sends a command; else returns immediately.</summary>
    Task OnNodeAsync(
        string nodeId,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> allOutputs,
        CancellationToken cancellationToken);

    /// <summary>Called after a node completes to capture the outputs snapshot.</summary>
    Task OnNodeCompletedAsync(
        string nodeId,
        IReadOnlyDictionary<string, object?> outputs,
        CancellationToken cancellationToken);

    /// <summary>Cancelled when the session's user kills the run, in whatever state the run is in: parked at a
    /// breakpoint, or running a node, including a long wait. The engine links it into the run's own token.</summary>
    CancellationToken KillToken { get; }
}
