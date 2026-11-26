using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What became of a run, as the one thing every reader of its ending agrees on.</summary>
public enum WorkflowRunPhase
{
    /// <summary>The walk returned. Whatever the writes after it threw, the run did everything it does.</summary>
    Finished,

    /// <summary>The walk reached a wait and the run is parked on it.</summary>
    Parked,

    /// <summary>A block, the definition or a child run failed it.</summary>
    Failed,

    /// <summary>The workflow's own timeout stopped it.</summary>
    TimedOut,

    /// <summary>A newer Restart trigger replaced it.</summary>
    Superseded,

    /// <summary>The caller that started it stopped it, or its budget went elsewhere, and it could not be left for a
    /// peer: its row ended cancelled, and the run is still owed to whoever keeps a row of it.</summary>
    Stopped,

    /// <summary>Something other than its caller cancelled it — its workflow torn down, or a block's own cancellation
    /// nobody asked for — and its row ended cancelled. Running it again would meet the same.</summary>
    Cancelled,

    /// <summary>Its host stopped, or its budget went elsewhere, and its row was left for a peer to replay.</summary>
    HandedOver,

    /// <summary>A peer holds its row now, and runs it. Nothing here ends that row.</summary>
    LostToPeer
}

/// <summary>What happens to a run's state row when the run ends.</summary>
public enum WorkflowRunRowEnding
{
    /// <summary>Not touched: there is none, or it is a peer's.</summary>
    Leave,

    /// <summary>Ended with <see cref="WorkflowRunEnding.RowStatus"/>, and only while it is still this replica's.</summary>
    EndIfOwned,

    /// <summary>Released from this replica's name, still reclaimable, for a peer's sweep to replay.</summary>
    Release
}

/// <summary>What a run's own code saw as it stopped, and nothing it has to ask a store for.</summary>
public sealed record WorkflowRunExit
{
    public required Guid WorkflowId { get; init; }

    /// <summary>The run's state row, or null when the host keeps none.</summary>
    public Guid? RunRow { get; init; }

    /// <summary>What the walk, or the writes after it, threw. Null for a walk that returned and was recorded.</summary>
    public Exception? Thrown { get; init; }

    /// <summary>The walk returned. Set before anything about it is written.</summary>
    public bool WalkDone { get; init; }

    /// <summary>The heartbeat found the row in somebody else's hands, and why.</summary>
    public WorkflowRunOwnershipLoss? RowLostByBeat { get; init; }

    /// <summary>The heartbeat found the run's lock or slot held elsewhere.</summary>
    public bool BudgetLostByBeat { get; init; }

    /// <summary>The user of the run's debug session killed it. Decides whatever the block turned the cancellation
    /// into, so a block that rethrows it as a failure is not retried.</summary>
    public bool DebugKilled { get; init; }

    /// <summary>The token the run was started with was cancelled.</summary>
    public bool CallerCancelled { get; init; }

    /// <summary>The host has begun to stop.</summary>
    public bool HostStopping { get; init; }

    /// <summary>The run's lease was cancelled: a newer Restart trigger, or its workflow forgotten.</summary>
    public bool LeaseCancelled { get; init; }

    /// <summary>The workflow's own timeout fired.</summary>
    public bool TimeoutFired { get; init; }

    public int? TimeoutSeconds { get; init; }

    /// <summary>The mode the run was admitted under.</summary>
    public WorkflowMode Mode { get; init; }

    /// <summary>The host hands runs over on a stop and this run is one a sweep would replay: a root run, not one a
    /// block of another run dispatched.</summary>
    public bool MayHandOver { get; init; }

    public int AttemptNumber { get; init; } = 1;

    public int MaxAttempts { get; init; } = 1;
}

/// <summary>How a run ended, decided once. The runner, the resumer and the recovery replay carry the answer out;
/// none of them reads the exception or the tokens again.</summary>
/// <remarks>Until then each of them worked it out for itself, and they disagreed: the runner told its
/// caller about a finished run whose journal could not be written by throwing, and the sweep read that throw during
/// a stop as a stopped replay, and replayed the finished run a second time.</remarks>
public sealed record WorkflowRunEnding
{
    private const int JournalMessageLimit = 2000;
    private const int RowReasonLimit = 400;
    private const string CancelledMessage = "Workflow execution was cancelled.";

    public required WorkflowRunPhase Phase { get; init; }

    /// <summary>The attempt's outcome, which the metric label and the retry policy read.</summary>
    public required WorkflowAttemptOutcome Outcome { get; init; }

