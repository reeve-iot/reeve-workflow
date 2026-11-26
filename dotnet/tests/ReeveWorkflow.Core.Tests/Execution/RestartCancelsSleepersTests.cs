using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RestartCancelsSleepersTests
{
    private const int Long = 600_000;

    [Fact]
    public async Task A_store_failing_on_the_second_sleeper_leaves_no_cancelled_row_whose_run_still_runs()
    {
        var world = new DurableSleepWorld();
        var seam = new Flaky(world.Suspensions);
        world.SuspensionSeam = seam;
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        seam.ListNothing = true;
        await engine.ExecuteAsync(workflowId, null, null);
        var sleepers = world.Journal.Runs.Values.ToList();
        sleepers.Should().HaveCount(2, "two runs are parked, as a Restart whose cancel pass failed leaves them");

        seam.ListNothing = false;
        seam.FailOnCancelCall = 2;
        await engine.ExecuteAsync(workflowId, null, null);

        foreach (var sleeper in sleepers)
        {
            sleeper.Status.Should().Be(WorkflowExecutionStatus.Cancelled, "each superseded run is ended before its row");
            var row = world.Suspensions.Rows.Single(r => r.ExecutionLogId == sleeper.Id);
            if (row.Status == WorkflowSuspensionStatus.Cancelled)
                world.RunState.Rows[StateRowOf(world, sleeper.Id)].Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        }

        world.Journal.Runs.Values.Where(r => r.Status == WorkflowExecutionStatus.Running).Should().ContainSingle(
            "only the newest run is still parked");
    }

    [Fact]
    public async Task A_restart_stopped_while_it_ends_the_sleepers_is_counted_cancelled()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Restart);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.Seam.HoldList = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Seam.ListAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Seam.HoldListOnCall = world.Seam.ListCalls + 2;
        using var stop = new CancellationTokenSource();
        var fire = Task.Run(() => engine.ExecuteAsync(workflowId, null, null, cancellationToken: stop.Token));
        await world.Seam.ListAsked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        stop.Cancel();
        try { await fire.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        world.Seam.HoldList = null;

        world.Metrics.RunEndings.Should().Contain(WorkflowRunStatusLabels.Cancelled);
        world.Metrics.RunEndings.Should().NotContain(WorkflowRunStatusLabels.Failed, "nothing failed; the caller stopped");
    }

    private static Guid StateRowOf(DurableSleepWorld world, Guid executionLogId) =>
        world.RunState.Rows.Values.Single(r => r.WorkflowId != Guid.Empty
            && world.Suspensions.Rows.Any(s => s.ExecutionLogId == executionLogId
                && s.StatePayloadJson.Contains(r.ExecutionId.ToString()))).ExecutionId;

    private sealed class Flaky(MemorySuspensions inner) : IWorkflowSuspensionStore, IWorkflowParkedSleepStore
    {
        private int cancelCalls;
        public volatile bool ListNothing;
        public int FailOnCancelCall;

        public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken) =>
            inner.SuspendAsync(request, cancellationToken);
        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            inner.CancelByExecutionAsync(executionLogId, cancellationToken);
        public void RegisterForResumption(WorkflowSuspension suspension) { }
        public void TearDownByExecution(Guid executionLogId) { }
        public Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.HasParkedSleepAsync(workflowId, cancellationToken);

        public async Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken) =>
            ListNothing ? [] : await inner.ListParkedSleepsAsync(workflowId, cancellationToken);

        public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref cancelCalls) == FailOnCancelCall
                ? throw new InvalidOperationException("the database went away mid-pass")
                : inner.CancelParkedSleepAsync(suspensionId, cancellationToken);

        public Task<bool> HasRunStartedSinceAsync(Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken) =>
            inner.HasRunStartedSinceAsync(workflowId, exceptExecutionLogId, sinceUtc, cancellationToken);
    }
}
