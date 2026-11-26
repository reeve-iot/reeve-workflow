using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class StructurePhaseTests
{
    [Fact]
    public void A_join_collision_inside_a_sub_canvas_is_refused_like_one_at_the_root()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("a", "notify", new { Message = "a" }), Node("b", "notify")],
            [Edge("be0", "bstart", "a"),
             LiteralEdge("be1", "bstart", "b", "Message", "first"),
             LiteralEdge("be2", "a", "b", "Message", "second")]);
        var loop = Node("loop", "loop.for");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), LoopForShape(),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.DuplicateInputMapping && d.CanvasPath.Contains(new CanvasPathSegment("loop", "Body")));
    }

    [Fact]
    public void A_cycle_at_the_root_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop"), Node("b", "noop")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b"), Edge("e3", "b", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.Cycle && d.CanvasPath.Count == 0);
    }

    [Fact]
    public void A_cycle_inside_a_sub_canvas_is_refused_like_a_cycle_at_the_root()
    {
        var body = Graph(
            [Node("b1", "noop"), Node("b2", "noop")],
            [Edge("be1", "b1", "b2"), Edge("be2", "b2", "b1")]);
        var loop = Node("loop", "loop.for");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), LoopForShape(), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.Cycle && d.CanvasPath.Contains(new CanvasPathSegment("loop", "Body")));
    }

    [Fact]
    public void A_sub_canvas_node_fed_only_by_configuration_with_no_wire_into_it_is_accepted()
    {
        var body = Graph([
            Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Node("cleanup", "noop", new { Message = "always runs, needs nothing from this iteration" }),
        ]);
        var loop = Node("loop", "loop.for");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), LoopForShape(), Shape("noop")), CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue(
            "the run accepts this today and the editor calls it valid; refusing it would disagree with both");
    }

    [Fact]
    public void A_cycle_inside_the_sub_canvas_of_an_unbound_container_is_still_refused()
    {
        var body = Graph(
            [Node("b1", "noop"), Node("b2", "noop")],
            [Edge("be1", "b1", "b2"), Edge("be2", "b2", "b1")]);
        var loop = Node("loop", "gone.block");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.Cycle && d.CanvasPath.Contains(new CanvasPathSegment("loop", "Body")));
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.BlockTypeUnknown && d.NodeId == "loop");
    }

    [Fact]
    public void A_duplicate_node_id_is_refused_by_name_instead_of_crashing_the_compile()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "noop"), Node("n1", "noop")],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.DuplicateNodeId && d.NodeId == "n1" && d.CanvasPath.Count == 0);
    }

    [Fact]
    public void A_duplicate_node_id_inside_a_sub_canvas_is_refused_by_name_not_reported_as_a_cycle()
    {
        var body = Graph([Node("b1", "noop"), Node("b1", "noop")]);
        var loop = Node("loop", "loop.for");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), LoopForShape(), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.DuplicateNodeId && d.NodeId == "b1" && d.CanvasPath.Contains(new CanvasPathSegment("loop", "Body")));
    }

    [Fact]
    public void A_definition_whose_nodes_deserialised_as_null_also_survives_the_run_paths_own_validator()
    {
        var definition = new WorkflowDefinition { Nodes = null! };

        var validate = () => WorkflowGraphHelpers.ValidateRootCanvas(definition);

        validate.Should().NotThrow(
            "WorkflowGraphExecutor calls this directly at depth 0, and the model normalises a null Nodes " +
            "list on set, not this method");
    }

    [Fact]
    public void An_edge_into_the_start_node_is_refused_by_name_not_by_the_extra_root_it_also_creates()
    {
        var graph = Graph([StartNode(), Node("a", "noop")], [Edge("e1", "a", "start")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.InputHasIncoming && d.CanvasPath.Count == 0);
    }

    [Fact]
    public void An_edge_naming_a_source_node_that_does_not_exist_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop")],
            [Edge("e1", "start", "a"), Edge("e2", "ghost", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost" && d.CanvasPath.Count == 0);
    }

    [Fact]
    public void An_edge_naming_a_target_node_that_does_not_exist_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "ghost")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost" && d.CanvasPath.Count == 0);
    }

    [Fact]
    public void An_edge_naming_a_missing_node_inside_a_sub_canvas_is_refused_like_one_at_the_root()
    {
        var body = Graph([Node("b1", "noop")], [Edge("be1", "ghost", "b1")]);
        var loop = Node("loop", "loop.for");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), LoopForShape(), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.EdgeEndpointUnknown
                && d.NodeId == "ghost" && d.CanvasPath.Contains(new CanvasPathSegment("loop", "Body")));
    }

    [Fact]
    public void An_edge_with_both_endpoints_missing_is_refused_once_per_endpoint()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop")],
            [Edge("e1", "start", "a"), Edge("e2", "ghost1", "ghost2")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost1");
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost2");
    }

    [Fact]
    public void Two_missing_edge_endpoints_are_both_reported()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop")],
            [Edge("start-a", "start", "a"), Edge("e1", "ghost1", "a"), Edge("e2", "ghost2", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost1");
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost2");
    }

    [Fact]
    public void A_wire_into_the_exit_node_from_a_source_node_that_does_not_exist_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop"), Node("wo", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("e1", "start", "a"), Edge("e2", "ghost", "wo", ("Value", "result"))]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.EdgeEndpointUnknown)
            .Which.Should().Match<CompilationDiagnostic>(d => d.NodeId == "ghost" && d.CanvasPath.Count == 0);
    }

    private static BlockShape LoopForShape() => Shape("loop.for",
        subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] },
        subCanvasNames: ["Body"]);
}
