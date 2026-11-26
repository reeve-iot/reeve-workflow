using System.Text.Json;

namespace ReeveWorkflow.Abstractions;

/// <summary>Reads the block type ids a workflow definition names, including the ones inside sub-canvases.</summary>
/// <remarks>Only the walk lives here. Which of those ids mean anything is the registry's business, which is what lets a gate be written without a list of block types built into it.</remarks>
public static class WorkflowDefinitionBlockTypes
{
    /// <summary>Every block type id the definition names, under every casing of its keys, deduplicated
    /// case-insensitively: <c>IWorkflowBlocks.Find</c> matches ids case-insensitively, so two casings are one block.</summary>
    /// <remarks>Text that is not JSON yields nothing, and a malformed node is skipped while its siblings are still read — a gate asking this question must not be able to be silenced by one bad node. Two texts throw rather than answer: an id whose escape does not decode (<see cref="InvalidOperationException"/>), and text holding a raw unpaired surrogate (<see cref="ArgumentException"/>).</remarks>
    public static IReadOnlySet<string> Collect(string? definitionJson)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return found;
        }

        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            Walk(document.RootElement, found);
        }
        catch (JsonException)
        {
        }

        return found;
    }

    private static IEnumerable<JsonElement> EveryCasing(JsonElement element, string name) =>
        element.EnumerateObject()
            .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value);

    private static void Walk(JsonElement element, HashSet<string> found)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var nodes in EveryCasing(element, "nodes").Where(n => n.ValueKind == JsonValueKind.Array))
        {
            foreach (var node in nodes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.Object))
            {
                foreach (var blockTypeId in EveryCasing(node, "blockTypeId").Where(b => b.ValueKind == JsonValueKind.String))
                {
                    found.Add(blockTypeId.GetString()!);
                }

                foreach (var subCanvases in EveryCasing(node, "subCanvases").Where(s => s.ValueKind == JsonValueKind.Object))
                {
                    foreach (var canvas in subCanvases.EnumerateObject())
                    {
                        Walk(canvas.Value, found);
                    }
                }
            }
        }
    }
}
