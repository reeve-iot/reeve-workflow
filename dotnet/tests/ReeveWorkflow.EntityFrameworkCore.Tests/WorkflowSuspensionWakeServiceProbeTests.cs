using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using NSubstitute;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class WorkflowSuspensionWakeServiceProbeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_park_signal_still_wakes_the_loop_after_many_idle_intervals_have_gone_by()
    {
        const int dueSoonPasses = 5;
        var store = new Store { Probe = call => call <= dueSoonPasses ? DueIn(TimeSpan.FromMilliseconds(5)) : Idle() };
        var signal = new WorkflowSuspensionParkSignal();
        using var service = Service(store, new Resumer(), signal, out var source);
        await StartAsync(service, source);

        await store.ProbesReached(dueSoonPasses + 1).WaitAsync(Patience);

        signal.Publish();
        await store.ProbesReached(dueSoonPasses + 2).WaitAsync(Patience);
        signal.Publish();
        await store.ProbesReached(dueSoonPasses + 3).WaitAsync(Patience);

        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_park_signal_wakes_an_idle_loop_that_waits_a_whole_poll_interval()
    {
        var store = new Store { Probe = _ => Idle() };
        var signal = new WorkflowSuspensionParkSignal();
        using var service = Service(store, new Resumer(), signal, out var source);
        await StartAsync(service, source);
        await store.ProbesReached(1).WaitAsync(Patience);

        signal.Publish();

        await store.ProbesReached(2).WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_park_that_arrives_while_a_timer_driven_pass_runs_is_not_lost()
    {
        const int timerPasses = 20;
        var signal = new WorkflowSuspensionParkSignal();
        var store = new Store
        {
            Probe = call =>
            {
                if (call % 2 == 0) signal.Publish();
                return call % 2 == 1 ? DueIn(TimeSpan.FromMilliseconds(5)) : Idle();
            }
        };
        using var service = Service(store, new Resumer(), signal, out var source);
        await StartAsync(service, source);

        await store.ProbesReached(timerPasses * 2 + 1).WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_row_that_falls_due_inside_the_poll_interval_is_taken_when_it_is_due_not_at_the_next_poll()
    {
        var store = new Store();
        var resumer = new Resumer();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddMilliseconds(500));
        using var service = Service(store, resumer, new WorkflowSuspensionParkSignal(), out var source);

        await StartAsync(service, source);

        await resumer.Entered(row.ExecutionLogId).WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_published_wake_starts_a_pass_beside_its_own_take()
    {
        var store = new Store { Probe = _ => Idle() };
        using var service = Service(store, new Resumer(), new WorkflowSuspensionParkSignal(), out var source);

        await StartAsync(service, source);

        await store.ProbesReached(1).WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    private static async Task StartAsync(WorkflowSuspensionWakeService service, WakeSource source)
    {
        await service.StartAsync(CancellationToken.None);
        await source.Subscribed.WaitAsync(Patience);
        await source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger
        });
    }

    private static WorkflowSuspensionWakeProbe Idle() =>
        new(DateTimeOffset.UtcNow, null, false, false);

    private static WorkflowSuspensionWakeProbe DueIn(TimeSpan span)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowSuspensionWakeProbe(now, now + span, false, false);
    }

    private static WorkflowSuspensionWakeService Service(
        Store store, Resumer resumer, WorkflowSuspensionParkSignal signal, out WakeSource source)
    {
        source = new WakeSource();
        var options = new WorkflowSuspensionWakeOptions { PollInterval = PollInterval, SettleFor = TimeSpan.Zero };
        return new WorkflowSuspensionWakeService(
            new WorkflowSuspensionWaker(store, resumer, NullLogger.Instance, new WorkflowCompilationService(Substitute.For<IWorkflowBlocks>(), Substitute.For<IWorkflowRunScopeFactory>()), options), options, NullLogger.Instance,
            signal, source);
    }

    private sealed class Store : IWorkflowSuspensionResumeStore
    {
        private readonly ConcurrentDictionary<Guid, WorkflowSuspensionCandidate> parked = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> reached = new();
        private int probes;

        public Func<int, WorkflowSuspensionWakeProbe>? Probe { get; init; }

        public int Probes => Volatile.Read(ref probes);

        public Task ProbesReached(int count)
        {
            var signal = reached.GetOrAdd(count, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Probes >= count)
                signal.TrySetResult();
            return signal.Task;
        }

        public WorkflowSuspensionCandidate Park(DateTimeOffset timeoutAtUtc)
        {
            var row = new WorkflowSuspensionCandidate(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "nap", WorkflowSuspensionKind.WaitForTrigger, "{}", "{}",
                DateTimeOffset.UtcNow.AddHours(-1), timeoutAtUtc);
            parked[row.SuspensionId] = row;
            return row;
        }

        public Task<WorkflowSuspensionWakeProbe?> ProbeWakeAsync(
            TimeSpan? abandonedAfter, WorkflowAbandonedTakeCursor abandonedAfterCursor, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref probes);
            var now = DateTimeOffset.UtcNow;
            var answer = Probe?.Invoke(call)
                ?? new WorkflowSuspensionWakeProbe(now, parked.Values.Select(row => (DateTimeOffset?)row.TimeoutAtUtc).Min(), false, false);
            foreach (var (count, signal) in reached)
                if (call >= count)
                    signal.TrySetResult();
            return Task.FromResult<WorkflowSuspensionWakeProbe?>(answer);
        }

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new WorkflowSuspensionScan(
                now, [.. parked.Values.Where(row => row.TimeoutAtUtc <= now)], 0));
        }

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(parked.TryRemove(suspensionId, out var row)
                ? new ResumeContext
                {
                    SuspensionId = row.SuspensionId, ExecutionLogId = row.ExecutionLogId, WorkflowId = row.WorkflowId,
                    BlockId = row.BlockId, Kind = row.Kind, OutcomeJson = outcomeJson,
                    StatePayloadJson = row.StatePayloadJson, SuspendedAtUtc = row.SuspendedAtUtc
                }
                : null);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class Resumer : IWorkflowRunResumer
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> entered = new();

        public Task Entered(Guid executionLogId) => Signal(executionLogId).Task;

        private TaskCompletionSource Signal(Guid executionLogId) =>
            entered.GetOrAdd(executionLogId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken)
        {
            Signal(ctx.ExecutionLogId).TrySetResult();
            return Task.FromResult(WorkflowResumeOutcome.Resumed);
        }
    }

    private sealed class WakeSource : IWorkflowSuspensionWakeSource
    {
        private readonly List<Func<WorkflowSuspensionWake, CancellationToken, Task>> handlers = [];
        private readonly TaskCompletionSource subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Subscribed => subscribed.Task;

        public IDisposable Subscribe(Func<WorkflowSuspensionWake, CancellationToken, Task> onWake)
        {
            lock (handlers)
                handlers.Add(onWake);
            subscribed.TrySetResult();
            return new Subscription(() => { lock (handlers) handlers.Remove(onWake); });
        }

        public async Task PublishAsync(WorkflowSuspensionWake wake)
        {
            Func<WorkflowSuspensionWake, CancellationToken, Task>[] current;
            lock (handlers)
                current = [.. handlers];
            foreach (var handler in current)
                await handler(wake, CancellationToken.None);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
