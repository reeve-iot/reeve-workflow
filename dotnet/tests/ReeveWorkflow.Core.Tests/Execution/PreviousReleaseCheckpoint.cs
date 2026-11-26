using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using System.Text.Json;
using System.Text.Json.Serialization;
using static ReeveWorkflow.Core.Execution.WorkflowRunCheckpoint;

namespace ReeveWorkflow.Core.Tests.Execution.PreviousRelease;

public sealed class WorkflowSuspensionStatePayload
{
    public required string VariablesJson { get; init; }

    public required string PreviousOutputsJson { get; init; }

    public required string TriggerEnvelopeJson { get; init; }

    public required Guid? ExecutionStateId { get; init; }

    public string? PinnedVersionsJson { get; init; }

    public string? InputNodeId { get; init; }

    public int CallDepth { get; init; }

    public Guid? JournalExecutionId { get; init; }

    public string? CheckpointFormat { get; init; }

    public string? NodeInFlight { get; init; }

    public string? WriteMode { get; init; }

    public bool ContainerInFlightRepeatsWhole { get; init; }

    public string? NotReenterableBecause { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContinuationJson { get; init; }

    public WorkflowSuspensionStatePayload WithExecutionStateId(Guid? executionStateId) => new()
    {
        VariablesJson = VariablesJson,
        PreviousOutputsJson = PreviousOutputsJson,
        TriggerEnvelopeJson = TriggerEnvelopeJson,
        ExecutionStateId = executionStateId,
        PinnedVersionsJson = PinnedVersionsJson,
        InputNodeId = InputNodeId,
        CallDepth = CallDepth,
        JournalExecutionId = JournalExecutionId,
        CheckpointFormat = CheckpointFormat,
        NodeInFlight = NodeInFlight,
        WriteMode = WriteMode,
        ContainerInFlightRepeatsWhole = ContainerInFlightRepeatsWhole,
        NotReenterableBecause = NotReenterableBecause,
        ContinuationJson = ContinuationJson
    };
}


internal static class MainCheckpoint
{
    public static string Build(
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        string nodeInFlight,
        Guid journalExecutionId,
        Guid stateExecutionId,
        string? inputNodeId,
        Guid? triggerId,
        TriggerEnvelope? triggerEnvelope,
        RunStateWriteMode writeMode,
        string? notReenterableBecause,
        int maxBytes,
        string? continuationJson = null,
        string? outputsJsonAlready = null)
    {
        if (notReenterableBecause is null)
        {
            var outputsJson = outputsJsonAlready;
            try
            {
                outputsJson ??= JsonSerializer.Serialize(
                    executedOutputs.ToDictionary(kv => kv.Key, kv => (object?)kv.Value), WorkflowGraphHelpers.JsonOptions);
            }
            catch (Exception ex) when (ReeveWorkflow.Core.Expressions.ExpressionJsonValue.Unwritable(ex))
            {
                notReenterableBecause = Unserialisable;
            }

            if (outputsJson is not null)
            {
                var named = inputNodeId is not null && executedOutputs.ContainsKey(inputNodeId) ? inputNodeId : null;
                var full = Serialize(Payload(outputsJson, nodeInFlight, journalExecutionId, stateExecutionId, named,
                    triggerId, triggerEnvelope, writeMode, reason: null, continuationJson));
                if (System.Text.Encoding.UTF8.GetByteCount(full) <= maxBytes)
                    return full;

                if (continuationJson is not null)
                {
                    var root = Serialize(Payload(outputsJson, nodeInFlight, journalExecutionId, stateExecutionId, named,
                        triggerId, triggerEnvelope, writeMode, reason: null, continuationJson: null));
                    if (System.Text.Encoding.UTF8.GetByteCount(root) <= maxBytes)
                        return root;
                }

                notReenterableBecause = TooLarge;
            }
        }

        return Serialize(Payload("{}", nodeInFlight, journalExecutionId, stateExecutionId, null, triggerId,
            triggerEnvelope, writeMode, notReenterableBecause, continuationJson: null));
    }

    private static string SerializeTriggerEnvelope(Guid? triggerId, TriggerEnvelope? envelope)
    {
        if (!triggerId.HasValue && envelope is null)
            return "{}";
        var document = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (triggerId.HasValue)
            document[TriggerEnvelope.TriggerIdKey] = triggerId.Value;
        if (envelope is not null)
        {
            document[TriggerEnvelope.FiredAtKey] = envelope.FiredAtUtc;
            if (envelope.Payload.Count > 0)
                document[TriggerEnvelope.PayloadKey] = envelope.Payload;
        }

        return JsonSerializer.Serialize(document, WorkflowGraphHelpers.JsonOptions);
    }

    private static WorkflowSuspensionStatePayload Payload(
        string previousOutputsJson, string nodeInFlight, Guid journalExecutionId, Guid stateExecutionId,
        string? inputNodeId, Guid? triggerId, TriggerEnvelope? triggerEnvelope, RunStateWriteMode writeMode,
        string? reason, string? continuationJson) => new()
        {
            VariablesJson = "{}",
            CheckpointFormat = Format,
            PreviousOutputsJson = previousOutputsJson,
            TriggerEnvelopeJson = SerializeTriggerEnvelope(triggerId, triggerEnvelope),
            ExecutionStateId = stateExecutionId,
            PinnedVersionsJson = WorkflowVersionPinScope.CurrentJson(),
            InputNodeId = inputNodeId,
            CallDepth = WorkflowCallDepthScope.Current,
            JournalExecutionId = journalExecutionId,
            NodeInFlight = nodeInFlight,
            WriteMode = writeMode.Kind.ToString(),
            ContainerInFlightRepeatsWhole = continuationJson is null,
            NotReenterableBecause = reason,
            ContinuationJson = continuationJson
        };

    private static string Serialize(WorkflowSuspensionStatePayload payload) =>
        JsonSerializer.Serialize(payload, WorkflowGraphHelpers.JsonOptions);
}
