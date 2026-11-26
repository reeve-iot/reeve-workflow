using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using NSubstitute;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class WorkflowSuspensionWakeServiceStartTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_wake_published_the_moment_StartAsync_returns_is_heard()
    {
        var store = new Store();
        var source = new WakeSource(Environment.CurrentManagedThreadId);
        using var service = Service(store, new WorkflowSuspensionParkSignal(), source);

        await service.StartAsync(CancellationToken.None);
        await source.PublishAsync(new WorkflowSuspensionWake
        {
            SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger
        });
        source.Gate.Set();

        try { await store.Probed.WaitAsync(Patience); }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(Patience); }
    }

    [Fact]
    public async Task A_park_published_the_moment_StartAsync_returns_starts_a_pass()
    {
        var store = new Store();
        var signal = new WorkflowSuspensionParkSignal();
        var source = new WakeSource(Environment.CurrentManagedThreadId);
        using var service = Service(store, signal, source);

        await service.StartAsync(CancellationToken.None);
        signal.Publish();
        source.Gate.Set();

        try { await store.Probed.WaitAsync(Patience); }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(Patience); }
    }

    private static WorkflowSuspensionWakeService Service(
        Store store, WorkflowSuspensionParkSignal signal, IWorkflowSuspensionWakeSource source)
    {
        var options = new WorkflowSuspensionWakeOptions { PollInterval = TimeSpan.FromHours(1), SettleFor = TimeSpan.Zero };
        return new WorkflowSuspensionWakeService(
            new WorkflowSuspensionWaker(store, new Resumer(), NullLogger.Instance, new WorkflowCompilationService(Substitute.For<IWorkflowBlocks>(), Substitute.For<IWorkflowRunScopeFactory>()), options), options, NullLogger.Instance,
            signal, source);
    }

    private sealed class WakeSource(int callerThread) : IWorkflowSuspensionWakeSource
    {
        private readonly List<Func<WorkflowSuspensionWake, CancellationToken, Task>> handlers = [];

        public ManualResetEventSlim Gate { get; } = new();

        public IDisposable Subscribe(Func<WorkflowSuspensionWake, CancellationToken, Task> onWake)
        {
            if (Environment.CurrentManagedThreadId != callerThread)
                Gate.Wait(Patience);
            lock (handlers)
                handlers.Add(onWake);
            return new Subscription();
        }

        public async Task PublishAsync(WorkflowSuspensionWake wake)
        {
            Func<WorkflowSuspensionWake, CancellationToken, Task>[] current;
            lock (handlers)
                current = [.. handlers];
            foreach (var handler in current)
                await handler(wake, CancellationToken.None);
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class Store : IWorkflowSuspensionResumeStore
    {
        private readonly TaskCompletionSource probed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Probed => probed.Task;

        public Task<WorkflowSuspensionWakeProbe?> ProbeWakeAsync(
            TimeSpan? abandonedAfter, WorkflowAbandonedTakeCursor abandonedAfterCursor, CancellationToken cancellationToken)
        {
            probed.TrySetResult();
            return Task.FromResult<WorkflowSuspensionWakeProbe?>(new WorkflowSuspensionWakeProbe(DateTimeOffset.UtcNow, null, false, false));
        }

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(DateTimeOffset.UtcNow, [], 0));

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken) => Task.FromResult<ResumeContext?>(null);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class Resumer : IWorkflowRunResumer
    {
        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken) =>
            Task.FromResult(WorkflowResumeOutcome.Resumed);
    }
}
