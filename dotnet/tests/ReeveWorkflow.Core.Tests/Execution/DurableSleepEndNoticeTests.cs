using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepEndNoticeTests
{
    private const int Long = 600_000;

    [Fact]
    public async Task A_sleeper_a_restart_cancels_is_announced_as_ended()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);

        await engine.ExecuteAsync(workflowId, null, null);

        await EndedAsync(world, sleeper.Id);
    }

    [Fact]
    public async Task A_sleeper_whose_resume_finds_it_superseded_is_announced_as_ended()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart, holdFirst: true);
        using var engine = world.Engine();
        world.Blocks.Release.SetResult();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.Seam.ListNothing = true;
        world.Blocks.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Blocks.HoldReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newer = Task.Run(() => engine.ExecuteAsync(workflowId, null, null));
        await world.Blocks.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world, sleeper.Id), default))
            .Should().Be(WorkflowResumeOutcome.Abandoned);
        world.Blocks.Release.SetResult();
        await newer;

        await EndedAsync(world, sleeper.Id);
    }

    [Fact]
    public async Task A_sleeper_whose_state_row_a_host_ended_is_abandoned_without_a_second_notice()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Cancelled);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world, sleeper.Id), default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere, "a row somebody else ended is read before the swap");
        await Task.Delay(100);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.Observer.Failed.Should().BeEmpty("whoever ends the state row announces the node; a host that does so itself, announces it");
    }

    [Fact]
    public async Task A_sleeper_whose_workflow_is_gone_when_it_wakes_is_announced_as_ended_and_its_state_row_failed()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var context = await TakeAsync(world, sleeper.Id);
        world.Store.Remove(workflowId);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);

        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Failed);
        await EndedAsync(world, sleeper.Id);
    }

    [Fact]
    public async Task A_sleeper_whose_payload_cannot_be_read_when_it_wakes_is_announced_as_ended_and_its_state_row_failed()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var taken = await TakeAsync(world, sleeper.Id);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(taken.StatePayloadJson)!;
        payload["PreviousOutputsJson"] = "{not json";
        var context = new ResumeContext
        {
            SuspensionId = taken.SuspensionId, ExecutionLogId = taken.ExecutionLogId, WorkflowId = taken.WorkflowId,
            BlockId = taken.BlockId, Kind = taken.Kind, OutcomeJson = taken.OutcomeJson,
            StatePayloadJson = payload.ToJsonString(), SuspendedAtUtc = taken.SuspendedAtUtc
        };

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);

        world.Journal.Runs[sleeper.Id].Status.Should().Be(WorkflowExecutionStatus.Failed);
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Failed);
        await EndedAsync(world, sleeper.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sleeper_whose_payload_will_not_parse_ends_the_state_row_its_payload_still_names(bool namesIt)
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var taken = await TakeAsync(world, sleeper.Id);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(taken.StatePayloadJson)!;
        payload["PinnedVersionsJson"] = 5;
        var broken = namesIt ? payload.ToJsonString() : "{not json";
        var context = new ResumeContext
        {
            SuspensionId = taken.SuspensionId, ExecutionLogId = taken.ExecutionLogId, WorkflowId = taken.WorkflowId,
            BlockId = taken.BlockId, Kind = taken.Kind, OutcomeJson = taken.OutcomeJson,
            StatePayloadJson = broken, SuspendedAtUtc = taken.SuspendedAtUtc
        };

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);

        world.Journal.Runs[sleeper.Id].Status.Should().Be(WorkflowExecutionStatus.Failed);
        world.RunState.Rows.Values.Single().Status.Should().Be(
            namesIt ? WorkflowRunStateStatus.Failed : WorkflowRunStateStatus.Suspended);
        await EndedAsync(world, sleeper.Id);
    }

    [Fact]
    public async Task A_sleeper_whose_row_a_restart_cannot_cancel_after_ending_its_journal_is_announced_as_ended()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.Seam.FailCancelParkedSleep = true;

        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Runs[sleeper.Id].Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        await EndedAsync(world, sleeper.Id);
    }

    private static DurableSleepWorld World()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        return world;
    }

    private static Task EndedAsync(DurableSleepWorld world, Guid executionId) =>
        DurableSleepWorld.EventuallyAsync(
            () => world.Observer.Failed.Any(f => f.ExecutionId == executionId && f.NodeId == DurableSleepWorld.SleepNode),
            "the parked sleep's node to be announced as ended");

    private static async Task<ResumeContext> TakeAsync(DurableSleepWorld world, Guid executionLogId)
    {
        var row = world.Suspensions.Rows.Single(r => r.ExecutionLogId == executionLogId
            && r.Status == WorkflowSuspensionStatus.Suspended);
        return (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc),
            world.Suspensions.Now, default))!;
    }
}
