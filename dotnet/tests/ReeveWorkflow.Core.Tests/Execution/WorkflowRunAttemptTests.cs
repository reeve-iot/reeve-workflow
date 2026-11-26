using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunAttemptTests
{
    private static readonly JsonSerializerOptions Json = WorkflowGraphHelpers.JsonOptions;

    [Fact]
    public void TheRunsOwnTimeoutIsNotTheSameAsBeingCancelled()
    {
        var cancellation = new OperationCanceledException();

        WorkflowRunAttempt.Classify(cancellation, timedOutLocally: true)
            .Should().Be(WorkflowAttemptOutcome.TimedOut);
        WorkflowRunAttempt.Classify(cancellation, timedOutLocally: false)
            .Should().Be(WorkflowAttemptOutcome.Cancelled,
                "the exception type is identical, so only the caller can say which token fired");
    }

    [Fact]
    public void AnUnrecognisedExceptionIsAnOrdinaryFailure() =>
        WorkflowRunAttempt.Classify(new InvalidOperationException("boom"), timedOutLocally: false)
            .Should().Be(WorkflowAttemptOutcome.Failed);

    [Fact]
    public void ADefinitionThatWillNotRunIsAValidationFailureAndNotAnOrdinaryOne()
    {
        var outcome = WorkflowRunAttempt.Classify(new WorkflowValidationException("bad graph"), timedOutLocally: false);

        outcome.Should().Be(WorkflowAttemptOutcome.ValidationFailed);
        WorkflowRunAttempt.AllowsAnotherAttempt(outcome, attemptNumber: 1, maxAttempts: 5)
            .Should().BeFalse("a second read of the same stored definition reaches the same verdict");
    }

    [Fact]
    public void ADefinitionTheEngineCannotReadIsAValidationFailureToo()
    {
        var outcome = WorkflowRunAttempt.Classify(
            new InvalidWorkflowDefinitionException("node 'n' has 'retryEnabled' set to String"), timedOutLocally: false);

        outcome.Should().Be(WorkflowAttemptOutcome.ValidationFailed);
        WorkflowRunAttempt.AllowsAnotherAttempt(outcome, attemptNumber: 1, maxAttempts: 5)
            .Should().BeFalse("the definition is the same on the next attempt, and so is the verdict");
    }

    [Theory]
    [InlineData(WorkflowAttemptOutcome.Succeeded, "succeeded")]
    [InlineData(WorkflowAttemptOutcome.Failed, "failed")]
    [InlineData(WorkflowAttemptOutcome.ValidationFailed, "failed")]
    [InlineData(WorkflowAttemptOutcome.TimedOut, "timed_out")]
    [InlineData(WorkflowAttemptOutcome.Cancelled, "cancelled")]
    public void EveryOutcomeCarriesTheLabelAnOperatorsDashboardAlreadyReads(
        WorkflowAttemptOutcome outcome, string expected) =>
        WorkflowRunAttempt.MetricsLabel(outcome).Should().Be(expected);

    [Theory]
    [InlineData(WorkflowAttemptOutcome.Succeeded, false)]
    [InlineData(WorkflowAttemptOutcome.Cancelled, false)]
    [InlineData(WorkflowAttemptOutcome.Failed, true)]
    [InlineData(WorkflowAttemptOutcome.ValidationFailed, true)]
    [InlineData(WorkflowAttemptOutcome.TimedOut, true)]
    public void ARunThatRanOutOfTimeFailed_ButOneThatWasCancelledDidNot(
        WorkflowAttemptOutcome outcome, bool expected) =>
        WorkflowRunAttempt.IsFailure(outcome).Should().Be(expected);

    [Theory]
    [InlineData(WorkflowAttemptOutcome.Failed, 1, 3, true)]
    [InlineData(WorkflowAttemptOutcome.Failed, 3, 3, false)]
    [InlineData(WorkflowAttemptOutcome.Failed, 1, 1, false)]
    [InlineData(WorkflowAttemptOutcome.TimedOut, 1, 3, false)]
    [InlineData(WorkflowAttemptOutcome.Cancelled, 1, 3, false)]
    [InlineData(WorkflowAttemptOutcome.ValidationFailed, 1, 3, false)]
    [InlineData(WorkflowAttemptOutcome.Succeeded, 1, 3, false)]
    public void OnlyAnOrdinaryFailureWithAttemptsLeftGetsAnother(
        WorkflowAttemptOutcome outcome, int attemptNumber, int maxAttempts, bool expected) =>
        WorkflowRunAttempt.AllowsAnotherAttempt(outcome, attemptNumber, maxAttempts).Should().Be(expected);

    [Fact]
    public void ARunTheAuthorAskedToBeRepeatedIsRepeated_WhateverItsBlocksSayAboutThemselves()
    {
        var policy = WorkflowRunAttempt.ParseRetryPolicy("""{"maxAttempts":4}""", Json, NullLogger.Instance);

        WorkflowRunAttempt.AllowsAnotherAttempt(WorkflowAttemptOutcome.Failed, 1, policy!.MaxAttempts)
            .Should().BeTrue("a block that says it cannot be repeated does not stop a run the author asked to be");
    }

    [Fact]
    public void ExponentialBackoffDoublesFromTheInitialDelay()
    {
        var policy = new WorkflowRetryPolicy { MaxAttempts = 5, InitialDelayMs = 100, BackoffStrategy = "exponential" };

        WorkflowRunAttempt.BackoffDelayMs(policy, 1).Should().Be(0, "there is no wait before the first attempt");
        WorkflowRunAttempt.BackoffDelayMs(policy, 2).Should().Be(200, "the exponent is the attempt behind, not the attempt");
        WorkflowRunAttempt.BackoffDelayMs(policy, 3).Should().Be(400);
        WorkflowRunAttempt.BackoffDelayMs(policy, 4).Should().Be(800);
    }

    [Fact]
    public void AFixedStrategyWaitsTheSameEveryTimeAndAnUnknownOneIsTreatedAsFixed()
    {
        var fixedPolicy = new WorkflowRetryPolicy { MaxAttempts = 5, InitialDelayMs = 250, BackoffStrategy = "fixed" };
        var nonsense = new WorkflowRetryPolicy { MaxAttempts = 5, InitialDelayMs = 250, BackoffStrategy = "fibonacci" };

        WorkflowRunAttempt.BackoffDelayMs(fixedPolicy, 4).Should().Be(250);
        WorkflowRunAttempt.BackoffDelayMs(nonsense, 4).Should().Be(250,
            "an unrecognised strategy must still produce a delay, not an exception and not zero");
    }

    [Theory]
    [InlineData(25)]
    [InlineData(32)]
    [InlineData(2000)]
    public void ABackoffTooLongForAnIntSaturatesInsteadOfWrapping(int attemptNumber) =>
        WorkflowRunAttempt.BackoffDelayMs(
            new WorkflowRetryPolicy { MaxAttempts = int.MaxValue, InitialDelayMs = 1000, BackoffStrategy = "exponential" },
            attemptNumber)
            .Should().Be(int.MaxValue,
                "the doubling runs in double, so far enough out it is infinity — and infinity must clamp, not wrap");

    [Theory]
    [InlineData(1, 100)]
    [InlineData(3, 0)]
    public void APolicyThatAsksForNoWaitGetsNone(int maxAttempts, int initialDelayMs) =>
        WorkflowRunAttempt.BackoffDelayMs(
            new WorkflowRetryPolicy { MaxAttempts = maxAttempts, InitialDelayMs = initialDelayMs }, 2)
            .Should().Be(0);

    [Fact]
    public void NoPolicyMeansNoWait() =>
        WorkflowRunAttempt.BackoffDelayMs(null, 4).Should().Be(0);

    [Fact]
    public void AMalformedPolicyIsNoPolicy_NotAFailedRun()
    {
        WorkflowRunAttempt.ParseRetryPolicy("{not json", Json, NullLogger.Instance)
            .Should().BeNull("a run with one attempt is a better answer than a run that never starts");
        WorkflowRunAttempt.ParseRetryPolicy("   ", Json, NullLogger.Instance).Should().BeNull();
        WorkflowRunAttempt.ParseRetryPolicy(null, Json, NullLogger.Instance).Should().BeNull();
    }

    [Fact]
    public void AStoredPolicyIsReadBack() =>
        WorkflowRunAttempt.ParseRetryPolicy(
            """{"maxAttempts":4,"initialDelayMs":50,"backoffStrategy":"exponential"}""", Json, NullLogger.Instance)
            !.MaxAttempts.Should().Be(4);

    [Fact]
    public void OnlyRestartModeSupersedes()
    {
        using var lease = new CancellationTokenSource();
        lease.Cancel();

        WorkflowRunAttempt.WasSupersededByRestart(WorkflowMode.Restart, lease.Token, CancellationToken.None)
            .Should().BeTrue();
        WorkflowRunAttempt.WasSupersededByRestart(WorkflowMode.Parallel, lease.Token, CancellationToken.None)
            .Should().BeFalse("only Restart replaces a running instance with a newer fire");
    }

    [Fact]
    public void ACallerWhoCancelledIsBeingObeyed_NotSuperseded()
    {
        using var lease = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        lease.Cancel();
        caller.Cancel();

        WorkflowRunAttempt.WasSupersededByRestart(WorkflowMode.Restart, lease.Token, caller.Token)
            .Should().BeFalse("the lease token is linked to the caller's, so it fires on shutdown too");
    }

    [Fact]
    public void AQuietLeaseIsNotASupersession()
    {
        WorkflowRunAttempt.WasSupersededByRestart(
            WorkflowMode.Restart, CancellationToken.None, CancellationToken.None).Should().BeFalse();
    }
}
