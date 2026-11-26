using ReeveWorkflow.Abstractions;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepModeParityTests
{
    private const int Long = 600_000;

    [Fact]
    public async Task Single_skips_a_new_run_while_one_is_parked_on_a_sleep()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Runs.Should().ContainSingle("the sleeping run is still the one run this workflow may have");
        world.Audit.Events.Should().Contain(e => e.Kind == WorkflowAuditEventKinds.RunSkipped
            && e.Data!.ToString()!.Contains("single_mode_parked_sleep"));
        world.Metrics.Admissions.Should().Equal(("Single", "started"), ("Single", "skipped"));
    }

    [Fact]
    public async Task Single_runs_again_once_the_sleeper_has_woken_and_finished()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.PassTime(TimeSpan.FromHours(1));
        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(1);
        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Runs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Restart_cancels_a_parked_sleeper_before_the_new_run_starts()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var sleeperRow = world.RunState.Rows.Keys.Single();

        await engine.ExecuteAsync(workflowId, null, null);

        sleeper.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.RunState.Rows[sleeperRow].Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        world.Suspensions.Rows.Should().HaveCount(2);
        world.Suspensions.Rows.Single(r => r.ExecutionLogId == sleeper.Id).Status
            .Should().Be(WorkflowSuspensionStatus.Cancelled, "a row left parked would wake a run nobody wants");
        world.Suspensions.Rows.Single(r => r.ExecutionLogId != sleeper.Id).Status
            .Should().Be(WorkflowSuspensionStatus.Suspended, "the newer run is the one now sleeping");
        world.Metrics.SuspensionsResolved.Should().Contain(("Sleep", WorkflowRunStatusLabels.Cancelled));
    }

    [Fact]
    public async Task A_restart_that_supersedes_only_a_parked_sleeper_reports_restarted()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        WorkflowAdmissionDecision? heard = null;

        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { OnAdmissionDecided = d => heard = d }))
            await engine.ExecuteAsync(workflowId, null, null);

        heard!.Outcome.Should().Be(WorkflowAdmissionOutcomes.Restarted);
        world.Metrics.Admissions.Should().Equal(
            ("Restart", WorkflowAdmissionOutcomes.Started),
            ("Restart", WorkflowAdmissionOutcomes.Restarted), ("Restart", WorkflowAdmissionOutcomes.Started));
        world.Audit.Kinds.Should().ContainSingle(kind => kind == WorkflowAuditEventKinds.RunRestarted);
    }

    [Theory]
    [InlineData(WorkflowMode.Parallel, false)]
    [InlineData(WorkflowMode.Queued, false)]
    [InlineData(WorkflowMode.Single, true)]
    [InlineData(WorkflowMode.Restart, true)]
    public async Task Only_single_and_restart_workflows_park_a_sleep(WorkflowMode mode, bool parks)
    {
        var world = World();
        world.ParkWhenAtLeast = TimeSpan.FromMilliseconds(1);
        var workflowId = world.AddWorkflow(30, mode, maxConcurrentRuns: 1, maxQueuedRuns: 1);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        world.Suspensions.Rows.Should().HaveCount(parks ? 1 : 0);
        world.Blocks.AfterInputs.Should().HaveCount(parks ? 0 : 1);
    }

    [Fact]
    public async Task A_host_that_never_parks_is_never_asked_about_parked_sleeps()
    {
        var world = World();
        world.ParkWhenAtLeast = null;
        var workflowId = world.AddWorkflow(20, WorkflowMode.Single);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        await engine.ExecuteAsync(workflowId, null, null);

        world.Suspensions.ParkedQueries.Should().Be(0);
        world.Journal.Runs.Should().HaveCount(2);
    }

    private static DurableSleepWorld World()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Suspensions;
        return world;
    }
}
