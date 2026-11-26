using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepResumeTests
{
    private const int Long = 600_000;

    [Fact]
    public async Task A_parked_sleep_wakes_after_its_time_and_the_graph_walks_on()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.PassTime(TimeSpan.FromHours(1));
        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(1);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Blocks.AfterInputs.Should().ContainSingle().Which["slept"].Should().Be(Long,
            "downstream reads the sleep's DurationMs as the long an in-memory sleep answers");
        await DurableSleepWorld.EventuallyAsync(
            () => world.Observer.Completed.Any(c => c.NodeId == DurableSleepWorld.SleepNode), "the sleep's completion notice");
        world.Observer.Completed.Single(c => c.NodeId == DurableSleepWorld.SleepNode).Outputs
            .Should().BeEquivalentTo(new Dictionary<string, object?> { ["Completed"] = true, ["DurationMs"] = Long });
        world.Audit.Kinds.Should().Contain(WorkflowAuditEventKinds.ResumedBySleep);
        world.Metrics.SuspensionsResolved.Should().Equal(("Sleep", WorkflowRunStatusLabels.Succeeded));
        world.Journal.Steps.Should().Contain(step => step.NodeId == DurableSleepWorld.SleepNode
            && step.Status == WorkflowExecutionStepStatus.Succeeded);
    }

    [Fact]
    public async Task A_sleep_of_more_than_int32_milliseconds_parks_and_wakes_reporting_a_long()
    {
        const long ThirtyFiveDays = 3_000_000_000L;
        var world = World();
        var workflowId = world.AddWorkflow(ThirtyFiveDays);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.PassTime(TimeSpan.FromDays(40));
        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(1);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().ContainSingle().Which["slept"].Should().BeOfType<long>().Which.Should().Be(ThirtyFiveDays);
    }

    [Fact]
    public async Task A_sleep_of_minus_1e14_milliseconds_fails_the_node_through_its_policy()
    {
        var world = World();
        var workflowId = world.AddWorkflow(-100_000_000_000_000L);
        using var engine = world.Engine();

        var act = () => engine.ExecuteAsync(workflowId, null, null);

        (await act.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which.Message.Should().Contain("greater than or equal to 0");
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_parked_sleep_is_not_woken_before_its_time()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.PassTime(TimeSpan.FromMinutes(1));

        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(0);
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sleep_parked_by_one_engine_is_finished_by_another()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using (var dying = world.Engine("replica-1"))
            await dying.ExecuteAsync(workflowId, null, null);

        using var surviving = world.Engine("replica-2");
        world.PassTime(TimeSpan.FromHours(1));
        (await world.Waker(world.Resumer(surviving, "replica-2")).WakeAndWalkAsync()).Should().Be(1);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_resume_that_loses_its_flip_to_a_cancelled_run_runs_nothing()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var stateRow = world.RunState.Rows.Keys.Single();
        world.RunState.Force(stateRow, WorkflowRunStateStatus.Cancelled);

        world.PassTime(TimeSpan.FromHours(1));
        var context = (await world.Suspensions.TryResolveAsync(world.Suspensions.SingleRow.Id,
            WorkflowSuspensionStatus.Resumed, WorkflowDurableSleep.OutcomeJson("{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow, default))!;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere, "putting the row back would take it again for ever");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.RunState.Rows[stateRow].Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task An_abandon_whose_journal_save_fails_still_walks_nothing()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Cancelled);
        var context = await TakeAsync(world);

        world.Journal.FailSaves = 1;
        var outcome = await world.Resumer(engine).ResumeFromSuspensionAsync(context, default);

        outcome.Should().Be(WorkflowResumeOutcome.HeldElsewhere, "the run ended; a failed save does not make it resumable");
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stop_landing_on_the_flip_never_leaves_a_parked_run_whose_row_says_running()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var stateRow = world.RunState.Rows.Keys.Single();
        var context = await TakeAsync(world);
        using var stop = new CancellationTokenSource();
        world.RunState.OnTransition = t =>
        {
            if (t.FromStatus == WorkflowRunStateStatus.Suspended)
                stop.Cancel();
        };

        var outcome = await world.Resumer(engine).ResumeFromSuspensionAsync(context, stop.Token);

        var row = world.RunState.Rows[stateRow].Status;
        row.Should().NotBe(WorkflowRunStateStatus.Running);
        (outcome == WorkflowResumeOutcome.Deferred).Should().Be(row == WorkflowRunStateStatus.Suspended,
            "a run handed back to the table is parked, and one that is not has ended");
    }

    private static async Task<ResumeContext> TakeAsync(DurableSleepWorld world) =>
        (await world.Suspensions.TryResolveAsync(world.Suspensions.SingleRow.Id,
            WorkflowSuspensionStatus.Resumed, WorkflowDurableSleep.OutcomeJson("{}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow, default))!;

    [Fact]
    public async Task A_second_long_sleep_after_a_resume_parks_again()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, secondSleepMs: Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.PassTime(TimeSpan.FromHours(1));
        await world.Waker(world.Resumer(engine)).WakeAndWalkAsync();

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running);
        world.Suspensions.Rows.Should().Contain(r => r.BlockId == "nap2" && r.Status == WorkflowSuspensionStatus.Suspended);
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Suspended);

        world.PassTime(TimeSpan.FromHours(2));
        await world.Waker(world.Resumer(engine)).WakeAndWalkAsync();

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_sleep_parked_while_single_resumes_without_a_slot_once_its_workflow_became_queued()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var workflow = (await world.Store.LoadAsync(workflowId, default))!;
        workflow.ConfigureConcurrency(WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, DateTimeOffset.UtcNow);
        var slot = (await engine.Coordinators.For(workflow).AcquireAsync(default)).Lease!;

        world.PassTime(TimeSpan.FromHours(1));
        var moved = await world.Waker(world.Resumer(engine)).WakeAndWalkAsync();

        moved.Should().Be(0, "a resume asks for a place and never waits for one; the row goes back for the next pass");
        await slot.DisposeAsync();
        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(1, "the place is free now");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    private static DurableSleepWorld World()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Suspensions;
        return world;
    }
}
