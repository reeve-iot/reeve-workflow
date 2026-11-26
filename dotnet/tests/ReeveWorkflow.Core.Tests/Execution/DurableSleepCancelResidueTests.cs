using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepCancelResidueTests
{
    private const int Long = 600_000;

    [Fact]
    public async Task A_failed_save_for_one_sleeper_does_not_stop_the_restart_ending_the_next()
    {
        var world = World();
        world.PoisonScopeOnFailedSave = true;
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.Seam.ListNothing = true;
        await engine.ExecuteAsync(workflowId, null, null);
        world.Seam.ListNothing = false;
        var sleepers = world.Suspensions.Rows.Select(r => r.Id).ToList();

        world.Journal.FailSaves = 1;
        await engine.ExecuteAsync(workflowId, null, null);

        world.Suspensions.Rows.Where(r => sleepers.Contains(r.Id))
            .Count(r => r.Status == WorkflowSuspensionStatus.Cancelled)
            .Should().Be(1, "the sleeper after the failed save is ended in a unit of work of its own");
    }

    [Fact]
    public async Task A_restart_whose_journal_save_fails_still_ends_the_sleepers_state_row()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var stateRow = world.RunState.Rows.Keys.Single();

        world.Journal.FailSaves = 1;
        await engine.ExecuteAsync(workflowId, null, null);

        world.RunState.Rows[stateRow].Status.Should().Be(WorkflowRunStateStatus.Cancelled,
            "an ended state row is what stops the sleeper counting and makes its wake abandon it");
    }

    [Fact]
    public async Task A_sleeper_whose_journal_was_ended_is_abandoned_with_its_state_row_and_its_node_announced()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var stateRow = world.RunState.Rows.Keys.Single();
        sleeper.Complete(WorkflowExecutionStatus.Cancelled, DateTimeOffset.UtcNow, "user");

        (await world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world, sleeper.Id), default))
            .Should().Be(WorkflowResumeOutcome.Abandoned);

        world.RunState.Rows[stateRow].Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        await DurableSleepWorld.EventuallyAsync(
            () => world.Observer.Failed.Any(f => f.ExecutionId == sleeper.Id), "the sleep node to be announced as ended");
    }

    [Fact]
    public async Task A_sleeper_ended_by_a_restart_and_then_found_by_its_resume_is_announced_and_counted_once()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var context = await TakeAsync(world, sleeper.Id);

        await engine.ExecuteAsync(workflowId, null, null);
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default))
            .Should().Be(WorkflowResumeOutcome.Abandoned, "the restart ended its journal entry as well as its row");
        await Task.Delay(100);

        world.Observer.Failed.Count(f => f.ExecutionId == sleeper.Id).Should().Be(1);
        world.Metrics.SuspensionsResolved.Count(m => m.Outcome == WorkflowRunStatusLabels.Cancelled).Should().Be(1);
    }

    [Fact]
    public async Task A_resume_whose_state_row_swap_stalls_is_deferred_once_the_bound_passes()
    {
        var world = World();
        world.StateRowTimeout = TimeSpan.FromMilliseconds(200);
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.RunState.HoldFlip = new TaskCompletionSource();

        var resume = world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world, sleeper.Id), default);

        (await resume.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(WorkflowResumeOutcome.Deferred,
            "a swap whose outcome is unknown, over a row still parked on this take, is tried again later rather than walked");
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resume_whose_state_row_read_stalls_while_the_host_stops_is_deferred()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Running);
        world.RunState.HoldGet = new TaskCompletionSource();
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var resume = world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world, sleeper.Id), stopping.Token);

        (await resume.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(WorkflowResumeOutcome.Deferred);
        sleeper.Status.Should().Be(WorkflowExecutionStatus.Running);
    }

    [Fact]
    public async Task A_resume_that_cannot_read_its_state_row_in_time_defers_rather_than_walking()
    {
        var world = World();
        world.StateRowTimeout = TimeSpan.FromMilliseconds(200);
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.RunState.HoldGet = new TaskCompletionSource();

        var resume = world.Resumer(engine).ResumeFromSuspensionAsync(await TakeAsync(world, sleeper.Id), default);

        (await resume.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(WorkflowResumeOutcome.Deferred);
        world.Blocks.AfterInputs.Should().BeEmpty();
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Suspended);
    }

    [Fact]
    public async Task An_abandon_whose_journal_save_fails_is_retried_once_its_take_is_put_back()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Cancelled);
        world.PassTime(TimeSpan.FromHours(1));
        var resumer = world.Resumer(engine);
        world.Journal.FailSaves = 1;

        (await resumer.ResumeFromSuspensionAsync(await TakeAsync(world, sleeper.Id), default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere);
        world.Journal.Runs[sleeper.Id] = new WorkflowExecutionLog(sleeper.Id, null, workflowId, sleeper.WorkflowVersionId,
            null, WorkflowExecutionStatus.Running, sleeper.StartedAtUtc);
        await Task.Delay(100);
        world.Observer.Failed.Should().BeEmpty("the node is announced by the abandon that lands");

        world.PassTime(TimeSpan.FromHours(2));
        var waker = new WorkflowSuspensionWaker(world.Suspensions, resumer, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, RetakeAbandonedTakesAfter = TimeSpan.FromMinutes(15) });
        await waker.WakeAndWalkAsync();

        world.Journal.Runs[sleeper.Id].Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        await Task.Delay(100);
        world.Observer.Failed.Should().BeEmpty("the state row was ended by the host, which announces the node itself");
    }

    [Fact]
    public async Task A_restart_whose_journal_save_fails_and_whose_sleeper_is_later_abandoned_announces_it_once()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var parked = world.Suspensions.SingleRow;

        world.Journal.FailSaves = 1;
        await engine.ExecuteAsync(workflowId, null, null);
        world.Journal.Runs[sleeper.Id] = new WorkflowExecutionLog(sleeper.Id, null, workflowId, sleeper.WorkflowVersionId,
            null, WorkflowExecutionStatus.Running, sleeper.StartedAtUtc);
        world.PassTime(TimeSpan.FromHours(1));
        var row = world.Suspensions.Rows.Single(r => r.Id == parked.Id);
        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), world.Suspensions.Now, default))!;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere, "the entry still reads Running, so the ended row is what says so");
        await Task.Delay(100);

        world.Observer.Failed.Count(f => f.ExecutionId == sleeper.Id).Should().Be(1);
        world.Metrics.SuspensionsResolved.Count(m => m.Outcome == WorkflowRunStatusLabels.Cancelled).Should().Be(1);
    }

    [Fact]
    public async Task A_template_resume_that_finds_its_journal_ended_ends_its_state_row_with_no_threshold()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.Extra = id => id == "test.wait"
            ? new LambdaFactory(id, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort])
            : null;
        var workflowId = world.AddRaw(System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "pause", blockTypeId = "test.wait", position = new { x = 0, y = 0 }, configuration = new { Condition = "expr:false" } }
            },
            edges = new object[] { new { id = "a", sourceNodeId = "start", targetNodeId = "pause", mappings = new { } } }
        }));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.Journal.Single(workflowId).Complete(WorkflowExecutionStatus.Failed, DateTimeOffset.UtcNow, "host ended it");

        var row = world.Suspensions.SingleRow;
        var ctx = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.TimedOut,
            """{"outcome":"timedOut"}""", DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(ctx, default)).Should().Be(WorkflowResumeOutcome.Abandoned);

        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Cancelled);
    }

    private static DurableSleepWorld World()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        return world;
    }

    private static async Task<ResumeContext> TakeAsync(DurableSleepWorld world, Guid executionLogId)
    {
        var row = world.Suspensions.Rows.Single(r => r.ExecutionLogId == executionLogId
            && r.Status == WorkflowSuspensionStatus.Suspended);
        return (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc),
            world.Suspensions.Now, default))!;
    }
}