    public required WorkflowExecutionStatus JournalStatus { get; init; }

    public string? JournalMessage { get; init; }

    public required WorkflowRunRowEnding Row { get; init; }

    /// <summary>The status the row ends as, when <see cref="Row"/> ends it.</summary>
    public WorkflowRunStateStatus RowStatus { get; init; }

    /// <summary>The failure reason the row ends with. On a release, why the run was left for a peer
    /// (<see cref="WorkflowRunHandover.StoppedReason"/> or <see cref="WorkflowRunHandover.LeaseLostReason"/>), which the
    /// release does not write.</summary>
    public string? RowReason { get; init; }

    /// <summary>Whether the run's waits are closed and their in-process wiring torn down.</summary>
    public bool ClosesWaits { get; init; }

    /// <summary>Whether the retry policy gets another attempt.</summary>
    public bool TryAgain { get; init; }

    /// <summary>What the caller that started the run is told, or null when the run finished and was recorded.</summary>
    public Exception? Error { get; init; }

    internal bool RowWriteFailed { get; init; }

    internal WorkflowRunStateRow? ReleasedRow { get; init; }

    public string MetricsLabel => WorkflowRunAttempt.MetricsLabel(Outcome);

    /// <summary>The same ending once a peer turned out to hold the row: the peer runs the run now. A finished walk
    /// is still finished — what it did is done, and the journal says so — only its row is left to the peer.</summary>
    public WorkflowRunEnding RowRefused() => Phase == WorkflowRunPhase.Finished
        ? this with { Row = WorkflowRunRowEnding.Leave }
        : this with
    {
        Phase = WorkflowRunPhase.LostToPeer,
        Outcome = WorkflowAttemptOutcome.Cancelled,
        JournalStatus = WorkflowExecutionStatus.Cancelled,
        Row = WorkflowRunRowEnding.Leave,
        ClosesWaits = false,
        TryAgain = false
    };

    /// <summary>Decides the ending. Order: completed walk, row ownership, debug kill, budget, child ending, payload
    /// size, caller, lease, clock, then exception type. A source that fired decides whatever the block turned it into.</summary>
    public static WorkflowRunEnding Decide(WorkflowRunExit exit)
    {
        var thrown = exit.Thrown;
        var hasRow = exit.RunRow is not null;

        if (exit.WalkDone)
            return new WorkflowRunEnding
            {
                Phase = WorkflowRunPhase.Finished,
                Outcome = WorkflowAttemptOutcome.Succeeded,
                JournalStatus = WorkflowExecutionStatus.Succeeded,
                Row = hasRow ? WorkflowRunRowEnding.EndIfOwned : WorkflowRunRowEnding.Leave,
                RowStatus = WorkflowRunStateStatus.Completed,
                Error = thrown is null ? null : new WorkflowRunNotRecordedException(exit.WorkflowId, thrown)
            };

        if (exit.RowLostByBeat is { } loss && hasRow)
            return LostToPeer(new WorkflowRunOwnershipLostException(exit.RunRow!.Value, string.Empty, loss));

        if (thrown is WorkflowRunOwnershipLostException own && hasRow && own.StateExecutionId == exit.RunRow)
            return LostToPeer(own);

        if (exit.DebugKilled)
            return Stopped(thrown ?? new OperationCanceledException(), CancelledMessage, closesWaits: true, hasRow);

        if (exit.BudgetLostByBeat)
        {
            var budgetLost = new WorkflowRunBudgetLostException(exit.WorkflowId, exit.Mode.ToString());
            return exit.MayHandOver && hasRow
                ? HandedOver(budgetLost, WorkflowRunHandover.LeaseLostReason, closesWaits: false)
                : Stopped(budgetLost, budgetLost.Message, closesWaits: false, hasRow);
        }

        if (thrown is WorkflowRunOwnershipLostException or WorkflowRunNotRecordedException)
            return Failed(thrown, WorkflowAttemptOutcome.Failed, tryAgain: false, hasRow);

        if (thrown is SuspendPayloadTooLargeException tooLarge)
            return Failed(tooLarge, WorkflowAttemptOutcome.Failed, tryAgain: false, hasRow) with
            {
                JournalMessage = Cut($"suspend_payload_too_large: {tooLarge.Message}", JournalMessageLimit)
            };

        if (exit.CallerCancelled)
            return exit.HostStopping && exit.MayHandOver && hasRow
                ? HandedOver(thrown ?? new OperationCanceledException(), WorkflowRunHandover.StoppedReason, closesWaits: true)
                : Stopped(thrown ?? new OperationCanceledException(), CancelledMessage, closesWaits: true, hasRow);

        if (exit.LeaseCancelled)
            return exit.Mode == WorkflowMode.Restart
                ? Stopped(new WorkflowSupersededException(exit.WorkflowId, WorkflowMode.Restart.ToString(), thrown),
                    CancelledMessage, closesWaits: true, hasRow) with { Phase = WorkflowRunPhase.Superseded }
                : Stopped(thrown ?? new OperationCanceledException(), CancelledMessage, closesWaits: true, hasRow) with
                {
                    Phase = WorkflowRunPhase.Cancelled
                };

        if (exit.TimeoutFired)
        {
            var timedOut = $"Workflow execution timed out after {exit.TimeoutSeconds} seconds.";
            return new WorkflowRunEnding
            {
                Phase = WorkflowRunPhase.TimedOut,
                Outcome = WorkflowAttemptOutcome.TimedOut,
                JournalStatus = WorkflowExecutionStatus.TimedOut,
                JournalMessage = timedOut,
                Row = hasRow ? WorkflowRunRowEnding.EndIfOwned : WorkflowRunRowEnding.Leave,
                RowStatus = WorkflowRunStateStatus.Failed,
                RowReason = Cut(timedOut, RowReasonLimit),
                Error = thrown ?? new OperationCanceledException()
            };
        }

        thrown ??= new InvalidOperationException("Workflow execution failed");
        if (thrown is OperationCanceledException)
            return Stopped(thrown, CancelledMessage, closesWaits: true, hasRow) with { Phase = WorkflowRunPhase.Cancelled };

        var outcome = WorkflowRunAttempt.Classify(thrown, timedOutLocally: false);
        return Failed(thrown, outcome,
            WorkflowRunAttempt.AllowsAnotherAttempt(outcome, exit.AttemptNumber, exit.MaxAttempts), hasRow);
    }

