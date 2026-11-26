using System.Text.Json;
using System.Text.Json.Serialization;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What a run parked on a run it started is resolved with: how that run ended, and what it produced.
///</summary>
/// <remarks>Written by whatever notices the child ended — this package's waker — and read by the resume, which
/// hands it to the block through <see cref="WorkflowSubRunResumeScope"/>. Kept out of both so that the shape is
/// stated once.</remarks>
public static class WorkflowSubRunOutcomes
{
    /// <summary>The config a parent stores when it parks: the run it is waiting on.</summary>
    /// <remarks>Named by <c>SubWorkflowBlock</c>, which writes it, and read here, which is why the shape lives
    /// on this side of the package boundary rather than in the block that writes it.</remarks>
    public sealed class Wait
    {
        /// <summary>The child's <c>workflow_execution_logs.id</c>.</summary>
        public Guid ChildExecutionLogId { get; init; }
    }

    /// <summary>What a parked parent is waiting on, or null when its config does not say.</summary>
    public static Guid? WaitingOn(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;
        try
        {
            var wait = JsonSerializer.Deserialize<Wait>(configJson, WorkflowGraphHelpers.JsonOptions);
            return wait is null || wait.ChildExecutionLogId == Guid.Empty ? null : wait.ChildExecutionLogId;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The outcome a resolve writes when the run a parent waited on has ended.</summary>
    public static string Resolved(Guid childExecutionLogId, bool succeeded, string? outputsJson, string? error) =>
        JsonSerializer.Serialize(
            new Outcome
            {
                Outcome1 = succeeded ? WorkflowWaitOutcomes.Satisfied : Failed,
                ChildExecutionLogId = childExecutionLogId,
                OutputsJson = outputsJson,
                Error = error
            },
            WorkflowGraphHelpers.JsonOptions);

    /// <summary>The answer for <paramref name="nodeId"/>, or null when this outcome is not one of these.</summary>
    public static WorkflowSubRunAnswer? Read(string? nodeId, string? outcomeJson)
    {
        if (nodeId is null || string.IsNullOrWhiteSpace(outcomeJson))
            return null;
        try
        {
            var outcome = JsonSerializer.Deserialize<Outcome>(outcomeJson, WorkflowGraphHelpers.JsonOptions);
            return outcome is null || outcome.ChildExecutionLogId == Guid.Empty
                ? null
                : new WorkflowSubRunAnswer(nodeId, outcome.ChildExecutionLogId,
                    succeeded: outcome.Outcome1 == WorkflowWaitOutcomes.Satisfied, outcome.OutputsJson, outcome.Error);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The answer as the outputs an audit reads an outcome out of, so that one road decides the kind.</summary>
    public static IReadOnlyDictionary<string, object?> Audited(WorkflowSubRunAnswer? answer) =>
        new Dictionary<string, object?>
        {
            ["outcome"] = answer is null ? null : answer.Succeeded ? WorkflowWaitOutcomes.Satisfied : Failed
        };

    /// <summary>What a parent reads when the run it waited on did not succeed. Not a timeout: the wait was
    /// answered, and what it was answered with is a failure.</summary>
    public const string Failed = "failed";

    private sealed class Outcome
    {
        [JsonPropertyName("outcome")]
        public string? Outcome1 { get; init; }

        public Guid ChildExecutionLogId { get; init; }

        public string? OutputsJson { get; init; }

        public string? Error { get; init; }
    }
}
