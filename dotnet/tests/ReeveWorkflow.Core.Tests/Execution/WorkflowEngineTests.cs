using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Two_runs_of_one_workflow_go_through_the_one_coordinator_that_holds_its_budget()
    {
        var world = new World();
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.WorkflowId, null, null);
        await engine.ExecuteAsync(world.WorkflowId, null, null);

        engine.CoordinatorCount.Should().Be(1,
            "a second coordinator over one workflow would believe it held that workflow's whole budget too");
    }

    [Fact]
    public async Task Two_workflows_are_two_budgets_and_not_one()
    {
        var world = new World();
        var second = world.AddWorkflow();
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.WorkflowId, null, null);
        await engine.ExecuteAsync(second, null, null);

        engine.CoordinatorCount.Should().Be(2);
    }

    [Fact]
    public async Task Restart_signals_use_one_subscription_and_cancel_the_named_workflow()
    {
        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var world = new World
        {
            Park = async token =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                    bothEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        var second = world.AddWorkflow(WorldGraphWithWork());
        var signal = new RecordingClusterSignal();
        using var engine = world.Engine(clusterSignal: signal);
        foreach (var workflowId in Enumerable.Range(0, 298).Select(_ => world.AddWorkflow()))
            engine.Coordinators.For(world.Workflow(workflowId));
        var firstRun = engine.ExecuteAsync(world.WorkNodeWorkflowId, null, null);
        var secondRun = engine.ExecuteAsync(second, null, null);
        await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        signal.SubscriptionCount.Should().Be(1);
        engine.CoordinatorCount.Should().Be(300);
        await signal.DeliverAsync(world.WorkNodeWorkflowId);

        var firstWasCancelled = false;
        try { await firstRun.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { firstWasCancelled = true; }
        firstWasCancelled.Should().BeTrue();
        engine.Coordinators.For(world.Workflow(second)).ActiveRunCount.Should().Be(1,
            "a restart cancel stops the runs of the workflow it names and no other's");
        engine.ForgetWorkflow(second);
        try { await secondRun; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task A_Restart_trigger_on_another_replica_cancels_the_running_replicas_run_on_the_signal()
    {
        var runs = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var world = new World
        {
            Park = async token =>
            {
                if (Interlocked.Increment(ref runs) > 1)
                    return;
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        world.Workflow(world.WorkNodeWorkflowId)
            .ConfigureConcurrency(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, T0);
        var cluster = new SharedCluster();
        using var replicaA = world.Engine(cluster: cluster, heartbeat: TimeSpan.FromMinutes(5));
        using var replicaB = world.Engine(cluster: cluster, heartbeat: TimeSpan.FromMinutes(5));
        var running = replicaA.ExecuteAsync(world.WorkNodeWorkflowId, null, null);
        await entered.Task.WaitAsync(Patience);
        cluster.Lock.HoldReleases();

        var trigger = replicaB.ExecuteAsync(world.WorkNodeWorkflowId, null, null);
        try { await cluster.Lock.FirstRefusal.Task.WaitAsync(Patience); }
        finally { cluster.Lock.ReleaseHeld(); }
        await trigger.WaitAsync(Patience);

        await FluentActions.Awaiting(() => running.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        cluster.Lock.Refusals.Should().BeGreaterThan(0, "the trigger on B met A's hold on the cluster lock");
        cluster.Signal.Published.Should().Contain(world.WorkNodeWorkflowId,
            "the engine's coordinators say the cancel on the one signal the engine was given");
        cluster.Signal.SubscriptionCount.Should().Be(2, "one subscription per engine, not per coordinator");
    }

    [Fact]
    public async Task A_cancel_naming_a_workflow_with_no_coordinator_creates_none()
    {
        var world = new World();
        var signal = new RecordingClusterSignal();
        using var engine = world.Engine(clusterSignal: signal);
        await engine.ExecuteAsync(world.WorkflowId, null, null);

        await signal.DeliverAsync(Guid.NewGuid());

        engine.CoordinatorCount.Should().Be(1, "a dispatch that built a coordinator for every id it heard would leak one per signal");
    }

    [Fact]
    public async Task A_cancel_reaches_a_coordinator_built_after_the_subscription_was_made()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var world = new World { Park = token => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, token); } };
        var signal = new RecordingClusterSignal();
        using var engine = world.Engine(clusterSignal: signal);
        var run = engine.ExecuteAsync(world.WorkNodeWorkflowId, null, null);
        await entered.Task.WaitAsync(Patience);

        await signal.DeliverAsync(world.WorkNodeWorkflowId);

        await FluentActions.Awaiting(() => run.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(1800, 60)]
    public async Task The_cleanup_timer_comes_from_the_injected_clock_and_its_tick_retires_an_idle_coordinator(
        int idleSeconds, int expectedPeriodSeconds)
    {
        var clock = new EngineClock(T0);
        var world = new World();
        using var engine = world.Engine(timeProvider: clock, idleTimeout: TimeSpan.FromSeconds(idleSeconds));
        await engine.ExecuteAsync(world.WorkflowId, null, null);

        clock.Timers.Should().ContainSingle().Which.Period.Should().Be(TimeSpan.FromSeconds(expectedPeriodSeconds),
            "the sweep runs as often as the idle period, and no less often than once a minute");
        clock.Advance(TimeSpan.FromSeconds(idleSeconds));
        clock.FireTimers();

        engine.CoordinatorCount.Should().Be(0);
    }

    [Fact]
    public async Task A_coordinator_used_again_within_the_idle_period_survives_the_sweep()
    {
        var clock = new EngineClock(T0);
        var world = new World();
        using var engine = world.Engine(timeProvider: clock, idleTimeout: TimeSpan.FromMinutes(2));
        await engine.ExecuteAsync(world.WorkflowId, null, null);
        clock.Advance(TimeSpan.FromSeconds(90));
        await engine.ExecuteAsync(world.WorkflowId, null, null);
        clock.Advance(TimeSpan.FromSeconds(90));

        engine.SweepIdleCoordinators();

        engine.CoordinatorCount.Should().Be(1, "the second run was ninety seconds ago, inside the period");
    }

    [Fact]
    public async Task Disposing_the_engine_unsubscribes_from_the_signal_and_stops_the_cleanup_timer()
    {
        var clock = new EngineClock(T0);
        var world = new World();
        var signal = new RecordingClusterSignal();
        var engine = world.Engine(timeProvider: clock, clusterSignal: signal);
        await engine.ExecuteAsync(world.WorkflowId, null, null);
        signal.SubscriptionCount.Should().Be(1);

        engine.Dispose();

        signal.SubscriptionCount.Should().Be(0);
        clock.Timers.Should().OnlyContain(timer => timer.Disposed);
    }

    [Fact]
    public async Task A_sweep_leaves_a_Queued_waiter_s_coordinator_while_its_holder_is_still_being_admitted()
    {
        var clock = new EngineClock(T0);
        var cluster = new SharedCluster();
        var slots = new HangingFirstSlotGate();
        cluster.Slots = slots;
        var world = new World();
        world.Workflow(world.WorkflowId).ConfigureConcurrency(WorkflowMode.Queued, 1, 1, WorkflowOverflowBehavior.Drop, T0);
        using var engine = world.Engine(cluster: cluster, timeProvider: clock, idleTimeout: TimeSpan.FromMinutes(2));
        var coordinator = engine.Coordinators.For(world.Workflow(world.WorkflowId));
        var holder = coordinator.AcquireAsync(CancellationToken.None);
        await slots.Asked.Task.WaitAsync(Patience);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = coordinator.AcquireAsync(parkedSleeps: null, onQueued: () => queued.TrySetResult(), CancellationToken.None);
        await queued.Task.WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMinutes(3));

        engine.SweepIdleCoordinators();

        engine.CoordinatorCount.Should().Be(1);
        slots.Admit();
        var held = (await holder.WaitAsync(Patience)).Lease!;
        await held.DisposeAsync();
        var promoted = await waiter.WaitAsync(Patience);
        promoted.Lease.Should().NotBeNull("the waiter's coordinator was not retired from under it");
        await promoted.Lease!.DisposeAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        engine.SweepIdleCoordinators();
        engine.CoordinatorCount.Should().Be(0, "nothing is running, queued or being admitted any more");
    }

    [Fact]
    public async Task A_sweep_leaves_the_coordinator_of_a_Restart_trigger_waiting_for_a_peer_s_lock_and_the_trigger_then_runs()
    {
        var clock = new EngineClock(T0);
        var cluster = new SharedCluster();
        cluster.Lock.PeerHolds = true;
        var world = new World();
        world.Workflow(world.WorkflowId).ConfigureConcurrency(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, T0);
        using var engine = world.Engine(cluster: cluster, timeProvider: clock, idleTimeout: TimeSpan.FromMinutes(2));
        var coordinator = engine.Coordinators.For(world.Workflow(world.WorkflowId));
        var trigger = coordinator.AcquireAsync(CancellationToken.None);
        await cluster.Lock.FirstRefusal.Task.WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMinutes(3));

        engine.SweepIdleCoordinators();

        engine.CoordinatorCount.Should().Be(1);
        trigger.IsCompleted.Should().BeFalse();
        cluster.Lock.PeerHolds = false;
        var decision = await trigger.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull("the newest trigger runs once the peer lets go");
        engine.Coordinators.For(world.Workflow(world.WorkflowId)).Should().BeSameAs(coordinator);
        await decision.Lease!.DisposeAsync();
        clock.Advance(TimeSpan.FromMinutes(3));
        engine.SweepIdleCoordinators();
        engine.CoordinatorCount.Should().Be(0);
    }

    [Fact]
    public void Coordinator_idle_timeout_rejects_values_below_one_second()
    {
        var world = new World();

        var act = () => world.Engine(idleTimeout: TimeSpan.FromMilliseconds(999));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("CoordinatorIdleTimeout");
    }

    [Fact]
    public async Task Idle_coordinators_expire_and_are_recreated_for_the_next_run()
    {
        var clock = new EngineClock(T0);
        var world = new World();
        using var engine = world.Engine(timeProvider: clock, idleTimeout: TimeSpan.FromMinutes(2));
        await engine.ExecuteAsync(world.WorkflowId, null, null);
        var prior = engine.Coordinators.For(world.Workflow(world.WorkflowId));

        clock.Advance(TimeSpan.FromMinutes(2));
        engine.SweepIdleCoordinators();

        engine.CoordinatorCount.Should().Be(0);
        await engine.ExecuteAsync(world.WorkflowId, null, null);
        engine.Coordinators.For(world.Workflow(world.WorkflowId)).Should().NotBeSameAs(prior);
    }

    [Fact]
    public void An_admission_pin_keeps_an_idle_coordinator_until_admission_finishes()
    {
        var clock = new EngineClock(T0);
        var world = new World();
        using var engine = world.Engine(timeProvider: clock, idleTimeout: TimeSpan.FromMinutes(2));
        var coordinator = engine.Coordinators.For(world.Workflow(world.WorkflowId));
        coordinator.TryPinAdmission(out var pin).Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));

        engine.SweepIdleCoordinators();
        engine.CoordinatorCount.Should().Be(1);

        pin.Dispose();
        engine.SweepIdleCoordinators();
        engine.CoordinatorCount.Should().Be(0);
    }

    [Fact]
    public async Task A_sweep_while_a_run_is_being_admitted_leaves_the_coordinator_and_the_run_is_admitted_on_it()
    {
        var clock = new EngineClock(T0);
        var cluster = new SharedCluster();
        var slots = new HangingFirstSlotGate();
        cluster.Slots = slots;
        var world = new World();
        world.Workflow(world.WorkflowId).ConfigureConcurrency(WorkflowMode.Parallel, 2, 0, WorkflowOverflowBehavior.Drop, T0);
        using var engine = world.Engine(cluster: cluster, timeProvider: clock, idleTimeout: TimeSpan.FromMinutes(2));
        var coordinator = engine.Coordinators.For(world.Workflow(world.WorkflowId));
        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await slots.Asked.Task.WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMinutes(3));

        engine.SweepIdleCoordinators();

        engine.CoordinatorCount.Should().Be(1, "an admission in flight pins its coordinator");
        slots.Admit();
        await run.WaitAsync(Patience);
        world.Journal.Opened.Should().ContainSingle("the run was admitted, on the coordinator the sweep left");
        engine.Coordinators.For(world.Workflow(world.WorkflowId)).Should().BeSameAs(coordinator);
        clock.Advance(TimeSpan.FromMinutes(3));
        engine.SweepIdleCoordinators();
        engine.CoordinatorCount.Should().Be(0, "the pin went with the admission");
    }

    [Fact]
    public async Task Coordinator_with_an_active_run_is_not_forgotten()
    {
        var clock = new EngineClock(T0);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var world = new World
        {
            Park = async token =>
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        using var engine = world.Engine(timeProvider: clock, idleTimeout: TimeSpan.FromMinutes(2));
        var run = engine.ExecuteAsync(world.WorkNodeWorkflowId, null, null);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(3));

        engine.SweepIdleCoordinators();

        engine.CoordinatorCount.Should().Be(1);
        engine.Coordinators.For(world.Workflow(world.WorkNodeWorkflowId)).ActiveRunCount.Should().Be(1);
        engine.ForgetWorkflow(world.WorkNodeWorkflowId);
        try { await run; } catch (OperationCanceledException) { }
    }

    private static string WorldGraphWithWork() =>
        $"{{\"nodes\":[{{\"id\":\"start\",\"blockTypeId\":\"WorkflowInput\",\"position\":{{\"x\":0,\"y\":0}},\"configuration\":{{}}}},"
        + $"{{\"id\":\"{World.WorkNodeId}\",\"blockTypeId\":\"Work\",\"configuration\":{{}},\"position\":{{\"x\":0,\"y\":0}}}}],"
        + $"\"edges\":[{{\"id\":\"e\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"{World.WorkNodeId}\",\"mappings\":{{}}}}]}}";

    [Fact]
    public async Task A_forgotten_workflow_takes_its_coordinator_with_it()
    {
        var world = new World();
        using var engine = world.Engine();
        await engine.ExecuteAsync(world.WorkflowId, null, null);

        engine.ForgetWorkflow(world.WorkflowId);

        engine.CoordinatorCount.Should().Be(0);
    }

    [Fact]
    public async Task Forgetting_a_workflow_answers_with_the_runs_it_was_holding()
    {
        var reached = new TaskCompletionSource();
        var world = new World
        {
            Park = token =>
            {
                reached.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            }
        };
        using var engine = world.Engine();

        var run = engine.ExecuteAsync(world.WorkNodeWorkflowId, null, null);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelled = engine.ForgetWorkflow(world.WorkNodeWorkflowId);

        cancelled.Should().HaveCount(1, "one run was in flight when the workflow was forgotten");
        try { await run; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Forgetting_a_workflow_nothing_is_running_answers_with_nothing()
    {
        var world = new World();
        using var engine = world.Engine();
        await engine.ExecuteAsync(world.WorkflowId, null, null);

        engine.ForgetWorkflow(world.WorkflowId).Should().BeEmpty(
            "an audit row per cancelled run must not appear for a workflow that had none");
    }

    [Fact]
    public void Disposing_twice_is_not_an_error()
    {
        var engine = new World().Engine();

        engine.Dispose();
        var again = () => engine.Dispose();

        again.Should().NotThrow(
            "a container that captured this object through two registrations disposes it twice, and the "
            + "second throw would abort the rest of a shutdown");
    }

    [Fact]
    public async Task An_abandoned_run_resumes_on_the_version_it_was_pinned_to()
    {
        var world = new World();
        var pinned = world.PublishAnotherVersion();
        world.RunState.Row = world.RowFor(pinnedVersionId: pinned);
        using var engine = world.Engine();

        await engine.ResumeAsync(world.RunState.Row.ExecutionId, world.WorkflowId);

        world.Store.VersionLoads.Should().ContainSingle().Which.Should().Be(pinned,
            "resuming on whatever is current now would run a different workflow from the one interrupted");
    }

    [Fact]
    public async Task A_run_whose_row_pinned_nothing_resumes_on_the_current_version()
    {
        var world = new World();
        world.RunState.Row = world.RowFor(pinnedVersionId: null);
        using var engine = world.Engine();

        await engine.ResumeAsync(world.RunState.Row.ExecutionId, world.WorkflowId);

        world.Store.VersionLoads.Should().BeEmpty();
        world.Journal.Opened.Should().ContainSingle("the run still happened");
    }

    [Fact]
    public async Task A_run_whose_row_predates_manifests_resumes_on_the_version_number_it_recorded()
    {
        var world = new World();
        var numbered = world.PublishAnotherVersion(number: 2);
        world.RunState.Row = world.RowFor(pinnedVersionId: null, workflowVersion: 2);
        using var engine = world.Engine();

        await engine.ResumeAsync(world.RunState.Row.ExecutionId, world.WorkflowId);

        world.Store.VersionLoads.Should().ContainSingle().Which.Should().Be(numbered,
            "the row records the number it ran on, and building from whatever is current now resumes a "
            + "different workflow from the one that was interrupted");
    }

    [Fact]
    public async Task A_resume_the_host_refused_to_admit_says_the_run_did_not_start()
    {
        var world = new World { SafeMode = true };
        world.RunState.Row = world.RowFor(pinnedVersionId: null);
        using var engine = world.Engine();

        var started = await engine.TryResumeAsync(world.RunState.Row.ExecutionId, world.WorkflowId);

        started.Should().BeFalse();
        world.Journal.Opened.Should().BeEmpty(
            "a caller that cannot tell this from a finished run ends the claimed row and loses the run");
    }

    [Fact]
    public async Task A_resume_that_was_admitted_says_the_run_started()
    {
        var world = new World();
        world.RunState.Row = world.RowFor(pinnedVersionId: null);
        using var engine = world.Engine();

        var started = await engine.TryResumeAsync(world.RunState.Row.ExecutionId, world.WorkflowId);

        started.Should().BeTrue();
        world.Journal.Opened.Should().ContainSingle();
    }

    [Fact]
    public async Task A_block_the_host_declares_idempotent_may_be_replayed()
    {
        var world = new World { WorkNodeIsIdempotent = true };
        using var engine = world.Engine();

        var replayable = await engine.IsBlockIdempotentAsync(world.WorkNodeWorkflowId, World.WorkNodeId);

        replayable.Should().BeTrue();
    }

    [Theory]
    [InlineData(World.WorkNodeId)]
    [InlineData("a-node-this-definition-does-not-have")]
    [InlineData("")]
    public async Task Anything_that_cannot_be_shown_idempotent_answers_no(string blockPath)
    {
        var world = new World { WorkNodeIsIdempotent = false };
        using var engine = world.Engine();

        (await engine.IsBlockIdempotentAsync(world.WorkNodeWorkflowId, blockPath)).Should().BeFalse(
            "answering false fails the run instead of replaying it, which is the safe half of the guess");
    }

    [Fact]
    public async Task A_workflow_whose_blocks_are_all_idempotent_may_be_run_again_from_the_start()
    {
        var world = new World { WorkNodeIsIdempotent = true };
        using var engine = world.Engine();

        (await engine.IsRunReplayableAsync(world.WorkNodeWorkflowId, World.WorkNodeId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_block_the_run_had_already_passed_is_what_makes_it_unreplayable()
    {
        var world = new World { WorkNodeIsIdempotent = true };
        using var engine = world.Engine();

        (await engine.IsBlockIdempotentAsync(world.TwoBlockWorkflowId, World.WorkNodeId)).Should().BeTrue();
        (await engine.IsRunReplayableAsync(world.TwoBlockWorkflowId, World.WorkNodeId)).Should().BeFalse(
            "the charge before the poll happens a second time, and no row records that it already happened");
    }

    [Fact]
    public async Task A_run_that_never_entered_a_block_is_replayable_although_its_path_is_empty()
    {
        var world = new World { WorkNodeIsIdempotent = true };
        using var engine = world.Engine();

        (await engine.IsRunReplayableAsync(world.WorkNodeWorkflowId, string.Empty)).Should().BeTrue(
            "nothing ran, so nothing can repeat; the older question answered no for exactly this case");
    }

    [Fact]
    public void A_deployment_that_declares_peers_and_hands_the_engine_no_cluster_seams_is_refused()
    {
        var world = new World();

        var compose = () => world.Engine(multiReplica: true);

        compose.Should().Throw<InvalidOperationException>()
            .WithMessage("*RunSlotGate*").WithMessage("*FireDedup*").WithMessage("*RunLockProvider*")
            .WithMessage("*RestartGenerations*",
                "each of them degrades to in-process behaviour, which on several replicas over-runs silently");
    }

    [Fact]
    public async Task A_restart_trigger_is_ordered_by_the_generations_the_host_handed_the_engine()
    {
        var world = new World();
        world.Workflow(world.WorkflowId).ConfigureConcurrency(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, T0);
        var generations = new InProcessRestartGenerations();
        using var engine = world.Engine(restartGenerations: generations);

        await engine.ExecuteAsync(world.WorkflowId, null, null);

        (await generations.ReadAsync(world.WorkflowId, CancellationToken.None))
            .Should().Be(new WorkflowRestartGenerationState(1, 1, null));
    }

    [Fact]
    public async Task A_store_that_throws_answers_no_rather_than_taking_the_sweep_down_with_it()
    {
        var world = new World { StoreThrows = true };
        using var engine = world.Engine();

        (await engine.IsBlockIdempotentAsync(world.WorkNodeWorkflowId, World.WorkNodeId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_run_and_its_blocks_publish_their_spans_under_the_librarys_own_name()
    {
        var world = new World { Park = _ => Task.CompletedTask };
        var started = new ConcurrentQueue<Activity>();
        using (Listening(started))
        {
            using var engine = world.Engine();
            await engine.ExecuteAsync(world.WorkNodeWorkflowId, null, null);
        }

        var ours = OfWorkflow(started, world.WorkNodeWorkflowId);
        ours.Select(a => a.OperationName).Should().Contain(["workflow.execute", "workflow.block.execute"]);
        ours.Should().OnlyContain(a => a.Source.Name == "ReeveWorkflow");
        started.Should().NotContain(a => a.Source.Name.StartsWith("Reeve.Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_whose_collector_names_the_source_something_else_keeps_its_traces()
    {
        var world = new World { Park = _ => Task.CompletedTask };
        using var renamed = new WorkflowActivitySource(
            new WorkflowTracingOptions { ActivitySourceName = "acme.dispatch.workflows" });
        var started = new ConcurrentQueue<Activity>();
        using (Listening(started))
        {
            using var engine = world.Engine(activitySource: renamed);
            await engine.ExecuteAsync(world.WorkNodeWorkflowId, null, null);
        }

        var ours = OfWorkflow(started, world.WorkNodeWorkflowId);
        ours.Select(a => a.OperationName).Should().Contain(["workflow.execute", "workflow.block.execute"]);
        ours.Should().OnlyContain(a => a.Source.Name == "acme.dispatch.workflows",
            "a rename this library half-applied drops every trace the host's collector is subscribed to");
    }

    private static ActivityListener Listening(ConcurrentQueue<Activity> started)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = started.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static List<Activity> OfWorkflow(IEnumerable<Activity> started, Guid workflowId) =>
        [.. started.Where(a => a.GetTagItem("workflow.id") as string == workflowId.ToString())];

    private sealed class World
    {
        public const string WorkNodeId = "work";
        private const string EmptyGraph = "{\"nodes\":[],\"edges\":[]}";
        private static readonly string OneNodeGraph =
            $"{{\"nodes\":[{{\"id\":\"start\",\"blockTypeId\":\"WorkflowInput\",\"position\":{{\"x\":0,\"y\":0}},\"configuration\":{{}}}},"
            + $"{{\"id\":\"{WorkNodeId}\",\"blockTypeId\":\"Work\",\"configuration\":{{}},\"position\":{{\"x\":0,\"y\":0}}}}],"
            + $"\"edges\":[{{\"id\":\"e\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"{WorkNodeId}\",\"mappings\":{{}}}}]}}";

        private static readonly string TwoNodeGraph =
            $"{{\"nodes\":[{{\"id\":\"charge\",\"blockTypeId\":\"Once\",\"position\":{{\"x\":0,\"y\":0}},\"configuration\":{{}}}},"
            + $"{{\"id\":\"{WorkNodeId}\",\"blockTypeId\":\"Work\",\"configuration\":{{}},\"position\":{{\"x\":0,\"y\":0}}}}],\"edges\":[]}}";

        private readonly Dictionary<Guid, Workflow> workflows = [];

        public Guid WorkflowId { get; }
        public Guid WorkNodeWorkflowId { get; }
        public Guid TwoBlockWorkflowId { get; }
        public bool WorkNodeIsIdempotent { get; init; }
        public bool StoreThrows { get; init; }

        public Func<CancellationToken, Task>? Park { get; init; }

        public RecordingStore Store { get; }
        public RecordingJournal Journal { get; } = new();
        public RecordingRunState RunState { get; } = new();

        public World()
        {
            WorkflowId = AddWorkflow();
            WorkNodeWorkflowId = AddWorkflow(OneNodeGraph);
            TwoBlockWorkflowId = AddWorkflow(TwoNodeGraph);
            Store = new RecordingStore(workflows) { Throws = StoreThrows };
        }

        public Guid AddWorkflow(string definitionJson = EmptyGraph)
        {
            var workflowId = Guid.NewGuid();
            var workflow = new Workflow(workflowId, "wf", null, OwnerId, T0);
            workflow.SetCurrentVersion(new WorkflowVersion(
                Guid.NewGuid(), workflowId, 1, definitionJson, null, null, OwnerId, T0, isPublished: true), T0);
            workflows[workflowId] = workflow;
            return workflowId;
        }

        public Guid PublishAnotherVersion(int number = 2)
        {
            var versionId = Guid.NewGuid();
            Store.Versions[versionId] = new WorkflowVersion(
                versionId, WorkflowId, number, EmptyGraph, null, null, OwnerId, T0, isPublished: true);
            return versionId;
        }

        public bool SafeMode { get; init; }

        public WorkflowRunStateRow RowFor(Guid? pinnedVersionId, int workflowVersion = 0) => new()
        {
            ExecutionId = Guid.NewGuid(),
            WorkflowId = WorkflowId,
            Status = WorkflowRunStateStatus.Running,
            WorkflowVersion = workflowVersion,
            CurrentBlockPath = WorkNodeId,
            PinnedVersionsJson = pinnedVersionId is { } id
                ? $"{{\"{WorkflowId}\":\"{id}\"}}"
                : null
        };

        public Workflow Workflow(Guid workflowId) => workflows[workflowId];

        public WorkflowEngine Engine(
            bool multiReplica = false, WorkflowActivitySource? activitySource = null,
            IWorkflowRestartGenerations? restartGenerations = null,
            IWorkflowClusterSignal? clusterSignal = null,
            TimeProvider? timeProvider = null,
            TimeSpan? idleTimeout = null,
            SharedCluster? cluster = null,
            TimeSpan? heartbeat = null)
        {
            Store.Throws = StoreThrows;
            var scopes = new StubScopeFactory(Store, Journal);
            var blocks = new FlaggedRegistry(WorkNodeIsIdempotent, Park);
            return new WorkflowEngine(
                new WorkflowEngineSeams
                {
                    RunScopes = scopes,
                    Blocks = blocks,
                    Compiler = new Core.Compilation.WorkflowCompilationService(blocks, scopes),
                    ExecutionGate = SafeMode ? SuspendedExecutionGate.Instance : AlwaysRunningExecutionGate.Instance,
                    Metrics = new CountingMetrics(),
                    NodeObserver = NullWorkflowNodeObserver.Instance,
                    RunStateStore = RunState,
                    RestartGenerations = cluster?.Generations ?? restartGenerations,
                    ClusterSignal = cluster?.Signal ?? clusterSignal,
                    RunLockProvider = cluster?.Lock,
                    RunSlotGate = cluster?.Slots,
                    FireDedup = cluster is null ? null : new SingleReplicaFireDedup()
                },
                new WorkflowEngineSettings
                {
                    ReplicaId = "replica-1", IsMultiReplica = multiReplica || cluster is not null,
                    RunHeartbeatInterval = heartbeat ?? TimeSpan.FromSeconds(10),
                    TimeProvider = timeProvider,
                    CoordinatorIdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30)
                },
                NullLoggerFactory.Instance,
                activitySource);
        }
    }

    private sealed class RecordingClusterSignal : IWorkflowClusterSignal
    {
        private readonly List<Func<Guid, string?, CancellationToken, Task>> handlers = [];
        private readonly List<Guid> published = [];

        public int SubscriptionCount
        {
            get { lock (handlers) return handlers.Count; }
        }

        public List<Guid> Published
        {
            get { lock (published) return [.. published]; }
        }

        public async Task PublishAsync(string channel, Guid id, string? payload = null,
            CancellationToken cancellationToken = default)
        {
            lock (published) published.Add(id);
            await DeliverAsync(id, payload, cancellationToken);
        }

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> onMessage)
        {
            lock (handlers) handlers.Add(onMessage);
            return new Subscription(() => { lock (handlers) handlers.Remove(onMessage); });
        }

        public Task DeliverAsync(Guid workflowId) =>
            DeliverAsync(workflowId, null, CancellationToken.None);

        private async Task DeliverAsync(Guid workflowId, string? payload, CancellationToken token)
        {
            Func<Guid, string?, CancellationToken, Task>[] snapshot;
            lock (handlers) snapshot = [.. handlers];
            foreach (var handler in snapshot)
                await handler(workflowId, payload, token);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class SharedCluster
    {
        public RecordingClusterSignal Signal { get; } = new();
        public SharedRunLock Lock { get; } = new();
        public InProcessRestartGenerations Generations { get; } = new();
        public IWorkflowRunSlotGate Slots { get; set; } = new SingleReplicaRunSlotGate();
    }

    private sealed class SharedRunLock : IWorkflowRunLockProvider
    {
        private readonly HashSet<string> held = [];
        private int refusals;

        public volatile bool PeerHolds;

        private volatile TaskCompletionSource? releaseGate;

        public void HoldReleases() => releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseHeld() => releaseGate?.TrySetResult();

        public int Refusals => Volatile.Read(ref refusals);

        public TaskCompletionSource FirstRefusal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            lock (held)
            {
                if (!PeerHolds && held.Add(lockKey))
                    return Task.FromResult<IWorkflowRunLock?>(new Held(this, lockKey));
            }

            Interlocked.Increment(ref refusals);
            FirstRefusal.TrySetResult();
            return Task.FromResult<IWorkflowRunLock?>(null);
        }

        private sealed class Held(SharedRunLock owner, string key) : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public async ValueTask DisposeAsync()
            {
                if (owner.releaseGate is { } gate)
                    await gate.Task.ConfigureAwait(false);
                lock (owner.held) owner.held.Remove(key);
            }
        }
    }

    private sealed class HangingFirstSlotGate : IWorkflowRunSlotGate
    {
        private int asks;
        private readonly TaskCompletionSource<bool> admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Admit() => admitted.TrySetResult(true);

        public Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct)
        {
            if (Interlocked.Increment(ref asks) > 1)
                return Task.FromResult(true);
            Asked.TrySetResult();
            return admitted.Task;
        }

        public Task ReleaseAsync(Guid workflowId, string slotToken) => Task.CompletedTask;
    }

    internal sealed class EngineClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public List<ManualTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => current;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, period);
            Timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration) => current += duration;

        public void FireTimers()
        {
            foreach (var timer in Timers.ToArray())
                timer.Fire();
        }

        public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
        {
            public TimeSpan Period { get; } = period;
            public bool Disposed { get; private set; }

            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class SuspendedExecutionGate : IWorkflowExecutionGate
    {
        public static SuspendedExecutionGate Instance { get; } = new();

        public bool IsExecutionSuspended => true;
    }

    private sealed class RecordingStore(Dictionary<Guid, Workflow> workflows) : IWorkflowStore
    {
        public List<Guid> VersionLoads { get; } = [];
        public Dictionary<Guid, WorkflowVersion> Versions { get; } = [];
        public bool Throws { get; set; }

        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            if (Throws) throw new InvalidOperationException("the database is not there");
            return Task.FromResult(workflows.GetValueOrDefault(workflowId));
        }

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken)
        {
            VersionLoads.Add(versionId);
            return Task.FromResult(Versions.GetValueOrDefault(versionId));
        }

        public Task<WorkflowVersion?> LoadVersionNumberedAsync(
            Guid workflowId, int versionNumber, CancellationToken cancellationToken) =>
            Task.FromResult(Versions.Values.FirstOrDefault(
                v => v.WorkflowId == workflowId && v.VersionNumber == versionNumber));

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
            Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<(Guid, string)?>(null);
    }

    private sealed class RecordingJournal : IWorkflowRunJournal
    {
        public List<WorkflowExecutionLog> Opened { get; } = [];

        public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken)
        {
            Opened.Add(log);
            return Task.CompletedTask;
        }

        public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowExecutionLog?>(null);

        public Task SaveRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveRunDetailsAsync(
            Guid? executionId, bool recordLogEntries,
            IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
            IReadOnlyList<WorkflowExecutionStep> executionSteps,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingRunState : IWorkflowRunStateStore
    {
        public WorkflowRunStateRow? Row { get; set; }
        public List<Guid> Completed { get; } = [];

        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            Task.FromResult(Guid.NewGuid());

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            Task.FromResult(Row?.ExecutionId == executionId ? Row : null);

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> CompleteAsync(
            Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason,
            CancellationToken cancellationToken)
        {
            Completed.Add(executionId);
            return Task.FromResult(true);
        }
    }

    private sealed class StubScopeFactory(IWorkflowStore store, IWorkflowRunJournal journal) : IWorkflowRunScopeFactory
    {
        public IWorkflowRunScope CreateScope() => new Scope(store, journal);

        private sealed class Scope(IWorkflowStore store, IWorkflowRunJournal journal) : IWorkflowRunScope
        {
            public IServiceProvider Services { get; } = new EmptyServices();
            public IWorkflowStore Store { get; } = store;
            public IWorkflowRunJournal Journal { get; } = journal;
            public IWorkflowAuditSink Audit { get; } = new SilentAudit();
            public IWorkflowAuthorization Authorization { get; } = new PermissiveAuthorization();
            public void Dispose() { }
        }

        private sealed class EmptyServices : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }

    private sealed class SilentAudit : IWorkflowAuditSink
    {
        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class PermissiveAuthorization : IWorkflowAuthorization
    {
        public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FlaggedRegistry(bool idempotent, Func<CancellationToken, Task>? park = null)
        : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId switch
        {
            "Work" => new WorkFactory(idempotent, park),
            "Once" => new WorkFactory(false, null, "Once"),
            _ => null
        };

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() =>
            [new WorkFactory(idempotent, park), new WorkFactory(false, null, "Once")];
    }

    private sealed class WorkFactory(bool idempotent, Func<CancellationToken, Task>? park, string id = "Work")
        : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent { get; } = idempotent;
        public IWorkflowBlockPlugin Create() => park is null
            ? throw new NotSupportedException("Never created unless a test wants a run to stay in flight.")
            : new ParkingBlock(park);
    }

    private sealed class ParkingBlock(Func<CancellationToken, Task> park) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await park(cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class CountingMetrics : IWorkflowMetrics
    {
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }
}
