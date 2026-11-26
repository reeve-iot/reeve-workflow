using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class BranchOutputTypeTests
{
    private static readonly BlockShape Source = Shape("s", outputs: [("i", PortValueType.Int), ("t", PortValueType.String)]);
    private static readonly BlockShape NeedsInt = Shape("needsInt", inputs: [("n", PortValueType.Int, false)]);
    private static readonly BlockShape NeedsString = Shape("needsString", inputs: [("n", PortValueType.String, false)]);

    private static readonly BlockShape TwoBranches = new(
        "two", [],
        [new PortShape("a", PortValueType.OfDeclaration("$A"), false), new PortShape("b", PortValueType.OfDeclaration("$B"), false)],
        [], default,
        new Dictionary<string, IReadOnlyList<PortShape>> { ["L"] = [], ["R"] = [] },
        new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["L"] = [new PortShape("r", PortValueType.OfDeclaration("$A"), false)], ["R"] = [new PortShape("r", PortValueType.OfDeclaration("$B"), false)],
        },
        null, null, ["L", "R"], [new WorkflowTypeParameter("A"), new WorkflowTypeParameter("B")]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_outputs_typed_from_two_ports_of_one_branch_each_get_their_own_ports_type(bool swapped)
    {
        var box = Node("b", "box2");
        box.SubCanvases = new()
        {
            ["Body"] = Graph(
                [StartNode("in"), Node("s", "s"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
                [Edge("b1", "in", "s"), Edge("b2", "s", "out", ("i", "Result"), ("t", "Other"))]),
        };
        var (intReader, stringReader) = swapped ? ("needsString", "needsInt") : ("needsInt", "needsString");
        var root = Graph(
            [StartNode(), box, Node("ni", intReader), Node("ns", stringReader)],
            [Edge("e1", "start", "b"), Edge("e2", "b", "ni", ("o0", "n")), Edge("e3", "b", "ns", ("o1", "n"))]);

        var result = WorkflowCompiler.Compile(
            root, Catalogue(GenericShapes.BoxK(2), Source, NeedsInt, NeedsString), CanvasSignature.Empty);

        if (swapped)
            result.Diagnostics.Select(d => (d.RuleId, d.NodeId)).Should().BeEquivalentTo(
                [(CompilationRules.PortTypeMismatch, "ni"), (CompilationRules.PortTypeMismatch, "ns")]);
        else
            result.Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_outputs_typed_from_like_named_ports_of_two_branches_each_get_their_own_branchs_type(bool swapped)
    {
        var node = Node("n", "two");
        static WorkflowDefinition Branch(string port) => Graph(
            [StartNode("in"), Node("s", "s"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("b1", "in", "s"), Edge("b2", "s", "out", (port, "r"))]);
        node.SubCanvases = new() { ["L"] = Branch("i"), ["R"] = Branch("t") };
        var (intReader, stringReader) = swapped ? ("needsString", "needsInt") : ("needsInt", "needsString");
        var root = Graph(
            [StartNode(), node, Node("ni", intReader), Node("ns", stringReader)],
            [Edge("e1", "start", "n"), Edge("e2", "n", "ni", ("a", "n")), Edge("e3", "n", "ns", ("b", "n"))]);

        var result = WorkflowCompiler.Compile(root, Catalogue(TwoBranches, Source, NeedsInt, NeedsString), CanvasSignature.Empty);

        if (swapped)
            result.Diagnostics.Select(d => (d.RuleId, d.NodeId)).Should().BeEquivalentTo(
                [(CompilationRules.PortTypeMismatch, "ni"), (CompilationRules.PortTypeMismatch, "ns")]);
        else
            result.Diagnostics.Should().BeEmpty();
    }
}
