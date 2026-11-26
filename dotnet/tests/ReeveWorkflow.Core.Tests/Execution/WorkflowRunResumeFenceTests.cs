using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunResumeFenceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(WorkflowRunStateStatus.Running, "replica-2")]
    [InlineData(WorkflowRunStateStatus.Sleeping, "replica-2")]
    [InlineData(WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId)]
    [InlineData(WorkflowRunStateStatus.Running, null)]
    public async Task A_parked_run_whose_row_has_moved_on_is_not_entered_and_nothing_of_it_is_written(
        WorkflowRunStateStatus status, string? owner)
    {
        var world = new RunLifecycleWorld();
        var parked = world.Parked();
        world.RunState.Put(world.ParkedStateId!.Value, status, owner, RunLifecycleWorld.HoldNodeId);

        var outcome = await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.HeldElsewhere);
        world.Entered.Task.IsCompleted.Should().BeFalse("no block of a run somebody else holds is run here");
        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(WorkflowExecutionStatus.Running, "the journal entry is the holder's to end");
        log.ErrorMessage.Should().BeNull();
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(status);
        world.RunState.Row(world.ParkedStateId!.Value).OwnedByReplicaId.Should().Be(owner);
        world.RunState.Completions.Should().BeEmpty();
        world.Audit.Events.Should().BeEmpty("an audit trail that says the run was resumed here would be false");
    }

    [Fact]
    public async Task A_parked_run_with_no_row_left_is_not_held_by_anyone_and_ends_where_the_walk_finds_it_gone()
    {
        var world = new RunLifecycleWorld();
        var parked = world.Parked();
        world.RunState.Remove(world.ParkedStateId!.Value);

        var outcome = await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Resumed);
        world.Entered.Task.IsCompleted.Should().BeFalse();
        world.Journal.Logs[parked.ExecutionLogId].ErrorMessage.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task A_parked_run_whose_refused_row_cannot_be_read_back_is_deferred_rather_than_guessed_at()
    {
        var world = new RunLifecycleWorld();
        var parked = world.Parked();
        world.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, "replica-2");
        world.RunState.ReadsFail = true;

        var outcome = await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Deferred, "a later pass asks again; nothing was done to the run");
        world.Entered.Task.IsCompleted.Should().BeFalse();
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Running);
    }

    [Fact]
    public async Task A_parked_run_whose_row_is_still_parked_is_entered_in_this_replicas_name()
    {
        var world = new RunLifecycleWorld();
        var parked = world.Parked();

        var resume = world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await world.Entered.Task.WaitAsync(Patience);
        var row = world.RunState.Row(world.ParkedStateId!.Value);
        world.Release.SetResult();

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().Be(RunLifecycleWorld.ReplicaId);
        world.Audit.Events.Should().ContainSingle("the resume that did happen is still recorded");
    }
    [Fact]
    public async Task A_late_resume_of_a_wait_the_run_has_since_walked_past_is_refused_though_the_run_is_parked_again()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        var parked = world.Parked();

        (await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended);

        var late = await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience);

        late.Should().Be(WorkflowResumeOutcome.HeldElsewhere);
        world.TickEntries.Should().Be(1, "the stretch between the two waits ran once, on the replica that took over");
        suspensions.Suspended.Should().ContainSingle("the run parks once at the next wait, not once per resume");
        world.RunState.Row(world.ParkedStateId!.Value).CurrentBlockPath.Should().Be(RunLifecycleWorld.HoldNodeId);
    }

    [Theory]
    [InlineData(WorkflowRunStateStatus.Completed, WorkflowRunRecoverySweep.ReplayedReason, WorkflowExecutionStatus.Cancelled)]
    [InlineData(WorkflowRunStateStatus.Failed, WorkflowRunRecoverySweep.ResumeFailedReason, WorkflowExecutionStatus.Failed)]
    [InlineData(WorkflowRunStateStatus.Cancelled, "Workflow execution was cancelled.", WorkflowExecutionStatus.Cancelled)]
    public async Task A_resume_of_a_run_whose_row_has_ended_closes_the_runs_waits_so_no_look_offers_them_again(
        WorkflowRunStateStatus ended, string reason, WorkflowExecutionStatus entry)
    {
        var world = new RunLifecycleWorld();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        var parked = world.Parked();
        world.RunState.Put(world.ParkedStateId!.Value, ended, null, reason: reason);

        (await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.HeldElsewhere);

        suspensions.CancelledExecutions.Should().ContainSingle().Which.Should().Be(parked.ExecutionLogId);
        world.Entered.Task.IsCompleted.Should().BeFalse();
        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(entry, "a row a sweep completed is a run replayed under another entry; it did not "
            + "finish here");
        log.ErrorMessage.Should().StartWith("run_state_ended");
    }

    [Fact]
    public async Task A_retake_of_a_resumed_segment_that_finished_but_was_not_recorded_records_it_succeeded()
    {
        var world = new RunLifecycleWorld();
        var parked = world.Parked();
        world.Release.SetResult();
        world.Journal.WhileSavingDetails = () => throw new InvalidOperationException("the database went away");

        (await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Completed);

        world.Journal.Logs[parked.ExecutionLogId] = new WorkflowExecutionLog(parked.ExecutionLogId, null,
            world.WorkflowId, world.Version.Id, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        world.Journal.WhileSavingDetails = null;

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(WorkflowExecutionStatus.Succeeded, "the run finished under this very entry");
        log.ErrorMessage.Should().BeNull();
    }

    [Theory]
    [InlineData(RunLifecycleWorld.ReplicaId)]
    [InlineData("replica-2")]
    public async Task A_resume_that_finds_the_row_running_at_its_own_wait_is_deferred_for_the_park_still_landing(string owner)
    {
        var world = new RunLifecycleWorld();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        var parked = world.Parked();
        world.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, owner, parked.BlockId);

        (await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Deferred);

        world.Entered.Task.IsCompleted.Should().BeFalse();
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Running);
        suspensions.CancelledExecutions.Should().BeEmpty();
        world.RunState.Row(world.ParkedStateId!.Value).OwnedByReplicaId.Should().Be(owner);
        world.RunState.Completions.Should().BeEmpty();
        world.Audit.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task A_predicate_resume_that_finds_the_row_running_at_its_own_wait_is_held_not_deferred()
    {
        var world = new RunLifecycleWorld();
        var template = world.Parked();
        var parked = new ResumeContext
        {
            SuspensionId = template.SuspensionId, ExecutionLogId = template.ExecutionLogId, WorkflowId = template.WorkflowId,
            BlockId = template.BlockId, Kind = WorkflowSuspensionKind.Predicate, OutcomeJson = template.OutcomeJson,
            StatePayloadJson = template.StatePayloadJson, SuspendedAtUtc = template.SuspendedAtUtc
        };
        world.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId, parked.BlockId);

        (await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.HeldElsewhere);

        world.Entered.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_resume_of_a_run_still_parked_or_running_elsewhere_leaves_its_waits_alone()
    {
        var world = new RunLifecycleWorld();
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        var parked = world.Parked();
        world.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, "replica-2", RunLifecycleWorld.HoldNodeId);

        (await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.HeldElsewhere);

        suspensions.CancelledExecutions.Should().BeEmpty("the run is somebody's, and its waits are its own");
    }
}
