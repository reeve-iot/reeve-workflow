using ReeveWorkflow.Core.Hosting;
using System.Text.Json;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>The write before a root block, as the payload a recovery re-enters the run from: the format a parked run's
/// payload already has, plus where the run was and which journal entry it writes into.</summary>
/// <remarks>0, as the row's <c>variables_json</c>, which held the flattened scope until then; nothing in the
/// engine read that. <see cref="Variables"/> gives the flattened scope back to a host that shows it.</remarks>
public static class WorkflowRunCheckpoint
{
    /// <summary>Why a checkpoint cannot be re-entered: its outputs did not fit under the cap.</summary>
    public const string TooLarge = "too_large";

    /// <summary>Why a checkpoint cannot be re-entered: a node's Skip policy caught it, and a re-entry would not know.</summary>
    public const string SkippedANode = "skipped_a_node";

    /// <summary>Why a checkpoint cannot be re-entered: the run's outputs cannot be written as JSON.</summary>
    public const string Unserialisable = "unserialisable";

    internal static string Build(
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        CompiledWalk walk,
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
        if (outputsJsonAlready is null)
        {
            return BuildCached(executedOutputs, walk, nodeInFlight, journalExecutionId, stateExecutionId, inputNodeId, triggerId,
                triggerEnvelope, writeMode, notReenterableBecause, maxBytes, continuationJson,
                new CheckpointOutputFragments());
        }

        if (notReenterableBecause is null)
        {
            var named = inputNodeId is not null && executedOutputs.ContainsKey(inputNodeId) ? inputNodeId : null;
            var full = Serialize(Payload(outputsJsonAlready, nodeInFlight, journalExecutionId, stateExecutionId, named,
                triggerId, triggerEnvelope, writeMode, reason: null, continuationJson));
            if (System.Text.Encoding.UTF8.GetByteCount(full) <= maxBytes)
                return full;

            if (continuationJson is not null)
            {
                var root = Serialize(Payload(outputsJsonAlready, nodeInFlight, journalExecutionId, stateExecutionId, named,
                    triggerId, triggerEnvelope, writeMode, reason: null, continuationJson: null));
                if (System.Text.Encoding.UTF8.GetByteCount(root) <= maxBytes)
                    return root;
            }

            notReenterableBecause = TooLarge;
        }

        return Serialize(Payload("{}", nodeInFlight, journalExecutionId, stateExecutionId, null, triggerId,
            triggerEnvelope, writeMode, notReenterableBecause, continuationJson: null));
    }

    internal static string BuildCached(
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        CompiledWalk walk,
        string nodeInFlight,
        Guid journalExecutionId,
        Guid stateExecutionId,
        string? inputNodeId,
        Guid? triggerId,
        TriggerEnvelope? triggerEnvelope,
        RunStateWriteMode writeMode,
        string? notReenterableBecause,
        int maxBytes,
        string? continuationJson,
        CheckpointOutputFragments fragments)
    {
        if (notReenterableBecause is null)
        {
            var named = inputNodeId is not null && executedOutputs.ContainsKey(inputNodeId) ? inputNodeId : null;
            using var full = Envelope(Payload("", nodeInFlight, journalExecutionId, stateExecutionId, named,
                triggerId, triggerEnvelope, writeMode, reason: null, continuationJson));
            using var root = continuationJson is null
                ? null
                : Envelope(Payload("", nodeInFlight, journalExecutionId, stateExecutionId, named,
                    triggerId, triggerEnvelope, writeMode, reason: null, continuationJson: null));

            var floor = Math.Min(full.Length, root?.Length ?? int.MaxValue) + OutputBraces;
            long outputs = 0;
            var count = 0;
            var unwritable = false;
            var over = false;
            foreach (var (nodeId, nodeOutputs) in executedOutputs)
            {
                if (over)
                {
                    unwritable = fragments.IsUnwritable(nodeId, nodeOutputs);
                    if (unwritable)
                        break;
                    continue;
                }

                var fragment = fragments.Get(nodeId, nodeOutputs, walk.Node(nodeId), maxBytes, needBytes: false);
                if (fragment.Unwritable)
                {
                    unwritable = true;
                    break;
                }

                outputs += fragment.Length + (count++ > 0 ? 1 : 0);
                over = floor + outputs > maxBytes;
            }

            if (unwritable)
            {
                notReenterableBecause = Unserialisable;
            }
            else if (!over)
            {
                if (full.Length + OutputBraces + outputs <= maxBytes)
                    return Assemble(full, executedOutputs, walk, fragments, maxBytes, outputs);

                if (root is not null && root.Length + OutputBraces + outputs <= maxBytes)
                    return Assemble(root, executedOutputs, walk, fragments, maxBytes, outputs);
            }

            notReenterableBecause ??= TooLarge;
        }

        return Serialize(Payload("{}", nodeInFlight, journalExecutionId, stateExecutionId, null, triggerId,
            triggerEnvelope, writeMode, notReenterableBecause, continuationJson: null));
    }

