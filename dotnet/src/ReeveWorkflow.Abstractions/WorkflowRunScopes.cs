namespace ReeveWorkflow.Abstractions;

/// <summary>Which definition a single run executes: its own version, and the manifest naming a version for every sub-workflow it may reach.</summary>
/// <remarks>Sub-workflow dispatch and crash recovery are the only things that set this — a caller running a workflow normally leaves it unset and gets the current version.</remarks>
public sealed record WorkflowRunPin
{
    /// <summary>The version to run instead of the workflow's current one. Null runs the current version.</summary>
    public Guid? VersionId { get; init; }

    /// <summary>Workflow id → version id for the whole sub-workflow tree, so a child republished mid-run does not change under the parent.</summary>
    public IReadOnlyDictionary<Guid, Guid>? Manifest { get; init; }

    /// <summary>How many runs called down to this one: 0 for a run nothing called, one more than its caller's for
    /// a sub-workflow. The engine refuses a run whose depth is past its limit.</summary>
    public int CallDepth { get; init; }
}

/// <summary>What a single run records about itself, and which debug session is watching it.</summary>
/// <remarks>Absent means a background run: no persisted log entries, nothing watching. Debugging is a package of its own, which is why it reaches the engine through a scope rather than through the engine's signature.</remarks>
public sealed record WorkflowRunObservation
{
    /// <summary>Persist this run's log entries. Off for trigger-driven runs, whose volume is the reason the gate exists.</summary>
    public bool RecordLogEntries { get; init; }

    /// <summary>The debug session to bind this run to, if one is waiting for it.</summary>
    public Guid? DebugSessionId { get; init; }

    /// <summary>Called exactly once, when this run's admission is decided and before its first node starts. A host
    /// that starts its next fire of the workflow from here keeps the fires in the order it called.</summary>
    /// <remarks>Called on the admitting thread, so it must be fast; an exception from it is logged and ignored.
    /// Sub-workflow runs and resumes never call it. See <see cref="WorkflowAdmissionDecisionKind.Queued"/>: not final.</remarks>
    public Action<WorkflowAdmissionDecision>? OnAdmissionDecided { get; init; }
}

/// <summary>How one call's admission ended, as <see cref="WorkflowRunObservation.OnAdmissionDecided"/> reports it.</summary>
public enum WorkflowAdmissionDecisionKind
{
    /// <summary>The run holds its slot and is starting. Outcome <c>started</c>, or <c>restarted</c> when it superseded runs.</summary>
    Admitted,

    /// <summary>The run waits in the workflow's queue at a fixed place. Outcome <c>queued</c>. Not final, and not heard again.</summary>
    /// <remarks>At its turn the run can still be dropped when a cluster slot is refused, or not run when its caller cancels
    /// first. <c>ExecuteAsync</c>'s own return or exception is what says whether it ran.</remarks>
    Queued,

    /// <summary>Admission said no. Outcome <c>skipped</c>, <c>dropped</c> or <c>errored</c>, with the reason.</summary>
    Refused,

    /// <summary>The call threw or was cancelled before admission decided. No outcome; the exception is attached.</summary>
    Failed
}

/// <summary>One admission decision: which workflow, which kind, and the coordinator's own outcome and reason.</summary>
/// <param name="WorkflowId">The workflow the call asked to run.</param>
/// <param name="Kind">Whether the run will execute now, later, or not at all.</param>
public sealed record WorkflowAdmissionDecision(Guid WorkflowId, WorkflowAdmissionDecisionKind Kind)
{
    /// <summary>The admission outcome the metrics and audit record: <c>started</c>, <c>restarted</c>, <c>queued</c>,
    /// <c>skipped</c>, <c>dropped</c> or <c>errored</c>. Null for <see cref="WorkflowAdmissionDecisionKind.Failed"/>.</summary>
    public string? Outcome { get; init; }

    /// <summary>Why a run was refused, such as <c>single_mode_already_running</c>. Null when there is none.</summary>
    public string? Reason { get; init; }

