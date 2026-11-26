namespace ReeveWorkflow.Core.Hosting;

/// <summary>What a run records for an operator to read later. The engine names the event; the host decides
/// what its own audit log calls it and where the row goes.</summary>
public interface IWorkflowAuditSink
{
    Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>A kind, the user it is attributed to (null when no user caused it), the workflow or execution it is
/// about, and detail the host serialises — never variable values or block outputs, which an audit row must not leak.</summary>
public sealed record WorkflowAuditEvent(string Kind, Guid? ActorId, Guid SubjectId, object? Data);

/// <summary>The events the engine emits. A host that does not recognise a kind must still write it under
/// that name rather than drop it: an unmapped kind is a newer library, not a bad event.</summary>
public static class WorkflowAuditEventKinds
{
    /// <summary>A run was cancelled because its workflow was deleted while it was executing.</summary>
    public const string RunCancelledByDelete = "run.cancelled_by_delete";

    /// <summary>The first start after an upgrade cancelled a run that had not ended. Its subject is the run-state row.</summary>
    /// <remarks><c>Data</c> names the workflow, the version number, the status before and the journal entries ended.
    /// Written after the cancellation commits, so a crash in between loses it.</remarks>
    public const string RunCancelledByUpgrade = "run.cancelled_by_upgrade";

    /// <summary>A trigger was refused a run slot and no run started.</summary>
    public const string RunDropped = "run.dropped";

    /// <summary>A trigger was ignored because a run of the same workflow was already in flight.</summary>
    public const string RunSkipped = "run.skipped";

    /// <summary>A run started by cancelling the run it superseded.</summary>
    public const string RunRestarted = "run.restarted";

    /// <summary>A run started after waiting in the queue for a slot.</summary>
    public const string RunQueued = "run.queued";

    /// <summary>A version containing a scripting block was refused because its owner may not run one.</summary>
    public const string ScriptingDenied = "scripting.denied";

    /// <summary>A run moved to the next block, recorded before the block executes.</summary>
    public const string ExecutionTransitioned = "execution.transitioned";

    /// <summary>A suspended run resumed because the trigger it waited for fired.</summary>
    public const string ResumedByTrigger = "suspension.resumed_by_trigger";

    /// <summary>A suspended run resumed because its wait expired.</summary>
    public const string ResumedByTimeout = "suspension.resumed_by_timeout";

    /// <summary>A suspended run resumed because a poll found its condition satisfied.</summary>
    public const string ResumedByPoll = "suspension.resumed_by_poll";

    /// <summary>A suspended run resumed because a helper it watched changed.</summary>
    public const string ResumedByHelperChange = "suspension.resumed_by_helper_change";

    /// <summary>A run parked on a durable sleep resumed because its wake time came.</summary>
    public const string ResumedBySleep = "suspension.resumed_by_sleep";

    /// <summary>A run parked on a run it started resumed because that run reached an ending.</summary>
    public const string ResumedBySubWorkflow = "suspension.resumed_by_sub_workflow";

    /// <summary>A run whose replica died was re-entered at the block its checkpoint names, by a recovery.</summary>
    public const string ReenteredByRecovery = "execution.reentered_by_recovery";

    /// <summary>A run whose replica died was claimed by a recovery, before it was decided what to do with it.
    ///</summary>
    /// <remarks>Its subject is the run's journal entry where its checkpoint names one, and its run-state row
    /// otherwise — the two are separate records and a claimed row without a checkpoint has only the second.
    /// <c>Data.subjectIs</c> says which, and both ids are in <c>Data</c> either way.</remarks>
    public const string ClaimedByRecovery = "execution.claimed_by_recovery";

    /// <summary>A claimed run was ended without being re-entered or replayed, and <c>Data.reason</c> says which
    /// rule ended it, from <see cref="WorkflowRecoveryEndingReasons"/>.</summary>
    /// <remarks>Subject as for <see cref="ClaimedByRecovery"/>.</remarks>
    public const string EndedByRecovery = "execution.ended_by_recovery";

    /// <summary>A parked wait was taken over from a taker that never re-entered it.</summary>
    /// <remarks>Its subject is the execution log id, as every other <c>suspension.*</c> kind's is.</remarks>
    public const string RetakenByWaker = "suspension.retaken_by_waker";

    /// <summary>A debug session was opened against a workflow. Its subject is the workflow, not the session —
    /// the session does not exist yet from the reader's point of view.</summary>
    public const string DebugSessionStarted = "debug.session_started";

    /// <summary>The breakpoint set of a debug session was replaced.</summary>
    public const string DebugBreakpointSet = "debug.breakpoint_set";

    /// <summary>A paused run advanced by one node.</summary>
    public const string DebugStep = "debug.step";

    /// <summary>A paused run was let go.</summary>
    public const string DebugResumed = "debug.resumed";

    /// <summary>A debug session's run was cancelled, by a user or by the orphan sweep.</summary>
    public const string DebugKilled = "debug.killed";
}
