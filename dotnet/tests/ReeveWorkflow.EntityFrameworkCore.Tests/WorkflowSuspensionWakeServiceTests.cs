using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using NSubstitute;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class WorkflowSuspensionWakeServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Past = DateTimeOffset.UtcNow.AddHours(-1);

    [Fact]
    public async Task A_run_resumed_by_one_tick_does_not_hold_up_the_next_ticks_takes()
    {
        var store = new Store();
        var resumer = new Resumer();
        var slow = store.Park();
        resumer.Hold(slow.ExecutionLogId);
        using var service = Service(store, resumer);

        await service.StartAsync(CancellationToken.None);
        await resumer.Entered(slow.ExecutionLogId).WaitAsync(Patience);
        var later = store.Park();

        await resumer.Entered(later.ExecutionLogId).WaitAsync(Patience);
        resumer.Release(slow.ExecutionLogId);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_stopping_service_waits_for_the_runs_its_ticks_took()
    {
        var store = new Store();
        var resumer = new Resumer();
        var slow = store.Park();
        resumer.Hold(slow.ExecutionLogId);
        using var service = Service(store, resumer);

        await service.StartAsync(CancellationToken.None);
        await resumer.Entered(slow.ExecutionLogId).WaitAsync(Patience);
        var stopping = service.StopAsync(CancellationToken.None);
        await Task.Delay(200);

        stopping.IsCompleted.Should().BeFalse("a run this replica took is still re-entering, and stopping abandons it");
        resumer.Release(slow.ExecutionLogId);
        await stopping.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_wake_published_while_the_service_runs_moves_its_row()
    {
        var store = new Store();
        var resumer = new Resumer();
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        using var service = Service(store, resumer, source);

        await service.StartAsync(CancellationToken.None);
        await source.Subscribed.WaitAsync(Patience);
        await source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });

        await resumer.Entered(row.ExecutionLogId).WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_wake_published_the_moment_the_service_has_started_still_moves_its_row()
    {
        var store = new Store();
        var resumer = new Resumer();
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        using var service = Service(store, resumer, source);

        await service.StartAsync(CancellationToken.None);
        await source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });

        await resumer.Entered(row.ExecutionLogId).WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task A_wake_published_after_the_service_has_stopped_reaches_nothing()
    {
        var store = new Store();
        var resumer = new Resumer();
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        using var service = Service(store, resumer, source);

        await service.StartAsync(CancellationToken.None);
        await source.Subscribed.WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);

        source.Subscribers.Should().Be(0);
        await source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });

        resumer.Entered(row.ExecutionLogId).IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_subscription_is_released_once_however_many_times_the_service_lets_go()
    {
        var source = new WakeSource();
        var service = Service(new Store(), new Resumer(), source);

        await service.StartAsync(CancellationToken.None);
        source.Subscribers.Should().Be(1);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
        service.Dispose();
        service.Dispose();

        source.Releases.Should().Be(1);
    }

    [Fact]
    public async Task A_service_disposed_without_a_stop_releases_its_subscription_once()
    {
        var source = new WakeSource();
        var service = Service(new Store(), new Resumer(), source);

        await service.StartAsync(CancellationToken.None);
        service.Dispose();
        service.Dispose();

        source.Releases.Should().Be(1);
    }

    [Fact]
    public void A_service_that_never_started_has_nothing_to_release()
    {
        var source = new WakeSource();
        var service = Service(new Store(), new Resumer(), source);

        service.Dispose();

        source.Releases.Should().Be(0);
    }

    [Fact]
    public async Task A_stop_reaches_a_wake_that_is_still_re_entering_its_run()
    {
        var store = new Store();
        var resumer = new Resumer();
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        resumer.Hold(row.ExecutionLogId);
        using var service = Service(store, resumer, source);

        await service.StartAsync(CancellationToken.None);
        await source.Subscribed.WaitAsync(Patience);
        var published = source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });
        await resumer.Entered(row.ExecutionLogId).WaitAsync(Patience);

        var stopping = service.StopAsync(CancellationToken.None);
        await resumer.Cancelled(row.ExecutionLogId).WaitAsync(Patience);

        resumer.Release(row.ExecutionLogId);
        await stopping.WaitAsync(Patience);
        await published.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_stop_waits_for_a_wake_that_was_already_inside_the_waker()
    {
        var store = new Store();
        var resumer = new Resumer();
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        using var service = Service(store, resumer, source);

        await service.StartAsync(CancellationToken.None);
        await source.Subscribed.WaitAsync(Patience);

        store.HoldTheNextRead();
        var published = source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });
        await store.ReadEntered.WaitAsync(Patience);

        var stopping = service.StopAsync(CancellationToken.None);
        await Task.Delay(200);

        stopping.IsCompleted.Should().BeFalse("a wake already inside the waker is one the stop waits for");
        store.ReleaseTheRead();
        await stopping.WaitAsync(Patience);
        await published.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_stop_waits_for_a_wake_inside_the_waker_when_the_loop_never_ran()
    {
        var store = new Store();
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        using var service = Service(store, new Resumer(), source);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        store.HoldTheNextRead();
        await service.StartAsync(cancelled.Token);
        var published = source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });
        await store.ReadEntered.WaitAsync(Patience);

        var stopping = service.StopAsync(CancellationToken.None);
        await Task.Delay(200);

        stopping.IsCompleted.Should().BeFalse("the stop is the only drain, and a wake is still inside the waker");
        store.ReleaseTheRead();
        await stopping.WaitAsync(Patience);
        await published.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_stop_gives_up_on_a_stuck_wake_when_its_token_is_cancelled()
    {
        var store = new Store();
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        using var service = Service(store, new Resumer(), source);

        store.HoldTheNextRead();
        await service.StartAsync(CancellationToken.None);
        var published = source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });
        await store.ReadEntered.WaitAsync(Patience);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StopAsync(deadline.Token).WaitAsync(Patience);

        store.ReleaseTheRead();
        await published.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_stop_gives_up_on_a_resume_that_ignores_its_token_when_the_stop_token_is_cancelled()
    {
        var store = new Store();
        var resumer = new Resumer();
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        resumer.Hold(row.ExecutionLogId);
        using var service = Service(store, resumer, source);

        await service.StartAsync(CancellationToken.None);
        var published = source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });
        await resumer.Entered(row.ExecutionLogId).WaitAsync(Patience);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StopAsync(deadline.Token).WaitAsync(Patience);

        resumer.Release(row.ExecutionLogId);
        await published.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_second_StartAsync_is_refused_and_leaves_one_subscription()
    {
        var source = new WakeSource();
        using var service = Service(new Store(), new Resumer(), source);

        await service.StartAsync(CancellationToken.None);
        var second = () => service.StartAsync(CancellationToken.None);

        await second.Should().ThrowAsync<InvalidOperationException>();
        source.Subscribers.Should().Be(1);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
        source.Subscribers.Should().Be(0);
    }

    [Fact]
    public async Task Dispose_twice_and_a_stop_after_it_do_not_throw()
    {
        var service = Service(new Store(), new Resumer(), new WakeSource());
        await service.StartAsync(CancellationToken.None);

        service.Dispose();
        var again = () => service.Dispose();
        var stop = () => service.StopAsync(CancellationToken.None).WaitAsync(Patience);

        again.Should().NotThrow();
        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_cancel_callback_that_throws_still_unsubscribes_the_service()
    {
        var store = new Store();
        var resumer = new Resumer { ThrowOnCancel = true };
        var source = new WakeSource();
        var row = store.Park(timeoutAtUtc: DateTimeOffset.UtcNow.AddHours(1));
        resumer.Hold(row.ExecutionLogId);
        using var service = Service(store, resumer, source);
        await service.StartAsync(CancellationToken.None);
        var published = source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        });
        await resumer.Entered(row.ExecutionLogId).WaitAsync(Patience);

        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var stop = () => service.StopAsync(deadline.Token).WaitAsync(Patience);

        await stop.Should().ThrowAsync<AggregateException>();
        source.Subscribers.Should().Be(0);
        resumer.Release(row.ExecutionLogId);
        await published.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_wake_arriving_after_Dispose_does_not_throw()
    {
        var store = new Store();
        var source = new WakeSource();
        var service = Service(store, new Resumer(), source);
        await service.StartAsync(CancellationToken.None);
        var handler = source.Last!;

        service.Dispose();
        var late = () => handler(
            new WorkflowSuspensionWake { SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger },
            CancellationToken.None);

        await late.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_host_that_fills_no_wake_source_still_runs_its_passes()
    {
        var store = new Store();
        var resumer = new Resumer();
        var row = store.Park();
        using var service = Service(store, resumer, source: null);

        await service.StartAsync(CancellationToken.None);

        await resumer.Entered(row.ExecutionLogId).WaitAsync(Patience);
        await service.StopAsync(CancellationToken.None).WaitAsync(Patience);
    }

    private static WorkflowSuspensionWakeService Service(
        Store store, Resumer resumer, IWorkflowSuspensionWakeSource? source = null)
    {
        var options = new WorkflowSuspensionWakeOptions { PollInterval = TimeSpan.FromMilliseconds(20), SettleFor = TimeSpan.Zero };
        return new WorkflowSuspensionWakeService(
            new WorkflowSuspensionWaker(store, resumer, NullLogger.Instance, new WorkflowCompilationService(Substitute.For<IWorkflowBlocks>(), Substitute.For<IWorkflowRunScopeFactory>()), options), options, NullLogger.Instance,
            new WorkflowSuspensionParkSignal(),
            source);
    }

    private sealed class WakeSource : IWorkflowSuspensionWakeSource
    {
        private readonly List<Func<WorkflowSuspensionWake, CancellationToken, Task>> handlers = [];
        private readonly TaskCompletionSource subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Subscribed => subscribed.Task;

        public Func<WorkflowSuspensionWake, CancellationToken, Task>? Last { get; private set; }

        private int releases;

        public int Subscribers
        {
            get { lock (handlers) return handlers.Count; }
        }

        public int Releases => Volatile.Read(ref releases);

        public IDisposable Subscribe(Func<WorkflowSuspensionWake, CancellationToken, Task> onWake)
        {
            lock (handlers)
                handlers.Add(onWake);
            Last = onWake;
            subscribed.TrySetResult();
            return new Subscription(() =>
            {
                Interlocked.Increment(ref releases);
                lock (handlers) handlers.Remove(onWake);
            });
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

    private sealed class Store : IWorkflowSuspensionResumeStore
    {
        private readonly ConcurrentDictionary<Guid, WorkflowSuspensionCandidate> parked = new();

        public WorkflowSuspensionCandidate Park(DateTimeOffset? timeoutAtUtc = null)
        {
            var row = new WorkflowSuspensionCandidate(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wait-1", WorkflowSuspensionKind.WaitForTrigger, "{}",
                "{}", Past, timeoutAtUtc ?? Past);
            parked[row.SuspensionId] = row;
            return row;
        }

        private TaskCompletionSource? heldRead;
        private int readsHeld;
        private readonly TaskCompletionSource readEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadEntered => readEntered.Task;

        public void HoldTheNextRead() =>
            heldRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseTheRead() => heldRead?.TrySetResult();

        public async Task<WorkflowParkedRowRead?> GetParkedAsync(
            Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken)
        {
            readEntered.TrySetResult();
            if (heldRead is { } held && Interlocked.Exchange(ref readsHeld, 1) == 0)
                await held.Task;

            return new WorkflowParkedRowRead(
                DateTimeOffset.UtcNow,
                parked.Values.FirstOrDefault(row =>
                    (suspensionId is null || row.SuspensionId == suspensionId)
                    && (executionLogId is null || row.ExecutionLogId == executionLogId)));
        }

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(DateTimeOffset.UtcNow, [.. parked.Values], 0));

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
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> held = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> cancelled = new();

        public bool ThrowOnCancel { get; init; }

        public Task Entered(Guid executionLogId) => Signal(entered, executionLogId).Task;

        public Task Cancelled(Guid executionLogId) => Signal(cancelled, executionLogId).Task;

        public void Hold(Guid executionLogId) => Signal(held, executionLogId);

        public void Release(Guid executionLogId) => Signal(held, executionLogId).TrySetResult();

        public async Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken)
        {
            using var watch = cancellationToken.Register(() =>
            {
                Signal(cancelled, ctx.ExecutionLogId).TrySetResult();
                if (ThrowOnCancel) throw new InvalidOperationException("A cancel callback that throws.");
            });

            Signal(entered, ctx.ExecutionLogId).TrySetResult();
            if (held.TryGetValue(ctx.ExecutionLogId, out var hold))
                await hold.Task;
            return WorkflowResumeOutcome.Resumed;
        }

        private static TaskCompletionSource Signal(ConcurrentDictionary<Guid, TaskCompletionSource> map, Guid id) =>
            map.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