    /// <summary>What was thrown: set for <see cref="WorkflowAdmissionDecisionKind.Failed"/> and for an <c>errored</c> refusal.</summary>
    public Exception? Exception { get; init; }
}

/// <summary>Hands a <see cref="WorkflowRunPin"/> to the next <c>ExecuteAsync</c> on this async flow.</summary>
/// <remarks>The engine CONSUMES it, so a sub-workflow started inside that run does not silently inherit its parent's pinned version — a run's own version is per-run, and only the manifest is meant to travel down the tree.</remarks>
public static class WorkflowRunPinScope
{
    private static readonly AsyncLocal<WorkflowRunPin?> _current = new();

    /// <summary>Reads the pending pin and clears it, so nothing further down inherits it.</summary>
    public static WorkflowRunPin? Consume()
    {
        var value = _current.Value;
        _current.Value = null;
        return value;
    }

    /// <summary>Sets the pin for the current async flow; disposing restores what was there before.</summary>
    public static IDisposable Push(WorkflowRunPin pin)
    {
        var previous = _current.Value;
        _current.Value = pin;
        return new Restore(previous);
    }

    private sealed class Restore(WorkflowRunPin? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}

/// <summary>Hands a <see cref="WorkflowRunObservation"/> to the next <c>ExecuteAsync</c> on this async flow.</summary>
/// <remarks>Consumed like the pin: a sub-workflow does not inherit its parent's debug session, which would bind two runs to one session.</remarks>
public static class WorkflowRunObservationScope
{
    private static readonly AsyncLocal<WorkflowRunObservation?> _current = new();

    /// <summary>Reads the pending observation and clears it, so nothing further down inherits it.</summary>
    public static WorkflowRunObservation? Consume()
    {
        var value = _current.Value;
        _current.Value = null;
        return value;
    }

    /// <summary>Sets the observation for the current async flow; disposing restores what was there before.</summary>
    public static IDisposable Push(WorkflowRunObservation observation)
    {
        var previous = _current.Value;
        _current.Value = observation;
        return new Restore(previous);
    }

    private sealed class Restore(WorkflowRunObservation? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}

/// <summary>What a run reports back to the block that started it: whether it parked rather than finishing, and
/// which run it was. The block pushes an empty one before the call and reads it after.</summary>
/// <remarks>The other scopes on this file hand something DOWN to the run that is about to start; this one is
/// how that run answers back. It has to be a holder rather than an ambient value for exactly that reason: an
/// <see cref="AsyncLocal{T}"/> set inside the run would not be seen by the caller awaiting it, while a field
/// set on an object the caller still holds is.</remarks>
public sealed class WorkflowSubRunOutcome
{
    /// <summary>Whether a run that started filled this in. False with <see cref="RefusedBecause"/> null means the
    /// engine that ran the child does not report back — a host's own <c>IWorkflowEngine</c> — and a child that
    /// parks is invisible to the block that started it.</summary>
    public bool Reported { get; private set; }

    /// <summary>Whether the run parked rather than reaching an ending.</summary>
    public bool Parked { get; private set; }

    /// <summary>The run's own execution-log id, which is what a waiter waits on.</summary>
    public Guid ExecutionLogId { get; private set; }

    /// <summary>Filled by whoever ran it, once, as the run ends. A second report is ignored: the first is the
    /// run the block started, and anything after it belongs to something the block did not start.</summary>
    public void Report(bool parked, Guid executionLogId)
    {
        if (Reported)
            return;
        Reported = true;
        Parked = parked;
        ExecutionLogId = executionLogId;
    }

    /// <summary>Why the engine did not start the run at all, or null when it started or nothing said. Set by
    /// <see cref="ReportRefused"/>. A refused run has no execution-log id, so <see cref="Reported"/> stays false.</summary>
    public string? RefusedBecause { get; private set; }

