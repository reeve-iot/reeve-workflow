using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>Where a host says a parked run is ready now, so a wait over host state is event-driven rather than polled. The waker subscribes at start and disposes the subscription at stop; a source with no subscriber drops its wakes.</summary>
/// <remarks>A wake is a hint about when to look and never an answer about what is true: the waker still reads the row and evaluates its condition, and a wake for a row that is not ready costs one read. The exception is a trigger, whose wait has no condition to evaluate — see <see cref="WorkflowSuspensionWakeReasons.Trigger"/>.</remarks>
public interface IWorkflowSuspensionWakeSource
{
    /// <summary>Called by the waker. Every wake the source delivers after this returns reaches <paramref name="onWake"/> until the returned subscription is disposed.</summary>
    /// <remarks>A source over a message bus subscribes asynchronously, so there is a window after this returns in which a wake published elsewhere reaches nobody. That is not a defect to engineer away here — the waker's own pass is the backstop for every wake that is never heard — but a source must not report a subscription it has not attempted.</remarks>
    IDisposable Subscribe(Func<WorkflowSuspensionWake, CancellationToken, Task> onWake);
}

/// <summary>One host's claim that a parked run should be looked at now. A wake carrying both ids is matched on both, and one carrying neither is refused.</summary>
/// <remarks>An execution id alone names a row only while that run is parked on one wait. A run that has forked is parked on several at once and an execution id names none of them in particular, so such a wake is answered with nothing rather than with a guess — a trigger's wake resolves the row it is handed without asking a condition, and a guess would fire one branch on another's event and strand them both. A host whose runs fork sends <see cref="SuspensionId"/>, which it was given when the run parked.
/// <para>For the same reason a trigger wake carrying only <see cref="ExecutionLogId"/> is held only for the wait its first read found. Read while its run is parked on no wait — a peer on another replica answered it first, or the park has not landed — or with the read failing, it is dropped rather than held: the next wait that run parks on is not the one its event was for, and a hold would answer it with that event. A trigger wake that names the suspension is held for that row, as every other wake is.</para></remarks>
public sealed record WorkflowSuspensionWake
{
    /// <summary>The run, as the host knows it — the execution log's id, which is what a block sees. Enough on its own only while that run is parked on one wait; see the remarks on this record.</summary>
    public Guid? ExecutionLogId { get; init; }

    /// <summary>The suspension row, for a host that kept the id it was given when the run parked.</summary>
    public Guid? SuspensionId { get; init; }

    /// <summary>Why, from <see cref="WorkflowSuspensionWakeReasons"/>. Not decoration: it is what the resumed run's audit kind is read from, and a value outside the set is refused rather than guessed at.</summary>
    public required string Reason { get; init; }

    /// <summary>What the host wants the resumed block to see, merged into the outcome under the waker's own answer — a trigger's payload travels here. Null for a wake that carries nothing.</summary>
    /// <remarks>For a wait the row itself answers: a trigger and a template. A sleep's outcome is its own duration and a sub-workflow park's is the run it waited on, so a wake for either of those kinds moves the row and carries nothing into it — and its <see cref="Reason"/> does not reach the audit either, which reads <c>resumed_by_sleep</c> or <c>resumed_by_sub_workflow</c> from the outcome those kinds build.</remarks>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }

    /// <summary>A <see cref="Model.WorkflowSuspensionKind.Predicate"/> row's scope variables, by name, with reason <c>Poll</c>; a wake missing
    /// one, naming an undeclared one or sent to a row of another kind is counted dropped and the row stays parked.</summary>
    public IReadOnlyDictionary<string, object?>? Variables { get; init; }
}

/// <summary>The reasons a wake may give, which are the reasons <see cref="WorkflowResumeDecisions"/> already reads out of an outcome — the same constants and not a second set that happens to match, because two sets would drift and a reason nothing reads back audits as a poll for ever. A closed set: each maps to one audit kind.</summary>
public static class WorkflowSuspensionWakeReasons
{
    /// <summary>A trigger the row waits for fired. Audits as <c>suspension.resumed_by_trigger</c>, and is the one reason that answers a wait rather than asking about it — up to the row's deadline, past which the wait missed it and resolves timed out as any other would.</summary>
    public const string Trigger = WorkflowResumeDecisions.TriggerReason;

    /// <summary>A helper the row's condition reads changed. Audits as <c>suspension.resumed_by_helper_change</c>.</summary>
    public const string HelperChange = WorkflowResumeDecisions.HelperChangeReason;

    /// <summary>Something else the host watches changed, and the row's own condition decides. Audits as <c>suspension.resumed_by_poll</c>.</summary>
    public const string Poll = WorkflowResumeDecisions.PollReason;

    /// <summary>Whether this package reads <paramref name="reason"/>. Everything else is refused at the seam: a reason silently folded into the poll is a resume whose audit says the wrong thing for ever.</summary>
    public static bool IsKnown(string? reason) =>
        reason is Trigger or HelperChange or Poll;

    /// <summary>The reasons, for a message that has to name them.</summary>
    public static IReadOnlyList<string> All { get; } = [Trigger, HelperChange, Poll];
}
