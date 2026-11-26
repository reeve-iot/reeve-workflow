using System.Collections.Concurrent;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunBackoffLeaseTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private const string LongBackoff = """{"maxAttempts":2,"initialDelayMs":600000}""";
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_fresh_run_waiting_out_its_backoff_ends_cancelled_when_its_workflow_is_forgotten()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: LongBackoff) { FailHoldTimes = 5 };
        var metrics = new CompletionMetrics();
        using var engine = world.Engine(metrics: metrics);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await WaitUntilAsync(() => world.Journal.Logs.Values.Any(log => log.Status == WorkflowExecutionStatus.Failed));
        engine.ForgetWorkflow(world.WorkflowId);

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowExactlyAsync<OperationCanceledException>(
            "a run its workflow's deletion ended is answered as one cancelled in a block is, not as a supersession");
        world.HoldEntries.Should().Be(1, "no attempt starts for a workflow that is gone");
        world.Journal.Logs.Should().ContainSingle("the second attempt opens no entry");
        world.RunState.Completions.Should().ContainSingle("only the first attempt's row was ever begun")
            .Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
        metrics.Completed.Should().ContainSingle().Which.Should().Be(WorkflowRunStatusLabels.Cancelled);
    }

    [Fact]
    public async Task A_run_whose_host_stops_during_its_backoff_keeps_waiting_for_its_caller()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":2,"initialDelayMs":300}""") { FailHoldTimes = 1 };
        world.Release.SetResult();
        using var stopping = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await WaitUntilAsync(() => world.Journal.Logs.Values.Any(log => log.Status == WorkflowExecutionStatus.Failed));
        await stopping.CancelAsync();

        await run.WaitAsync(Patience);
        world.HoldEntries.Should().Be(2, "a stopping host alone does not end a backoff");
    }

    [Fact]
    public async Task A_resumed_segment_waiting_out_its_backoff_ends_cancelled_when_its_workflow_is_forgotten()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: LongBackoff) { FailHoldTimes = 5 };
        using var engine = world.Engine();
        var parked = world.Parked();

        var resume = world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await WaitUntilAsync(() => world.Journal.DetailsSaved >= 1);
        engine.ForgetWorkflow(world.WorkflowId);

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.HoldEntries.Should().Be(1, "the second attempt never starts");
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
    }

    [Fact]
    public async Task A_caller_stop_and_a_forget_together_end_the_run_as_its_last_attempt_ended()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: LongBackoff) { FailHoldTimes = 5 };
        var metrics = new CompletionMetrics();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(metrics: metrics);
        var fired = 0;
        world.Journal.WhileSavingDetails = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) != 0)
                return;
            caller.Cancel();
            engine.ForgetWorkflow(world.WorkflowId);
        };

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);

        var thrown = await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeAssignableTo<OperationCanceledException>("the attempt before it failed, and that is its ending");
        world.HoldEntries.Should().Be(1);
        world.Journal.Logs.Should().ContainSingle();
        metrics.Completed.Should().ContainSingle().Which.Should().Be(WorkflowRunStatusLabels.Failed);
    }

    [Fact]
    public async Task A_retry_with_no_backoff_forgotten_while_the_attempt_before_ends_opens_no_further_entry()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":2,"initialDelayMs":0}""") { FailHoldTimes = 5 };
        using var engine = world.Engine();
        var fired = 0;
        world.Journal.WhileSavingDetails = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
                engine.ForgetWorkflow(world.WorkflowId);
        };

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowExactlyAsync<OperationCanceledException>();
        world.HoldEntries.Should().Be(1);
        world.Journal.Logs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_restart_trigger_supersedes_a_run_waiting_out_its_backoff_at_once()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: LongBackoff) { FailHoldTimes = 1 };
        world.Workflow.ConfigureConcurrency(WorkflowMode.Restart, 1, 0, WorkflowOverflowBehavior.Drop, T0);
        world.Release.SetResult();
        using var engine = world.Engine();

        var first = engine.ExecuteAsync(world.WorkflowId, null, null);
        await WaitUntilAsync(() => world.Journal.Logs.Values.Any(log => log.Status == WorkflowExecutionStatus.Failed));
        var second = engine.ExecuteAsync(world.WorkflowId, null, null);

        await first.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowExactlyAsync<WorkflowSupersededException>();
        await second.WaitAsync(Patience);
        world.HoldEntries.Should().Be(2, "the superseded run makes no second attempt, and the newer run makes its one");
        world.Journal.Logs.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_workflow_forgotten_while_its_resume_is_audited_ends_the_segment_cancelled()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: LongBackoff) { FailHoldTimes = 5 };
        using var engine = world.Engine();
        var parked = world.Parked();
        var fired = 0;
        world.Audit.WhileWriting = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
                engine.ForgetWorkflow(world.WorkflowId);
        };

        var resume = world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None);

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        fired.Should().Be(1, "setup: the forget landed during the resume's audit write");
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
    }

    [Fact]
    public async Task A_workflow_forgotten_while_its_resume_claims_the_parked_row_ends_the_segment_cancelled()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: LongBackoff) { FailHoldTimes = 5 };
        using var engine = world.Engine();
        var parked = world.Parked();
        var fired = 0;
        world.RunState.WhileClaimingParkedRow = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
                engine.ForgetWorkflow(world.WorkflowId);
        };

        var resume = world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None);

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        fired.Should().Be(1, "setup: the forget landed during the claim of the parked row");
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var patience = new CancellationTokenSource(Patience);
        while (!condition())
            await Task.Delay(5, patience.Token);
    }

    private sealed class CompletionMetrics : IWorkflowMetrics
    {
        public ConcurrentQueue<string> Completed { get; } = new();

        public void RecordExecutionStarted() { }

        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) =>
            Completed.Enqueue(status);

        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }

        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }

        public void RecordDebugSession(string outcome) { }

        public void RecordDebugPauseDuration(double durationSeconds) { }

        public void RecordSuspensionStarted(string kind) { }

        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }
}
