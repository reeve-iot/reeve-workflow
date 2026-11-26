using System.Text.Json;
using System.Text.Json.Serialization;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Stateless, pure helpers for <see cref="WorkflowRunner"/> that need a run to mean anything:
/// executed outputs, expression-context mapping, and suspension/resume payloads. No field or shared state here.</summary>
public static class WorkflowExecutionHelpers
{
    internal static Dictionary<string, object?> BuildResultOutputs(
        Dictionary<string, IReadOnlyDictionary<string, object?>> allOutputs,
        CompiledWalk compiled)
    {
        var resultOutputs = new Dictionary<string, object?>();

        foreach (var nodeId in compiled.Canvas.OutputNodeOrder)
        {
            var node = compiled.Required(nodeId);
            IReadOnlyDictionary<string, object?> resolved;
            try
            {
                var inputs = new Dictionary<string, object?>();
                foreach (var edge in node.Incoming)
                    ApplyMappings(inputs, edge.SourceNodeId, edge.Mappings, allOutputs, node);
                resolved = inputs;
            }
            catch (Exception ex) when (ex is PortConversionException or JsonProjectionException)
            {
                var message = ex is JsonProjectionException projection ? $"{projection.Code} ({projection.Category}): {ex.Message}" : ex.Message;
                throw new WorkflowNodeExecutionException(message, node.NodeId, node.Shape.BlockTypeId, ex);
            }

            foreach (var kvp in resolved)
                resultOutputs[kvp.Key] = kvp.Value;
        }

        return resultOutputs;
    }

