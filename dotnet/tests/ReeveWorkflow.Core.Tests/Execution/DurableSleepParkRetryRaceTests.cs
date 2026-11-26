using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepParkRetryRaceTests
{
    private static string Definition(int sleepMs) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = DurableSleepWorld.SleepNode, blockTypeId = DurableSleepWorld.SleepType,
                  configuration = new Dictionary<string, object?> { ["DurationMs"] = sleepMs }, position = new { x = 0, y = 0 } },
            new { id = "hold", blockTypeId = DurableSleepWorld.HoldType, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "e1", sourceNodeId = "start", targetNodeId = DurableSleepWorld.SleepNode, mappings = new { } },
            new { id = "e2", sourceNodeId = DurableSleepWorld.SleepNode, targetNodeId = "hold", mappings = new { } },
            new { id = "e3", sourceNodeId = "hold", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_park_whose_row_an_unrelated_writer_moved_is_not_judged_taken(bool keepsNoFence)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        world.RunState.KeepsNoFence = keepsNoFence;
        var workflowId = world.AddRaw(Definition(30));
        using var a = world.Engine("replica-1");

        world.RunState.ParkFailures.Enqueue(SuspendFailure.BeforeWrite);
        world.RunState.BeforeParkThrows = () =>
        {
            world.RunState.BeforeParkThrows = null;
            world.RunState.Bump(world.RunState.Rows.Keys.Single());
            return Task.CompletedTask;
        };

        await a.ExecuteAsync(workflowId, null, null);

        world.PassTime(TimeSpan.FromMinutes(10));
        var waker = world.Waker(world.Resumer(a, "replica-1"));
        var moved = await waker.TakeOnceAsync(default);
        world.Blocks.Release.TrySetResult();
        try { await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        await DurableSleepWorld.EventuallyAsync(
            () => world.Journal.Single(workflowId).Status != WorkflowExecutionStatus.Running, "the run to end");

        var journal = world.Journal.Single(workflowId);
        var state = world.RunState.Rows.Values.Single();
        journal.Status.Should().Be(WorkflowExecutionStatus.Succeeded,
            $"the run is nobody else's; waker moved {moved}, state {state.Status}/{state.WaitForEventKey}, "
            + $"parked-sleep listing = {await world.Suspensions.HasParkedSleepAsync(workflowId, default)}");
        world.Blocks.AfterInputs.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_park_whose_row_a_peer_reclaimed_ends_the_run_as_ownership_lost(bool keepsNoFence)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        world.RunState.KeepsNoFence = keepsNoFence;
        var workflowId = world.AddRaw(Definition(30));
        using var a = world.Engine("replica-1");

        world.RunState.ParkFailures.Enqueue(SuspendFailure.BeforeWrite);
        world.RunState.BeforeParkThrows = () =>
        {
            world.RunState.BeforeParkThrows = null;
            world.RunState.Bump(world.RunState.Rows.Keys.Single(), "replica-2");
            return Task.CompletedTask;
        };

        var run = async () => await a.ExecuteAsync(workflowId, null, null);

        await run.Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running, "the entry is the peer's now: it carries the run on under it, or closes it");
        world.Blocks.AfterInputs.Should().BeEmpty("the segment is the peer's to run");
        (await world.Suspensions.HasParkedSleepAsync(workflowId, default))
            .Should().BeFalse("a row nothing will wake is withdrawn, so Single is not wedged");
    }

    [Fact]
    public async Task A_park_retry_does_not_land_on_a_row_an_unrelated_writer_moved_since_the_read()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddRaw(Definition(30));
        using var a = world.Engine("replica-1");

        world.RunState.ParkFailures.Enqueue(SuspendFailure.BeforeWrite);
        world.RunState.ParkFailures.Enqueue(SuspendFailure.BeforeWrite);
        world.RunState.AfterRead = _ =>
        {
            world.RunState.Bump(world.RunState.Rows.Keys.Single());
            return Task.CompletedTask;
        };

        world.Blocks.Release.TrySetResult();
        await a.ExecuteAsync(workflowId, null, null);

        var state = world.RunState.Rows.Values.Single();
        state.Status.Should().NotBe(WorkflowRunStateStatus.Suspended,
            "a retry that could not prove the row is the one it read parks nothing");
        (await world.Suspensions.HasParkedSleepAsync(workflowId, default))
            .Should().BeFalse("the withdrawn park leaves no row for a waker to wake");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().ContainSingle("the sleep ran on in this process instead");
    }

    [Fact]
    public async Task A_take_landing_between_the_read_and_the_retry_does_not_park_over_the_resumed_walk()
    {
        const bool keepsNoFence = false;
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        world.RunState.KeepsNoFence = keepsNoFence;
        var workflowId = world.AddRaw(Definition(30));
        using var a = world.Engine("replica-1");
        using var b = world.Engine("replica-2");
        var peerWaker = world.Waker(world.Resumer(b, "replica-1"));

        world.RunState.ParkFailures.Enqueue(SuspendFailure.AfterWrite);
        world.RunState.AfterRead = async ordinal =>
        {
            if (ordinal != 1) return;
            world.RunState.AfterRead = null;
            world.PassTime(TimeSpan.FromMinutes(10));
            (await peerWaker.TakeOnceAsync(default)).Should().Be(1, "the peer claims the parked row and begins its walk");
            await world.Blocks.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Running);
        };

        await a.ExecuteAsync(workflowId, null, null);
        var afterRetry = world.RunState.Rows.Values.Single();
        world.Blocks.Release.TrySetResult();
        try { await peerWaker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        await Task.Delay(300);

        var journal = world.Journal.Single(workflowId);
        var state = world.RunState.Rows.Values.Single();
        var rows = string.Join(", ", world.Suspensions.Rows.Select(r => $"{r.Kind}:{r.Status}"));
        afterRetry.Status.Should().Be(WorkflowRunStateStatus.Running,
            "the retry required the version the read saw, so it could not park the row back under the peer's walk");
        journal.Status.Should().Be(WorkflowExecutionStatus.Succeeded,
            $"state after retry = {afterRetry.Status}/{afterRetry.WaitForEventKey}, now {state.Status}/{state.WaitForEventKey}, "
            + $"rows [{rows}], journal error = {journal.ErrorMessage}, after ran {world.Blocks.AfterInputs.Count}x");
        world.Blocks.AfterInputs.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_park_retry_leaves_alone_a_state_row_a_peer_has_already_resumed(bool keepsNoFence)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        world.RunState.KeepsNoFence = keepsNoFence;
        var workflowId = world.AddRaw(Definition(30));
        using var a = world.Engine("replica-1");
        using var b = world.Engine("replica-2");
        var peerWaker = world.Waker(world.Resumer(b, "replica-2"));

        world.RunState.ParkFailures.Enqueue(SuspendFailure.AfterWrite);
        world.RunState.AfterParkLanded = async () =>
        {
            world.RunState.AfterParkLanded = null;
            world.PassTime(TimeSpan.FromMinutes(10));
            (await peerWaker.TakeOnceAsync(default)).Should().Be(1, "the peer claims the parked row and begins its walk");
            await world.Blocks.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Running);
            world.RunState.Rows.Values.Single().WaitForEventKey.Should().StartWith("reeveworkflow:resumed:");
        };

        await a.ExecuteAsync(workflowId, null, null);

        var afterRetry = world.RunState.Rows.Values.Single();
        world.Blocks.Release.TrySetResult();
        await DurableSleepWorld.EventuallyAsync(
            () => world.Journal.Single(workflowId).Status != WorkflowExecutionStatus.Running, "the run to end");

        var journal = world.Journal.Single(workflowId);
        var state = world.RunState.Rows.Values.Single();
        var rows = string.Join(", ", world.Suspensions.Rows.Select(r => $"{r.Kind}:{r.Status}"));
        journal.Status.Should().Be(WorkflowExecutionStatus.Succeeded,
            $"the segment after the sleep ran on the peer; state row after retry = {afterRetry.Status}/{afterRetry.WaitForEventKey}, now {state.Status}/{state.WaitForEventKey}, suspension rows = [{rows}], journal error = {journal.ErrorMessage}");
        world.Blocks.AfterInputs.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_park_whose_row_a_peer_took_is_not_withdrawn_and_its_segment_runs_once(bool keepsNoFence)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        world.RunState.KeepsNoFence = keepsNoFence;
        var workflowId = world.AddRaw(Definition(30));
        using var a = world.Engine("replica-1");
        using var b = world.Engine("replica-2");
        var peerWaker = world.Waker(world.Resumer(b, "replica-1"));

        world.RunState.ParkFailures.Enqueue(SuspendFailure.AfterWrite);
        world.RunState.ParkFailures.Enqueue(SuspendFailure.BeforeWrite);
        world.RunState.AfterParkLanded = async () =>
        {
            world.RunState.AfterParkLanded = null;
            world.PassTime(TimeSpan.FromMinutes(10));
            (await peerWaker.TakeOnceAsync(default)).Should().Be(1);
            await world.Blocks.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var run = a.ExecuteAsync(workflowId, null, null);
        await Task.Delay(300);
        world.Blocks.Release.TrySetResult();
        await run;
        await DurableSleepWorld.EventuallyAsync(
            () => world.Journal.Single(workflowId).Status != WorkflowExecutionStatus.Running, "the run to end");
        await Task.Delay(200);

        world.Blocks.AfterInputs.Should().ContainSingle("the segment after the sleep must run once");
    }
}
