using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>Someone watching a run step by step. Optional, and its absence is the common case — a run with no
/// session pays one null check and never touches this again.</summary>
public interface IWorkflowDebugSessions
{
    /// <summary>Ties a waiting session to the run that has just started. Called only after the run's own
    /// record exists, because the session references it.</summary>
    Task BindToRunAsync(Guid debugSessionId, Guid executionId, CancellationToken cancellationToken);

    /// <summary>Moves a bound session to running, before the first node. Without it the first breakpoint has
    /// no state to pause from, and the pause is swallowed while the session still reads as bound.</summary>
    Task MarkRunningAsync(Guid debugSessionId, CancellationToken cancellationToken);

    /// <summary>The hook for this run, or null when nobody is watching it.</summary>
    IWorkflowDebugHook? HookFor(Guid executionId);

    /// <summary>Drops the hook and records that the run is over. Called on every exit from a run, including the ones that
    /// threw: the session watching it reaches a status it does not leave, and whoever was watching is told. <paramref name="runStatus"/> is the run's own label.</summary>
    /// <remarks>An implementation that does not end the session leaves a debugger panel open on a run that finished.</remarks>
    Task ReleaseHookAsync(Guid executionId, string runStatus, CancellationToken cancellationToken = default);
}
