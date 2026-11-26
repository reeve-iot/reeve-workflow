using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Debug;

/// <summary>What became of a debugger command. Every command is answered with one of these, and none is accepted
/// and dropped: a caller that is not told <see cref="Applied"/> knows the run was not touched.</summary>
public enum WorkflowDebugCommandOutcome
{
    /// <summary>The command took effect where the run is: a pause was released, or the run's cancellation was
    /// requested. For a delete, the run had ended and the session is gone.</summary>
    Applied,

    /// <summary>No such session.</summary>
    NotFound,

    /// <summary>The session's status does not admit the command, and nothing was sent. The result carries the
    /// status that was read.</summary>
    WrongState,

    /// <summary>The status admits the command, but no replica holds the run: it ended in the meantime, its replica
    /// is gone, or this deployment has no cluster signal and the run is not in this process. Nothing was applied.</summary>
    NotHeld,

    /// <summary>The command was sent to the other replicas and none confirmed within
    /// <see cref="WorkflowDebugOptions.CommandAckTimeoutSeconds"/>. It may have been applied by a replica whose
    /// confirmation was lost; the session's status is what says.</summary>
    Unconfirmed,

    /// <summary>A delete's kill was applied, but the run had not ended within
    /// <see cref="WorkflowDebugOptions.KillWaitTimeoutSeconds"/>, so the session was kept: deleting it would leave
    /// a run that is still going without a debugger.</summary>
    NotEnded
}

/// <summary>The answer to a debugger command: what happened, and the session status that decided it.</summary>
/// <param name="Outcome">What became of the command.</param>
/// <param name="Status">The session status the command was judged against, when the session was read; null for
/// <see cref="WorkflowDebugCommandOutcome.NotFound"/>.</param>
public sealed record WorkflowDebugCommandResult(
    WorkflowDebugCommandOutcome Outcome, WorkflowDebugSessionStatus? Status = null)
{
    /// <summary>True when the command took effect.</summary>
    public bool Succeeded => Outcome == WorkflowDebugCommandOutcome.Applied;
}
