using System.Buffers;
using System.Text;
using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal sealed class PersistedPayloadException(string path, string? nodeId, string? port, string reason, Exception? inner = null)
    : Exception($"{path}{(nodeId is null ? "" : $", node '{nodeId}'")}{(port is null ? "" : $", port '{port}'")}: {reason}", inner)
{
    public string PayloadPath { get; } = path;

    public string? NodeId { get; } = nodeId;

    public string? Port { get; } = port;
}

internal static class PersistedValueCodec
{
    internal static string WriteNodeOutputs(
        IEnumerable<KeyValuePair<string, IReadOnlyDictionary<string, object?>>> outputs, CompiledWalk walk)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (nodeId, values) in outputs)
            {
                writer.WritePropertyName(nodeId);
                WriteOutputs(writer, values, walk.Node(nodeId));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static void WriteOutputs(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> outputs, CompiledNode? node)
    {
        writer.WriteStartObject();
        foreach (var (port, value) in outputs)
        {
            writer.WritePropertyName(port);
            WriteValue(writer, node is not null && node.OutputTypes.TryGetValue(port, out var type) ? type : null, value);
        }

        writer.WriteEndObject();
    }

    internal static string WriteOutputs(IReadOnlyDictionary<string, object?> outputs, CompiledNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            WriteOutputs(writer, outputs, node);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string WriteAnswer(
        IReadOnlyDictionary<string, object?> outputs, IReadOnlyDictionary<string, ClosedPortType?> outputTypes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (port, value) in outputs)
            {
                writer.WritePropertyName(port);
                WriteValue(writer, outputTypes.GetValueOrDefault(port), value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static Dictionary<string, JsonElement> WriteVariables(
        IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<string, ClosedPortType> types)
    {
        var written = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                types[name].WriteTo(writer, value);
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            written[name] = document.RootElement.Clone();
        }

        return written;
    }

    internal static string WriteTriggerEnvelope(Guid? triggerId, TriggerEnvelope? envelope)
    {
        if (!triggerId.HasValue && envelope is null)
            return "{}";
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (triggerId.HasValue)
            {
                writer.WritePropertyName(TriggerEnvelope.TriggerIdKey);
                JsonSerializer.Serialize(writer, triggerId.Value, WorkflowGraphHelpers.JsonOptions);
            }

            if (envelope is not null)
            {
                writer.WritePropertyName(TriggerEnvelope.FiredAtKey);
                JsonSerializer.Serialize(writer, envelope.FiredAtUtc, WorkflowGraphHelpers.JsonOptions);
                if (envelope.Payload.Count > 0)
                {
                    writer.WritePropertyName(TriggerEnvelope.PayloadKey);
                    writer.WriteStartObject();
                    foreach (var (key, value) in envelope.Payload)
                    {
                        var type = envelope.Terms?.GetValueOrDefault(key)
                            ?? throw new InvalidOperationException($"The trigger payload key '{key}' holds no term to be written by.");
                        writer.WritePropertyName(key);
                        type.WriteTo(writer, value);
                    }

                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter writer, ClosedPortType? type, object? value)
    {
        if (type is not null && (value is null || !type.IsJsonSerializable || type.ClrType.IsInstanceOfType(type.Normalised(value))))
            type.WriteTo(writer, value);
        else
            JsonSerializer.Serialize(writer, value, WorkflowGraphHelpers.JsonOptions);
    }

    internal static Dictionary<string, IReadOnlyDictionary<string, object?>> ReadNodeOutputs(
        string? json, CompiledCanvas canvas, string path)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        using var document = Parse(json, path);
        if (document is null)
            return result;

        var nodes = canvas.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (!nodes.TryGetValue(entry.Name, out var node))
                throw new PersistedPayloadException(path, entry.Name, null, "the pinned program has no such node");
            result[entry.Name] = ReadOutputs(entry.Value, node, path);
        }

        return result;
    }

    internal static IReadOnlySet<string> NodeIds(string? json)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var document = Parse(json, "outputs");
        if (document is not null)
        {
            foreach (var entry in document.RootElement.EnumerateObject())
                ids.Add(entry.Name);
        }

        return ids;
    }

    internal static IReadOnlyDictionary<string, object?> ReadOutputs(JsonElement outputs, CompiledNode node, string path)
    {
        if (outputs.ValueKind != JsonValueKind.Object)
            throw new PersistedPayloadException(path, node.NodeId, null, "the node's outputs are not a JSON object");
        var read = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in outputs.EnumerateObject())
        {
            if (!node.OutputNames.Contains(entry.Name))
                continue;
            read[entry.Name] = node.OutputTypes.TryGetValue(entry.Name, out var type)
                ? Read(type, entry.Value, path, node.NodeId, entry.Name)
                : UntypedAny(entry.Value);
        }

        return read;
    }

    internal static IReadOnlyDictionary<string, object?> ReadAnswer(
        string? json, IReadOnlyDictionary<string, ClosedPortType?> outputTypes, string path, string? nodeId)
    {
        var read = new Dictionary<string, object?>(StringComparer.Ordinal);
        using var document = Parse(json, path, nodeId);
        if (document is null)
            return read;

        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (!outputTypes.TryGetValue(entry.Name, out var type))
                continue;
            read[entry.Name] = type is null ? UntypedAny(entry.Value) : Read(type, entry.Value, path, nodeId, entry.Name);
        }

        return read;
    }

    internal static IReadOnlyDictionary<string, object?> ReadChildAnswer(
        string? json, IReadOnlyDictionary<string, ClosedPortType> types, string? nodeId)
    {
        var read = new Dictionary<string, object?>(StringComparer.Ordinal);
        using var document = Parse(json, "child", nodeId);
        if (document is null)
            return read;
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            read[entry.Name] = types.TryGetValue(entry.Name, out var type)
                ? Read(type, entry.Value, "child", nodeId, entry.Name)
                : UntypedAny(entry.Value);
        }

        return read;
    }

    internal static IReadOnlyDictionary<string, ClosedPortType?> CallTypes(CompiledNode container, string subCanvas, CompiledCanvas called)
    {
        var types = new Dictionary<string, ClosedPortType?>(called.OutputTypes, StringComparer.Ordinal);
        foreach (var port in container.OutputPorts)
        {
            if (port.Sources.TryGetValue(subCanvas, out var rawKey) && called.OutputTypes.TryGetValue(rawKey, out var type))
                types.TryAdd(port.Name, type);
        }

        return types;
    }

    internal static (Guid? TriggerId, TriggerEnvelope? Envelope) ReadTriggerEnvelope(
        string? json, Func<string, ClosedPortType?> termOf)
    {
        const string path = "trigger";
        using var document = Parse(json, path);
        if (document is null)
            return (null, null);

        var root = document.RootElement;
        var hasId = root.TryGetProperty(TriggerEnvelope.TriggerIdKey, out var idElement);
        var hasFiredAt = root.TryGetProperty(TriggerEnvelope.FiredAtKey, out var firedAtElement);
        var hasPayload = root.TryGetProperty(TriggerEnvelope.PayloadKey, out var payloadElement);
        if (!hasId && !hasFiredAt && !hasPayload)
            return (null, null);

        Guid? triggerId = null;
        if (hasId)
        {
            triggerId = idElement.ValueKind == JsonValueKind.String && idElement.TryGetGuid(out var id)
                ? id
                : throw new PersistedPayloadException(path, null, TriggerEnvelope.TriggerIdKey, "not a trigger id");
        }

        if (!hasFiredAt && !hasPayload)
            return (triggerId, null);
        if (!hasFiredAt || firedAtElement.ValueKind != JsonValueKind.String
            || !WorkflowDateTimeText.TryRead(firedAtElement, out var firedAt))
            throw new PersistedPayloadException(path, null, TriggerEnvelope.FiredAtKey, "not the time of a fire");

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var terms = new Dictionary<string, ClosedPortType>(StringComparer.Ordinal);
        if (hasPayload)
        {
            if (payloadElement.ValueKind != JsonValueKind.Object)
                throw new PersistedPayloadException(path, null, TriggerEnvelope.PayloadKey, "not a JSON object");
            foreach (var property in payloadElement.EnumerateObject())
            {
                if (termOf(property.Name) is not { } type)
                    continue;
                values[property.Name] = Read(type, property.Value, path, null, property.Name);
                terms[property.Name] = type;
            }
        }

        return (triggerId, new TriggerEnvelope(firedAt, values) { Terms = terms });
    }

    internal static void ReadPosition(
        IReadOnlyList<WorkflowContinuationFrame> frames, string? continuationJson, CompiledWalk root, string rootNode)
    {
        if (frames.Count == 0 && !string.IsNullOrWhiteSpace(continuationJson))
            throw new PersistedPayloadException("frame", rootNode, null, "the position does not read");
        var walk = root;
        var container = rootNode;
        foreach (var frame in frames)
        {
            if (walk.Node(container) is null)
                throw new PersistedPayloadException("frame", container, null, "the pinned program has no such container");
            foreach (var (subCanvas, calls) in frame.CompletedBySubCanvas)
            {
                var called = walk.Child(container, subCanvas)
                    ?? throw new PersistedPayloadException("call", container, null, $"the pinned program has no sub-canvas '{subCanvas}'");
                var types = CallTypes(walk.Node(container)!, subCanvas, called.Canvas);
                foreach (var call in calls)
                    ReadAnswer(call.OutputsJson, types, "call", container);
            }

            var level = walk.Child(container, frame.SubCanvas)
                ?? throw new PersistedPayloadException("frame", container, null, $"the pinned program has no sub-canvas '{frame.SubCanvas}'");
            ReadNodeOutputs(frame.PreviousOutputsJson, level.Canvas, "frame");
            walk = level;
            container = frame.NodeInFlight;
        }

        if (walk.Node(container) is null)
            throw new PersistedPayloadException(frames.Count == 0 ? "outputs" : "frame", container, null, "the pinned program has no such node");
    }

    internal static Dictionary<string, IReadOnlyDictionary<string, object?>> ForDisplay(string? json)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        JsonDocument? document;
        try
        {
            document = Parse(json, "outputs");
        }
        catch (PersistedPayloadException)
        {
            return result;
        }

        using (document)
        {
            if (document is null)
                return result;
            foreach (var node in document.RootElement.EnumerateObject())
            {
                result[node.Name] = node.Value.ValueKind == JsonValueKind.Object
                    ? node.Value.EnumerateObject().ToDictionary(p => p.Name, p => UntypedAny(p.Value), StringComparer.Ordinal)
                    : new Dictionary<string, object?>();
            }
        }

        return result;
    }

    internal static object? Read(ClosedPortType type, JsonElement value, string path, string? nodeId, string? port)
    {
        try
        {
            return type.FromJson(value);
        }
        catch (PersistedValueReadException ex)
        {
            throw new PersistedPayloadException(path, nodeId, port, ex.Reason, ex);
        }
    }

    private static JsonDocument? Parse(string? json, string path, string? nodeId = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new PersistedPayloadException(path, nodeId, null, "the payload is not JSON", ex);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object)
            return document;
        document.Dispose();
        throw new PersistedPayloadException(path, nodeId, null, "the payload is not a JSON object");
    }

    private static object? UntypedAny(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var whole) => whole,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => UntypedAny(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(UntypedAny).ToList(),
        _ => element.GetRawText(),
    };
}