    internal static void ApplyMappings(
        Dictionary<string, object?> inputs,
        string sourceNodeId,
        IReadOnlyList<WorkflowMappingDefinition> mappings,
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        CompiledNode compiled,
        HashSet<string>? wired = null)
    {
        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            if (mapping.IsLiteral)
            {
                wired?.Remove(mapping.TargetInput);
                inputs[mapping.TargetInput] = compiled.TryBindLiteral(sourceNodeId, mapping.TargetInput, out var literal)
                    ? literal
                    : FreshLiteral(mapping.LiteralValue);
                continue;
            }

            if (executedOutputs.TryGetValue(sourceNodeId, out var sourceOutputs) &&
                sourceOutputs.TryGetValue(mapping.SourceOutput, out var value))
            {
                if (compiled.ProjectionOf(sourceNodeId, mapping.TargetInput) is { } projection)
                {
                    if (projection.TryBind(value, $"Input '{mapping.TargetInput}' of '{compiled.NodeId}' from '{sourceNodeId}'", out var projected))
                    {
                        inputs[mapping.TargetInput] = projected;
                        wired?.Add(mapping.TargetInput);
                    }

                    continue;
                }

                inputs[mapping.TargetInput] = compiled.BindWire(sourceNodeId, mapping.TargetInput, value);
                wired?.Add(mapping.TargetInput);
            }
        }
    }

    internal static object? FreshLiteral(object? value) => value switch
    {
        List<object?> list => FreshList(list),
        Dictionary<string, object?> map => FreshMap(map),
        _ => value
    };

    private static List<object?> FreshList(List<object?> source)
    {
        var copy = new List<object?>(source.Count);
        for (var index = 0; index < source.Count; index++) copy.Add(FreshLiteral(source[index]));
        return copy;
    }

    private static Dictionary<string, object?> FreshMap(Dictionary<string, object?> source)
    {
        var copy = new Dictionary<string, object?>(source.Count, source.Comparer);
        foreach (var entry in source) copy.Add(entry.Key, FreshLiteral(entry.Value));
        return copy;
    }

    internal static IReadOnlySet<string>? ExpressionInputNames(IWorkflowBlockFactory factory)
    {
        HashSet<string>? names = null;
        foreach (var input in factory.Inputs)
        {
            if (input.Type.IsExpression)
                (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(input.Name);
        }

        return names;
    }

    public static bool HasExpressionInputs(
        Dictionary<string, object?> inputs,
        IReadOnlySet<string>? rawInputs)
    {
        foreach (var kv in inputs)
        {
            if (rawInputs != null && rawInputs.Contains(kv.Key))
                continue;
            if (kv.Value is string s && ExpressionSource.IsExpression(s))
                return true;
        }
        return false;
    }

    /// <summary>Flattens executed-outputs into a single variables snapshot (last-write wins across nodes).
    /// SECURITY: the result must not be logged.</summary>
    public static IReadOnlyDictionary<string, object?> BuildVariablesSnapshot(
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs)
    {
        var snapshot = new Dictionary<string, object?>();
        foreach (var kvp in executedOutputs)
        {
            foreach (var output in kvp.Value)
            {
                snapshot[output.Key] = output.Value;
            }
        }
        return snapshot;
    }

    /// <summary>Resolve the integer DurationMs input value the binder delivered as a long.</summary>
    public static long ResolveSleepDurationMs(IReadOnlyDictionary<string, object?> inputs)
    {
        if (!inputs.TryGetValue("DurationMs", out var raw) || raw is null)
            throw new ArgumentException("DurationMs is required.");

        return RunTimeValueReading.TryInt(raw, out var milliseconds)
            ? milliseconds
            : throw new ArgumentException("DurationMs must be an integer.");
    }

    internal static DateTimeOffset SleepEnd(DateTimeOffset now, long durationMs) =>
        durationMs > (DateTimeOffset.MaxValue - now).TotalMilliseconds - 1
            ? throw new OverflowException($"DurationMs is {durationMs}, which is beyond the last time a clock can name (overflow).")
            : now.AddMilliseconds(durationMs);

    internal static Compilation.CanvasSignature? RootSignature(string? inputParametersJson) =>
        Compilation.CanvasSignature.TryRead(inputParametersJson, null, out var signature, out _) ? signature : null;

    internal static IReadOnlyDictionary<string, object?>? ParameterScope(
        IReadOnlyDictionary<string, object?>? supplied, Compilation.CanvasSignature? signature)
    {
        var missing = signature?.Inputs.Select(p => (p.Name, p.IsRequired))
            .Concat(signature.UntypedInputs.Select(p => (p.Name, p.IsRequired)))
            .Where(p => !p.IsRequired && supplied?.ContainsKey(p.Name) != true).Select(p => p.Name).Distinct().ToList();
        if (supplied is null && missing is not { Count: > 0 })
            return null;
        var read = supplied is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(supplied);
        foreach (var name in missing ?? [])
            read[name] = null;
        return read;
    }

    internal static IReadOnlyDictionary<string, object?>? RunParametersOf(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> previousOutputs, string? inputNodeId) =>
        inputNodeId is not null && previousOutputs.TryGetValue(inputNodeId, out var parameters) ? parameters : null;

    internal static string BuildSuspensionStatePayload(
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        CompiledWalk walk,
        Guid? triggerId,
        TriggerEnvelope? triggerEnvelope,
        string? inputNodeId,
        string? continuationJson = null,
        string? rootNodeInFlight = null)
    {
        var payload = new WorkflowSuspensionStatePayload
        {
            VariablesJson = System.Text.Json.JsonSerializer.Serialize(
                BuildVariablesSnapshot(executedOutputs), WorkflowGraphHelpers.JsonOptions),
            PreviousOutputsJson = PersistedValueCodec.WriteNodeOutputs(executedOutputs, walk),
            TriggerEnvelopeJson = PersistedValueCodec.WriteTriggerEnvelope(triggerId, triggerEnvelope),
            ExecutionStateId = null,
            PinnedVersionsJson = WorkflowVersionPinScope.CurrentJson(),
            InputNodeId = inputNodeId is not null && executedOutputs.ContainsKey(inputNodeId) ? inputNodeId : null,
            CallDepth = WorkflowCallDepthScope.Current,
            ContinuationJson = continuationJson,
            NodeInFlight = rootNodeInFlight
        };
        return System.Text.Json.JsonSerializer.Serialize(payload, WorkflowGraphHelpers.JsonOptions);
    }

    /// <summary>A resumed wait node's outputs in their terms' types: <c>Outcome</c>, <c>TriggerData</c> and <c>LastEvaluation</c>,
    /// the last two null on a timeout, beside the keys the audit reads, which the output gate drops.</summary>
    /// <exception cref="JsonException">The outcome does not read.</exception>
    public static Dictionary<string, object?> BuildResumeBlockOutput(ResumeContext ctx)
    {
        var outputs = new Dictionary<string, object?>
        {
            ["resumedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["suspensionId"] = ctx.SuspensionId.ToString()
        };

        void Emit(string declaredPortName, string camelCaseKey, object? value)
        {
            outputs[declaredPortName] = value;
            outputs[camelCaseKey] = value;
        }

        using var doc = JsonDocument.Parse(ctx.OutcomeJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The wait's outcome is not a JSON object.");

        string? outcome = null;
        if (root.TryGetProperty("outcome", out var outcomeEl))
        {
            outcome = outcomeEl.ValueKind == JsonValueKind.String
                ? outcomeEl.GetString()
                : throw new JsonException("The wait's outcome is not text.");
            Emit("Outcome", "outcome", outcome);
        }

        var timedOut = outcome == WorkflowResumeDecisions.TimedOutOutcome;
        if (timedOut)
        {
            Emit("TriggerData", "triggerData", null);
            Emit("LastEvaluation", WorkflowResumeDecisions.LastEvaluationKey, null);
        }
        else
        {
            if (root.TryGetProperty("triggerData", out var tdEl))
                Emit("TriggerData", "triggerData", tdEl.ValueKind is JsonValueKind.Null ? null : tdEl.Clone());

            if (ctx.Kind is WorkflowSuspensionKind.WaitTemplate or WorkflowSuspensionKind.Predicate
                && root.TryGetProperty(WorkflowResumeDecisions.LastEvaluationKey, out var leEl))
            {
                Emit("LastEvaluation", WorkflowResumeDecisions.LastEvaluationKey, leEl.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => throw new JsonException("The wait's lastEvaluation is not a Boolean."),
                });
            }
        }

        if (root.TryGetProperty(WorkflowResumeDecisions.ResumeReasonKey, out var rrEl) && rrEl.ValueKind == JsonValueKind.String)
            outputs[WorkflowResumeDecisions.ResumeReasonKey] = rrEl.GetString();

        return outputs;
    }
}