    private static WorkflowRunEnding LostToPeer(WorkflowRunOwnershipLostException lost) => new()
    {
        Phase = WorkflowRunPhase.LostToPeer,
        Outcome = WorkflowAttemptOutcome.Cancelled,
        JournalStatus = WorkflowExecutionStatus.Cancelled,
        JournalMessage = Cut(lost.Message, JournalMessageLimit),
        Row = WorkflowRunRowEnding.Leave,
        Error = lost
    };

    private static WorkflowRunEnding HandedOver(Exception error, string reason, bool closesWaits) => new()
    {
        Phase = WorkflowRunPhase.HandedOver,
        Outcome = WorkflowAttemptOutcome.Cancelled,
        JournalStatus = WorkflowExecutionStatus.Cancelled,
        JournalMessage = WorkflowRunHandover.JournalMessage(reason),
        Row = WorkflowRunRowEnding.Release,
        RowReason = reason,
        ClosesWaits = closesWaits,
        Error = error
    };

    private static WorkflowRunEnding Stopped(Exception error, string journalMessage, bool closesWaits, bool hasRow) => new()
    {
        Phase = WorkflowRunPhase.Stopped,
        Outcome = WorkflowAttemptOutcome.Cancelled,
        JournalStatus = WorkflowExecutionStatus.Cancelled,
        JournalMessage = Cut(journalMessage, JournalMessageLimit),
        Row = hasRow ? WorkflowRunRowEnding.EndIfOwned : WorkflowRunRowEnding.Leave,
        RowStatus = WorkflowRunStateStatus.Cancelled,
        RowReason = Cut(error.Message, RowReasonLimit),
        ClosesWaits = closesWaits,
        Error = error
    };

    private static WorkflowRunEnding Failed(Exception error, WorkflowAttemptOutcome outcome, bool tryAgain, bool hasRow) => new()
    {
        Phase = WorkflowRunPhase.Failed,
        Outcome = outcome,
        JournalStatus = WorkflowExecutionStatus.Failed,
        JournalMessage = Cut(error.Message, JournalMessageLimit),
        Row = hasRow ? WorkflowRunRowEnding.EndIfOwned : WorkflowRunRowEnding.Leave,
        RowStatus = WorkflowRunStateStatus.Failed,
        RowReason = Cut(error.Message, RowReasonLimit),
        TryAgain = tryAgain,
        Error = error
    };

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;
}
