using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepParkFenceTests
{
    private const int Long = 600_000;
    private const int TwoHours = 2 * 60 * 60 * 1000;

    [Theory]
    [InlineData(WorkflowMode.Single)]
    [InlineData(WorkflowMode.Restart)]
    public async Task A_run_that_woke_and_parked_again_does_not_have_its_first_sleep_put_back(WorkflowMode mode)
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, mode, secondSleepMs: TwoHours);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.PassTime(TimeSpan.FromMinutes(11));
        (await Sweeping(world, engine).WakeAndWalkAsync()).Should().Be(1);
        world.Blocks.AfterInputs.Should().HaveCount(1);

        world.PassTime(TimeSpan.FromMinutes(31));
        await Sweeping(world, engine).WakeAndWalkAsync();

        world.Blocks.AfterInputs.Should().HaveCount(1, "the run is parked on its second sleep");
        world.Suspensions.Rows.Where(r => r.BlockId == DurableSleepWorld.SleepNode).Select(r => r.Status)
            .Should().Equal(WorkflowSuspensionStatus.Resumed);
        world.Suspensions.Rows.Count(r => r.Status == WorkflowSuspensionStatus.Suspended).Should().Be(1);
    }

    [Fact]
    public async Task Over_the_second_sleep_every_segment_runs_once()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single, secondSleepMs: TwoHours);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var start = DateTimeOffset.UtcNow;

        for (var minute = 11; minute <= 161; minute += 2)
        {
            world.Suspensions.Now = start + TimeSpan.FromMinutes(minute);
            await Sweeping(world, engine).WakeAndWalkAsync();
        }

        world.Blocks.AfterInputs.Should().HaveCount(2, "after once, after2 once");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_run_that_woke_and_parked_on_a_trigger_does_not_run_its_middle_twice()
    {
        var world = World();
        world.Blocks.Extra = id => id == "test.wait"
            ? new LambdaFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitForTrigger, "{\"TriggerIds\":[]}", DateTimeOffset.UtcNow.AddDays(1))))
            : null;
        var workflowId = world.AddRaw(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = DurableSleepWorld.SleepNode, blockTypeId = DurableSleepWorld.SleepType, configuration = new { DurationMs = 120_000 }, position = new { x = 0, y = 0 } },
                new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "door", blockTypeId = "test.wait", position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[]
            {
                new { id = "a", sourceNodeId = "start", targetNodeId = DurableSleepWorld.SleepNode, mappings = new { } },
                new { id = "b", sourceNodeId = DurableSleepWorld.SleepNode, targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } },
                new { id = "c", sourceNodeId = DurableSleepWorld.AfterNode, targetNodeId = "door", mappings = new { } }
            }
        }));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.PassTime(TimeSpan.FromMinutes(3));
        (await Sweeping(world, engine).WakeAndWalkAsync()).Should().Be(1);
        world.PassTime(TimeSpan.FromMinutes(28));
        await Sweeping(world, engine).WakeAndWalkAsync();

        world.Blocks.AfterInputs.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_take_that_waited_while_a_peer_resumed_the_run_re_enters_nothing()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single, secondSleepMs: TwoHours);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromMinutes(11));
        var late = await TakeAsync(world);

        world.PassTime(TimeSpan.FromMinutes(31));
        (await Sweeping(world, engine).WakeAndWalkAsync()).Should().Be(1);
        world.Blocks.AfterInputs.Should().HaveCount(1);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(late, default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere, "the row is parked at the next wait, not at this one");

        world.Blocks.AfterInputs.Should().HaveCount(1, "the late take is not this run's park any more");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running);
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Suspended);
    }

    [Fact]
    public async Task A_resume_whose_state_row_is_running_elsewhere_does_not_walk()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Running);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world), default))
            .Should().Be(WorkflowResumeOutcome.Deferred, "a park still landing reads the same; the next pass decides");

        world.Blocks.AfterInputs.Should().BeEmpty("a row running without this park's token is another walk's");
        world.Audit.Events.Should().NotContain(e => e.Kind == WorkflowAuditEventKinds.ResumedBySleep, "nothing was resumed");
    }

    [Theory]
    [InlineData(SuspendFailure.None)]
    [InlineData(SuspendFailure.AfterWrite)]
    public async Task A_host_store_that_keeps_no_fence_still_resumes_a_sleep_once(SuspendFailure swap)
    {
        var world = World();
        world.RunState.KeepsNoFence = true;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromMinutes(11));
        world.RunState.FailNextFlip = swap;

        (await Sweeping(world, engine).WakeAndWalkAsync()).Should().Be(1, "a swap that landed is this take's, even unread");

        world.Blocks.AfterInputs.Should().HaveCount(1);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_host_store_that_keeps_no_fence_defers_a_sleep_whose_swap_never_landed()
    {
        var world = World();
        world.RunState.KeepsNoFence = true;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromMinutes(11));
        world.RunState.FailNextFlip = SuspendFailure.BeforeWrite;

        (await Sweeping(world, engine).WakeAndWalkAsync()).Should().Be(0);
        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended, "put back for the next pass");

        (await Sweeping(world, engine).WakeAndWalkAsync()).Should().Be(1);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pass_stopped_while_its_lost_swap_is_read_back_does_not_put_back_the_row_it_took(bool keepsNoFence)
    {
        var world = World();
        world.RunState.KeepsNoFence = keepsNoFence;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromMinutes(11));
        using var pass = new CancellationTokenSource();
        world.RunState.FailNextFlip = SuspendFailure.AfterWrite;
        world.RunState.AfterFlipLanded = () => pass.CancelAsync();
        var waker = Sweeping(world, engine);

        try { await waker.WakeOnceAsync(pass.Token); }
        catch (OperationCanceledException) { }
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Resumed, "the row the swap took is not put back");
        world.Blocks.AfterInputs.Should().BeEmpty("the stop that ended the pass ends the walk it began");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_run_resumed_as_its_pass_stops_is_still_audited()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromMinutes(11));
        using var pass = new CancellationTokenSource();
        world.RunState.OnTransition = request =>
        {
            if (request.FromStatus == WorkflowRunStateStatus.Suspended && request.ToStatus == WorkflowRunStateStatus.Running)
                pass.Cancel();
        };
        var waker = Sweeping(world, engine);

        try { await waker.WakeOnceAsync(pass.Token); }
        catch (OperationCanceledException) { }
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Cancelled,
            "the walk stops with the host that began it, and saves itself");
        world.Audit.Kinds.Should().Contain(WorkflowAuditEventKinds.ResumedBySleep, "the run the swap took did resume");
    }

    private static DurableSleepWorld World()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        return world;
    }

    private static WorkflowSuspensionWaker Sweeping(DurableSleepWorld world, WorkflowEngine engine) =>
        new(world.Suspensions, world.Resumer(engine), NullLogger.Instance, ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, RetakeAbandonedTakesAfter = TimeSpan.FromMinutes(15) });

    private static async Task<ResumeContext> TakeAsync(DurableSleepWorld world)
    {
        var row = world.Suspensions.Rows.Single(r => r.Status == WorkflowSuspensionStatus.Suspended);
        return (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), world.Suspensions.Now, default))!;
    }
}
