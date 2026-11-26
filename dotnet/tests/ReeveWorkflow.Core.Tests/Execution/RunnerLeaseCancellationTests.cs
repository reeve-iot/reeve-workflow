using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RunnerLeaseCancellationTests
{
    private const int Long = 600_000;

    [Fact]
    public async Task A_restart_run_cancelled_while_cancelling_parked_sleepers_releases_its_lease()
    {
        var world = new DurableSleepWorld();
        var seam = new SlowCancel(world.Suspensions);
        world.SuspensionSeam = seam;
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        seam.Slow = true;
        using (var callerGivesUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            var act = () => engine.ExecuteAsync(workflowId, null, null, cancellationToken: callerGivesUp.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        var workflow = (await world.Store.LoadAsync(workflowId, default))!;
        engine.Coordinators.For(workflow).ActiveRunCount.Should().Be(0, "the abandoned Restart run must release its lease");

        seam.Slow = false;
        var runsBefore = world.Journal.Runs.Count;
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await engine.ExecuteAsync(workflowId, null, null, cancellationToken: bounded.Token);
        world.Journal.Runs.Count.Should().Be(runsBefore + 1, "a later Restart fire still runs");
    }

    private sealed class SlowCancel(MemorySuspensions inner) : IWorkflowSuspensionStore, IWorkflowParkedSleepStore
    {
        public volatile bool Slow;

        public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken) =>
            inner.SuspendAsync(request, cancellationToken);
        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            inner.CancelByExecutionAsync(executionLogId, cancellationToken);
        public void RegisterForResumption(WorkflowSuspension suspension) { }
        public void TearDownByExecution(Guid executionLogId) { }
        public Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.HasParkedSleepAsync(workflowId, cancellationToken);

        public async Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            if (Slow)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return await inner.ListParkedSleepsAsync(workflowId, cancellationToken);
        }

        public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken) =>
            inner.CancelParkedSleepAsync(suspensionId, cancellationToken);

        public Task<bool> HasRunStartedSinceAsync(Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken) =>
            inner.HasRunStartedSinceAsync(workflowId, exceptExecutionLogId, sinceUtc, cancellationToken);
    }
}
