using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunResumeAttemptTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_resumed_segment_that_outlives_the_workflows_timeout_is_stopped_and_recorded_as_timed_out()
    {
        var world = new RunLifecycleWorld(timeoutSeconds: 1);
        var parked = world.Parked();

        var outcome = await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Resumed);
        world.Release.Task.IsCompleted.Should().BeFalse();
        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(WorkflowExecutionStatus.TimedOut);
        log.ErrorMessage.Should().Be("Workflow execution timed out after 1 seconds.", "the words a fresh run's timeout writes");
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public async Task A_cancellation_a_block_throws_on_its_own_is_not_the_workflows_timeout()
    {
        var world = new RunLifecycleWorld(timeoutSeconds: 60) { HoldCancelsItself = true };
        var parked = world.Parked();

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.Journal.Logs[parked.ExecutionLogId].Status.Should().NotBe(WorkflowExecutionStatus.TimedOut,
            "the workflow's clock had fifty-nine seconds left");
    }

    [Fact]
    public async Task A_resumed_segment_that_timed_out_is_not_tried_again_whatever_the_policy_says()
    {
        var world = new RunLifecycleWorld(timeoutSeconds: 1, retryPolicyJson: """{"maxAttempts":3}""");
        var parked = world.Parked();

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1, "the next attempt gets the same budget and would spend it the same way");
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.TimedOut);
    }

    [Fact]
    public async Task A_resumed_segment_that_fails_is_tried_again_from_where_it_resumed_as_the_retry_policy_says()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""", tickBeforeHold: true)
        {
            FailHoldTimes = 2
        };
        world.Release.SetResult();
        var parked = world.Parked();

        var outcome = await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Resumed);
        world.HoldEntries.Should().Be(3);
        world.TickEntries.Should().Be(3,
            "an attempt walks the segment from the wait again, not from wherever the failed attempt got to");
        world.Journal.Logs.Should().ContainSingle("a segment's attempts are one run's, under the entry it parked with");
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Journal.Logs[parked.ExecutionLogId].ErrorMessage.Should().BeNull();
        world.RunState.Completions.Should().ContainSingle("the row stays the run's between attempts")
            .Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_resumed_segment_between_attempts_still_reads_as_running_and_its_success_logs_no_failure()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":2}""") { FailHoldTimes = 1 };
        var parked = world.Parked();
        var logger = new RecordingLogger();

        var resume = world.Resumer(logger: logger).ResumeFromSuspensionAsync(parked, CancellationToken.None);
        using (var patience = new CancellationTokenSource(Patience))
            while (world.HoldEntries < 2)
                await Task.Delay(5, patience.Token);

        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(WorkflowExecutionStatus.Running, "an attempt that will be tried again has not ended the run");
        log.ErrorMessage.Should().BeNull();
        world.Release.SetResult();
        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        logger.Errors.Should().BeEmpty("a run that succeeded on its second attempt did not fail after resume");
    }

    [Fact]
    public async Task A_resumed_segment_whose_version_does_not_compile_is_abandoned_and_not_tried_again()
    {
        const string twoHolds = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"hold","blockTypeId":"Hold"},{"id":"hold","blockTypeId":"Hold"}],
             "edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"hold","mappings":{}}]}
            """;
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""", definitionJson: twoHolds);
        var parked = world.Parked();

        var outcome = await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Abandoned);
        world.HoldEntries.Should().Be(0, "a version with no program is never walked");
        world.Journal.DetailsSaved.Should().Be(0, "no attempt ran, so none left steps to save");
        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().StartWith("pinned_version_uncompilable: ");
    }

    [Fact]
    public async Task A_resumed_segment_that_fails_every_attempt_ends_failed_after_the_last_one()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":2}""") { FailHoldTimes = 5 };
        var parked = world.Parked();

        var outcome = await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Resumed);
        world.HoldEntries.Should().Be(2);
        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().Contain("The hold block failed.");
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public async Task A_resumed_segment_with_no_retry_policy_is_tried_once()
    {
        var world = new RunLifecycleWorld { FailHoldTimes = 1 };
        world.Release.SetResult();
        var parked = world.Parked();

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Failed);
    }

    [Fact]
    public async Task A_resumed_segment_waits_the_policys_backoff_between_attempts()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":2,"initialDelayMs":400}""")
        {
            FailHoldTimes = 1
        };
        world.Release.SetResult();
        var parked = world.Parked();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350));
        world.HoldEntries.Should().Be(2);
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_fresh_run_waits_the_policys_backoff_before_its_first_retry_too()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":2,"initialDelayMs":400}""")
        {
            FailHoldTimes = 1
        };
        world.Release.SetResult();
        using var engine = world.Engine();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350));
        world.HoldEntries.Should().Be(2);
    }

    [Theory]
    [InlineData("""{"maxAttempts":0}""")]
    [InlineData("""{"initialDelayMs":100}""")]
    public async Task A_retry_policy_that_names_no_attempts_still_runs_the_workflow_once(string policy)
    {
        var fresh = new RunLifecycleWorld(retryPolicyJson: policy);
        fresh.Release.SetResult();
        using (var engine = fresh.Engine())
            await engine.ExecuteAsync(fresh.WorkflowId, null, null).WaitAsync(Patience);
        fresh.HoldEntries.Should().Be(1);
        fresh.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);

        var resumed = new RunLifecycleWorld(retryPolicyJson: policy);
        resumed.Release.SetResult();
        var parked = resumed.Parked();
        await resumed.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);
        resumed.HoldEntries.Should().Be(1);
        resumed.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Errors { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error)
                Errors.Enqueue(formatter(state, exception));
        }
    }
}