    /// <summary>Filled instead of <see cref="Report"/> when admission did not start the run: safe mode, a Single
    /// run already in flight, no room in the budget or the queue. The block that started it fails rather than
    /// answering with outputs nothing produced. Ignored after either report.</summary>
    public void ReportRefused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Reported || RefusedBecause is not null)
            return;
        RefusedBecause = reason;
    }
}

/// <summary>Where a run finds the holder the block that started it is holding, or null when nothing is.</summary>
public static class WorkflowSubRunOutcomeScope
{
    private static readonly AsyncLocal<WorkflowSubRunOutcome?> _current = new();

    /// <summary>The holder to report into, or null for a run nothing started.</summary>
    public static WorkflowSubRunOutcome? Current => _current.Value;

    /// <summary>Puts a holder in front of the run about to start; disposing restores what was there before.</summary>
    public static IDisposable Push(WorkflowSubRunOutcome outcome)
    {
        var previous = _current.Value;
        _current.Value = outcome;
        return new Restore(previous);
    }

    /// <summary>Takes the holder away for the walk of a run that has begun, answering it to restore. A block
    /// inside the run must not report into the holder of the block that started the run.</summary>
    public static WorkflowSubRunOutcome? Take()
    {
        var previous = _current.Value;
        _current.Value = null;
        return previous;
    }

    private sealed class Restore(WorkflowSubRunOutcome? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}

/// <summary>What a run started for a block ended as, handed back to that block when the run it was in is
/// resumed.</summary>
/// <remarks>Taken by a flag on this object rather than by clearing the scope, for the reason the scope exists
/// at all: an <see cref="AsyncLocal{T}"/> cleared inside the block does not reach the walk that called it, so a
/// second pass through the same node would be answered with the same run all over again.</remarks>
public sealed class WorkflowSubRunAnswer(
    string nodeId, Guid executionLogId, bool succeeded, string? outputsJson, string? error)
{
    private int taken;

    /// <summary>The block the answer belongs to.</summary>
    public string NodeId { get; } = nodeId;

    /// <summary>The run it started, so a block answers only for the run it is waiting on.</summary>
    public Guid ExecutionLogId { get; } = executionLogId;

    /// <summary>Whether that run reached an ending of its own rather than failing or being cancelled.</summary>
    public bool Succeeded { get; } = succeeded;

    /// <summary>What it produced, as the journal stored it, or null when it produced nothing.</summary>
    public string? OutputsJson { get; } = outputsJson;

    /// <summary>Why it did not succeed, or null.</summary>
    public string? Error { get; } = error;

    /// <summary>True for the first caller only. What makes this the answer to one pass through the block.</summary>
    public bool Take() => Interlocked.Exchange(ref taken, 1) == 0;
}

/// <summary>Where a block picks up the answer of the run it parked on. A resume runs that block again rather
/// than injecting an output for it, so a child that failed fails the block — and is then the node's error policy's
/// and the enclosing try/catch's to handle, exactly as a child that fails in the process is.</summary>
public static class WorkflowSubRunResumeScope
{
    private static readonly AsyncLocal<WorkflowSubRunAnswer?> _current = new();

    /// <summary>The answer waiting for a block on this flow, or null.</summary>
    public static WorkflowSubRunAnswer? Current => _current.Value;

    /// <summary>Puts the answer in front of the walk about to be resumed, answering what was there to restore.</summary>
    public static WorkflowSubRunAnswer? Swap(WorkflowSubRunAnswer? answer)
    {
        var previous = _current.Value;
        _current.Value = answer;
        return previous;
    }

    /// <summary>The answer for this node, taken, so that a second pass through the same node — another iteration
    /// of the loop it is in, a retry of it — starts the run it is there to start.</summary>
    public static WorkflowSubRunAnswer? TakeFor(string? nodeId) =>
        nodeId is not null && _current.Value is { } answer && answer.NodeId == nodeId && answer.Take()
            ? answer
            : null;
}
