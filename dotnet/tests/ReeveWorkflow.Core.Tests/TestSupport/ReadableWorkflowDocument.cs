using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReeveWorkflow.Core.Tests.TestSupport;

internal static class ReadableWorkflowDocument
{
    public static string CompleteMissingNodeFields(string definition)
    {
        var root = JsonNode.Parse(definition);
        if (root is JsonObject document)
            CompleteCanvas(document);
        return root?.ToJsonString() ?? definition;
    }

    private static void CompleteCanvas(JsonObject canvas)
    {
        if (!canvas.ContainsKey("edges"))
            canvas["edges"] = new JsonArray();
        if (canvas["edges"] is JsonArray edges)
            foreach (var edge in edges.OfType<JsonObject>())
                edge.TryAdd("mappings", new JsonObject());
        if (canvas["nodes"] is JsonArray nodes)
        {
            foreach (var item in nodes)
            {
                if (item is not JsonObject node)
                    continue;
                if (!node.Any(p => string.Equals(p.Key, "position", StringComparison.OrdinalIgnoreCase)))
                    node["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 };
                if (!node.Any(p => p.Key.Equals("configuration", StringComparison.OrdinalIgnoreCase)
                        || p.Key.Equals("config", StringComparison.OrdinalIgnoreCase)))
                    node["configuration"] = new JsonObject();
                if (node["subCanvases"] is JsonObject subCanvases)
                    foreach (var subCanvas in subCanvases.Select(pair => pair.Value).OfType<JsonObject>())
                        CompleteCanvas(subCanvas);
            }
        }
    }
}
