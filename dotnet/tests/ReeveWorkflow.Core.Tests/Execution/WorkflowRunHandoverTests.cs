using ReeveWorkflow.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunHandoverTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_run_its_host_stops_leaves_its_row_running_and_unowned_for_a_peer()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running, "a Cancelled row is one recovery never selects");
        row.OwnedByReplicaId.Should().BeNull("the row is left for whichever peer's sweep reaches it first");
        world.RunState.Completions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_its_host_stops_asleep_is_handed_over_still_asleep_until_the_same_wake_up_time()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true, durableSleepAtHold: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        using (var patience = new CancellationTokenSource(Patience))
        {
            while (world.RunState.LastBegun is not { } begun || world.RunState.Row(begun).Status != WorkflowRunStateStatus.Sleeping)
                await Task.Delay(5, patience.Token);
        }
        var asleep = world.RunState.Row(world.RunState.LastBegun!.Value);
        await stopping.CancelAsync();
        await caller.CancelAsync();

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        var row = world.RunState.Row(asleep.ExecutionId);
        row.Status.Should().Be(WorkflowRunStateStatus.Sleeping,
            "a recovery re-enters a Sleeping row at the wake-up time it names; a Running one sleeps the whole wait again");
        row.WaitUntilUtc.Should().Be(asleep.WaitUntilUtc);
        row.CurrentBlockPath.Should().Be(RunLifecycleWorld.HoldNodeId);
        row.OwnedByReplicaId.Should().BeNull("the row is left for whichever peer's sweep reaches it first");
        world.RunState.Completions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_journal_says_the_run_was_handed_over_rather_than_cancelled_by_someone()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        log.Status.Should().Be(WorkflowExecutionStatus.Running, "the entry is not over: the sweep that takes the row carries the run on under it");
        log.ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason,
            "whoever reads the history has to be able to tell a deploy from an operator");
    }

    [Fact]
    public async Task A_peer_that_ends_the_released_run_before_the_stopper_reads_it_back_keeps_the_entry()
    {
        var world = new RunLifecycleWorld();
        world.RunState.AfterRelease = id => world.RunState.Put(id, WorkflowRunStateStatus.Completed, "replica-2");
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        log.Status.Should().Be(WorkflowExecutionStatus.Running,
            "the release landed, so the entry is the peer's to end; closing it here would write over what the peer did");
        log.ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason);
    }

    [Fact]
    public async Task A_stopping_run_whose_released_row_names_no_entry_closes_its_own_entry()
    {
        var world = new RunLifecycleWorld { HoldIdempotent = true };
        world.RunState.PreBlockWritesFail = true;
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        world.RunState.Row(world.RunState.LastBegun!.Value).OwnedByReplicaId.Should().BeNull("the release landed");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().NotBe(WorkflowExecutionStatus.Running,
            "no checkpoint ever reached the row, so the peer replays the run under an entry of its own and nobody else ends this one");
    }

    [Fact]
    public async Task A_run_cancelled_while_its_host_keeps_running_is_still_ended_as_cancelled()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await caller.CancelAsync();

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        world.RunState.Completions.Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled, "a cancel is not a stop");
    }

    [Fact]
    public async Task A_host_that_did_not_ask_for_a_handover_ends_the_row_as_it_did_before()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: false);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
    }

    [Fact]
    public async Task A_run_whose_workflow_is_forgotten_during_a_stop_is_ended_not_handed_over()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        engine.ForgetWorkflow(world.WorkflowId);

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
    }

    [Fact]
    public async Task A_row_a_peer_took_before_the_stop_is_neither_released_nor_ended()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        var rowId = world.RunState.LastBegun!.Value;
        world.RunState.TakeOver(rowId, "replica-2");
        await stopping.CancelAsync();
        await caller.CancelAsync();

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        world.RunState.Row(rowId).OwnedByReplicaId.Should().Be("replica-2", "the release is fenced on this replica's name");
        world.RunState.Completions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resumed_segment_its_host_stops_is_handed_over_too()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var parked = world.Parked();

        var resume = world.Resumer(stopping.Token, handOverOnStop: true).ResumeFromSuspensionAsync(parked, caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed, "the run was taken; its wait is not put back");
        var row = world.RunState.Row(world.ParkedStateId!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().BeNull();
        world.RunState.Completions.Should().BeEmpty();
        world.Journal.Logs[parked.ExecutionLogId].ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason);
    }

    [Fact]
    public async Task A_resumed_segment_cancelled_while_its_host_keeps_running_is_ended_as_cancelled()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var parked = world.Parked();

        var resume = world.Resumer(stopping.Token, handOverOnStop: true).ResumeFromSuspensionAsync(parked, caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await caller.CancelAsync();

        await resume.WaitAsync(Patience);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
    }
    [Fact]
    public async Task A_replay_its_host_stops_is_ended_so_that_only_the_row_it_replays_comes_back()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        var replay = engine.TryResumeAsync(abandoned, world.WorkflowId, caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await replay.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        var replayRow = world.RunState.LastBegun!.Value;
        replayRow.Should().NotBe(abandoned);
        world.RunState.Completions.Should().ContainSingle()
            .Which.Should().Match<(Guid Id, WorkflowRunStateStatus Status, string? Reason)>(
                ending => ending.Id == replayRow && ending.Status == WorkflowRunStateStatus.Cancelled);
        world.RunState.Row(abandoned).Status.Should().Be(WorkflowRunStateStatus.Running);
    }

    [Fact]
    public async Task A_run_another_run_dispatched_that_its_host_stops_is_ended_rather_than_handed_over()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, parentExecutionId: Guid.NewGuid(), cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        world.Journal.Logs.Values.Should().ContainSingle().Which.ErrorMessage.Should().NotStartWith(WorkflowRunHandover.StoppedReason);
    }

    [Fact]
    public async Task A_resumed_segment_of_a_run_another_run_dispatched_is_ended_rather_than_handed_over()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var parked = world.Parked();
        world.RunState.DispatchedBy(world.ParkedStateId!.Value, Guid.NewGuid());

        var resume = world.Resumer(stopping.Token, handOverOnStop: true).ResumeFromSuspensionAsync(parked, caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await resume.WaitAsync(Patience);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        world.Journal.Logs[parked.ExecutionLogId].ErrorMessage.Should().NotStartWith(WorkflowRunHandover.StoppedReason);
    }
    [Fact]
    public async Task A_run_its_host_stops_is_handed_over_although_its_block_turned_the_stop_into_a_failure()
    {
        var world = new RunLifecycleWorld { HoldWrapsItsCancellation = true };
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<Exception>();

        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().BeNull();
        world.Journal.Logs.Values.Should().ContainSingle().Which.ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason);
    }

    [Fact]
    public async Task A_resumed_segment_its_host_stops_is_handed_over_although_its_block_turned_the_stop_into_a_failure()
    {
        var world = new RunLifecycleWorld { HoldWrapsItsCancellation = true };
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var parked = world.Parked();

        var resume = world.Resumer(stopping.Token, handOverOnStop: true).ResumeFromSuspensionAsync(parked, caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await resume.WaitAsync(Patience);

        var row = world.RunState.Row(world.ParkedStateId!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().BeNull();
        world.Journal.Logs[parked.ExecutionLogId].ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason);
    }
    [Fact]
    public async Task A_run_that_finished_its_walk_is_not_handed_over_by_a_stop_that_lands_while_its_journal_is_saved()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = () => { stopping.Cancel(); caller.Cancel(); };

        await engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token).WaitAsync(Patience);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_resumed_segment_that_finished_its_walk_is_not_handed_over_by_a_stop_that_lands_while_its_journal_is_saved()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var parked = world.Parked();
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = () => { stopping.Cancel(); caller.Cancel(); };

        (await world.Resumer(stopping.Token, handOverOnStop: true).ResumeFromSuspensionAsync(parked, caller.Token)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_run_that_finished_its_walk_is_not_handed_over_when_its_journal_save_fails_during_a_stop()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = OnceThen(() =>
        {
            stopping.Cancel();
            caller.Cancel();
            throw new ObjectDisposedException("journal");
        });

        (await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowRunNotRecordedException>("the journal could not be written, and the caller is told"))
            .WithInnerException<ObjectDisposedException>();

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed,
            "the walk did everything it does: a peer replaying it would do it all again");
    }

    [Fact]
    public async Task A_run_that_finished_its_walk_is_not_tried_again_when_its_journal_save_fails()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""");
        using var engine = world.Engine();
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = OnceThen(() => throw new InvalidOperationException("the database went away"));

        (await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowRunNotRecordedException>()).WithInnerException<InvalidOperationException>();

        world.HoldEntries.Should().Be(1, "a retry walks a finished run again");
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_resumed_segment_that_finished_its_walk_is_not_handed_over_when_its_journal_save_fails_during_a_stop()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var parked = world.Parked();
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = OnceThen(() =>
        {
            stopping.Cancel();
            caller.Cancel();
            throw new ObjectDisposedException("journal");
        });

        (await world.Resumer(stopping.Token, handOverOnStop: true).ResumeFromSuspensionAsync(parked, caller.Token)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_resumed_segment_that_finished_its_walk_is_not_tried_again_when_its_journal_save_fails()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""");
        var parked = world.Parked();
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = OnceThen(() => throw new InvalidOperationException("the database went away"));

        (await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        world.HoldEntries.Should().Be(1, "a retry walks the segment again from the wait");
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_run_whose_caller_cancels_while_it_parks_is_parked_all_the_same()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        using var caller = new CancellationTokenSource();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions { WhileSuspending = caller.Cancel };
        using var engine = world.Engine(suspensions: suspensions);
        world.Journal.WhileSavingDetails = caller.Cancel;

        await engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token).WaitAsync(Patience);

        suspensions.Suspended.Should().ContainSingle();
        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended);
    }

    [Fact]
    public async Task A_resumed_segment_whose_caller_cancels_while_it_parks_again_is_parked_and_says_it_resumed()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        using var caller = new CancellationTokenSource();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions { WhileSuspending = caller.Cancel };
        var parked = world.Parked();
        world.Journal.WhileSavingDetails = caller.Cancel;

        (await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, caller.Token).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        suspensions.Suspended.Should().ContainSingle();
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended);
    }

    [Fact]
    public async Task A_run_whose_row_a_peer_took_while_it_persisted_its_wait_does_not_park_that_row()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        suspensions.WhileSuspending = () =>
            world.RunState.Put(world.RunState.LastBegun!.Value, WorkflowRunStateStatus.Running, "replica-2");
        using var engine = world.Engine(suspensions: suspensions);

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowRunOwnershipLostException>();

        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running, "the peer is running it");
        row.OwnedByReplicaId.Should().Be("replica-2");
    }

    [Fact]
    public async Task A_resumed_segment_whose_row_a_peer_took_while_it_persisted_its_wait_does_not_park_that_row()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        var parked = world.Parked();
        suspensions.WhileSuspending = () =>
            world.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, "replica-2");

        await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        var row = world.RunState.Row(world.ParkedStateId!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().Be("replica-2");
    }

    [Fact]
    public async Task A_run_that_finished_its_walk_but_could_not_save_its_steps_saves_its_entry_again_and_ends_its_row_with_no_failure()
    {
        var world = new RunLifecycleWorld();
        using var engine = world.Engine();
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = OnceThen(() => throw new InvalidOperationException("the database went away"));

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowRunNotRecordedException>();

        world.Journal.RunsSaved.Should().Be(1, "the entry is written again on its own once its steps could not be");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        var ending = world.RunState.Completions.Should().ContainSingle().Subject;
        ending.Status.Should().Be(WorkflowRunStateStatus.Completed);
        ending.Reason.Should().BeNull("a completed row with a failure reason reads as a failed run");
    }

    [Fact]
    public async Task A_replay_that_finished_its_walk_says_it_finished_though_its_journal_save_failed_during_a_stop()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = OnceThen(() =>
        {
            stopping.Cancel();
            caller.Cancel();
            throw new ObjectDisposedException("journal");
        });

        var outcome = await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, caller.Token)
            .WaitAsync(Patience);

        outcome.Should().Be(WorkflowReplayOutcome.Finished);
        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_replay_its_host_stops_through_a_block_that_rethrew_the_stop_says_it_stopped()
    {
        var world = new RunLifecycleWorld { HoldWrapsItsCancellation = true };
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        var replay = ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await stopping.CancelAsync();
        await caller.CancelAsync();

        (await replay.WaitAsync(Patience)).Should().Be(WorkflowReplayOutcome.Stopped);
    }

    [Fact]
    public async Task A_replay_whose_block_failed_says_it_failed()
    {
        var world = new RunLifecycleWorld { FailHoldTimes = 1 };
        using var engine = world.Engine();
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        (await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowReplayOutcome.Failed);
    }

    [Fact]
    public async Task A_replay_whose_block_cancels_itself_says_it_failed_rather_than_that_it_was_stopped()
    {
        var world = new RunLifecycleWorld { HoldCancelsItself = true };
        using var engine = world.Engine();
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        (await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowReplayOutcome.Failed);
    }

    [Fact]
    public async Task A_replay_that_parked_on_a_wait_says_it_finished()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        using var engine = world.Engine(suspensions: new RunLifecycleWorld.RecordingSuspensions());
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        (await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowReplayOutcome.Finished);
    }

    [Fact]
    public async Task A_run_whose_caller_cancels_it_through_a_block_that_wraps_the_stop_is_cancelled_and_not_tried_again()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""") { HoldWrapsItsCancellation = true };
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine();

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowNodeExecutionException>();

        world.HoldEntries.Should().Be(1, "retrying defeats whoever stopped it");
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_run_whose_block_turns_the_workflows_timeout_into_a_failure_of_its_own_is_timed_out_and_not_tried_again()
    {
        var world = new RunLifecycleWorld(timeoutSeconds: 1, retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldWrapsItsCancellation = true
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        world.HoldEntries.Should().Be(1, "the next attempt gets the same budget and spends it the same way");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.TimedOut);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public async Task A_run_whose_row_a_peer_took_as_its_block_failed_neither_ends_that_row_nor_tries_again()
    {
        RunLifecycleWorld? world = null;
        world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () =>
            {
                world!.RunState.Put(world.RunState.LastBegun!.Value, WorkflowRunStateStatus.Running, "replica-2");
                return new InvalidOperationException("the block failed");
            }
        };
        var captured = world;
        using var engine = captured.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(captured.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        captured.HoldEntries.Should().Be(1, "the peer runs the workflow now");
        captured.RunState.Completions.Should().BeEmpty("the row is the peer's to end");
        captured.RunState.Row(captured.RunState.LastBegun!.Value).OwnedByReplicaId.Should().Be("replica-2");
        captured.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Running, "the entry is the peer's now: it carries the run on under it, or closes it");
    }

    [Fact]
    public async Task A_resumed_segment_whose_row_a_peer_took_as_its_block_failed_does_not_end_that_row()
    {
        RunLifecycleWorld? world = null;
        world = new RunLifecycleWorld
        {
            HoldThrows = () =>
            {
                world!.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, "replica-2");
                return new InvalidOperationException("the block failed");
            }
        };
        var captured = world;
        var parked = captured.Parked();

        (await captured.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        captured.RunState.Completions.Should().BeEmpty();
        captured.RunState.Row(captured.ParkedStateId!.Value).OwnedByReplicaId.Should().Be("replica-2");
        captured.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Running, "the entry is the peer's now: it carries the run on under it, or closes it");
    }

    [Fact]
    public async Task A_run_whose_row_a_peer_took_while_it_persisted_its_wait_closes_that_wait_and_wires_nothing()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        suspensions.WhileSuspending = () =>
            world.RunState.Put(world.RunState.LastBegun!.Value, WorkflowRunStateStatus.Running, "replica-2");
        using var engine = world.Engine(suspensions: suspensions);

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowRunOwnershipLostException>("the run is the peer's now, and the caller is told");

        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        suspensions.CancelledExecutions.Should().ContainSingle().Which.Should().Be(log.Id);
        suspensions.Registered.Should().BeEmpty();
        log.Status.Should().Be(WorkflowExecutionStatus.Running, "the entry is the peer's now: it carries the run on under it, or closes it");
        world.RunState.Completions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resumed_segment_whose_row_a_peer_took_while_it_persisted_its_wait_closes_that_wait_and_wires_nothing()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        var parked = world.Parked();
        suspensions.WhileSuspending = () =>
            world.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, "replica-2");

        (await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);

        suspensions.CancelledExecutions.Should().ContainSingle().Which.Should().Be(parked.ExecutionLogId);
        suspensions.Registered.Should().BeEmpty();
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Running, "the entry is the peer's now: it carries the run on under it, or closes it");
    }

    [Fact]
    public async Task A_replay_whose_wait_cannot_be_persisted_while_its_host_stops_leaves_one_row_to_replay()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        using var caller = new CancellationTokenSource();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions
        {
            WhileSuspending = () =>
            {
                caller.Cancel();
                throw new ObjectDisposedException("suspension store");
            }
        };
        using var engine = world.Engine(suspensions: suspensions);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        var outcome = await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, caller.Token)
            .WaitAsync(Patience);

        outcome.Should().Be(WorkflowReplayOutcome.Stopped, "the old row is what brings the run back");
        var replayRow = world.RunState.Row(world.RunState.LastBegun!.Value);
        replayRow.Status.Should().Be(WorkflowRunStateStatus.Cancelled, "only one row may be left to replay");
    }

    [Fact]
    public async Task A_stopped_replay_whose_own_row_cannot_be_ended_answers_finished_since_that_row_carries_the_run()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        using var caller = new CancellationTokenSource();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions
        {
            WhileSuspending = () =>
            {
                caller.Cancel();
                world.RunState.EndingsFail = true;
                throw new ObjectDisposedException("suspension store");
            }
        };
        using var engine = world.Engine(suspensions: suspensions);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        var outcome = await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, caller.Token)
            .WaitAsync(Patience);

        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Running);
        outcome.Should().Be(WorkflowReplayOutcome.Finished, "the replay's row is replayed, and the old row is not owed as well");
    }

    [Fact]
    public async Task A_sweep_whose_stopped_replay_left_its_own_row_running_ends_the_old_row_as_replayed()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true, HoldIdempotent = true };
        using var host = new CancellationTokenSource();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions
        {
            WhileSuspending = () =>
            {
                host.Cancel();
                world.RunState.NextEndingsFail = 3;
                throw new ObjectDisposedException("suspension store");
            }
        };
        using var engine = world.Engine(suspensions: suspensions);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        await Sweep(world, engine, abandoned).SweepAsync(host.Token).WaitAsync(Patience);

        var replayRow = world.RunState.Row(world.RunState.LastBegun!.Value);
        replayRow.ExecutionId.Should().NotBe(abandoned);
        replayRow.Status.Should().Be(WorkflowRunStateStatus.Running, "setup: the replay's own row could not be ended");
        world.RunState.Row(abandoned).Should().Match<WorkflowRunStateRow>(row =>
            row.Status == WorkflowRunStateStatus.Completed && row.FailureReason == WorkflowRunRecoverySweep.ReplayedReason);
    }

    [Fact]
    public async Task A_stopped_replay_whose_ending_landed_before_the_store_went_away_leaves_the_old_row_to_replay()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true, HoldIdempotent = true };
        using var host = new CancellationTokenSource();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions
        {
            WhileSuspending = () =>
            {
                host.Cancel();
                world.RunState.NextEndingLandsThenFails = 2;
                throw new ObjectDisposedException("suspension store");
            }
        };
        using var engine = world.Engine(suspensions: suspensions);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        await Sweep(world, engine, abandoned).SweepAsync(host.Token).WaitAsync(Patience);

        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Cancelled,
            "setup: the replay's ending landed");
        world.RunState.Row(abandoned).Status.Should().Be(WorkflowRunStateStatus.Running, "the stopped run is still owed");
    }

    [Fact]
    public async Task A_sweep_whose_replay_was_stopped_asleep_and_could_not_be_ended_leaves_one_row_to_replay()
    {
        var world = new RunLifecycleWorld { HoldIdempotent = true };
        using var host = new CancellationTokenSource();
        using var engine = world.Engine(durableSleepAtHold: true);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");
        var sweeping = Sweep(world, engine, abandoned).SweepAsync(host.Token);
        using (var patience = new CancellationTokenSource(Patience))
        {
            while (world.RunState.LastBegun is not { } id || world.RunState.Row(id).Status != WorkflowRunStateStatus.Sleeping)
                await Task.Delay(5, patience.Token);
        }

        world.RunState.TransitionsFail = true;
        world.RunState.NextEndingsFail = 3;
        host.Cancel();
        await sweeping.WaitAsync(Patience);

        var replayRow = world.RunState.Row(world.RunState.LastBegun!.Value);
        replayRow.Status.Should().Be(WorkflowRunStateStatus.Sleeping, "setup: the replay's row could be neither woken nor ended");
        world.RunState.NextEndingsFail.Should().Be(0, "setup: all three ending writes threw");
        world.RunState.Row(abandoned).Should().Match<WorkflowRunStateRow>(row =>
            row.Status == WorkflowRunStateStatus.Completed && row.FailureReason == WorkflowRunRecoverySweep.ReplayedReason);
    }

    [Fact]
    public async Task A_stopped_replay_on_an_engine_with_no_replica_id_whose_row_cannot_be_ended_answers_finished()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true, EngineReplicaId = null };
        using var caller = new CancellationTokenSource();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions
        {
            WhileSuspending = () =>
            {
                caller.Cancel();
                world.RunState.EndingsFail = true;
                throw new ObjectDisposedException("suspension store");
            }
        };
        using var engine = world.Engine(suspensions: suspensions);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, null, "start");

        var outcome = await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, caller.Token)
            .WaitAsync(Patience);

        var replayRow = world.RunState.Row(world.RunState.LastBegun!.Value);
        replayRow.Status.Should().Be(WorkflowRunStateStatus.Running, "setup: the replay's row could not be ended");
        replayRow.OwnedByReplicaId.Should().BeNull("setup: its rows name no owner");
        outcome.Should().Be(WorkflowReplayOutcome.Finished);
    }

    [Fact]
    public async Task A_stopped_replay_whose_ending_can_be_neither_written_nor_read_back_answers_finished()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        using var caller = new CancellationTokenSource();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions
        {
            WhileSuspending = () =>
            {
                caller.Cancel();
                world.RunState.EndingsFail = true;
                world.RunState.ReadsFail = true;
                throw new ObjectDisposedException("suspension store");
            }
        };
        using var engine = world.Engine(suspensions: suspensions);
        var abandoned = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, "start");

        var outcome = await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, world.WorkflowId, caller.Token)
            .WaitAsync(Patience);

        outcome.Should().Be(WorkflowReplayOutcome.Finished);
    }

    private static WorkflowRunRecoverySweep Sweep(RunLifecycleWorld world, WorkflowEngine engine, Guid abandoned) =>
        new(engine, world.RunState, new ClaimOnce(world.RunState, abandoned), new WorkflowRunRecoveryOptions(),
            RunLifecycleWorld.ReplicaId, NullLogger.Instance);

    private sealed class ClaimOnce(RunLifecycleWorld.MemoryRunState rows, Guid id) : IWorkflowRunRecoveryStore
    {
        private int claimed;

        public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
            WorkflowRunRecoveryClaim request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>(
                Interlocked.Exchange(ref claimed, 1) == 0 ? [rows.Row(id)] : []);

        public Task<bool> RenewClaimAsync(Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    [Fact]
    public async Task A_run_whose_wait_cannot_be_persisted_ends_as_a_failure_rather_than_leaving_its_row_running()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions
        {
            WhileSuspending = () => throw new InvalidOperationException("The suspension store is away.")
        };
        using var engine = world.Engine(suspensions: suspensions);

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<InvalidOperationException>();

        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Failed);
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Failed);
    }

    [Fact]
    public async Task A_resumed_segment_whose_wait_cannot_be_persisted_ends_as_a_failure_rather_than_escaping()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions
        {
            WhileSuspending = () => throw new InvalidOperationException("The suspension store is away.")
        };
        var parked = world.Parked();

        (await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);

        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Failed);
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Failed);
    }

    [Fact]
    public async Task A_handover_whose_row_had_already_ended_does_not_say_the_row_was_released()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        world.RunState.Put(world.RunState.LastBegun!.Value, WorkflowRunStateStatus.Cancelled, RunLifecycleWorld.ReplicaId);
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        log.ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason).And.NotContain("was released")
            .And.Contain("already ended");
    }

    [Fact]
    public async Task A_handover_whose_release_could_not_be_confirmed_does_not_say_the_row_was_released()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        world.RunState.ReadsFail = true;
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        log.ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason).And.NotContain("was released")
            .And.Contain("could not be released");
    }

    private static Action OnceThen(Action first)
    {
        var done = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref done, 1) == 0)
                first();
        };
    }
}
