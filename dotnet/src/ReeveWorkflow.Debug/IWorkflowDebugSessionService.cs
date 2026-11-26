using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Debug;

/// <summary>Manages workflow debug sessions: create, bind, step/resume/kill. Implementations hold the per-session
/// pause TaskCompletionSource and the debug hook cache for ReeveWorkflowEngine's zero-cost fast path.</summary>
public interface IWorkflowDebugSessionService
{
    /// <summary>Create an Idle debug session for the given workflow.</summary>
    Task<WorkflowDebugSessionResponse> CreateAsync(Guid workflowId, Guid ownerUserId, CancellationToken cancellationToken = default);

    /// <summary>Transitions Idle to Bound and associates the execution log row. The next
    /// ReeveWorkflowEngine.ExecuteAsync call for this workflow will pick up the debug hook.</summary>
    Task<WorkflowDebugSessionResponse> BindToExecutionAsync(Guid sessionId, Guid executionLogId, CancellationToken cancellationToken = default);

    /// <summary>Transitions Bound to Running. Called by ReeveWorkflowEngine right after BindToExecutionAsync and before
    /// the per-node dispatch loop, so the first breakpoint hit can transition Running -> Paused.</summary>
    Task MarkRunningAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Set/replace breakpoints on the session. Serialized as JSON array of node ids.</summary>
    Task SetBreakpointsAsync(Guid sessionId, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken = default);

    /// <summary>Send a Step command — dispatch the current paused node and pause at the next breakpoint.
    /// Admitted only while the session is Paused; see <see cref="WorkflowDebugCommandOutcome"/> for the answers.</summary>
    Task<WorkflowDebugCommandResult> StepAsync(Guid sessionId);

    /// <summary>Send a StepInto command — enter the sub-canvas of the current node. Admitted only while Paused.</summary>
    Task<WorkflowDebugCommandResult> StepIntoAsync(Guid sessionId);

    /// <summary>Send a StepOver command — dispatch through any sub-canvas without pausing inside. Admitted only
    /// while Paused.</summary>
    Task<WorkflowDebugCommandResult> StepOverAsync(Guid sessionId);

    /// <summary>Send a Resume command — runs until the next breakpoint or completion. Admitted only while Paused.
    /// <paramref name="actorUserId"/> is audit metadata; when null, the session owner is used. The audit event is
    /// written only when the command was applied.</summary>
    Task<WorkflowDebugCommandResult> ResumeAsync(Guid sessionId, Guid? actorUserId = null);

    /// <summary>Send a Kill command — cancels the run, whether it is walking, waiting inside a node or parked at a
    /// breakpoint, and the run ends Cancelled. Admitted while the session is Bound, Running or Paused. Applied means
    /// the cancellation was requested where the run is, not that the run has ended yet; the session reaches
    /// Cancelled when it does. <paramref name="actorUserId"/> (default: session owner) and <paramref name="reason"/>
    /// feed audit metadata, written only when the command was applied.</summary>
    Task<WorkflowDebugCommandResult> KillAsync(Guid sessionId, Guid? actorUserId = null, string? reason = null);

    Task<WorkflowDebugSessionResponse?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowDebugSnapshotResponse>> GetSnapshotsAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Kill the run if one is live, wait for it to end, then delete the session and all its snapshots. A
    /// session whose run cannot be stopped is kept and the result says why (<see cref="WorkflowDebugCommandOutcome.NotHeld"/>,
    /// <see cref="WorkflowDebugCommandOutcome.Unconfirmed"/>, <see cref="WorkflowDebugCommandOutcome.NotEnded"/>),
    /// unless <paramref name="force"/> is set, which deletes it regardless and leaves any run still going unattended.</summary>
    Task<WorkflowDebugCommandResult> DeleteAsync(
        Guid sessionId, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>Debug hook for an active run, if one is registered. Returns null immediately (no allocation) when
    /// none exists — the zero-cost fast path ReeveWorkflowEngine.ExecuteAsync relies on.</summary>
    IWorkflowDebugHook? GetHookForExecution(Guid executionLogId);

    /// <summary>Register a debug hook for an active execution (called by ReeveWorkflowEngine on run start).</summary>
    void RegisterExecutionHook(Guid executionLogId, IWorkflowDebugHook hook);

    /// <summary>Unregister a debug hook when an execution completes or is cancelled.</summary>
    void UnregisterExecutionHook(Guid executionLogId);

    /// <summary>Drops the hook and ends the session watching that run, by the run's label: Failed when it failed or timed
    /// out while the session was bound, running or paused; Completed when it succeeded while the session was running;
    /// Cancelled when the run was killed or cancelled, or succeeded while the session was not running. This is what sends
    /// the terminal notice to whoever is watching.</summary>
    Task EndSessionForExecutionAsync(
        Guid executionLogId, string runStatus, CancellationToken cancellationToken = default);
}
