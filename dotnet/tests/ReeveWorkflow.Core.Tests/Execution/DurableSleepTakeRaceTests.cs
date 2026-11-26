using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepTakeRaceTests
{
    private const int Long = 600_000;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_single_fire_after_a_waker_took_the_sleep_is_skipped_and_the_sleeper_still_finishes()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.PassTime(TimeSpan.FromHours(1));

        var context = await TakeAsync(world, sleeper.Id);
        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Runs.Should().ContainSingle("the taken sleeper is still the workflow's one run");
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);
        sleeper.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_resumed_sleep_that_meets_a_single_fire_mid_admission_defers_and_the_fire_is_skipped()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.PassTime(TimeSpan.FromHours(1));

        world.Seam.QuestionAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Seam.HoldQuestion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fire = Task.Run(() => engine.ExecuteAsync(workflowId, null, null));
        await world.Seam.QuestionAsked.Task.WaitAsync(Patience);
        var context = await TakeAsync(world, sleeper.Id);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default))
            .Should().Be(WorkflowResumeOutcome.Deferred, "the fire holds the Single permit while it asks");
        (await world.Suspensions.ReleaseAsync(context.SuspensionId, WorkflowSuspensionStatus.Resumed, default)).Should().BeTrue();
        world.Seam.HoldQuestion.SetResult();
        await fire.WaitAsync(Patience);

        world.Journal.Runs.Should().ContainSingle("the fire found the sleeper parked");
        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(1);
        sleeper.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_restart_fire_after_a_waker_took_the_sleep_ends_the_sleeper_before_its_rest_runs()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart, holdFirst: true);
        using var engine = world.Engine();
        world.Blocks.Release.SetResult();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.PassTime(TimeSpan.FromHours(1));

        var context = await TakeAsync(world, sleeper.Id);
        var newer = HoldNewerRunAsync(world, engine, workflowId);
        await world.Blocks.HoldReached.Task.WaitAsync(Patience);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);
        (await world.Suspensions.ReleaseAsync(context.SuspensionId, WorkflowSuspensionStatus.Resumed, default))
            .Should().BeFalse("a superseded run's row does not go back");
        world.Blocks.Release.SetResult();
        await newer.WaitAsync(Patience);

        sleeper.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.Suspensions.Rows.Single(r => r.Id == context.SuspensionId).Status.Should().Be(WorkflowSuspensionStatus.Cancelled);
        world.Blocks.AfterInputs.Should().BeEmpty("neither the superseded run's rest nor the sleeping newer run got past the sleep");
    }

    [Fact]
    public async Task A_resumed_sleep_that_finds_a_restart_run_holding_the_lease_is_abandoned_as_superseded()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart, holdFirst: true);
        using var engine = world.Engine();
        world.Blocks.Release.SetResult();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var stateRow = world.RunState.Rows.Keys.Single();
        world.PassTime(TimeSpan.FromHours(1));

        world.Seam.ListNothing = true;
        var newer = HoldNewerRunAsync(world, engine, workflowId);
        await world.Blocks.HoldReached.Task.WaitAsync(Patience);
        var context = await TakeAsync(world, sleeper.Id);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);
        world.Blocks.Release.SetResult();
        await newer.WaitAsync(Patience);

        sleeper.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.RunState.Rows[stateRow].Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        world.Suspensions.Rows.Single(r => r.Id == context.SuspensionId).Status.Should().Be(WorkflowSuspensionStatus.Cancelled);
        world.Metrics.SuspensionsResolved.Should().Contain(("Sleep", WorkflowRunStatusLabels.Cancelled));
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_restart_sleeper_woken_while_its_own_parking_run_still_holds_the_lease_is_not_abandoned()
    {
        var world = World();
        var seam = new HoldAfterSuspend(world.Suspensions);
        world.SuspensionSeam = seam;
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();

        var parker = Task.Run(() => engine.ExecuteAsync(workflowId, null, null));
        await seam.Persisted.Task.WaitAsync(Patience);
        var sleeper = world.Journal.Single(workflowId);
        world.PassTime(TimeSpan.FromHours(1));
        var context = await TakeAsync(world, sleeper.Id);

        var outcome = await world.Resumer(engine).ResumeFromSuspensionAsync(context, default);
        seam.Release.SetResult();
        await parker.WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Deferred, "nothing superseded it; its own run had not let go of the lease yet");
        sleeper.Status.Should().Be(WorkflowExecutionStatus.Running);
    }

    [Fact]
    public async Task A_restart_fire_holding_the_lease_but_not_yet_started_defers_the_sleeper_and_it_finishes_if_the_fire_gives_up()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.PassTime(TimeSpan.FromHours(1));

        world.Seam.HoldList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Seam.ListAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Seam.HoldListOnCall = world.Seam.ListCalls + 2;
        using var callerGivesUp = new CancellationTokenSource();
        var fire = Task.Run(() => engine.ExecuteAsync(workflowId, null, null, cancellationToken: callerGivesUp.Token));
        await world.Seam.ListAsked.Task.WaitAsync(Patience);
        var context = await TakeAsync(world, sleeper.Id);

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Deferred);
        (await world.Suspensions.ReleaseAsync(context.SuspensionId, WorkflowSuspensionStatus.Resumed, default)).Should().BeTrue();
        callerGivesUp.Cancel();
        try { await fire.WaitAsync(Patience); } catch (OperationCanceledException) { }
        world.Seam.HoldList = null;

        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(1);
        sleeper.Status.Should().Be(WorkflowExecutionStatus.Succeeded, "the fire that held the lease never became a run");
        world.Journal.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_restart_fire_long_after_a_waker_took_the_sleep_still_ends_the_sleeper()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var row = world.Suspensions.SingleRow;
        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc),
            DateTimeOffset.UtcNow - TimeSpan.FromHours(3), default))!;

        await engine.ExecuteAsync(workflowId, null, null);
        await world.Resumer(engine).ResumeFromSuspensionAsync(context, default);

        sleeper.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.Blocks.AfterInputs.Should().BeEmpty("the superseded run's rest must not run after a newer Restart run started");
    }

    [Fact]
    public async Task A_sleep_whose_waker_died_after_taking_it_is_put_back_and_resumed_by_a_later_pass()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.PassTime(TimeSpan.FromHours(1));
        _ = await TakeAsync(world, sleeper.Id);
        world.PassTime(TimeSpan.FromHours(2));

        var waker = new WorkflowSuspensionWaker(world.Suspensions, world.Resumer(engine), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, RetakeAbandonedTakesAfter = TimeSpan.FromMinutes(15) });

        (await waker.WakeAndWalkAsync()).Should().Be(1, "the stranded take is put back before the pass reads what is due");
        sleeper.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_restart_sleeper_woken_after_a_newer_run_started_and_let_go_of_the_lease_is_superseded()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        var context = await TakeAsync(world, sleeper.Id);
        world.Seam.ListNothing = true;
        await engine.ExecuteAsync(workflowId, null, null);
        world.Seam.ListNothing = false;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);

        sleeper.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.Blocks.AfterInputs.Should().BeEmpty("a Restart run a newer one started after never runs its rest");
    }

    [Theory]
    [InlineData(WorkflowRunStateStatus.Suspended, 1)]
    [InlineData(WorkflowRunStateStatus.Running, 2)]
    public async Task A_taken_sleep_counts_for_single_only_until_its_resume_moves_the_state_row_on(
        WorkflowRunStateStatus state, int runs)
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        _ = await TakeAsync(world, sleeper.Id);
        world.RunState.Force(world.RunState.Rows.Keys.Single(), state);

        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Runs.Should().HaveCount(runs, "a resume that moved the row on holds the lease, which is what Single asks");
    }

    [Fact]
    public async Task A_stale_take_whose_resume_is_walking_is_not_put_back()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.PassTime(TimeSpan.FromHours(1));
        var context = await TakeAsync(world, sleeper.Id);
        world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Running);
        world.PassTime(TimeSpan.FromHours(2));

        var waker = new WorkflowSuspensionWaker(world.Suspensions, world.Resumer(engine), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, RetakeAbandonedTakesAfter = TimeSpan.FromMinutes(15) });

        (await waker.WakeAndWalkAsync()).Should().Be(0);
        world.Suspensions.Rows.Single(r => r.Id == context.SuspensionId).Status.Should().Be(WorkflowSuspensionStatus.Resumed);
    }

    public enum Ending { Journal, StateRow }

    [Theory]
    [InlineData(Ending.Journal)]
    [InlineData(Ending.StateRow)]
    public async Task A_single_fire_is_admitted_once_the_parked_run_has_ended(Ending ending)
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        if (ending == Ending.Journal)
            world.Journal.Single(workflowId).Complete(WorkflowExecutionStatus.Cancelled, DateTimeOffset.UtcNow, "user");
        else
            world.RunState.Force(world.RunState.Rows.Keys.Single(), WorkflowRunStateStatus.Cancelled);

        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Runs.Should().HaveCount(2, "nothing is asleep any more, so nothing holds Single's one run");
    }

    [Fact]
    public async Task A_resumer_without_the_engines_coordinators_leaves_a_parked_sleep_parked()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var sleeper = world.Journal.Single(workflowId);
        world.PassTime(TimeSpan.FromHours(1));

        var context = await TakeAsync(world, sleeper.Id);

        (await world.Resumer(engine: null).ResumeFromSuspensionAsync(context, default))
            .Should().Be(WorkflowResumeOutcome.Deferred, "with no lease to take, its rest could walk beside a Single run");
        sleeper.Status.Should().Be(WorkflowExecutionStatus.Running);
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    private sealed class HoldAfterSuspend(MemorySuspensions inner) : IWorkflowSuspensionStore, IWorkflowParkedSleepStore
    {
        public TaskCompletionSource Persisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken)
        {
            var suspension = await inner.SuspendAsync(request, cancellationToken);
            Persisted.TrySetResult();
            await Release.Task;
            return suspension;
        }

        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            inner.CancelByExecutionAsync(executionLogId, cancellationToken);
        public void RegisterForResumption(WorkflowSuspension suspension) { }
        public void TearDownByExecution(Guid executionLogId) { }
        public Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.HasParkedSleepAsync(workflowId, cancellationToken);
        public Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.ListParkedSleepsAsync(workflowId, cancellationToken);
        public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken) =>
            inner.CancelParkedSleepAsync(suspensionId, cancellationToken);
        public Task<bool> HasRunStartedSinceAsync(Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken) =>
            inner.HasRunStartedSinceAsync(workflowId, exceptExecutionLogId, sinceUtc, cancellationToken);
    }

    private static DurableSleepWorld World()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        return world;
    }

    private static Task HoldNewerRunAsync(DurableSleepWorld world, WorkflowEngine engine, Guid workflowId)
    {
        world.Blocks.Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Blocks.HoldReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Task.Run(() => engine.ExecuteAsync(workflowId, null, null));
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
