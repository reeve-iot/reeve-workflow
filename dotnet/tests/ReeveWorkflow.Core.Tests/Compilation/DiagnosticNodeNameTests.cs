using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class DiagnosticNodeNameTests
{
    private static BlockShape Compare() =>
        Shape("compare", inputs: [("Value", PortValueType.Boolean, false)], displayName: "Compare");

    [Fact]
    public void A_refusal_names_the_node_by_what_its_block_is_called_and_still_points_at_its_id()
    {
        var graph = Graph(
            [StartNode(), Node("nc20d3fd71a36", "notify")],
            [LiteralEdge("e1", "start", "nc20d3fd71a36", "Value", "maybe")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(BlockShape.From(new NotifyFactory())), CanvasSignature.Empty);

        var refusal = result.Diagnostics.Should().ContainSingle().Which;
        (refusal.RuleId, refusal.NodeId, refusal.PortName)
            .Should().Be((CompilationRules.LiteralPortValueInvalid, "nc20d3fd71a36", "Value"));
        refusal.Message.Should().Be("Literal for input 'Value' of 'Notify' cannot be read as Boolean.");
    }

    [Fact]
    public void Two_nodes_called_alike_on_one_canvas_are_told_apart_by_their_ids_and_a_third_is_not()
    {
        var graph = Graph(
            [StartNode(), Node("first", "compare"), Node("second", "compare"), Node("lone", "notify")],
            [
                LiteralEdge("e1", "start", "first", "Value", "maybe"),
                LiteralEdge("e2", "start", "second", "Value", "maybe"),
                LiteralEdge("e3", "start", "lone", "Value", "maybe"),
            ]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Compare(), BlockShape.From(new NotifyFactory())), CanvasSignature.Empty);

        result.Diagnostics.ToDictionary(d => d.NodeId!, d => d.Message).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["first"] = "Literal for input 'Value' of 'Compare (first)' cannot be read as Boolean.",
            ["second"] = "Literal for input 'Value' of 'Compare (second)' cannot be read as Boolean.",
            ["lone"] = "Literal for input 'Value' of 'Notify' cannot be read as Boolean.",
        });
    }

    [Fact]
    public void Two_nodes_called_alike_on_different_canvases_are_told_apart_and_the_entry_nodes_are_not()
    {
        var body = Graph(
            [StartNode("inner-start"), Node("inner", "compare")],
            [LiteralEdge("b1", "inner-start", "inner", "Value", "maybe")]);
        var box = Node("box", "box");
        box.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph(
            [StartNode(), Node("outer", "compare"), box],
            [LiteralEdge("e1", "start", "outer", "Value", "maybe"), Edge("e2", "start", "box")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Compare(), Shape("box", subCanvasNames: ["Body"], displayName: "Box")), CanvasSignature.Empty);

        result.Diagnostics.Where(d => d.RuleId == CompilationRules.LiteralPortValueInvalid)
            .ToDictionary(d => d.NodeId!, d => d.Message).Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["outer"] = "Literal for input 'Value' of 'Compare (outer)' cannot be read as Boolean.",
                ["inner"] = "Literal for input 'Value' of 'Compare (inner)' cannot be read as Boolean.",
            });
    }
    [Fact]
    public void Nodes_called_alike_that_share_an_id_on_different_canvases_are_told_apart_by_their_canvas()
    {
        Dictionary<string, WorkflowDefinition> Body() => new()
        {
            ["Body"] = Graph(
                [StartNode("inner-start"), Node("c", "compare")],
                [LiteralEdge("b1", "inner-start", "c", "Value", "maybe")]),
        };
        var first = Node("box1", "box");
        first.SubCanvases = Body();
        var second = Node("box2", "box");
        second.SubCanvases = Body();
        var graph = Graph(
            [StartNode(), Node("c", "compare"), first, second],
            [LiteralEdge("e1", "start", "c", "Value", "maybe"), Edge("e2", "start", "box1"), Edge("e3", "start", "box2")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Compare(), Shape("box", subCanvasNames: ["Body"], displayName: "Box")), CanvasSignature.Empty);

        result.Diagnostics.Where(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).Select(d => d.Message)
            .Should().BeEquivalentTo(
                "Literal for input 'Value' of 'Compare (c)' cannot be read as Boolean.",
                "Literal for input 'Value' of 'Compare (c in box1→Body)' cannot be read as Boolean.",
                "Literal for input 'Value' of 'Compare (c in box2→Body)' cannot be read as Boolean.");
    }

    [Fact]
    public void A_wire_names_both_of_its_ends_and_the_entry_node_by_what_the_editor_calls_it()
    {
        var graph = Graph([StartNode(), Node("c", "compare")], [Edge("e1", "start", "c", ("Flag", "Value"))]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Compare()), Signature(inputs: [("Flag", PortValueType.String, true)]));

        var refusal = result.Diagnostics.Should().ContainSingle().Which;
        (refusal.RuleId, refusal.NodeId).Should().Be((CompilationRules.PortTypeMismatch, "c"));
        refusal.Message.Should().StartWith("Output 'Flag' of 'Workflow Input' carries String, which input 'Value' of 'Compare' cannot take");
    }

    [Fact]
    public void A_block_that_declares_no_display_name_is_named_by_its_type_id()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "bare")],
            [LiteralEdge("e1", "start", "n1", "Value", "maybe")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape("bare", inputs: [("Value", PortValueType.Boolean, false)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Message.Should().Be("Literal for input 'Value' of 'bare' cannot be read as Boolean.");
    }

    private sealed class NotifyFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => "notify";
        public string DisplayName => "Notify";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs { get; } = [new("Value", "Value", PortValueType.Boolean, false)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
