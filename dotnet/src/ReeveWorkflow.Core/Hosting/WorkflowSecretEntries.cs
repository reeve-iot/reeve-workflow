using System.Text.Json;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Hosting;

internal readonly record struct WorkflowSecretEntry(
    CanvasPath Canvas, string NodeId, string Input, string? EdgeId, string Key, string Value, int Row, string Pointer, bool IsText = true);

internal static class WorkflowSecretEntries
{
    internal const string Masked = "***";

    internal static bool IsMasked(IWorkflowSecretCodec? codec, string key, string value) =>
        codec?.Classify(value) switch
        {
            WorkflowSecretForm.Protected => true,
            WorkflowSecretForm.Reference => false,
            _ => WorkflowSensitiveNames.Matches(key),
        };

    internal static List<WorkflowSecretEntry> Walk(JsonElement root, IWorkflowBlocks blocks, bool includeNonText = false)
    {
        var walk = new Walker(blocks, includeNonText);
        if (root.ValueKind == JsonValueKind.Object)
            walk.Canvas(root, "", CanvasPath.Root);
        return walk.Found;
    }

    private sealed class Walker(IWorkflowBlocks blocks, bool includeNonText)
    {
        private readonly Dictionary<string, IReadOnlyList<string>> secretInputs = new(StringComparer.OrdinalIgnoreCase);

        public List<WorkflowSecretEntry> Found { get; } = [];

        public void Canvas(JsonElement canvas, string at, CanvasPath path)
        {
            var secretNodes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (canvas.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var node in nodes.EnumerateArray())
                    Node(node, JsonPointer.Append(JsonPointer.Append(at, "nodes"), index++), path, secretNodes);
            }

            if (secretNodes.Count == 0 || !canvas.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
                return;

            var edgeIndex = 0;
            foreach (var edge in edges.EnumerateArray())
            {
                var edgeAt = JsonPointer.Append(JsonPointer.Append(at, "edges"), edgeIndex++);
                if (Text(edge, "targetNodeId") is not { } target || !secretNodes.TryGetValue(target, out var inputs)
                    || !edge.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.Object)
                    continue;

                var edgeId = Text(edge, "id") ?? "";
                foreach (var input in inputs)
                {
                    if (mappings.TryGetProperty(input, out var mapping) && mapping.ValueKind == JsonValueKind.Object
                        && string.Equals(Text(mapping, "type"), "literal", StringComparison.OrdinalIgnoreCase)
                        && mapping.TryGetProperty("literalValue", out var literal))
                    {
                        Entries(literal, JsonPointer.Append(JsonPointer.Append(JsonPointer.Append(edgeAt, "mappings"), input), "literalValue"),
                            path, target, input, edgeId);
                    }
                }
            }
        }

        private void Node(JsonElement node, string at, CanvasPath path, Dictionary<string, IReadOnlyList<string>> secretNodes)
        {
            if (node.ValueKind != JsonValueKind.Object)
                return;

            var id = Text(node, "id") ?? "";
            var inputs = SecretInputs(Text(node, "blockTypeId"));
            if (inputs.Count > 0)
            {
                secretNodes.TryAdd(id, inputs);
                if (node.TryGetProperty("configuration", out var configuration) && configuration.ValueKind == JsonValueKind.Object)
                {
                    foreach (var input in inputs)
                    {
                        if (configuration.TryGetProperty(input, out var literal))
                            Entries(literal, JsonPointer.Append(JsonPointer.Append(at, "configuration"), input), path, id, input, null);
                    }
                }
            }

            if (node.TryGetProperty("subCanvases", out var subCanvases) && subCanvases.ValueKind == JsonValueKind.Object)
            {
                foreach (var sub in subCanvases.EnumerateObject())
                {
                    if (sub.Value.ValueKind == JsonValueKind.Object)
                        Canvas(sub.Value, JsonPointer.Append(JsonPointer.Append(at, "subCanvases"), sub.Name), path.Child(id, sub.Name));
                }
            }
        }

        private void Entries(JsonElement literal, string at, CanvasPath path, string nodeId, string input, string? edgeId)
        {
            if (literal.ValueKind != JsonValueKind.Object)
                return;

            var row = 0;
            foreach (var entry in literal.EnumerateObject())
            {
                row++;
                if (entry.Value.ValueKind == JsonValueKind.String)
                    Found.Add(new(path, nodeId, input, edgeId, entry.Name, entry.Value.GetString()!, row, JsonPointer.Append(at, entry.Name)));
                else if (includeNonText)
                    Found.Add(new(path, nodeId, input, edgeId, entry.Name, "", row, JsonPointer.Append(at, entry.Name), IsText: false));
            }
        }

        private IReadOnlyList<string> SecretInputs(string? blockTypeId)
        {
            if (blockTypeId is null)
                return [];
            if (!secretInputs.TryGetValue(blockTypeId, out var names))
            {
                names = blocks.Find(blockTypeId) is { } factory ? [.. factory.Inputs.Where(p => p.SecretValues).Select(p => p.Name)] : [];
                secretInputs[blockTypeId] = names;
            }

            return names;
        }

        private static string? Text(JsonElement element, string key) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
