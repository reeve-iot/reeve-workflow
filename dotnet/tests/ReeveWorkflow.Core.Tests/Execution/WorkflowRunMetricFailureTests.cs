using System.Collections.Concurrent;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunMetricFailureTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_run_whose_completion_metric_throws_gives_its_place_back()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        using var engine = world.Engine(metrics: new ThrowingMetrics());

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);
        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(2, "a Single workflow's one place was given back after the first run");
    }

    [Fact]
    public async Task A_run_whose_start_metric_throws_runs_and_gives_its_place_back()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        using var engine = world.Engine(metrics: new ThrowingMetrics { StartThrows = true });

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);
        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(2);
    }

    [Fact]
    public async Task A_run_whose_admission_fails_after_its_place_was_granted_gives_the_place_back()
    {
        var world = new RunLifecycleWorld(definitionJson: "{\"nodes\":5,\"edges\":[]}");
        using var engine = world.Engine();

        var first = await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<Exception>();
        var second = await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<Exception>("the second run is admitted and fails the same way, rather than being refused");

        second.Which.GetType().Should().Be(first.Which.GetType());
    }

    [Fact]
    public async Task A_resumed_segment_whose_metrics_throw_still_says_it_resumed()
    {
        var world = new RunLifecycleWorld();
        var parked = world.Parked();
        world.Release.SetResult();

        (await world.Resumer(metrics: new ThrowingMetrics()).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);

        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_resumed_segment_that_parks_again_counts_the_wait_it_resolved_and_the_one_it_started()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var parked = world.Parked();
        var metrics = new RecordingMetrics();

        (await world.Resumer(suspensions: new RunLifecycleWorld.RecordingSuspensions(), metrics: metrics)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        world.RunState.Row(world.ParkedStateId!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended);
        metrics.Calls.Should().Contain(call => call.StartsWith("resolved:") && call.EndsWith(":succeeded"));
        metrics.Calls.Should().Contain(call => call.StartsWith("suspended:"));
        metrics.Calls.Should().NotContain(call => call.StartsWith("completed:"), "the run has not completed");
    }

    [Fact]
    public async Task A_resumed_segment_that_parks_again_after_a_failed_attempt_counts_its_wait_as_resolved()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":2}""") { ParkAtHold = true, FailHoldTimes = 1 };
        var parked = world.Parked();
        var metrics = new RecordingMetrics();

        (await world.Resumer(suspensions: new RunLifecycleWorld.RecordingSuspensions(), metrics: metrics)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        world.HoldEntries.Should().Be(2);
        metrics.Calls.Should().Contain(call => call.StartsWith("resolved:") && call.EndsWith(":succeeded"));
    }

    [Fact]
    public async Task A_run_whose_row_cannot_be_opened_is_not_counted_succeeded_nor_left_running_in_the_journal()
    {
        var world = new RunLifecycleWorld();
        var metrics = new RecordingMetrics();
        using var engine = world.Engine(metrics: metrics);
        world.RunState.BeginsFail = true;

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<InvalidOperationException>();

        metrics.Calls.Should().Contain("completed:failed");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Failed);
    }

    private sealed class ThrowingMetrics : IWorkflowMetrics
    {
        public bool StartThrows { get; init; }

        public void RecordExecutionStarted()
        {
            if (StartThrows)
                throw new InvalidOperationException("The metrics exporter is away.");
        }

        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) =>
            throw new InvalidOperationException("The metrics exporter is away.");

        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }

        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }

        public void RecordDebugSession(string outcome) { }

        public void RecordDebugPauseDuration(double durationSeconds) { }

        public void RecordSuspensionStarted(string kind) =>
            throw new InvalidOperationException("The metrics exporter is away.");

        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) =>
            throw new InvalidOperationException("The metrics exporter is away.");
    }

    private sealed class RecordingMetrics : IWorkflowMetrics
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public void RecordExecutionStarted() => Calls.Enqueue("started");

        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) =>
            Calls.Enqueue($"completed:{status}");

        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }

        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }

        public void RecordDebugSession(string outcome) { }

        public void RecordDebugPauseDuration(double durationSeconds) { }

        public void RecordSuspensionStarted(string kind) => Calls.Enqueue($"suspended:{kind}");

        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) =>
            Calls.Enqueue($"resolved:{kind}:{outcome}");
    }
}
