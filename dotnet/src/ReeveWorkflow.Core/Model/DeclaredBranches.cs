using System.Text.Json;

namespace ReeveWorkflow.Core.Model;

internal static class DeclaredBranches
{
    public const string EntryNodeId = "__workflow_input__";
    public const string ExitNodeId = "__workflow_output__";

    public static WorkflowDefinition Empty(bool withExit) => new()
    {
        Nodes = withExit
            ? [Boundary(EntryNodeId, WorkflowGraphHelpers.WorkflowInputBlockTypeId, 50),
               Boundary(ExitNodeId, WorkflowGraphHelpers.WorkflowOutputBlockTypeId, 800)]
            : [Boundary(EntryNodeId, WorkflowGraphHelpers.WorkflowInputBlockTypeId, 50)],
        Edges = [],
    };

    public static bool WritesExit(WorkflowNodeDefinition node, string branch, bool blockDeclaresOutputs) =>
        blockDeclaresOutputs
        || (node.OutputPorts ?? []).Any(p => p?.Sources is { } sources
            && sources.TryGetValue(branch, out var key) && !string.IsNullOrEmpty(key));

    public static IEnumerable<string> Absent(WorkflowNodeDefinition node, IEnumerable<string> declared) =>
        declared.Where(name => node.SubCanvases?.ContainsKey(name) != true);

    public static WorkflowDefinition Filled(
        WorkflowDefinition definition, Func<WorkflowNodeDefinition, Declared?> declared,
        Action<WorkflowNodeDefinition, WorkflowNodeDefinition>? copied = null)
    {
        List<WorkflowNodeDefinition>? nodes = null;
        var i = 0;
        foreach (var node in definition.Nodes)
        {
            var filled = node is null ? null : FilledNode(node, declared, copied);
            if (filled is not null && nodes is null)
                nodes = [.. definition.Nodes.Take(i)];
            nodes?.Add(filled ?? node!);
            i++;
        }

        return nodes is null ? definition : definition.WithNodes(nodes);
    }

    private static WorkflowNodeDefinition? FilledNode(
        WorkflowNodeDefinition node, Func<WorkflowNodeDefinition, Declared?> declared,
        Action<WorkflowNodeDefinition, WorkflowNodeDefinition>? copied)
    {
        Dictionary<string, WorkflowDefinition>? branches = null;
        foreach (var (name, canvas) in node.SubCanvases ?? [])
        {
            var below = canvas is null ? canvas : Filled(canvas, declared, copied);
            if (!ReferenceEquals(below, canvas))
                (branches ??= new(node.SubCanvases!, node.SubCanvases!.Comparer))[name] = below!;
        }

        if (declared(node) is { } shape)
        {
            foreach (var name in Absent(node, shape.Names).ToList())
            {
                branches ??= node.SubCanvases is null ? new(StringComparer.Ordinal) : new(node.SubCanvases, node.SubCanvases.Comparer);
                branches[name] = Empty(WritesExit(node, name, shape.DeclaresOutputs(name)));
            }
        }

        if (branches is null)
            return null;

        var copy = node.WithSubCanvases(branches);
        copied?.Invoke(node, copy);
        return copy;
    }

    private static WorkflowNodeDefinition Boundary(string id, string blockTypeId, double x) => new()
    {
        Id = id, BlockTypeId = blockTypeId, Position = new WorkflowNodePosition { X = x, Y = 100 },
        Config = new Dictionary<string, JsonElement>(),
    };

    public sealed record Declared(IReadOnlyCollection<string> Names, Func<string, bool> DeclaresOutputs);
}
