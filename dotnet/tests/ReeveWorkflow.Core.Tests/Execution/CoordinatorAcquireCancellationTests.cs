using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class CoordinatorAcquireCancellationTests
{
    private static readonly Guid WorkflowId = Guid.NewGuid();
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Single_cancelled_while_asking_about_parked_sleeps_gives_back_its_permit_and_its_cluster_lock()
    {
        var locks = new CountingLockProvider();
        var coordinator = Coordinator(WorkflowMode.Single, locks: locks);
        using var callerGivesUp = new CancellationTokenSource();

        var act = () => coordinator.AcquireAsync(new CancellingParkedSleeps(callerGivesUp), null, callerGivesUp.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        locks.Held.Should().Be(0, "the cluster lock taken before the question must be released");
        var next = await coordinator.AcquireAsync(CancellationToken.None);
        next.Outcome.Should().Be(WorkflowAdmissionOutcomes.Started, "the permit taken before the question must come back");
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task Restart_cancelled_while_asking_about_parked_sleeps_gives_back_its_permit_and_its_cluster_lock()
    {
        var locks = new CountingLockProvider();
        var coordinator = Coordinator(WorkflowMode.Restart, locks: locks);
        using var callerGivesUp = new CancellationTokenSource();

        var act = () => coordinator.AcquireAsync(new CancellingParkedSleeps(callerGivesUp), null, callerGivesUp.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        locks.Held.Should().Be(0);
        var next = await coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);
        next.Outcome.Should().Be(WorkflowAdmissionOutcomes.Started, "the abandoned acquire left no permit and no live trigger behind");
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task Single_cancelled_while_taking_the_cluster_lock_gives_back_its_permit()
    {
        using var callerGivesUp = new CancellationTokenSource();
        var coordinator = Coordinator(WorkflowMode.Single, locks: new CancellingLockProvider(callerGivesUp, contended: false));

        var act = () => coordinator.AcquireAsync(callerGivesUp.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var next = await coordinator.AcquireAsync(CancellationToken.None);
        next.Outcome.Should().Be(WorkflowAdmissionOutcomes.Started);
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task Restart_cancelled_while_retrying_a_contended_cluster_lock_gives_back_its_permit()
    {
        using var callerGivesUp = new CancellationTokenSource();
        var locks = new CancellingLockProvider(callerGivesUp, contended: true);
        var coordinator = Coordinator(WorkflowMode.Restart, locks: locks);

        var act = () => coordinator.AcquireAsync(callerGivesUp.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        locks.Contended = false;
        var next = await coordinator.AcquireAsync(CancellationToken.None).WaitAsync(Patience);
        next.Lease.Should().NotBeNull("a Restart fire after the cancelled one must not wait for a permit nobody holds");
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_queued_waiter_promoted_after_its_workflow_left_queued_mode_runs_instead_of_throwing()
    {
        var coordinator = Coordinator(WorkflowMode.Queued, maxQueuedRuns: 5);
        var holder = (await coordinator.AcquireAsync(default)).Lease!;
        var waiter = coordinator.AcquireAsync(default);

        coordinator.Reconfigure(WorkflowMode.Parallel, 1, 0, WorkflowOverflowBehavior.Drop);
        await holder.DisposeAsync();
        var promoted = await waiter.WaitAsync(Patience);

        promoted.Lease.Should().NotBeNull();
        await promoted.Lease!.DisposeAsync();
        var next = await coordinator.AcquireAsync(default);
        next.Outcome.Should().Be(WorkflowAdmissionOutcomes.Started, "the promoted waiter's permit came back");
        await next.Lease!.DisposeAsync();
    }

    private static WorkflowRunCoordinator Coordinator(
        WorkflowMode mode, IWorkflowRunLockProvider? locks = null, int maxQueuedRuns = 0) =>
        new(WorkflowId, mode, 1, maxQueuedRuns, WorkflowOverflowBehavior.Drop, new RecordingMetrics(), lockProvider: locks);

    private sealed class CancellingParkedSleeps(CancellationTokenSource callerGivesUp) : IWorkflowParkedSleepStore
    {
        public Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            callerGivesUp.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowParkedSleep>>([]);

        public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> HasRunStartedSinceAsync(Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class CountingLockProvider : IWorkflowRunLockProvider
    {
        public int Held;

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IWorkflowRunLock?>(Interlocked.CompareExchange(ref Held, 1, 0) == 0 ? new Lock(this) : null);

        private sealed class Lock(CountingLockProvider owner) : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref owner.Held, 0);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CancellingLockProvider(CancellationTokenSource callerGivesUp, bool contended) : IWorkflowRunLockProvider
    {
        public volatile bool Contended = contended;

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            if (!callerGivesUp.IsCancellationRequested)
            {
                callerGivesUp.Cancel();
                if (!Contended)
                    cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult<IWorkflowRunLock?>(Contended ? null : new Free());
        }

        private sealed class Free : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
