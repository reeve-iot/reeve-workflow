using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowCheckpointWriteFailureTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_block_that_may_not_run_twice_is_not_run_when_the_write_before_it_did_not_land()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        using var engine = world.Engine(handOverOnStop: false, recoveryReenters: true);

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<Exception>();

        world.HoldEntries.Should().Be(0,
            "the row still names the tick, and a replica that died inside the hold would have it repeated unasked");
        var entry = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        entry.Status.Should().Be(WorkflowExecutionStatus.Failed);
        entry.ErrorMessage.Should().Contain("checkpoint_not_written");
    }

    [Fact]
    public async Task A_block_inside_a_container_that_may_not_run_twice_waits_for_its_write_too()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundTickThenHold)
        {
            LoopIterations = 2,
            TickIdempotent = true,
            HoldIdempotent = false
        };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        using var engine = world.Engine(handOverOnStop: false, recoveryReenters: true);

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<Exception>();

        world.HoldEntries.Should().Be(0, "the position still names the tick before it");
        world.Journal.Logs.Values.Should().ContainSingle()
            .Which.ErrorMessage.Should().Contain("checkpoint_not_written");
    }

    [Fact]
    public async Task A_lost_write_inside_a_container_is_not_handed_to_the_container_s_error_policy()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundTickThenHoldContinuing)
        {
            LoopIterations = 1,
            TickIdempotent = true,
            HoldIdempotent = false
        };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        using var engine = world.Engine(handOverOnStop: false, recoveryReenters: true);

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<Exception>();

        world.HoldEntries.Should().Be(0);
        world.Journal.Logs.Values.Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowExecutionStatus.Failed, "a run that skipped the block did not succeed");
    }

    [Fact]
    public async Task A_block_that_may_run_twice_runs_although_the_write_before_it_did_not_land()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        using var engine = world.Engine(recoveryReenters: true);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1, "a lost write before a block a recovery may repeat costs nothing");
    }

    [Fact]
    public async Task On_a_host_with_no_sweep_a_lost_write_costs_nothing_because_nothing_re_enters_the_run()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        using var engine = world.Engine(recoveryReenters: false);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_host_that_hands_its_runs_to_a_sweep_that_only_replays_them_does_not_refuse_the_block()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        using var engine = world.Engine(handOverOnStop: true, recoveryReenters: false);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_resumed_segment_does_not_run_a_block_that_may_not_run_twice_when_the_write_before_it_did_not_land()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        var parked = world.Parked();

        await world.Resumer(handOverOnStop: false, recoveryReenters: true)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldEntries.Should().Be(0, "a recovery would re-enter the resumed segment at the tick and repeat the hold unasked");
        var entry = world.Journal.Logs[parked.ExecutionLogId];
        entry.Status.Should().Be(WorkflowExecutionStatus.Failed);
        entry.ErrorMessage.Should().Contain("checkpoint_not_written");
    }

    [Fact]
    public async Task A_resumed_segment_on_a_host_whose_sweep_does_not_re_enter_runs_the_block()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        var parked = world.Parked();

        await world.Resumer(handOverOnStop: true, recoveryReenters: false)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_run_another_run_dispatched_is_not_refused_because_a_recovery_ends_it_rather_than_re_entering_it()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        using var engine = world.Engine(recoveryReenters: true);

        await engine.ExecuteAsync(world.WorkflowId, null, null, parentExecutionId: Guid.NewGuid()).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_blip_the_write_outlasts_does_not_refuse_the_block()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        world.RunState.FailWritesBefore = RunLifecycleWorld.HoldNodeId;
        world.RunState.FailWritesBeforeTimes = 2;
        using var engine = world.Engine(recoveryReenters: true);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1, "the third try landed");
        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_write_that_did_not_land_is_not_counted_as_the_last_one()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        world.RunState.FailWritesBefore = "tick";
        using var engine = world.Engine(writeMode: RunStateWriteMode.Heartbeat(TimeSpan.FromHours(1)));

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);

        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        row.CurrentBlockPath.Should().Be(RunLifecycleWorld.HoldNodeId);
        WorkflowRunCheckpoint.Read(row.VariablesJson)!.NodeInFlight.Should().Be(RunLifecycleWorld.HoldNodeId);

        world.Release.TrySetResult();
        await run.WaitAsync(Patience);
    }
}