    private const int OutputBraces = 2;

    private static ReadOnlySpan<byte> EmptyOutputs => "\"PreviousOutputsJson\":\"\""u8;

    private static PooledBuffer Envelope(WorkflowSuspensionStatePayload payload)
    {
        var buffer = new PooledBuffer();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            JsonSerializer.Serialize(writer, payload, WorkflowGraphHelpers.JsonOptions);
            writer.Flush();
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static string Assemble(
        PooledBuffer envelope,
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        CompiledWalk walk,
        CheckpointOutputFragments fragments,
        int maxBytes,
        long outputsLength)
    {
        var shell = envelope.WrittenSpan;
        var at = shell.IndexOf(EmptyOutputs);
        if (at < 0)
            throw new InvalidOperationException("A checkpoint envelope does not carry the outputs property.");

        var split = at + EmptyOutputs.Length - 1;
        using var output = new PooledBuffer(shell.Length + OutputBraces + (int)outputsLength);
        output.Write(shell[..split]);
        output.Write((byte)'{');
        var first = true;
        foreach (var (nodeId, nodeOutputs) in executedOutputs)
        {
            var bytes = fragments.Get(nodeId, nodeOutputs, walk.Node(nodeId), maxBytes, needBytes: true).Bytes
                ?? throw new InvalidOperationException("A checkpoint output was not serialised.");
            if (!first)
                output.Write((byte)',');
            first = false;
            output.Write(bytes);
        }

        output.Write((byte)'}');
        output.Write(shell[split..]);
        return System.Text.Encoding.UTF8.GetString(output.WrittenSpan);
    }

    /// <summary>What a checkpoint says it is, and what <see cref="Read"/> requires: a row's variables written without a checkpoint are the run's inputs, which a caller chose, so the shape of the payload alone does not tell them apart.</summary>
    public const string Format = "reeve-workflow/checkpoint@1";

    /// <summary>The flattened scope a row's variables describe, whichever of the two shapes they are in: the outputs of
    /// every node merged, the last one winning, as the column held for a row without a checkpoint. Never log it.</summary>
    public static IReadOnlyDictionary<string, object?> Variables(string variablesJson)
    {
        if (Read(variablesJson) is not { } checkpoint)
            return WorkflowVariablesEncoder.Decode(variablesJson);

        var outputs = PersistedValueCodec.ForDisplay(checkpoint.PreviousOutputsJson);
        return WorkflowExecutionHelpers.BuildVariablesSnapshot(outputs);
    }

    /// <summary>Where the run was below the root canvas, outermost frame first, and empty for a run at the root or
    /// a continuation that will not read. A run with frames is re-entered inside the container
    /// <see cref="WorkflowSuspensionStatePayload.NodeInFlight"/> names, at the position they describe.</summary>
    /// <remarks>An older checkpoint has none, and says so in
    /// <see cref="WorkflowSuspensionStatePayload.ContainerInFlightRepeatsWhole"/>.</remarks>
    public static IReadOnlyList<WorkflowContinuationFrame> Continuation(WorkflowSuspensionStatePayload? payload) =>
        WorkflowContinuation.Read(payload?.ContinuationJson);

    /// <summary>The checkpoint a row's variables carry, or null when they are the flattened scope or will not read.</summary>
    public static WorkflowSuspensionStatePayload? Read(string? checkpointJson)
    {
        if (string.IsNullOrWhiteSpace(checkpointJson))
            return null;
        try
        {
            var payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(checkpointJson, WorkflowGraphHelpers.JsonOptions);
            return payload is { CheckpointFormat: Format, NodeInFlight: not null, JournalExecutionId: not null, PreviousOutputsJson: not null }
                ? payload
                : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The checkpoint this row carries about itself, or null. A checkpoint naming another row is not this
    /// row's to re-enter, close the entry of, or leave the entry open for.</summary>
    /// <remarks>Public for a host whose own sweep ends these rows reads the entry to close off this, and
    /// <see cref="Read"/> alone would hand it a checkpoint that names somebody else's row.</remarks>
    public static WorkflowSuspensionStatePayload? Of(WorkflowRunStateRow? row) =>
        row is not null && Read(row.VariablesJson) is { } checkpoint && checkpoint.ExecutionStateId == row.ExecutionId
            ? checkpoint
            : null;

    private static WorkflowSuspensionStatePayload Payload(
        string previousOutputsJson, string nodeInFlight, Guid journalExecutionId, Guid stateExecutionId,
        string? inputNodeId, Guid? triggerId, TriggerEnvelope? triggerEnvelope, RunStateWriteMode writeMode,
        string? reason, string? continuationJson) => new()
        {
            VariablesJson = "{}",
            CheckpointFormat = Format,
            PreviousOutputsJson = previousOutputsJson,
            TriggerEnvelopeJson = PersistedValueCodec.WriteTriggerEnvelope(triggerId, triggerEnvelope),
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
