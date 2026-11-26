using System.Text.Json.Nodes;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public static class LoopWaitGraphs
{
    public const string WaitNodeId = "wait";

    public static readonly string[] Loops = ["ForLoop", "WhileLoop", "IterateOver"];

    public static string Definition(string loop, int depth, int passes, JsonObject wait)
    {
        var waitNode = (JsonObject)wait.DeepClone();
        waitNode["id"] = WaitNodeId;
        CompleteNode(waitNode);
        var body = Canvas(waitNode);
        var top = WaitNodeId;

        for (var level = 1; level <= depth; level++)
        {
            top = $"w{level}";
            var wrapper = level == 1
                ? new JsonObject
                {
                    ["id"] = top, ["blockTypeId"] = "IfElse", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject { ["Condition"] = true },
                    ["subCanvases"] = new JsonObject { ["Then"] = body }
                }
                : new JsonObject
                {
                    ["id"] = top, ["blockTypeId"] = "TryCatch", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject(), ["subCanvases"] = new JsonObject { ["Try"] = body }
                };
            body = Canvas(wrapper);
        }

        var loopNode = new JsonObject { ["id"] = "loop", ["blockTypeId"] = loop, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() };
        switch (loop)
        {
            case "ForLoop":
                loopNode["configuration"] = new JsonObject { ["From"] = 0, ["To"] = passes };
                break;
            case "IterateOver":
                loopNode["configuration"] = new JsonObject
                {
                    ["Collection"] = new JsonArray([.. Enumerable.Range(1, passes).Select(item => (JsonNode)item)])
                };
                break;
            case "WhileLoop":
                loopNode["configuration"] = new JsonObject { ["MaxIterations"] = passes };
                loopNode["errorPolicy"] = new JsonObject { ["policy"] = "ContinueWithDefault" };
                ((JsonArray)body["nodes"]!).Add(new JsonObject { ["id"] = "out", ["blockTypeId"] = "WorkflowOutput", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() });
                ((JsonArray)body["edges"]!).Add(new JsonObject
                {
                    ["id"] = "continue", ["sourceNodeId"] = top, ["targetNodeId"] = "out",
                    ["mappings"] = new JsonObject
                    {
                        ["Continue"] = new JsonObject { ["type"] = "literal", ["literalValue"] = true }
                    }
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(loop), loop, "not a loop the standard set ships");
        }

        loopNode["subCanvases"] = new JsonObject { ["Body"] = body };
        CompleteCanvas(body);
        CompleteNode(loopNode);
        return new JsonObject
        {
            ["nodes"] = new JsonArray(new JsonObject { ["id"] = "start", ["blockTypeId"] = "WorkflowInput", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() }, loopNode),
            ["edges"] = new JsonArray(new JsonObject { ["id"] = "e1", ["sourceNodeId"] = "start", ["targetNodeId"] = "loop", ["mappings"] = new JsonObject() })
        }.ToJsonString();
    }

    private static void CompleteCanvas(JsonObject canvas)
    {
        if (canvas["nodes"] is not JsonArray nodes)
            return;
        foreach (var node in nodes.OfType<JsonObject>())
        {
            CompleteNode(node);
            if (node["subCanvases"] is JsonObject branches)
                foreach (var branch in branches.Select(pair => pair.Value).OfType<JsonObject>())
                    CompleteCanvas(branch);
        }
    }

    private static void CompleteNode(JsonObject node)
    {
        node.TryAdd("position", new JsonObject { ["x"] = 0, ["y"] = 0 });
        node.TryAdd("configuration", new JsonObject());
    }

    private static JsonObject Canvas(JsonObject node) => new()
    {
        ["nodes"] = new JsonArray(new JsonObject { ["id"] = "in", ["blockTypeId"] = "WorkflowInput", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() }, node),
        ["edges"] = new JsonArray(new JsonObject
        {
            ["id"] = "b-" + node["id"]!.GetValue<string>(), ["sourceNodeId"] = "in", ["targetNodeId"] = node["id"]!.GetValue<string>(), ["mappings"] = new JsonObject()
        })
    };
}
