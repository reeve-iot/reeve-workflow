using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class UnsortableCanvasTests
{
    private static IBlockShapeSource Shapes() => Catalogue(
        GenericShapes.Pass, GenericShapes.Box, Shape("s", outputs: [("v", PortValueType.Int)]),
        Shape("needs", inputs: [("n", PortValueType.String, false)]),
        Shape("wants", inputs: [("n", PortValueType.String, true)]));

    private static IEnumerable<(string Rule, string? Node)> Rules(CompilationResult result) =>
        result.Diagnostics.Select(d => (d.RuleId, d.NodeId));

    private static WorkflowDefinition Cyclic() => Graph(
        [
            StartNode(), Node("s", "s"), Node("a", "pass", passthrough: ["v"]), Node("b", "pass", passthrough: ["v"]),
            Node("needs", "needs"), Node("wants", "wants"),
        ],
        [
            Edge("e1", "start", "s"), Edge("e2", "s", "a", ("v", "v")), Edge("e3", "a", "b", ("v", "v")),
            Edge("e4", "b", "a", ("v", "w")), Edge("e5", "b", "needs", ("v", "n")), Edge("e6", "start", "wants"),
        ]);

    [Fact]
    public void The_same_wires_without_the_cycle_are_refused_for_their_types()
    {
        var acyclic = Cyclic();
        acyclic.Edges = [.. acyclic.Edges!.Where(e => e.Id != "e4")];

        var result = WorkflowCompiler.Compile(acyclic, Shapes(), CanvasSignature.Empty);

        Rules(result).Should().BeEquivalentTo(new[]
        {
            (CompilationRules.RequiredInputUnfed, (string?)"wants"), (CompilationRules.PortTypeMismatch, "needs"),
        });
    }

    [Fact]
    public void A_canvas_with_a_cycle_is_refused_as_a_cycle_and_its_port_types_are_not_checked()
    {
        var result = WorkflowCompiler.Compile(Cyclic(), Shapes(), CanvasSignature.Empty);

        Rules(result).Select(r => r.Rule).Should().Equal(CompilationRules.Cycle);
    }

    [Fact]
    public void A_cycle_inside_a_branch_leaves_only_that_branch_unchecked_and_its_output_untyped()
    {
        var box = Node("box", "box");
        var body = Cyclic();
        body.Nodes = [StartNode("in"), .. body.Nodes.Skip(1)];
        body.Edges = [.. body.Edges!.Select(e => e.SourceNodeId == "start" ? Edge(e.Id, "in", e.TargetNodeId) : e)];
        box.SubCanvases = new() { ["Body"] = body };
        var root = Graph(
            [StartNode(), box, Node("s", "s"), Node("needs", "needs"), Node("typed", "needs")],
            [
                Edge("e1", "start", "box"), Edge("e2", "start", "s"), Edge("e3", "s", "needs", ("v", "n")),
                Edge("e4", "box", "typed", ("Results", "n")),
            ]);

        var result = WorkflowCompiler.Compile(root, Shapes(), CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.CanvasPath.Count)).Should().BeEquivalentTo(new[]
        {
            (CompilationRules.PortTypeMismatch, (string?)"needs", 0), (CompilationRules.Cycle, null, 1),
        });
    }

    [Fact]
    public void A_branch_below_a_cycle_is_checked_with_what_a_trace_would_type_left_untyped()
    {
        var box = Node("box", "box", passthrough: ["v"]);
        box.SubCanvases = new()
        {
            ["Body"] = Graph(
                [StartNode("in"), Node("s", "s"), Node("needs", "needs"), Node("item", "needs"), Node("fwd", "needs")],
                [
                    Edge("b1", "in", "s"), Edge("b2", "s", "needs", ("v", "n")), Edge("b3", "in", "item", ("Item", "n")),
                    Edge("b4", "in", "fwd", ("v", "n")),
                ]),
        };
        var root = Graph(
            [StartNode(), Node("a", "pass", passthrough: ["v"]), Node("b", "pass", passthrough: ["v"]), box],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("v", "v")), Edge("e3", "b", "a", ("v", "w")), Edge("e4", "b", "box", ("v", "v"))]);

        var result = WorkflowCompiler.Compile(root, Shapes(), CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.CanvasPath.Count)).Should().BeEquivalentTo(new[]
        {
            (CompilationRules.Cycle, (string?)null, 0), (CompilationRules.PortTypeMismatch, "needs", 1),
        });
    }

    [Fact]
    public void A_canvas_with_an_id_used_twice_is_refused_for_that_and_its_port_types_are_not_checked()
    {
        var twice = Cyclic();
        twice.Edges = [.. twice.Edges!.Where(e => e.Id != "e4")];
        twice.Nodes = [.. twice.Nodes, Node("s", "s")];

        var result = WorkflowCompiler.Compile(twice, Shapes(), CanvasSignature.Empty);

        Rules(result).Should().Equal((CompilationRules.DuplicateNodeId, (string?)"s"));
    }
}
