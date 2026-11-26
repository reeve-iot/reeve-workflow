using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunCoordinatorReconfigureTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task B1_A_queued_run_is_admitted_after_the_limit_is_raised_and_the_running_one_finishes()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 5);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);
        waiter.IsCompleted.Should().BeFalse("it is queued behind the one permit");

        coordinator.Reconfigure(WorkflowMode.Queued, maxConcurrentRuns: 2, maxQueuedRuns: 5, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        (await Task.WhenAny(waiter, Task.Delay(Soon))).Should().BeSameAs(waiter,
            "the waiter sits on the semaphore that existed when it queued, and the release landed on the one "
            + "Reconfigure swapped in — so it waits for ever while later triggers barge past it");
        (await waiter).Lease.Should().NotBeNull();
    }

    [Fact]
    public async Task B1_A_queued_run_is_admitted_after_the_mode_is_changed_and_the_running_one_finishes()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 5);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);

        coordinator.Reconfigure(WorkflowMode.Single, maxConcurrentRuns: 1, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        (await Task.WhenAny(waiter, Task.Delay(Soon))).Should().BeSameAs(waiter);
    }

    [Fact]
    public async Task A_queued_waiter_that_starts_after_the_mode_became_single_asks_for_the_cluster_lock_not_a_slot()
    {
        var peerHoldsTheLock = new PeerHoldsTheRunLock();
        var slots = new CountingSlotGate();
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Queued, maxConcurrentRuns: 1, maxQueuedRuns: 5, WorkflowOverflowBehavior.Drop,
            NullWorkflowMetrics.Instance, lockProvider: peerHoldsTheLock, slotGate: slots, isMultiReplica: true);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);

        coordinator.Reconfigure(WorkflowMode.Single, maxConcurrentRuns: 1, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        (await Task.WhenAny(waiter, Task.Delay(Soon))).Should().BeSameAs(waiter);
        var decision = await waiter;
        decision.Lease.Should().BeNull("a peer holds the Single workflow's cluster lock");
        decision.Reason.Should().Be("single_mode_already_running_on_another_replica");
        peerHoldsTheLock.Asked.Should().Be(1);
        slots.Asked.Should().Be(1, "only the run from before the change took a slot");
        coordinator.ActiveRunCount.Should().Be(0);
        (await coordinator.TryAcquireForResumeAsync(CancellationToken.None)).Should().BeNull(
            "the waiter gave its permit back, so the lock is what refuses this one");
        peerHoldsTheLock.Asked.Should().Be(2);
    }

    private sealed class PeerHoldsTheRunLock : IWorkflowRunLockProvider
    {
        private int asked;

        public int Asked => Volatile.Read(ref asked);

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult<IWorkflowRunLock?>(null);
        }
    }

    private sealed class CountingSlotGate : IWorkflowRunSlotGate
    {
        private int asked;

        public int Asked => Volatile.Read(ref asked);

        public Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct)
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(true);
        }

        public Task ReleaseAsync(Guid workflowId, string slotToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task B2_Shrinking_a_parallel_limit_admits_nothing_until_the_active_runs_drop_below_it()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Parallel, 3, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        var held = new List<WorkflowRunLease>();
        for (var i = 0; i < 3; i++)
            held.Add((await coordinator.AcquireAsync(CancellationToken.None)).Lease!);

        coordinator.Reconfigure(WorkflowMode.Parallel, maxConcurrentRuns: 2, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);

        (await coordinator.AcquireAsync(CancellationToken.None)).Outcome.Should().Be(WorkflowAdmissionOutcomes.Dropped,
            "three runs are active against a limit of two; a fresh semaphore with two permits would admit two more");
        coordinator.ActiveRunCount.Should().Be(3);

        await held[0].DisposeAsync();
        (await coordinator.AcquireAsync(CancellationToken.None)).Outcome.Should().Be(WorkflowAdmissionOutcomes.Dropped,
            "two are still active, which is the whole budget");

        await held[1].DisposeAsync();
        var admitted = await coordinator.AcquireAsync(CancellationToken.None);
        admitted.Outcome.Should().Be(WorkflowAdmissionOutcomes.Started, "one is active, so there is room for one");
        await admitted.Lease!.DisposeAsync();
        await held[2].DisposeAsync();
    }

    [Fact]
    public async Task B2_Growing_a_parallel_limit_admits_exactly_the_difference()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Parallel, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        var first = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        coordinator.Reconfigure(WorkflowMode.Parallel, maxConcurrentRuns: 3, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);

        (await coordinator.AcquireAsync(CancellationToken.None)).Lease.Should().NotBeNull();
        (await coordinator.AcquireAsync(CancellationToken.None)).Lease.Should().NotBeNull();
        (await coordinator.AcquireAsync(CancellationToken.None)).Outcome.Should().Be(WorkflowAdmissionOutcomes.Dropped,
            "one was already running, so a limit of three has room for two more, not three");
        coordinator.ActiveRunCount.Should().Be(3);
        await first.DisposeAsync();
    }

    [Fact]
    public async Task B2_Releasing_the_leases_taken_before_a_shrink_never_over_fills_the_budget()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Parallel, 3, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        var held = new List<WorkflowRunLease>();
        for (var i = 0; i < 3; i++)
            held.Add((await coordinator.AcquireAsync(CancellationToken.None)).Lease!);
        coordinator.Reconfigure(WorkflowMode.Parallel, 2, 0, WorkflowOverflowBehavior.Drop);

        var raced = new List<WorkflowRunLease>();
        for (var i = 0; i < 2; i++)
        {
            var decision = await coordinator.AcquireAsync(CancellationToken.None);
            if (decision.Lease is not null) raced.Add(decision.Lease);
        }

        raced.Should().BeEmpty("three runs are in flight against a budget that has just shrunk to two");
        foreach (var lease in raced) await lease.DisposeAsync();

        foreach (var lease in held)
            await lease.DisposeAsync();

        var admitted = new List<WorkflowRunLease>();
        for (var i = 0; i < 3; i++)
        {
            var decision = await coordinator.AcquireAsync(CancellationToken.None);
            if (decision.Lease is not null) admitted.Add(decision.Lease);
        }

        admitted.Should().HaveCount(2, "the budget is two once everything before the shrink has finished");
        foreach (var lease in admitted) await lease.DisposeAsync();
    }

    [Fact]
    public async Task B3_A_trigger_that_joins_the_queue_after_a_newer_one_still_stands_aside_for_it()
    {
        var metrics = new GateTheFirstOutcome();
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics);
        var running = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        var older = Task.Run(() => coordinator.AcquireAsync(CancellationToken.None));
        await metrics.FirstOutcomeInFlight.WaitAsync(Soon);
        var newer = coordinator.AcquireAsync(CancellationToken.None);

        WorkflowRunDecision newerDecision;
        try
        {
            await running.DisposeAsync();
            newerDecision = await newer.WaitAsync(Soon);
            newerDecision.Lease.Should().NotBeNull("it is the newest trigger and the permit was free");
        }
        finally
        {
            metrics.ReleaseFirstOutcome();
        }

        await newerDecision.Lease!.DisposeAsync();

        var olderDecision = await older.WaitAsync(Soon);
        olderDecision.Lease.Should().BeNull("a newer trigger arrived before this one ever reached the queue");
        olderDecision.Outcome.Should().Be(WorkflowAdmissionOutcomes.Skipped);
        olderDecision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
    }

    [Fact]
    public async Task A_restart_storm_that_outlives_all_its_newer_triggers_still_runs_the_one_left_standing()
    {
        var publishes = new GateTheseRestartPublishes(2, 4);
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop,
            NullWorkflowMetrics.Instance, clusterSignal: publishes);

        var holder = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        var oldest = Task.Run(() => coordinator.AcquireAsync(CancellationToken.None));
        await publishes.Entered(2).WaitAsync(Soon);

        var parked = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(100);

        using var givesUp = new CancellationTokenSource();
        var newest = Task.Run(() => coordinator.AcquireAsync(givesUp.Token));
        await publishes.Entered(4).WaitAsync(Soon);

        await holder.DisposeAsync();
        await Task.Delay(300);
        parked.IsCompleted.Should().BeFalse("a newer trigger is still waiting, so it gives way rather than run or leave");

        await givesUp.CancelAsync();
        publishes.Release(4);
        var walkingAway = async () => await newest;
        await walkingAway.Should().ThrowAsync<OperationCanceledException>();

        var decision = await parked.WaitAsync(Soon);
        decision.Lease.Should().NotBeNull(
            "every newer trigger is gone, so this one is the newest left, and nothing has run since it arrived");

        publishes.Release(2);
        var oldestDecision = await oldest.WaitAsync(Soon);
        oldestDecision.Lease.Should().BeNull("a newer trigger has started");
        oldestDecision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        await decision.Lease!.DisposeAsync();
    }

    private sealed class GateTheseRestartPublishes : IWorkflowClusterSignal
    {
        private readonly Dictionary<string, (TaskCompletionSource Entered, TaskCompletionSource Release)> _gates = [];

        public GateTheseRestartPublishes(params int[] generations)
        {
            foreach (var n in generations)
            {
                _gates[n.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                     new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }

        public Task Entered(int generation) => _gates[generation.ToString(System.Globalization.CultureInfo.InvariantCulture)].Entered.Task;

        public void Release(int generation) => _gates[generation.ToString(System.Globalization.CultureInfo.InvariantCulture)].Release.TrySetResult();

        public Task PublishAsync(string channel, Guid id, string? payload = null, CancellationToken ct = default)
        {
            if (payload is not null && _gates.TryGetValue(payload, out var gate) && gate.Entered.TrySetResult())
            {
                gate.Release.Task.Wait(TimeSpan.FromSeconds(30));
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
            => new NotSubscribed();

        private sealed class NotSubscribed : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task A_queued_trigger_whose_metric_throws_gives_its_place_in_the_queue_back()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, new ThrowOnQueued());
        var holder = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        await coordinator.Awaiting(c => c.AcquireAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        coordinator.QueueDepth.Should().Be(0);
        await holder.DisposeAsync();
    }

    [Theory]
    [InlineData(WorkflowMode.Single)]
    [InlineData(WorkflowMode.Restart)]
    [InlineData(WorkflowMode.Parallel)]
    [InlineData(WorkflowMode.Queued)]
    public async Task A_trigger_whose_started_metric_throws_gives_its_place_back(WorkflowMode mode)
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), mode, 1, mode == WorkflowMode.Queued ? 5 : 0, WorkflowOverflowBehavior.Drop, new ThrowOnStarted());

        await coordinator.Awaiting(c => c.AcquireAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        coordinator.ActiveRunCount.Should().Be(0);
        await using var next = await coordinator.TryAcquireForResumeAsync(CancellationToken.None);
        next.Should().NotBeNull("the place is free again");
    }

    [Fact]
    public async Task A_restart_whose_started_word_throws_on_the_way_out_still_runs()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            lockProvider: null, slotGate: null, isMultiReplica: true, clusterSignal: new ThrowsOnPublish(),
            restartChannel: "restart");

        var decision = await coordinator.AcquireAsync(CancellationToken.None);

        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_queued_waiter_that_starts_after_the_mode_became_restart_stands_aside_for_a_restart_trigger_that_came_after_it()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 5);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);

        coordinator.Reconfigure(WorkflowMode.Restart, maxConcurrentRuns: 1, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);
        var trigger = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);
        await running.Lease!.DisposeAsync();

        var older = await waiter.WaitAsync(Soon);
        older.Lease.Should().BeNull();
        older.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        var newer = await trigger.WaitAsync(Soon);
        newer.Lease.Should().NotBeNull();
        await newer.Lease!.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_queued_waiter_that_starts_after_the_mode_became_restart_stands_aside_for_a_peer_trigger_issued_after_it(
        bool started)
    {
        var workflowId = Guid.NewGuid();
        var generations = new InProcessRestartGenerations();
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: null, slotGate: null, isMultiReplica: false, clusterSignal: null,
            restartChannel: null, restartGenerations: generations);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);

        coordinator.Reconfigure(WorkflowMode.Restart, maxConcurrentRuns: 1, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);
        var peer = await generations.IssueAsync(workflowId, TimeSpan.FromMinutes(1), CancellationToken.None);
        if (started) await generations.StartAsync(workflowId, peer, CancellationToken.None);
        await running.Lease!.DisposeAsync();

        var older = await waiter.WaitAsync(Soon);
        older.Lease.Should().BeNull("the peer's trigger came after this waiter, and its run is the one that should start");
        older.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
    }

    [Fact]
    public async Task A_queued_waiter_that_starts_after_the_mode_became_restart_runs_when_the_peer_trigger_after_it_died_waiting()
    {
        var workflowId = Guid.NewGuid();
        var generations = new InProcessRestartGenerations();
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: null, slotGate: null, isMultiReplica: false, clusterSignal: null,
            restartChannel: null, restartGenerations: generations);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);

        coordinator.Reconfigure(WorkflowMode.Restart, maxConcurrentRuns: 1, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);
        await generations.IssueAsync(workflowId, TimeSpan.Zero, CancellationToken.None);
        await running.Lease!.DisposeAsync();

        var promoted = await waiter.WaitAsync(Soon);
        promoted.Lease.Should().NotBeNull("nothing newer is waiting or has run, so standing aside would lose both");
        await promoted.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_queued_waiter_promoted_under_restart_whose_caller_gives_up_as_it_takes_a_generation_leaves_nothing_waiting()
    {
        var workflowId = Guid.NewGuid();
        using var caller = new CancellationTokenSource();
        var generations = new CancelWhileIssuing(new InProcessRestartGenerations(), caller);
        using var coordinator = new WorkflowRunCoordinator(
            workflowId, WorkflowMode.Queued, 1, 5, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance,
            logger: null, lockProvider: null, slotGate: null, isMultiReplica: false, clusterSignal: null,
            restartChannel: null, restartGenerations: generations);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(caller.Token);
        await Task.Delay(50);

        coordinator.Reconfigure(WorkflowMode.Restart, maxConcurrentRuns: 1, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        await waiter.Awaiting(w => w.WaitAsync(Soon)).Should().ThrowAsync<OperationCanceledException>();
        (await generations.ReadAsync(workflowId, CancellationToken.None)).NewestWaiting.Should().BeNull();
        var next = await coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Soon);
        next.Lease.Should().NotBeNull("the waiter gave its permit back");
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_queued_waiter_stops_waiting_once_its_workflow_was_forgotten()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 5);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);

        coordinator.Dispose();

        await waiter.Awaiting(w => w.WaitAsync(Soon)).Should().ThrowAsync<ObjectDisposedException>();
        await running.Lease!.DisposeAsync();
    }

    private sealed class CancelWhileIssuing(IWorkflowRestartGenerations inner, CancellationTokenSource caller)
        : IWorkflowRestartGenerations
    {
        private int reads;

        public async Task<long> IssueAsync(Guid workflowId, TimeSpan liveFor, CancellationToken cancellationToken)
        {
            var generation = await inner.IssueAsync(workflowId, liveFor, CancellationToken.None);
            await caller.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return generation;
        }

        public Task TouchAsync(Guid workflowId, long generation, TimeSpan liveFor, CancellationToken cancellationToken) =>
            inner.TouchAsync(workflowId, generation, liveFor, cancellationToken);

        public Task RetireAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.RetireAsync(workflowId, generation, cancellationToken);

        public Task StartAsync(Guid workflowId, long generation, CancellationToken cancellationToken) =>
            inner.StartAsync(workflowId, generation, cancellationToken);

        public Task<WorkflowRestartGenerationState> ReadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref reads) == 1
                ? Task.FromException<WorkflowRestartGenerationState>(new InvalidOperationException("The store is away."))
                : inner.ReadAsync(workflowId, cancellationToken);
    }

    private sealed class ThrowOnStarted : IWorkflowMetrics
    {
        public void RecordSupersession(Guid workflowId, string mode, string outcome)
        {
            if (outcome == WorkflowAdmissionOutcomes.Started)
                throw new InvalidOperationException("The metrics exporter is away.");
        }

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class ThrowsOnPublish : IWorkflowClusterSignal
    {
        public Task PublishAsync(string channel, Guid id, string? payload, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The broker is away.");

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class ThrowOnQueued : IWorkflowMetrics
    {
        public void RecordSupersession(Guid workflowId, string mode, string outcome)
        {
            if (outcome == WorkflowAdmissionOutcomes.Queued)
                throw new InvalidOperationException("The metrics exporter is away.");
        }

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class GateTheFirstOutcome : IWorkflowMetrics
    {
        private readonly TaskCompletionSource _inFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task FirstOutcomeInFlight => _inFlight.Task;

        public void ReleaseFirstOutcome() => _release.TrySetResult();

        public void RecordSupersession(Guid workflowId, string mode, string outcome)
        {
            if (outcome != WorkflowAdmissionOutcomes.Restarted) return;
            if (Interlocked.Increment(ref _calls) != 1) return;
            _inFlight.TrySetResult();
            _release.Task.Wait(TimeSpan.FromSeconds(30));
        }

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    [Fact]
    public async Task B3_A_restart_trigger_that_gives_up_while_waiting_does_not_supersede_the_one_still_waiting()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        var first = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;

        var second = coordinator.AcquireAsync(CancellationToken.None);
        using var goesAway = new CancellationTokenSource();
        var third = coordinator.AcquireAsync(goesAway.Token);
        await goesAway.CancelAsync();
        var awaitingThird = async () => await third;
        await awaitingThird.Should().ThrowAsync<OperationCanceledException>();

        await first.DisposeAsync();

        var secondDecision = await second.WaitAsync(Soon);
        secondDecision.Lease.Should().NotBeNull(
            "the newest trigger abandoned its wait, so the one still waiting is the newest one left");
        await secondDecision.Lease!.DisposeAsync();
    }

    [Fact]
    public void Reconfiguring_a_coordinator_whose_workflow_was_deleted_under_it_is_a_no_op()
    {
        var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Parallel, 3, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        coordinator.Dispose();

        var shrink = () => coordinator.Reconfigure(WorkflowMode.Parallel, 1, 0, WorkflowOverflowBehavior.Drop);

        shrink.Should().NotThrow("a disposed coordinator has no budget left to resize");
    }

    [Fact]
    public void Growing_the_budget_of_a_coordinator_whose_workflow_was_deleted_is_a_no_op()
    {
        var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Parallel, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        coordinator.Dispose();

        var grow = () => coordinator.Reconfigure(WorkflowMode.Parallel, 4, 0, WorkflowOverflowBehavior.Drop);

        grow.Should().NotThrow();
    }

    [Fact]
    public async Task B3_A_burst_of_restart_triggers_leaves_only_the_newest_one_running()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        var first = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = first.Token.Register(() => firstCancelled.TrySetResult());

        var second = coordinator.AcquireAsync(CancellationToken.None);
        var third = coordinator.AcquireAsync(CancellationToken.None);
        first.Token.IsCancellationRequested.Should().BeTrue("a Restart trigger cancels before it queues");
        await firstCancelled.Task.WaitAsync(Soon);
        cancellationRegistration.Dispose();
        await first.DisposeAsync();

        var secondDecision = await second.WaitAsync(Soon);
        secondDecision.Lease.Should().BeNull("the trigger in the middle of a burst stands aside; Restart is not a queue");
        secondDecision.Reason.Should().Be("restart_mode_superseded_by_a_newer_trigger");
        secondDecision.Outcome.Should().Be(WorkflowAdmissionOutcomes.Skipped);

        var thirdDecision = await third.WaitAsync(Soon);
        thirdDecision.Lease.Should().NotBeNull("the newest trigger is the one that runs");
        await thirdDecision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task B3_The_newest_restart_trigger_is_the_one_that_runs()
    {
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        var first = (await coordinator.AcquireAsync(CancellationToken.None)).Lease!;
        var firstMayFinish = new TaskCompletionSource();
        first.Token.Register(() => _ = Task.Run(async () => { await firstMayFinish.Task; await first.DisposeAsync(); }));

        var second = coordinator.AcquireAsync(CancellationToken.None);
        var third = coordinator.AcquireAsync(CancellationToken.None);
        firstMayFinish.SetResult();

        var thirdDecision = await third.WaitAsync(Soon);
        thirdDecision.Lease.Should().NotBeNull("the newest trigger is the one Restart keeps");
        thirdDecision.Lease!.Token.IsCancellationRequested.Should().BeFalse(
            "nothing newer arrived, so nothing may have cancelled it");

        var secondDecision = await second.WaitAsync(Soon);
        (secondDecision.Lease is null || secondDecision.Lease.Token.IsCancellationRequested).Should().BeTrue(
            "the trigger in the middle of the burst is superseded before or as it starts, never queued");

        await thirdDecision.Lease.DisposeAsync();
        if (secondDecision.Lease is not null) await secondDecision.Lease.DisposeAsync();
    }

    [Fact]
    public async Task M4_Shrinking_a_queue_below_its_depth_keeps_every_waiter_counted_and_refuses_new_ones_until_it_drains()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 5);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiters = Enumerable.Range(0, 5).Select(_ => coordinator.AcquireAsync(CancellationToken.None)).ToList();
        await Task.Delay(50);
        coordinator.QueueDepth.Should().Be(5);

        coordinator.Reconfigure(WorkflowMode.Queued, maxConcurrentRuns: 1, maxQueuedRuns: 2, WorkflowOverflowBehavior.Drop);

        coordinator.QueueDepth.Should().Be(5, "every waiter keeps its place, and the depth says how many there are");
        var late = await coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Soon);
        late.Outcome.Should().Be(WorkflowAdmissionOutcomes.Dropped, "five waiting is over a limit of two");
        late.Reason.Should().Be("queue_full");

        await running.Lease!.DisposeAsync();
        var first = await Task.WhenAny(waiters).WaitAsync(Soon);
        waiters.Remove(first);
        coordinator.QueueDepth.Should().Be(4, "one waiter started and four still wait");

        var lease = (await first).Lease!;
        while (waiters.Count > 0)
        {
            await lease.DisposeAsync();
            var next = await Task.WhenAny(waiters).WaitAsync(Soon);
            waiters.Remove(next);
            lease = (await next).Lease!;
            coordinator.QueueDepth.Should().Be(waiters.Count);
        }

        var queuedAgain = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);
        queuedAgain.IsCompleted.Should().BeFalse("under the new limit again, a trigger may queue");
        coordinator.QueueDepth.Should().Be(1);
        await lease.DisposeAsync();
        (await queuedAgain.WaitAsync(Soon)).Outcome.Should().Be(WorkflowAdmissionOutcomes.Queued);
    }

    [Fact]
    public async Task M4_A_shrunk_queue_refuses_a_trigger_the_old_limit_would_have_queued()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 5);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiters = Enumerable.Range(0, 2).Select(_ => coordinator.AcquireAsync(CancellationToken.None)).ToList();
        await Task.Delay(50);

        coordinator.Reconfigure(WorkflowMode.Queued, maxConcurrentRuns: 1, maxQueuedRuns: 2, WorkflowOverflowBehavior.Drop);

        (await coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Soon)).Reason.Should().Be("queue_full");
        await running.Lease!.DisposeAsync();
        foreach (var waiter in waiters)
            await (await waiter.WaitAsync(Soon)).Lease!.DisposeAsync();
    }

    [Fact]
    public async Task M4_A_queue_turned_off_refuses_as_disabled_rather_than_full()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 5);
        var running = await coordinator.AcquireAsync(CancellationToken.None);

        coordinator.Reconfigure(WorkflowMode.Queued, maxConcurrentRuns: 1, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);

        (await coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Soon)).Reason.Should().Be("queue_disabled");
        await running.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task M4_A_waiter_queued_before_the_mode_left_queued_starts_with_a_lease_rather_than_faulting()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 5);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiter = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);

        coordinator.Reconfigure(WorkflowMode.Parallel, maxConcurrentRuns: 1, maxQueuedRuns: 0, WorkflowOverflowBehavior.Drop);
        await running.Lease!.DisposeAsync();

        var decision = await waiter.WaitAsync(Soon);
        decision.Lease.Should().NotBeNull("the permit it waited for is its own; nothing is left to throw on the way out");
        coordinator.QueueDepth.Should().Be(0);
        await decision.Lease!.DisposeAsync();
        (await coordinator.AcquireAsync(CancellationToken.None)).Lease.Should().NotBeNull("the permit went back");
    }

    [Fact]
    public async Task M4_A_waiter_whose_caller_gives_up_leaves_the_queue_and_the_depth()
    {
        using var coordinator = Queued(maxConcurrentRuns: 1, maxQueuedRuns: 1);
        var running = await coordinator.AcquireAsync(CancellationToken.None);
        using var caller = new CancellationTokenSource();
        var waiter = coordinator.AcquireAsync(caller.Token);
        await Task.Delay(50);
        coordinator.QueueDepth.Should().Be(1);

        await caller.CancelAsync();
        await waiter.Awaiting(w => w.WaitAsync(Soon)).Should().ThrowAsync<OperationCanceledException>();

        coordinator.QueueDepth.Should().Be(0);
        var next = coordinator.AcquireAsync(CancellationToken.None);
        await Task.Delay(50);
        next.IsCompleted.Should().BeFalse("the place the cancelled waiter left is free to queue in");
        await running.Lease!.DisposeAsync();
        (await next.WaitAsync(Soon)).Lease.Should().NotBeNull();
    }

    private static WorkflowRunCoordinator Queued(int maxConcurrentRuns, int maxQueuedRuns) =>
        new(Guid.NewGuid(), WorkflowMode.Queued, maxConcurrentRuns, maxQueuedRuns,
            WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
}
