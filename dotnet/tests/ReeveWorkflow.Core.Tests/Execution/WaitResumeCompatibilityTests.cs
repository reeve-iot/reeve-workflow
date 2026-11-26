using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WaitResumeCompatibilityTests
{
    [Theory]
    [InlineData(SuspendFailure.BeforeWrite)]
    [InlineData(SuspendFailure.AfterWrite)]
    public async Task A_trigger_wait_whose_state_row_swap_throws_walks_on(SuspendFailure failure)
    {
        var (world, engine, workflowId) = await ParkedOnATriggerAsync();
        using var _ = engine;
        world.RunState.FailNextFlip = failure;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world), default))
            .Should().Be(WorkflowResumeOutcome.Resumed, "never deferred: a host that ignores Deferred would strand the row");

        world.Journal.Single(workflowId).Status.Should().Be(failure == SuspendFailure.AfterWrite
            ? WorkflowExecutionStatus.Succeeded
            : WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_trigger_wait_whose_state_row_cannot_be_read_resumes_anyway()
    {
        var (world, engine, workflowId) = await ParkedOnATriggerAsync();
        using var _ = engine;
        world.RunState.FailGet = true;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world), default))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_trigger_wait_whose_swap_is_lost_and_whose_row_cannot_be_read_walks_on()
    {
        var (world, engine, workflowId) = await ParkedOnATriggerAsync();
        using var _ = engine;
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Running);
        world.RunState.FailGet = true;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world), default))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Theory]
    [InlineData(SuspendFailure.None)]
    [InlineData(SuspendFailure.BeforeWrite)]
    public async Task A_trigger_wait_whose_state_row_is_running_is_held_rather_than_walked(SuspendFailure swap)
    {
        var (world, engine, workflowId) = await ParkedOnATriggerAsync();
        using var _ = engine;
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Running);
        world.RunState.FailNextFlip = swap;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world), default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere, "held is not deferred: nothing puts a trigger's wait back");

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running,
            "nothing was written about a run this resume never entered");
        world.Blocks.AfterInputs.Should().BeEmpty("the segment belongs to whoever is running the row");
    }

    [Fact]
    public async Task A_trigger_wait_whose_swap_is_refused_by_a_row_that_moved_is_held()
    {
        var (world, engine, _) = await ParkedOnATriggerAsync();
        using var __ = engine;
        world.RunState.AfterRead = _ =>
        {
            world.RunState.AfterRead = null;
            world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Running);
            return Task.CompletedTask;
        };

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world), default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere, "a swap a row still there refused says somebody has the run");

        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trigger_wait_parks_and_parks_again_without_a_token_in_its_state_row()
    {
        var (world, engine, _) = await ParkedOnATriggerAsync(twoDoors: true);
        using var __ = engine;
        world.RunState.Rows.Values.Single().WaitForEventKey.Should().BeNull("hosts show this column, as Reeve's executions API does");

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world), default))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Suspended);
        world.RunState.Rows.Values.Single().WaitForEventKey.Should().BeNull();
    }

    [Fact]
    public async Task A_trigger_wait_whose_state_row_a_host_ended_is_abandoned_without_a_resolved_metric()
    {
        var (world, engine, workflowId) = await ParkedOnATriggerAsync();
        using var _ = engine;
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Cancelled);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world), default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere, "a run whose row ended is somebody else's or nobody's");

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.Metrics.SuspensionsResolved.Should().BeEmpty();
    }

    internal static async Task<(DurableSleepWorld World, WorkflowEngine Engine, Guid WorkflowId)> ParkedOnATriggerAsync(
        bool twoDoors = false)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.Extra = id => id == "test.wait"
            ? new LambdaFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitForTrigger, "{\"TriggerIds\":[]}", DateTimeOffset.UtcNow.AddHours(1))))
            : null;
        var workflowId = world.AddRaw(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "door", blockTypeId = "test.wait", position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
            }.Concat(twoDoors ? [new { id = "door2", blockTypeId = "test.wait", position = new { x = 0, y = 0 }, configuration = new { } }] : Array.Empty<object>()).ToArray(),
            edges = twoDoors
                ? new object[]
                {
                    new { id = "a", sourceNodeId = "start", targetNodeId = "door", mappings = new { } },
                    new { id = "b", sourceNodeId = "door", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } },
                    new { id = "c", sourceNodeId = DurableSleepWorld.AfterNode, targetNodeId = "door2", mappings = new { } }
                }
                : new object[]
                {
                    new { id = "a", sourceNodeId = "start", targetNodeId = "door", mappings = new { } },
                    new { id = "b", sourceNodeId = "door", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
                }
        }));
        var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Suspended);
        return (world, engine, workflowId);
    }

    internal static async Task<ResumeContext> TakeAsync(DurableSleepWorld world)
    {
        var row = world.Suspensions.Rows.Single(r => r.Status == WorkflowSuspensionStatus.Suspended);
        return (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.TimedOut,
            """{"outcome":"timedOut"}""", DateTimeOffset.UtcNow, default))!;
    }
}
