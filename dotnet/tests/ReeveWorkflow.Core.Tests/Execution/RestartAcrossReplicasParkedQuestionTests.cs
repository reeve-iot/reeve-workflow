using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RestartAcrossReplicasParkedQuestionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly Guid WfId = Guid.NewGuid();

    [Fact]
    public async Task A_restart_on_a_peer_while_this_one_asks_about_parked_sleeps_supersedes_it()
    {
        var locks = new SharedLock();
        var bus = new Bus();
        var metrics = new RecordingMetrics();
        var generations = new InProcessRestartGenerations();
        using var a = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: bus.For("A"),
            restartChannel: "restart", restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        using var b = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: bus.For("B"),
            restartChannel: "restart", restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var question = new HeldQuestion();
        var aAcquire = a.AcquireAsync(question, null, default);
        await question.Asked.Task.WaitAsync(Patience);

        var bDecision = await b.AcquireAsync(null, null, default).WaitAsync(Patience);

        question.Release.SetResult();
        var aDecision = await aAcquire.WaitAsync(Patience);

        bDecision.Lease.Should().NotBeNull(
            $"B outcome={bDecision.Outcome}/{bDecision.Reason}; A lease cancelled={aDecision.Lease?.Token.IsCancellationRequested}");
    }

    [Fact]
    public async Task A_restart_on_a_peer_while_this_one_runs_supersedes_it()
    {
        var locks = new SharedLock();
        var bus = new Bus();
        var metrics = new RecordingMetrics();
        var generations = new InProcessRestartGenerations();
        using var a = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: bus.For("A"),
            restartChannel: "restart", restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        using var b = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: bus.For("B"),
            restartChannel: "restart", restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var aDecision = await a.AcquireAsync(null, null, default);
        var aLease = aDecision.Lease!;
        aLease.Token.Register(() => _ = Task.Run(async () => await aLease.DisposeAsync()));

        var bDecision = await b.AcquireAsync(null, null, default).WaitAsync(Patience);
        bDecision.Lease.Should().NotBeNull($"B outcome={bDecision.Outcome}/{bDecision.Reason}");
    }

    [Fact]
    public async Task A_sleeper_is_not_resumed_on_a_peer_while_a_restart_trigger_is_waiting_for_the_parked_answer()
    {
        var locks = new SharedLock();
        var bus = new Bus();
        var metrics = new RecordingMetrics();
        var generations = new InProcessRestartGenerations();
        using var a = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: bus.For("A"),
            restartChannel: "restart", restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        using var b = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: bus.For("B"),
            restartChannel: "restart", restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var question = new HeldQuestion();
        var bAcquire = b.AcquireAsync(question, null, default);
        await question.Asked.Task.WaitAsync(Patience);

        (await a.TryAcquireForResumeAsync(default)).Should().BeNull(
            "a resumed segment is generation zero, and a trigger waiting anywhere is newer than it");

        question.Release.SetResult();
        var bDecision = await bAcquire.WaitAsync(Patience);

        bDecision.Lease.Should().NotBeNull($"B outcome={bDecision.Outcome}/{bDecision.Reason}");
        await bDecision.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_restart_that_finds_the_lock_held_by_a_run_newer_than_it_leaves_that_run_alone()
    {
        var locks = new SharedLock();
        var bus = new Bus();
        var metrics = new RecordingMetrics();
        var generations = new InProcessRestartGenerations();
        using var a = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: bus.For("A"),
            restartChannel: "restart", restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));
        using var b = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, metrics,
            logger: null, lockProvider: locks, slotGate: null, isMultiReplica: true, clusterSignal: bus.For("B"),
            restartChannel: "restart", restartGenerations: generations, restartWatchInterval: TimeSpan.FromMilliseconds(50));

        var question = new HeldQuestion();
        var bAcquire = b.AcquireAsync(question, null, default);
        await question.Asked.Task.WaitAsync(Patience);

        var holder = (await a.AcquireAsync(null, null, default)).Lease!;

        question.Release.SetResult();
        var bDecision = await bAcquire.WaitAsync(Patience);

        bDecision.Lease.Should().BeNull();
        holder.Token.IsCancellationRequested.Should().BeFalse("a run newer than the trigger is not the trigger's to cancel");
        await holder.DisposeAsync();
    }

    [Fact]
    public async Task A_sleeper_does_not_resume_while_a_restart_trigger_on_this_replica_is_on_its_way()
    {
        var question = new HeldQuestion();
        using var a = new WorkflowRunCoordinator(WfId, WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, new RecordingMetrics());

        var trigger = a.AcquireAsync(question, null, default);
        await question.Asked.Task.WaitAsync(Patience);

        (await a.TryAcquireForResumeAsync(default)).Should().BeNull(
            "the trigger's cancel pass has already gone by, so a lease taken now would be one it waits out");

        question.Release.SetResult();
        var decision = await trigger.WaitAsync(Patience);
        decision.Lease.Should().NotBeNull();
        await decision.Lease!.DisposeAsync();
    }

    private sealed class HeldQuestion : IWorkflowParkedSleepStore
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            Asked.TrySetResult();
            await Release.Task;
            return false;
        }
        public Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowParkedSleep>>([]);
        public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> HasRunStartedSinceAsync(Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class SharedLock : IWorkflowRunLockProvider
    {
        public int Held;
        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IWorkflowRunLock?>(Interlocked.CompareExchange(ref Held, 1, 0) == 0 ? new Lock(this) : null);
        private sealed class Lock(SharedLock owner) : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
            public ValueTask DisposeAsync() { Interlocked.Exchange(ref owner.Held, 0); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Bus
    {
        private readonly List<(string Replica, string Channel, Func<Guid, string?, CancellationToken, Task> Handler)> subs = [];
        private readonly List<string> channels = [];
        public IWorkflowClusterSignal For(string replica) => new Signal(this, replica);

        public IReadOnlyList<string> Channels { get { lock (subs) return channels.ToList(); } }
        private sealed class Signal(Bus bus, string replica) : IWorkflowClusterSignal
        {
            public Task PublishAsync(string channel, Guid id, string? payload = null, CancellationToken cancellationToken = default)
            {
                List<Func<Guid, string?, CancellationToken, Task>> targets;
                lock (bus.subs)
                {
                    bus.channels.Add(channel);
                    targets = bus.subs.Where(s => s.Replica != replica && s.Channel == channel).Select(s => s.Handler).ToList();
                }
                foreach (var t in targets) t(id, payload, default);
                return Task.CompletedTask;
            }
            public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
            {
                lock (bus.subs)
                {
                    bus.channels.Add(channel);
                    bus.subs.Add((replica, channel, handler));
                }
                return new Nop();
            }
        }
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }
}
