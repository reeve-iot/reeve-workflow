using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class AdmissionLeaseTests
{
    private const int Long = 600_000;

    [Fact]
    public async Task A_caller_giving_up_just_after_the_lease_was_taken_leaves_the_workflow_runnable()
    {
        var world = new DurableSleepWorld();
        using var callerGivesUp = new CancellationTokenSource();
        var seam = new CancelOnAnswer(world.Suspensions, callerGivesUp);
        world.SuspensionSeam = seam;
        var workflowId = world.AddWorkflow(Long, WorkflowMode.Single);
        using var engine = world.Engine();

        var act = () => engine.ExecuteAsync(workflowId, null, null, cancellationToken: callerGivesUp.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        var workflow = (await world.Store.LoadAsync(workflowId, default))!;
        engine.Coordinators.For(workflow).ActiveRunCount.Should().Be(0, "the abandoned admission gave its lease back");
        seam.Armed = false;
        await engine.ExecuteAsync(workflowId, null, null);
        world.Journal.Runs.Should().ContainSingle("a later Single fire runs");
    }

    [Fact]
    public async Task A_store_that_fails_while_the_versions_are_pinned_leaves_the_workflow_runnable()
    {
        var world = new DurableSleepWorld();
        var workflowId = world.AddRaw(SubWorkflowDefinition(Guid.NewGuid()));
        using var engine = world.Engine();
        world.Store.FailCurrentVersionLookups = true;

        var act = () => engine.ExecuteAsync(workflowId, null, null);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var workflow = (await world.Store.LoadAsync(workflowId, default))!;
        engine.Coordinators.For(workflow).ActiveRunCount.Should().Be(0);
    }

    [Fact]
    public async Task A_metrics_sink_that_throws_on_an_admission_does_not_keep_the_permit()
    {
        var metrics = new ThrowingMetrics();
        using var coordinator = new WorkflowRunCoordinator(
            Guid.NewGuid(), WorkflowMode.Single, 1, 0, WorkflowOverflowBehavior.Drop, metrics);

        await coordinator.Awaiting(c => c.AcquireAsync(default)).Should().ThrowAsync<InvalidOperationException>();

        metrics.Throws = false;
        var next = await coordinator.AcquireAsync(default);
        next.Outcome.Should().Be(WorkflowAdmissionOutcomes.Started, "the permit the broken exporter cost was given back");
        await next.Lease!.DisposeAsync();
    }

    [Fact]
    public async Task A_metrics_sink_that_throws_as_a_run_ends_does_not_keep_its_lease()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        var workflowId = world.AddWorkflow(1, WorkflowMode.Single);
        using var engine = world.Engine();
        world.Metrics.ThrowOnRunEnd = true;

        try { await engine.ExecuteAsync(workflowId, null, null); }
        catch (InvalidOperationException) { }

        var workflow = (await world.Store.LoadAsync(workflowId, default))!;
        engine.Coordinators.For(workflow).ActiveRunCount.Should().Be(0);
    }

    private static string SubWorkflowDefinition(Guid child) => System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "sub", blockTypeId = WorkflowGraphHelpers.SubWorkflowBlockTypeId, configuration = new Dictionary<string, object> { ["WorkflowId"] = child.ToString() }, position = new { x = 0, y = 0 } }
        },
        edges = new object[] { new { id = "e", sourceNodeId = "start", targetNodeId = "sub", mappings = new { } } }
    });

    private sealed class CancelOnAnswer(MemorySuspensions inner, CancellationTokenSource callerGivesUp)
        : IWorkflowSuspensionStore, IWorkflowParkedSleepStore
    {
        public volatile bool Armed = true;
        public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken) =>
            inner.SuspendAsync(request, cancellationToken);
        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            inner.CancelByExecutionAsync(executionLogId, cancellationToken);
        public void RegisterForResumption(WorkflowSuspension suspension) { }
        public void TearDownByExecution(Guid executionLogId) { }

        public async Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken)
        {
            var answer = await inner.HasParkedSleepAsync(workflowId, CancellationToken.None);
            if (Armed)
                callerGivesUp.Cancel();
            return answer;
        }

        public Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken) =>
            inner.ListParkedSleepsAsync(workflowId, cancellationToken);
        public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken) =>
            inner.CancelParkedSleepAsync(suspensionId, cancellationToken);

        public Task<bool> HasRunStartedSinceAsync(Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc,
            CancellationToken cancellationToken) =>
            inner.HasRunStartedSinceAsync(workflowId, exceptExecutionLogId, sinceUtc, cancellationToken);
    }

    private sealed class ThrowingMetrics : IWorkflowMetrics
    {
        public volatile bool Throws = true;
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome)
        {
            if (Throws)
                throw new InvalidOperationException("the metrics exporter is broken");
        }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }
}
