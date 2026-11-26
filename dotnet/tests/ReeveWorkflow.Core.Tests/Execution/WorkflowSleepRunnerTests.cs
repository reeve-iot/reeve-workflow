using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowSleepRunnerTests
{
    private const string ThisReplica = "replica-a";
    private const string NodeId = "sleep-node";

    private static WorkflowSleepRunner RunnerOver(FakeStateStore store, int pollMs = 20) =>
        new(store, NullLogger.Instance, new WorkflowSleepOptions
        {
            CancellationPollInterval = TimeSpan.FromMilliseconds(pollMs),
            ReplicaId = ThisReplica
        });

    private static Dictionary<string, object?> ForMs(long ms) => new() { ["DurationMs"] = ms };

    [Fact]
    public async Task ARunCancelledInTheStoreStopsSleepingInsteadOfWaitingOutItsTimer()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        store.CancelAfterReads(2);

        var act = () => RunnerOver(store).ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "nobody can push a cancellation to a replica that is asleep, so it has to go and look");
    }

    [Fact]
    public async Task ACancelledSleeperStillLeavesTheRowOutOfSleeping()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        store.CancelAfterReads(2);

        try
        {
            await RunnerOver(store).ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }

        store.Transitions.Should().ContainSingle(t =>
            t.FromStatus == WorkflowRunStateStatus.Sleeping && t.ToStatus == WorkflowRunStateStatus.Running,
            "a row left Sleeping would be reclaimed and the run would happen twice");
    }

    [Fact]
    public async Task AnAbandonedSleepLeavesTheRowOutOfSleepingEvenThoughItsOwnTokenIsGone()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        using var caller = new CancellationTokenSource(60);

        var act = () => RunnerOver(store).ExecuteAsync(ForMs(60_000), NodeId, executionId, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        store.Transitions.Should().ContainSingle(t =>
            t.FromStatus == WorkflowRunStateStatus.Sleeping && t.ToStatus == WorkflowRunStateStatus.Running,
            "the token that ended the sleep must not also stop the row from coming back out of it");
    }

    [Fact]
    public async Task ASleepItsHostStopsLeavesTheRowSleepingUntilTheSameWakeUpTime()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        using var hostStopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var runner = new WorkflowSleepRunner(store, NullLogger.Instance, new WorkflowSleepOptions
        {
            CancellationPollInterval = TimeSpan.FromMilliseconds(20),
            ReplicaId = ThisReplica,
            HostStopping = hostStopping.Token
        });

        var sleep = runner.ExecuteAsync(ForMs(60_000), NodeId, executionId, caller.Token);
        using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (!store.Transitions.Any(t => t.ToStatus == WorkflowRunStateStatus.Sleeping))
                await Task.Delay(5, patience.Token);
        }
        await hostStopping.CancelAsync();
        await caller.CancelAsync();

        await sleep.Awaiting(s => s.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<OperationCanceledException>();
        store.Transitions.Should().NotContain(t => t.ToStatus == WorkflowRunStateStatus.Running,
            "the handover after the stop copies the row as it stands, and only a Sleeping row keeps its wake-up time");
    }

    [Fact]
    public async Task ASleepWhoseBlockAloneIsCancelledDuringAStopGoesBackToRunning()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        using var hostStopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var block = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
        var runner = new WorkflowSleepRunner(store, NullLogger.Instance, new WorkflowSleepOptions
        {
            CancellationPollInterval = TimeSpan.FromMilliseconds(20),
            ReplicaId = ThisReplica,
            HostStopping = hostStopping.Token
        });
        WorkflowRunWrites.Begin(executionId, recoverable: true, caller: caller.Token);

        var sleep = runner.ExecuteAsync(ForMs(60_000), NodeId, executionId, block.Token);
        using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while (!store.Transitions.Any(t => t.ToStatus == WorkflowRunStateStatus.Sleeping))
                await Task.Delay(5, patience.Token);
        }
        await hostStopping.CancelAsync();
        await block.CancelAsync();

        await sleep.Awaiting(s => s.WaitAsync(TimeSpan.FromSeconds(10))).Should().ThrowAsync<OperationCanceledException>();
        store.Transitions.Should().Contain(t => t.ToStatus == WorkflowRunStateStatus.Running,
            "the run's caller did not stop it, so the walk goes on and writes the row as Running next");
    }

    [Fact]
    public async Task AShortSleepNeitherParksNorPollsMoreThanItHasTo()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();

        var outputs = await RunnerOver(store, pollMs: 5_000)
            .ExecuteAsync(ForMs(1), NodeId, executionId, CancellationToken.None);

        outputs["Completed"].Should().Be(true);
        outputs["DurationMs"].Should().Be(1);
        store.Transitions.Should().HaveCount(2, "in and out of Sleeping, and no heartbeat in between");
    }

    [Fact]
    public async Task ASleepWakesWhenItsClockPassesTheWakeUpTime()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        var clock = new TimerCountingClock();
        var expectedWake = DateTimeOffset.UtcNow.AddSeconds(60);
        var runner = new WorkflowSleepRunner(store, NullLogger.Instance, new WorkflowSleepOptions
        {
            CancellationPollInterval = TimeSpan.FromSeconds(5),
            ReplicaId = ThisReplica,
            TimeProvider = clock
        });

        var sleep = runner.ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None);
        await clock.NextTimer();
        clock.Advance(TimeSpan.FromSeconds(59));
        await clock.NextTimer();
        sleep.IsCompleted.Should().BeFalse("a second of the sleep is still left on its clock");

        clock.Advance(TimeSpan.FromSeconds(1));
        var outputs = await sleep.WaitAsync(TimeSpan.FromSeconds(10));

        outputs["Completed"].Should().Be(true);
        store.Transitions.First().WaitUntilUtc.Should().BeCloseTo(expectedWake, TimeSpan.FromSeconds(10),
            "the row's wake-up time is wall-clock time, which a store compares with its own clock, not the fake one");
    }

    [Fact]
    public async Task A_clock_that_only_tells_the_time_of_day_still_sleeps_for_real()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        var runner = new WorkflowSleepRunner(store, NullLogger.Instance, new WorkflowSleepOptions
        {
            CancellationPollInterval = TimeSpan.FromMilliseconds(20),
            ReplicaId = ThisReplica,
            TimeProvider = new FrozenWallClock()
        });

        var outputs = await runner.ExecuteAsync(ForMs(50), NodeId, executionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        outputs["Completed"].Should().Be(true);
    }

    [Fact]
    public async Task ARunWithNoStateRowStillSleepsAndReportsWhatItSlept()
    {
        var store = new FakeStateStore();

        var outputs = await RunnerOver(store).ExecuteAsync(ForMs(1), NodeId, null, CancellationToken.None);

        outputs["Completed"].Should().Be(true);
        store.Transitions.Should().BeEmpty("a host that keeps no run state gets a sleep and nothing else");
    }

    [Fact]
    public async Task ARowThatWillNotEnterSleepingIsNeverAskedToLeaveIt()
    {
        var store = new FakeStateStore { RefuseEnteringSleeping = true };
        var executionId = store.Open();

        await RunnerOver(store, pollMs: 5_000).ExecuteAsync(ForMs(1), NodeId, executionId, CancellationToken.None);

        store.Transitions.Should().ContainSingle()
            .Which.ToStatus.Should().Be(WorkflowRunStateStatus.Sleeping,
                "the attempt is made once; a run that never parked must not claim to be waking up");
    }

    [Fact]
    public async Task ANegativeDurationIsRefusedBeforeAnythingIsWritten()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();

        var act = () => RunnerOver(store).ExecuteAsync(ForMs(-1), NodeId, executionId, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        store.Transitions.Should().BeEmpty();
    }

    [Fact]
    public async Task ARenewalAPeerRefusedStopsTheSleepInsteadOfSleepingOnAsIfTheRowWereStillOurs()
    {
        var store = new FakeStateStore { RefuseRenewal = true };
        var executionId = store.Open();

        var act = () => RunnerOver(store)
            .ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var thrown = await act.Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.ReclaimedByPeer);
        thrown.Which.NodeId.Should().Be(NodeId);
    }

    [Fact]
    public async Task ARenewalRefusedBecauseTheRowIsGoneStopsTheSleepAndSaysSo()
    {
        var store = new FakeStateStore { RenewalFindsRowGone = true };
        var executionId = store.Open();

        var act = () => RunnerOver(store)
            .ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var thrown = await act.Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.RowMissing);
    }

    [Fact]
    public async Task ARowAPeerTookAndStartedReplayingStopsTheSleepAlthoughNoRenewalWasRefused()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        store.PeerTakesOverAfterReads(2);

        var act = () => RunnerOver(store, pollMs: 20)
            .ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var thrown = await act.Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.ReclaimedByPeer);
        thrown.Which.StateExecutionId.Should().Be(executionId);
    }

    [Fact]
    public async Task ARunAPeerFinishedStopsTheSleepAsAnEndingRatherThanAsATakeover()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        store.FinishesAfterReads(2, by: "replica-b");

        var act = () => RunnerOver(store, pollMs: 20)
            .ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var thrown = await act.Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.StatusMoved);
        thrown.Which.StateExecutionId.Should().Be(executionId);
    }

    [Fact]
    public async Task ARunThisReplicaFinishedItselfStopsTheSleepRatherThanWalkingOn()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        store.FinishesAfterReads(2, by: ThisReplica, terminal: WorkflowRunStateStatus.Failed);

        var act = () => RunnerOver(store, pollMs: 20)
            .ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var thrown = await act.Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.StatusMoved);
        thrown.Which.StateExecutionId.Should().Be(executionId);
    }

    [Fact]
    public async Task ARunWhoseRowVanishedStopsTheSleepAsAMissingRow()
    {
        var store = new FakeStateStore();
        var executionId = store.Open();
        store.VanishesAfterReads(2);

        var act = () => RunnerOver(store, pollMs: 20)
            .ExecuteAsync(ForMs(60_000), NodeId, executionId, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        var thrown = await act.Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.RowMissing);
        thrown.Which.StateExecutionId.Should().Be(executionId);
    }

    private sealed class FrozenWallClock : TimeProvider
    {
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TimerCountingClock : FakeTimeProvider
    {
        private readonly SemaphoreSlim created = new(0);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            created.Release();
            return timer;
        }

        public Task NextTimer() => created.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(
            t => t.Result ? Task.CompletedTask : throw new TimeoutException("the sleeper never started its wait"),
            TaskScheduler.Default).Unwrap();
    }

    private sealed class FakeStateStore : IWorkflowRunStateStore
    {
        private readonly ConcurrentQueue<WorkflowRunStateTransition> transitions = new();
        private Guid executionId;
        private WorkflowRunStateStatus status = WorkflowRunStateStatus.Running;
        private int reads;
        private int cancelAfterReads = int.MaxValue;
        private int peerTakesOverAfterReads = int.MaxValue;
        private int finishesAfterReads = int.MaxValue;
        private int vanishesAfterReads = int.MaxValue;
        private string? finishedBy = "replica-b";
        private WorkflowRunStateStatus finishedAs = WorkflowRunStateStatus.Completed;

        public bool RefuseEnteringSleeping { get; init; }

        public bool RefuseRenewal { get; init; }

        public bool RenewalFindsRowGone { get; init; }

        private volatile bool gone;

        private volatile string? owner = ThisReplica;

        public IReadOnlyList<WorkflowRunStateTransition> Transitions => transitions.ToList();

        public Guid Open()
        {
            executionId = Guid.NewGuid();
            return executionId;
        }

        public void CancelAfterReads(int count) => cancelAfterReads = count;

        public void PeerTakesOverAfterReads(int count) => peerTakesOverAfterReads = count;

        public void FinishesAfterReads(
            int count, string? by, WorkflowRunStateStatus terminal = WorkflowRunStateStatus.Completed)
        {
            finishesAfterReads = count;
            finishedBy = by;
            finishedAs = terminal;
        }

        public void VanishesAfterReads(int count) => vanishesAfterReads = count;

        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            Task.FromResult(Open());

        public Task<WorkflowRunStateRow?> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            var read = Interlocked.Increment(ref reads);
            if (read >= cancelAfterReads)
                status = WorkflowRunStateStatus.Cancelled;
            if (read >= peerTakesOverAfterReads)
            {
                owner = "replica-b";
                status = WorkflowRunStateStatus.Running;
            }

            if (read >= finishesAfterReads)
            {
                owner = finishedBy;
                status = finishedAs;
            }

            if (read >= vanishesAfterReads)
                gone = true;

            return Task.FromResult<WorkflowRunStateRow?>(id != executionId || gone ? null : new WorkflowRunStateRow
            {
                ExecutionId = executionId,
                WorkflowId = Guid.NewGuid(),
                Status = status,
                CurrentBlockPath = NodeId,
                VariablesJson = "{}",
                OwnedByReplicaId = owner,
                LastTransitionUtc = DateTimeOffset.UtcNow
            });
        }

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transitions.Enqueue(request);
            if (RefuseEnteringSleeping && request.ToStatus == WorkflowRunStateStatus.Sleeping)
                return Task.FromResult(false);
            if (RenewalFindsRowGone && request.FromStatus == WorkflowRunStateStatus.Sleeping
                && request.ToStatus == WorkflowRunStateStatus.Sleeping)
            {
                gone = true;
                return Task.FromResult(false);
            }
            if (RefuseRenewal && request.FromStatus == WorkflowRunStateStatus.Sleeping
                && request.ToStatus == WorkflowRunStateStatus.Sleeping)
            {
                owner = "replica-b";
                return Task.FromResult(false);
            }

            status = request.ToStatus;
            return Task.FromResult(true);
        }

        public Task<bool> CompleteAsync(
            Guid id, WorkflowRunStateStatus terminalStatus, string? failureReason, CancellationToken cancellationToken)
        {
            status = terminalStatus;
            return Task.FromResult(true);
        }
    }
}
