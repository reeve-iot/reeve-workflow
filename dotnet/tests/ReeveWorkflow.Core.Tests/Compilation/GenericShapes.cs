using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

internal static class GenericShapes
{
    private static readonly Dictionary<string, IReadOnlyList<PortShape>> NoBranches = [];

    private static PortValueType Declared(string text) => PortValueType.OfDeclaration(text);

    internal static readonly BlockShape Filter = new(
        "Filter",
        [
            new PortShape("Collection", Declared("Collection<$T>"), true),
            new PortShape("Predicate", Declared("Expression<Boolean>"), true) { ScopeVariables = [new ExpressionScopeVariable("item", "$T")] },
        ],
        [new PortShape("Filtered", Declared("Collection<$T>"), true)],
        [], default, NoBranches, NoBranches, null, TypeParameters: [new WorkflowTypeParameter("T")]);

    internal static WorkflowDefinition FilterChain(int length)
    {
        var nodes = new List<WorkflowNodeDefinition> { StartNode() };
        var edges = new List<WorkflowConnectionDefinition>();
        var (previous, output) = ("start", "c");
        for (var i = 1; i <= length; i++)
        {
            nodes.Add(Node("f" + i, "Filter", new Dictionary<string, object?> { ["Predicate"] = "expr:true" }, passthrough: ["p"]));
            edges.Add(Edge("e" + i, previous, "f" + i, (output, "Collection"), (output, "p")));
            (previous, output) = ("f" + i, "p");
        }

        return Graph(nodes, edges);
    }

    internal static readonly BlockShape Pass = Shape("pass");

    internal static WorkflowDefinition PlainChain(int length)
    {
        var nodes = new List<WorkflowNodeDefinition> { StartNode() };
        var edges = new List<WorkflowConnectionDefinition>();
        var (previous, output) = ("start", "c");
        for (var i = 1; i <= length; i++)
        {
            nodes.Add(Node("p" + i, "pass", passthrough: ["p"]));
            edges.Add(Edge("e" + i, previous, "p" + i, (output, "p")));
            (previous, output) = ("p" + i, "p");
        }

        return Graph(nodes, edges);
    }

    internal static readonly BlockShape Box = new(
        "box", [new PortShape("Collection", Declared("Collection<$T>"), false)],
        [new PortShape("Results", Declared("Collection<$R>"), false)],
        [], default,
        new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [new PortShape("Item", Declared("$T"), false)] },
        new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [new PortShape("Result", Declared("$R"), false)] },
        null, null, ["Body"], [new WorkflowTypeParameter("T"), new WorkflowTypeParameter("R")]);

    internal static readonly string[] BranchPorts = ["Result", "Other", "Third", "Fourth"];

    internal static BlockShape BoxK(int k) => new(
        "box" + k, [new PortShape("Collection", Declared("Collection<$T>"), false)],
        [.. Enumerable.Range(0, k).Select(i => new PortShape("o" + i, Declared("$R" + i), false))],
        [], default,
        new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] },
        new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["Body"] = [.. Enumerable.Range(0, k).Select(i => new PortShape(BranchPorts[i], Declared("$R" + i), false))],
        },
        null, null, ["Body"],
        [new WorkflowTypeParameter("T"), .. Enumerable.Range(0, k).Select(i => new WorkflowTypeParameter("R" + i))]);
}
