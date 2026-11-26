using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>How one attempt at a run ended. The metric label, the run-state row and the retry decision
/// all read it.</summary>
public enum WorkflowAttemptOutcome
{
    /// <summary>The graph finished. Nothing was thrown.</summary>
    Succeeded,

    /// <summary>The graph threw something the engine does not recognise, or a block failed. The only
    /// outcome a retry policy may try again.</summary>
    Failed,

    /// <summary>The run's own timeout fired. Reported as a failure, but never retried: the next attempt
    /// gets the same budget and would spend it the same way.</summary>
    TimedOut,

    /// <summary>Someone else stopped this run — the caller, a shutdown, or a Restart-mode supersession.
    /// Retrying would defeat whoever cancelled it.</summary>
    Cancelled,

    /// <summary>The stored definition could not be run. Reported as a failure; retrying re-reads the same
    /// definition and reaches the same verdict.</summary>
    ValidationFailed
}

/// <summary>The decisions one attempt makes, none of them touching I/O: what it ended as, whether there may
/// be another, how long to wait first, and whether a cancellation was really a supersession.</summary>
public static class WorkflowRunAttempt
{
    /// <summary>Reads the exception the attempt ended with. <paramref name="timedOutLocally"/> separates
    /// the run's own timeout from every other cancellation, which the exception type cannot do on its own.</summary>
    public static WorkflowAttemptOutcome Classify(Exception exception, bool timedOutLocally) =>
        exception switch
        {
            OperationCanceledException when timedOutLocally => WorkflowAttemptOutcome.TimedOut,
            OperationCanceledException => WorkflowAttemptOutcome.Cancelled,
            WorkflowValidationException => WorkflowAttemptOutcome.ValidationFailed,
            InvalidWorkflowDefinitionException => WorkflowAttemptOutcome.ValidationFailed,
            _ => WorkflowAttemptOutcome.Failed
        };

    /// <summary>The metric label for an outcome. A validation failure and a block failure are one series:
    /// the distinction matters to the retry decision, not to an operator counting failed runs.</summary>
    public static string MetricsLabel(WorkflowAttemptOutcome outcome) => outcome switch
    {
        WorkflowAttemptOutcome.Succeeded => WorkflowRunStatusLabels.Succeeded,
        WorkflowAttemptOutcome.TimedOut => WorkflowRunStatusLabels.TimedOut,
        WorkflowAttemptOutcome.Cancelled => WorkflowRunStatusLabels.Cancelled,
        _ => WorkflowRunStatusLabels.Failed
    };

    /// <summary>True when the run's terminal record should read failed. Cancellation is its own terminal
    /// state and a timeout is not — a run that ran out of time did fail, it just failed on the clock.</summary>
    public static bool IsFailure(WorkflowAttemptOutcome outcome) =>
        outcome is not (WorkflowAttemptOutcome.Succeeded or WorkflowAttemptOutcome.Cancelled);

    /// <summary>Whether the policy gets to try again. Only an unrecognised failure is retryable, and only
    /// while attempts remain: everything else either succeeded or would reach the same verdict twice.</summary>
    public static bool AllowsAnotherAttempt(WorkflowAttemptOutcome outcome, int attemptNumber, int maxAttempts) =>
        outcome == WorkflowAttemptOutcome.Failed && attemptNumber < maxAttempts;

    /// <summary>How many attempts a run gets: what the policy says, and never fewer than one. A policy that
    /// names no count, or zero, still means the run is made once.</summary>
    /// <remarks>Until then the runner read the count as written, and a policy with no
    /// <c>maxAttempts</c> made no attempt at all and answered as if the run had succeeded.</remarks>
    public static int AttemptsAllowed(WorkflowRetryPolicy? retryPolicy) => Math.Max(1, retryPolicy?.MaxAttempts ?? 1);

    /// <summary>How long to wait before the next attempt. The doubling runs in double and is clamped, so a
    /// long-enough policy saturates instead of wrapping — Reeve's extra exponent guard could change no answer.</summary>
    /// <param name="retryPolicy">The run's policy.</param>
    /// <param name="attemptNumber">The attempt about to start, not the one that just failed.</param>
    public static int BackoffDelayMs(WorkflowRetryPolicy? retryPolicy, int attemptNumber)
    {
        if (retryPolicy == null || retryPolicy.MaxAttempts <= 1 || attemptNumber <= 1)
            return 0;

        var baseDelay = retryPolicy.InitialDelayMs;
        if (baseDelay <= 0)
            return 0;

        if (retryPolicy.BackoffStrategy is not ("exponential" or "Exponential"))
            return baseDelay;

        return (int)Math.Min(baseDelay * Math.Pow(2, attemptNumber - 1), int.MaxValue);
    }

    /// <summary>The stored retry policy, or null when there is none and null again when what is stored will
    /// not parse. A malformed policy must not stop a run that would otherwise have made one attempt.</summary>
    public static WorkflowRetryPolicy? ParseRetryPolicy(string? retryPolicyJson, JsonSerializerOptions jsonOptions, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(retryPolicyJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<WorkflowRetryPolicy>(retryPolicyJson, JsonMembers.Refusing(jsonOptions));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize RetryPolicyJson: {RetryPolicy}", retryPolicyJson);
            return null;
        }
    }

    /// <summary>Whether a cancellation was this workflow replacing itself. Only the caller's own token
    /// staying quiet tells a Restart supersession from a shutdown, or from a caller simply being obeyed.</summary>
    public static bool WasSupersededByRestart(WorkflowMode mode, CancellationToken leaseToken, CancellationToken callerToken) =>
        mode == WorkflowMode.Restart && leaseToken.IsCancellationRequested && !callerToken.IsCancellationRequested;
}
