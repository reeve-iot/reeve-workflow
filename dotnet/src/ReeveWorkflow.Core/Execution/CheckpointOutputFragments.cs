using System.Text.Json;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Execution;

internal sealed class CheckpointOutputFragments
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Fragment> entries = new(StringComparer.Ordinal);
    private long retained;

    internal readonly record struct Fragment(
        IReadOnlyDictionary<string, object?> Source, byte[]? Bytes, int Length, bool Unwritable);

    public void Record(string nodeId, IReadOnlyDictionary<string, object?> outputs, CompiledNode? node, int maxBytes) =>
        Get(nodeId, outputs, node, maxBytes, needBytes: false);

    public Fragment Get(
        string nodeId, IReadOnlyDictionary<string, object?> outputs, CompiledNode? node, int maxBytes, bool needBytes)
    {
        lock (gate)
        {
            if (Held(nodeId, outputs, needBytes) is { } held)
                return held;
        }

        using var raw = new PooledBuffer();
        using var escaped = new PooledBuffer();
        try
        {
            Write(raw, nodeId, outputs, node);
            using var writer = new Utf8JsonWriter(escaped);
            writer.WriteStringValue(raw.WrittenSpan[1..^1]);
        }
        catch (Exception ex) when (ExpressionJsonValue.Unwritable(ex))
        {
            lock (gate)
            {
                retained -= entries.GetValueOrDefault(nodeId).Bytes?.Length ?? 0;
                return entries[nodeId] = new Fragment(outputs, null, 0, Unwritable: true);
            }
        }

        var inner = escaped.WrittenSpan[1..^1];
        lock (gate)
        {
            if (Held(nodeId, outputs, needBytes) is { } raced)
                return raced;

            var others = retained - (entries.GetValueOrDefault(nodeId).Bytes?.Length ?? 0);
            var keep = needBytes || others + inner.Length <= maxBytes;
            var fragment = new Fragment(outputs, keep ? inner.ToArray() : null, inner.Length, Unwritable: false);
            retained = others + (fragment.Bytes?.Length ?? 0);
            return entries[nodeId] = fragment;
        }
    }

    public bool IsUnwritable(string nodeId, IReadOnlyDictionary<string, object?> outputs)
    {
        lock (gate)
            return entries.TryGetValue(nodeId, out var held) && ReferenceEquals(held.Source, outputs) && held.Unwritable;
    }

    private Fragment? Held(string nodeId, IReadOnlyDictionary<string, object?> outputs, bool needBytes) =>
        entries.TryGetValue(nodeId, out var held) && ReferenceEquals(held.Source, outputs)
            && (held.Unwritable || held.Bytes is not null || !needBytes)
                ? held
                : null;

    private static void Write(PooledBuffer into, string nodeId, IReadOnlyDictionary<string, object?> outputs, CompiledNode? node)
    {
        using var writer = new Utf8JsonWriter(into);
        writer.WriteStartObject();
        writer.WritePropertyName(nodeId);
        PersistedValueCodec.WriteOutputs(writer, outputs, node);
        writer.WriteEndObject();
    }
}
