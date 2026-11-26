namespace ReeveWorkflow.Core.Hosting;

/// <summary>Every moment of a run worth counting. The host owns the meter, the instrument names and the
/// export; this only says when something happened and with which labels.</summary>
public interface IWorkflowMetrics
{
    void RecordExecutionStarted();

    void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null);

    void RecordNodeError(string blockType, string policy, bool caught, string exceptionType);

    void RecordSupersession(Guid workflowId, string mode, string outcome);

    void RecordDebugSession(string outcome);

    void RecordDebugPauseDuration(double durationSeconds);

    void RecordSuspensionStarted(string kind);

    void RecordSuspensionResolved(string kind, string outcome, double durationSeconds);

    /// <summary>A recovery pass took this many rows.</summary>
    /// <remarks>Zero is recorded too, and deliberately: a sweep that finds nothing and a sweep that is not
    /// running look identical on a counter that only moves when there is something to count.
    ///
    /// Default-bodied, so a host's own implementation keeps working without being
    /// recompiled against it.</remarks>
    void RecordRecoveryClaim(int rows) { }

    /// <summary>A claimed run reached an ending without re-entering, under one of
    /// <see cref="WorkflowRecoveryEndingReasons"/>.</summary>
    /// <remarks>The reason is one of those constants and nothing else. The sweep writes a longer string on the
    /// row for the refusal — it names the block — and that string must not reach here: one series per node id
    /// is a cardinality incident on a fleet recovering a few hundred runs. The node id is in the audit event's
    /// data instead.</remarks>
    void RecordRecoveryEnding(string reason) { }

    /// <summary>A wake a host published, and what the waker did with it, from
    /// <see cref="WorkflowSuspensionWakeOutcomes"/>.</summary>
    /// <remarks>A wake that takes its row is counted here and the re-entry that follows is counted by
    /// <see cref="RecordSuspensionResolved"/>, exactly as a row the page took is. Two instruments, both — one
    /// answers "are the host's wakes arriving and landing", the other "are parked runs being resumed".
    ///
    /// <b>The terminal outcomes add up to the wakes a host published; <c>held</c> does not, and is not meant
    /// to.</b> A wake that could not be answered yet is counted <c>held</c> once, and counted again when it is
    /// finally taken, refused or given up on. So <c>taken + not_ready + not_mine + lost + failed + dropped</c>
    /// is the wakes that have been finished with, and <c>held</c> is how many of them had to wait for a pass.
    /// <c>not_mine</c> belongs in that sum and is usually the largest term in a fleet that splits the kinds
    /// across wakers, where every waker reads every other waker's rows once.</remarks>
    void RecordSuspensionWake(string outcome) { }
}

/// <summary>The closed set of outcomes a wake can have. These are metric label VALUES: a new one is a new
/// series on every dashboard reading the old ones.</summary>
public static class WorkflowSuspensionWakeOutcomes
{
    /// <summary>The wake took the row, and the run is on its way back in.</summary>
    public const string Taken = "taken";

    /// <summary>The row was read and answered no: a condition still false, a sleep not yet due, or a child that
    /// has not ended.</summary>
    public const string NotReady = "not_ready";

    /// <summary>The row is not this waker's to take: a kind left out of its <c>Kinds</c>, a store with no wake
    /// path at all, or a run parked on a run it started read by a waker composed without the scope factory that
    /// reads one. Apart from <see cref="NotReady"/> on purpose — a row that is nearly ready and a row
    /// this waker will never take are not the same news, and a misconfigured <c>Kinds</c> list buried in
    /// <c>not_ready</c> looks exactly like a fleet of conditions that are merely still false.</summary>
    public const string NotMine = "not_mine";

    /// <summary>A peer took the row first, which is what makes every replica running this safe. Also a trigger wake whose read said <see cref="WorkflowParkedRowRead.NoLongerWaiting"/>.</summary>
    public const string Lost = "lost";

    /// <summary>The wake could not be answered yet and is held for a later pass. <b>Not terminal:</b> the same
    /// wake is counted again under one of the others when a pass finishes with it, so this one is how many
    /// wakes had to wait rather than a share of how they ended.</summary>
    public const string Held = "held";

    /// <summary>The wake was given up on without an answer: its hold ran out, the hold was full, a newer wake
    /// for the same row replaced it, this waker holds nothing, the store could not be read and there was no
    /// instant to date a hold from, or the row's own configuration names nothing that could ever end its wait. A wake a host published and nothing ever acted on.</summary>
    public const string Dropped = "dropped";

    /// <summary>The take itself threw — including the read of a child run, which is the take for a run parked
    /// on a run it started. Counted apart from <see cref="Lost"/> on purpose: "my wakes are losing
    /// races with the page" and "my store is failing takes" look the same from outside and are not the same
    /// problem. Without it the outcomes would not add up to the wakes a host published, which is the first
    /// thing an operator checks them for.</summary>
    public const string Failed = "failed";
}

/// <summary>The closed set of reasons a recovery ends a run without re-entering it. Metric label VALUES, and
/// defined from the sweep's own constants so that the two are one set rather than two that happen to match.</summary>
public static class WorkflowRecoveryEndingReasons
{
    /// <summary>A run another run started, with no checkpoint to go back to.</summary>
    public const string DispatchedByAnotherRun = Execution.WorkflowRunRecoverySweep.DispatchedByAnotherRunReason;

    /// <summary>The whole workflow could not be replayed from its start.</summary>
    public const string NotIdempotent = Execution.WorkflowRunRecoverySweep.NotIdempotentReason;

    /// <summary>Re-entry refused the block the run died inside. The block's id is in the audit event's data
    /// and never in this label.</summary>
    public const string RefusedAtReentry = Execution.WorkflowRunRecoverySweep.RefusedAtReentryReason;

    /// <summary>The run was replayed from its start under a journal entry of its own, and the claimed row was
    /// closed so that later sweeps stop picking it up.</summary>
    public const string Replayed = Execution.WorkflowRunRecoverySweep.ReplayedReason;

    /// <summary>The re-dispatch itself threw.</summary>
    public const string ResumeFailed = Execution.WorkflowRunRecoverySweep.ResumeFailedReason;

    /// <summary>Admission refused the replay until the host's bound on refusals had passed.</summary>
    public const string NotAdmitted = Execution.WorkflowRunRecoverySweep.NotAdmittedReason;

    /// <summary>The run's checkpoint does not read, so the run is abandoned, not replayed. The path, node and port are
    /// in the audit event's data and never in this label.</summary>
    public const string ResumeValueInvalid = Model.WorkflowAbandonmentReasons.ResumeValueInvalid;
}

/// <summary>Status values for <see cref="IWorkflowMetrics.RecordExecutionCompleted"/>. They are exported as
/// label values, so changing one renames a series an operator's dashboard already reads.</summary>
public static class WorkflowRunStatusLabels
{
    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    public const string TimedOut = "timed_out";
}

/// <summary>The closed set of outcomes a debug session can end with. These are metric label VALUES, so a
/// host reading the counter groups on exactly these strings and a new one is a new series.</summary>
public static class WorkflowDebugSessionOutcomes
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Killed = "killed";
    public const string Abandoned = "abandoned";
}
