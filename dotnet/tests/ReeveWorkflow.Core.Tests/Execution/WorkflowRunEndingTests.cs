using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunEndingTests
{
    private static readonly Guid Workflow = Guid.NewGuid();
    private static readonly Guid Row = Guid.NewGuid();

    private static WorkflowRunExit Exit(Exception? thrown = null) => new()
    {
        WorkflowId = Workflow, RunRow = Row, Thrown = thrown, Mode = WorkflowMode.Single, MaxAttempts = 3
    };

    [Fact]
    public void A_walk_that_returned_is_finished_and_its_row_ends_completed()
    {
        var ending = WorkflowRunEnding.Decide(Exit() with { WalkDone = true });

        ending.Phase.Should().Be(WorkflowRunPhase.Finished);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Succeeded);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Completed);
        ending.RowReason.Should().BeNull();
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Succeeded);
        ending.TryAgain.Should().BeFalse();
        ending.Error.Should().BeNull();
    }

    [Fact]
    public void A_walk_that_returned_before_a_write_failed_during_a_stop_is_still_finished_and_says_it_was_not_recorded()
    {
        var cause = new ObjectDisposedException("journal");

        var ending = WorkflowRunEnding.Decide(Exit(cause) with
        {
            WalkDone = true, CallerCancelled = true, HostStopping = true, MayHandOver = true,
            LeaseCancelled = true, TimeoutFired = true, RowLostByBeat = WorkflowRunOwnershipLoss.ReclaimedByPeer
        });

        ending.Phase.Should().Be(WorkflowRunPhase.Finished, "the walk did everything the run does");
        ending.RowReason.Should().BeNull("a completed row with a failure reason reads as a failed run");
        ending.TryAgain.Should().BeFalse();
        ending.Error.Should().BeOfType<WorkflowRunNotRecordedException>().Which.InnerException.Should().BeSameAs(cause);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_run_its_debugger_killed_is_stopped_and_not_retried_whatever_the_block_threw(bool hostStopping)
    {
        var ending = WorkflowRunEnding.Decide(Exit(new InvalidOperationException("wrapped the kill")) with
        {
            DebugKilled = true, CallerCancelled = hostStopping, HostStopping = hostStopping, MayHandOver = true
        });

        ending.Phase.Should().Be(WorkflowRunPhase.Stopped);
        ending.TryAgain.Should().BeFalse();
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
    }

    [Fact]
    public void A_row_the_beat_found_taken_is_left_to_the_peer_whatever_the_block_threw()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new InvalidOperationException("wrapped the stop")) with
        {
            RowLostByBeat = WorkflowRunOwnershipLoss.ReclaimedByPeer, CallerCancelled = true, HostStopping = true,
            MayHandOver = true
        });

        ending.Phase.Should().Be(WorkflowRunPhase.LostToPeer);
        ending.Row.Should().Be(WorkflowRunRowEnding.Leave);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Cancelled);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.TryAgain.Should().BeFalse();
        ending.Error.Should().BeOfType<WorkflowRunOwnershipLostException>().Which.StateExecutionId.Should().Be(Row);
    }

    [Fact]
    public void A_debug_kill_wins_over_a_lost_budget_beat_so_the_run_is_not_handed_over()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new OperationCanceledException()) with
        {
            DebugKilled = true, BudgetLostByBeat = true, MayHandOver = true
        });

        ending.Phase.Should().Be(WorkflowRunPhase.Stopped);
        ending.TryAgain.Should().BeFalse();
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
    }

    [Fact]
    public void A_debug_kill_wins_over_a_lost_budget_beat_when_handover_is_not_allowed()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new OperationCanceledException()) with
        {
            DebugKilled = true, BudgetLostByBeat = true, MayHandOver = false
        });

        ending.Phase.Should().Be(WorkflowRunPhase.Stopped);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Cancelled);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
        ending.TryAgain.Should().BeFalse();
    }

    [Fact]
    public void A_row_lost_to_a_peer_stays_with_the_peer_even_when_this_replica_observed_a_debug_kill()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new OperationCanceledException()) with
        {
            DebugKilled = true, RowLostByBeat = WorkflowRunOwnershipLoss.ReclaimedByPeer
        });

        ending.Phase.Should().Be(WorkflowRunPhase.LostToPeer);
        ending.Row.Should().Be(WorkflowRunRowEnding.Leave);
        ending.TryAgain.Should().BeFalse();
    }

    [Fact]
    public void The_runs_own_row_refusing_a_write_is_left_to_the_peer_before_any_stop_is_read()
    {
        var lost = new WorkflowRunOwnershipLostException(Row, "hold", WorkflowRunOwnershipLoss.ReclaimedByPeer);

        var ending = WorkflowRunEnding.Decide(Exit(lost) with { CallerCancelled = true, HostStopping = true, MayHandOver = true });

        ending.Phase.Should().Be(WorkflowRunPhase.LostToPeer);
        ending.Row.Should().Be(WorkflowRunRowEnding.Leave);
        ending.Error.Should().BeSameAs(lost);
    }

    [Theory]
    [InlineData(true, WorkflowRunPhase.HandedOver, WorkflowRunRowEnding.Release)]
    [InlineData(false, WorkflowRunPhase.Stopped, WorkflowRunRowEnding.EndIfOwned)]
    public void A_budget_the_beat_found_lost_hands_the_run_over_only_where_it_may_be(
        bool mayHandOver, WorkflowRunPhase phase, WorkflowRunRowEnding row)
    {
        var ending = WorkflowRunEnding.Decide(Exit(new OperationCanceledException()) with
        {
            BudgetLostByBeat = true, MayHandOver = mayHandOver
        });

        ending.Phase.Should().Be(phase);
        ending.Row.Should().Be(row);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Cancelled);
        ending.Error.Should().BeOfType<WorkflowRunBudgetLostException>();
        ending.TryAgain.Should().BeFalse();
        if (mayHandOver)
            ending.JournalMessage.Should().StartWith(WorkflowRunHandover.LeaseLostReason);
        else
            ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
    }

    [Fact]
    public void A_childs_lost_row_fails_this_run_once_and_ends_its_row_only_while_it_is_still_this_replicas()
    {
        var child = new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child", WorkflowRunOwnershipLoss.ReclaimedByPeer);

        var ending = WorkflowRunEnding.Decide(Exit(child));

        ending.Phase.Should().Be(WorkflowRunPhase.Failed);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Failed);
        ending.TryAgain.Should().BeFalse("a retry runs the child beside the peer that holds it now");
    }

    [Fact]
    public void A_child_that_finished_but_was_not_recorded_fails_this_run_once()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new WorkflowRunNotRecordedException(Guid.NewGuid(), new InvalidOperationException())));

        ending.Phase.Should().Be(WorkflowRunPhase.Failed);
        ending.TryAgain.Should().BeFalse("a retry runs a child that has already done everything it does");
    }

    [Fact]
    public void A_debug_kill_wins_over_a_child_lost_row_error()
    {
        var child = new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child", WorkflowRunOwnershipLoss.ReclaimedByPeer);

        var ending = WorkflowRunEnding.Decide(Exit(child) with { DebugKilled = true });

        ending.Phase.Should().Be(WorkflowRunPhase.Stopped);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Cancelled);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
        ending.TryAgain.Should().BeFalse();
    }

    [Fact]
    public void A_debug_kill_wins_over_a_child_not_recorded_error()
    {
        var child = new WorkflowRunNotRecordedException(Guid.NewGuid(), new InvalidOperationException());

        var ending = WorkflowRunEnding.Decide(Exit(child) with { DebugKilled = true });

        ending.Phase.Should().Be(WorkflowRunPhase.Stopped);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Cancelled);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
        ending.TryAgain.Should().BeFalse();
    }

    [Fact]
    public void A_refused_ending_becomes_the_peers_run_with_a_cancelled_journal_and_nothing_to_retry()
    {
        var child = new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child", WorkflowRunOwnershipLoss.ReclaimedByPeer);

        var refused = WorkflowRunEnding.Decide(Exit(child)).RowRefused();

        refused.Phase.Should().Be(WorkflowRunPhase.LostToPeer);
        refused.Row.Should().Be(WorkflowRunRowEnding.Leave);
        refused.Outcome.Should().Be(WorkflowAttemptOutcome.Cancelled);
        refused.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        refused.TryAgain.Should().BeFalse();
        refused.ClosesWaits.Should().BeFalse("the waits are the peer's run's now");
        refused.Error.Should().BeSameAs(child);
    }

    [Fact]
    public void A_finished_walk_whose_row_a_peer_holds_stays_finished_and_leaves_the_row()
    {
        var refused = WorkflowRunEnding.Decide(Exit() with { WalkDone = true }).RowRefused();

        refused.Phase.Should().Be(WorkflowRunPhase.Finished, "what it did is done, and its journal says so");
        refused.JournalStatus.Should().Be(WorkflowExecutionStatus.Succeeded);
        refused.Row.Should().Be(WorkflowRunRowEnding.Leave);
    }

    [Fact]
    public void A_wait_too_large_to_persist_fails_the_run_once_and_says_so()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new SuspendPayloadTooLargeException(10_000_000)));

        ending.Phase.Should().Be(WorkflowRunPhase.Failed);
        ending.TryAgain.Should().BeFalse("the same walk parks the same payload");
        ending.JournalMessage.Should().StartWith("suspend_payload_too_large: ");
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public void A_debug_kill_wins_over_a_suspend_payload_that_is_too_large()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new SuspendPayloadTooLargeException(10_000_000)) with
        {
            DebugKilled = true
        });

        ending.Phase.Should().Be(WorkflowRunPhase.Stopped);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Cancelled);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
        ending.TryAgain.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_stop_from_the_host_hands_a_run_over_whatever_its_block_turned_it_into(bool blockWrappedIt)
    {
        Exception thrown = blockWrappedIt ? new InvalidOperationException("carrier failed") : new OperationCanceledException();

        var ending = WorkflowRunEnding.Decide(Exit(thrown) with { CallerCancelled = true, HostStopping = true, MayHandOver = true });

        ending.Phase.Should().Be(WorkflowRunPhase.HandedOver);
        ending.Row.Should().Be(WorkflowRunRowEnding.Release);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.JournalMessage.Should().StartWith(WorkflowRunHandover.StoppedReason);
        ending.ClosesWaits.Should().BeTrue();
        ending.TryAgain.Should().BeFalse();
        ending.Error.Should().BeSameAs(thrown);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_caller_cancelling_while_the_run_may_not_be_handed_over_ends_it_cancelled_whatever_the_block_threw(
        bool hostStopping, bool mayHandOver)
    {
        var ending = WorkflowRunEnding.Decide(Exit(new InvalidOperationException("carrier failed")) with
        {
            CallerCancelled = true, HostStopping = hostStopping, MayHandOver = mayHandOver
        });

        ending.Phase.Should().Be(WorkflowRunPhase.Stopped);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Cancelled);
        ending.JournalMessage.Should().Be("Workflow execution was cancelled.");
        ending.ClosesWaits.Should().BeTrue();
        ending.TryAgain.Should().BeFalse("retrying defeats whoever stopped it");
    }

    [Theory]
    [InlineData(WorkflowMode.Restart, WorkflowRunPhase.Superseded)]
    [InlineData(WorkflowMode.Single, WorkflowRunPhase.Cancelled)]
    public void A_lease_cancelled_under_a_quiet_caller_is_a_supersession_only_in_restart_mode(
        WorkflowMode mode, WorkflowRunPhase phase)
    {
        var ending = WorkflowRunEnding.Decide(Exit(new InvalidOperationException("block wrapped it")) with
        {
            LeaseCancelled = true, TimeoutFired = true, HostStopping = true, MayHandOver = true, Mode = mode
        });

        ending.Phase.Should().Be(phase);
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Cancelled);
        ending.Row.Should().Be(WorkflowRunRowEnding.EndIfOwned, "a torn-down or replaced run is not left for a peer");
        ending.TryAgain.Should().BeFalse();
        if (mode == WorkflowMode.Restart)
            ending.Error.Should().BeOfType<WorkflowSupersededException>();
    }

    [Fact]
    public void The_runs_own_timeout_ends_it_timed_out_whatever_the_block_threw_and_is_never_retried()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new InvalidOperationException("client wrapped it")) with
        {
            TimeoutFired = true, TimeoutSeconds = 7
        });

        ending.Phase.Should().Be(WorkflowRunPhase.TimedOut);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.TimedOut);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.TimedOut);
        ending.JournalMessage.Should().Be("Workflow execution timed out after 7 seconds.");
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Failed);
        ending.RowReason.Should().Be("Workflow execution timed out after 7 seconds.");
        ending.TryAgain.Should().BeFalse();
    }

    [Fact]
    public void A_cancellation_nobody_asked_for_ends_the_run_cancelled_and_not_retried()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new TaskCanceledException("the client gave up")));

        ending.Phase.Should().Be(WorkflowRunPhase.Cancelled, "a replay of it meets the same, so no sweep keeps it owed");
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Cancelled);
        ending.TryAgain.Should().BeFalse();
    }

    [Fact]
    public void A_definition_that_will_not_run_fails_once()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new InvalidWorkflowDefinitionException("bad graph")));

        ending.Phase.Should().Be(WorkflowRunPhase.Failed);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.ValidationFailed);
        ending.TryAgain.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public void A_failure_is_tried_again_while_the_policy_has_attempts_left(int attempt, bool again)
    {
        var ending = WorkflowRunEnding.Decide(Exit(new InvalidOperationException("boom")) with { AttemptNumber = attempt });

        ending.Phase.Should().Be(WorkflowRunPhase.Failed);
        ending.Outcome.Should().Be(WorkflowAttemptOutcome.Failed);
        ending.TryAgain.Should().Be(again);
        ending.JournalStatus.Should().Be(WorkflowExecutionStatus.Failed);
        ending.JournalMessage.Should().Be("boom");
        ending.RowStatus.Should().Be(WorkflowRunStateStatus.Failed);
        ending.RowReason.Should().Be("boom");
    }

    [Fact]
    public void Long_messages_are_cut_to_what_the_journal_and_the_row_hold()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new InvalidOperationException(new string('x', 5000))));

        ending.JournalMessage!.Length.Should().Be(2000);
        ending.RowReason!.Length.Should().Be(400);
    }

    [Fact]
    public void A_run_with_no_row_never_hands_over_and_has_no_row_to_end()
    {
        var ending = WorkflowRunEnding.Decide(Exit(new OperationCanceledException()) with
        {
            RunRow = null, CallerCancelled = true, HostStopping = true, MayHandOver = true
        });

        ending.Phase.Should().Be(WorkflowRunPhase.Stopped);
        ending.Row.Should().Be(WorkflowRunRowEnding.Leave);
    }
}
