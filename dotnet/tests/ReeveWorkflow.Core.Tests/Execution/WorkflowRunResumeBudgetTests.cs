using System.Collections.Concurrent;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunResumeBudgetTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData(WorkflowMode.Single)]
    [InlineData(WorkflowMode.Restart)]
    [InlineData(WorkflowMode.Parallel)]
    [InlineData(WorkflowMode.Queued)]
    public async Task A_parked_run_whose_workflow_has_no_room_is_deferred_untouched_and_the_run_in_flight_carries_on(
        WorkflowMode mode)
    {
        var world = World(mode);
        using var engine = world.Engine();
        var parked = world.Parked();
        var fresh = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);

        var outcome = await world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Deferred,
            "the wait goes back and a later pass tries again; a resume never queues behind the budget on a waker's worker");
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended);
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Running);
        world.Audit.Events.Should().NotContain(e => e.SubjectId == parked.ExecutionLogId);
        fresh.IsCompleted.Should().BeFalse("a resume that finds no room does not take the room of the run that has it");

        world.Release.SetResult();
        await fresh.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_late_resume_of_a_wait_the_run_has_walked_past_is_refused_though_the_workflow_has_no_room()
    {
        var world = World(WorkflowMode.Single);
        using var engine = world.Engine();
        var parked = world.Parked();
        var fresh = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        world.RunState.Park(world.ParkedStateId!.Value, RunLifecycleWorld.HoldNodeId);

        (await world.Resumer(coordinators: engine.Coordinators).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.HeldElsewhere);

        world.Release.SetResult();
        await fresh.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_retaken_wait_of_a_run_still_running_is_refused_before_the_run_is_asked_for_its_place()
    {
        var world = World(WorkflowMode.Single);
        using var engine = world.Engine();
        var parked = world.Parked();
        var fresh = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        world.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, RunLifecycleWorld.ReplicaId,
            RunLifecycleWorld.HoldNodeId);

        (await world.Resumer(coordinators: engine.Coordinators).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.HeldElsewhere);

        world.Release.SetResult();
        await fresh.WaitAsync(Patience);
    }

    [Theory]
    [InlineData(WorkflowMode.Single)]
    [InlineData(WorkflowMode.Restart)]
    [InlineData(WorkflowMode.Parallel)]
    [InlineData(WorkflowMode.Queued)]
    public async Task A_parked_run_deferred_for_room_is_entered_once_the_run_holding_it_has_finished(WorkflowMode mode)
    {
        var world = World(mode);
        using var engine = world.Engine();
        var parked = world.Parked();
        var fresh = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        var resumer = world.Resumer(coordinators: engine.Coordinators);
        (await resumer.ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Deferred);

        world.Release.SetResult();
        await fresh.WaitAsync(Patience);

        (await resumer.ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Theory]
    [InlineData(WorkflowMode.Single)]
    [InlineData(WorkflowMode.Parallel)]
    public async Task A_trigger_that_fires_while_a_resumed_run_holds_the_workflows_only_place_does_not_start(
        WorkflowMode mode)
    {
        var world = World(mode);
        using var engine = world.Engine();
        var parked = world.Parked();
        var resume = world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await world.Entered.Task.WaitAsync(Patience);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.RunState.LastBegun.Should().BeNull("the trigger's run never opened a row");
        world.Journal.Logs.Keys.Should().Equal([parked.ExecutionLogId], "nor a journal entry");

        world.Release.SetResult();
        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
    }

    [Fact]
    public async Task A_restart_trigger_supersedes_a_resumed_run_the_way_it_supersedes_a_fresh_one()
    {
        var world = World(WorkflowMode.Restart);
        using var engine = world.Engine();
        var parked = world.Parked();
        var resume = world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await world.Entered.Task.WaitAsync(Patience);

        var trigger = engine.ExecuteAsync(world.WorkflowId, null, null);

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Cancelled,
            "a superseded run is ended, not handed over: the trigger that replaced it is the run now");
        world.Release.SetResult();
        await trigger.WaitAsync(Patience);
        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_parked_single_run_whose_cluster_lock_a_peer_holds_is_deferred_untouched_and_entered_once_it_is_free()
    {
        var world = World(WorkflowMode.Single);
        using var engine = world.Engine(locks: new PeerHoldsTheLockAtFirst());
        var parked = world.Parked();
        var resumer = world.Resumer(coordinators: engine.Coordinators);

        (await resumer.ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Deferred);
        world.Entered.Task.IsCompleted.Should().BeFalse();
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended);

        world.Release.SetResult();
        (await resumer.ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed, "the local place the refused lock was asked under was given back");
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_parked_parallel_run_whose_cluster_budget_is_full_elsewhere_is_deferred_untouched_and_entered_once_there_is_room()
    {
        var world = World(WorkflowMode.Parallel);
        var slots = new BudgetFullAtFirst();
        using var engine = world.Engine(locks: new PeerHoldsTheLockAtFirst(), slots: slots);
        var parked = world.Parked();
        var resumer = world.Resumer(coordinators: engine.Coordinators);

        (await resumer.ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Deferred);
        world.Entered.Task.IsCompleted.Should().BeFalse();
        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended);

        world.Release.SetResult();
        (await resumer.ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);
        slots.Asked.Should().Be(2);
        slots.Released.Should().ContainSingle("the place taken on the second ask is given back when the segment ends");
    }

    [Fact]
    public async Task A_resumed_run_gives_its_place_back_when_it_parks_again()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        using var engine = world.Engine();
        var parked = world.Parked();

        (await world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);
        world.RunState.LastBegun.Should().NotBeNull("the place the resumed segment held is free again once it parked");
    }

    [Fact]
    public async Task A_resume_whose_coordinator_is_retired_before_it_is_pinned_runs_on_a_fresh_one()
    {
        var clock = new WorkflowEngineTests.EngineClock(T0);
        var world = World(WorkflowMode.Single);
        using var engine = world.Engine(timeProvider: clock, idleTimeout: IdleTimeout);
        var pool = new RetireOnFirstLookup(engine, clock);
        var parked = world.Parked();

        var resume = world.Resumer(coordinators: pool).ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await world.Entered.Task.WaitAsync(Patience);

        pool.Lookups.Should().Be(2, "the retired coordinator was refused the pin and the pool asked again");
        var live = engine.Coordinators.For(world.Workflow);
        live.Should().NotBeSameAs(pool.First);
        live.ActiveRunCount.Should().Be(1, "the resumed run holds its place on the coordinator the engine now keeps");
        engine.CoordinatorCount.Should().Be(1);
        world.Release.SetResult();
        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
    }

    [Fact]
    public async Task A_sweep_while_a_resume_takes_its_place_leaves_the_coordinator_and_the_resume_runs_on_it()
    {
        var clock = new WorkflowEngineTests.EngineClock(T0);
        var world = World(WorkflowMode.Single);
        var locks = new GatedLockProvider();
        using var engine = world.Engine(timeProvider: clock, idleTimeout: IdleTimeout, locks: locks);
        var coordinator = engine.Coordinators.For(world.Workflow);
        var parked = world.Parked();
        var resume = world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await locks.Asked.Task.WaitAsync(Patience);
        clock.Advance(IdleTimeout + TimeSpan.FromMinutes(1));

        engine.SweepIdleCoordinators();

        engine.CoordinatorCount.Should().Be(1, "the resume is mid-acquire; no run is registered yet to say so");
        locks.Open();
        await world.Entered.Task.WaitAsync(Patience);
        engine.Coordinators.For(world.Workflow).Should().BeSameAs(coordinator);
        coordinator.ActiveRunCount.Should().Be(1);
        world.Release.SetResult();
        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
    }

    [Fact]
    public async Task A_retired_coordinator_registers_no_lease_for_a_resume_that_reaches_it()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Parallel, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        coordinator.TryRetireIfIdle().Should().BeTrue();

        var act = () => coordinator.TryAcquireForResumeAsync(CancellationToken.None);

        await act.Should().ThrowAsync<ObjectDisposedException>();
        coordinator.ActiveRunCount.Should().Be(0, "a run registered here is one the disposal that follows would cancel");
    }

    private static RunLifecycleWorld World(WorkflowMode mode)
    {
        var world = new RunLifecycleWorld();
        world.Workflow.ConfigureConcurrency(mode, 1, mode == WorkflowMode.Queued ? 5 : 0,
            WorkflowOverflowBehavior.Drop, DateTimeOffset.UtcNow);
        return world;
    }

    private sealed class RetireOnFirstLookup(WorkflowEngine engine, WorkflowEngineTests.EngineClock clock)
        : IWorkflowRunCoordinatorPool
    {
        private int lookups;

        public int Lookups => Volatile.Read(ref lookups);

        public WorkflowRunCoordinator? First { get; private set; }

        public WorkflowRunCoordinator For(Workflow workflow)
        {
            var coordinator = engine.Coordinators.For(workflow);
            if (Interlocked.Increment(ref lookups) != 1)
                return coordinator;

            First = coordinator;
            clock.Advance(IdleTimeout + TimeSpan.FromMinutes(1));
            engine.SweepIdleCoordinators();
            return coordinator;
        }
    }

    private sealed class GatedLockProvider : IWorkflowRunLockProvider
    {
        private readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => open.TrySetResult();

        public async Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            Asked.TrySetResult();
            await open.Task.WaitAsync(cancellationToken);
            return new Held();
        }

        private sealed class Held : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class PeerHoldsTheLockAtFirst : IWorkflowRunLockProvider
    {
        private int asked;

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IWorkflowRunLock?>(Interlocked.Increment(ref asked) == 1 ? null : new Held());

        private sealed class Held : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class BudgetFullAtFirst : IWorkflowRunSlotGate
    {
        private int asked;

        public int Asked => asked;

        public ConcurrentQueue<string> Released { get; } = new();

        public Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct) =>
            Task.FromResult(Interlocked.Increment(ref asked) > 1);

        public Task ReleaseAsync(Guid workflowId, string slotToken)
        {
            Released.Enqueue(slotToken);
            return Task.CompletedTask;
        }
    }
}
