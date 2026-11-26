using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class BindPhaseTests
{
    [Fact]
    public void A_node_naming_a_block_the_catalogue_does_not_carry_is_refused_by_name()
    {
        var graph = Graph([StartNode(), Node("n1", "gone.block")], [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId)), CanvasSignature.Empty);

        result.Succeeded.Should().BeFalse();
        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.BlockTypeUnknown && d.NodeId == "n1");
    }

    [Fact]
    public void A_graph_whose_every_node_resolves_succeeds_with_a_program_and_no_diagnostics()
    {
        var graph = Graph([StartNode()]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId)), CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue();
        result.Program.Should().NotBeNull();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_block_type_id_binds_case_insensitively_against_the_catalogue()
    {
        var graph = Graph([StartNode(), Node("n1", "Mixed.CASE")], [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("mixed.case")), CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_refusal_inside_a_sub_canvas_carries_the_chain_of_node_id_and_name_above_it()
    {
        var body = Graph([Node("n1", "gone.block")]);
        var container = Node("container", "workflow.container");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "container")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), WorkflowContainerShape()), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.CanvasPath.Should().Equal(new CanvasPathSegment("container", "Body"));
    }

    [Fact]
    public void An_unknown_container_still_reports_every_refusal_inside_its_sub_canvases()
    {
        var body = Graph([Node("a", "gone.a"), Node("b", "gone.b")]);
        var container = Node("container", "gone.container");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "container")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId)), CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(3);
        result.Diagnostics.Should().OnlyContain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        result.Diagnostics.Select(d => d.NodeId).Should().BeEquivalentTo(["container", "a", "b"]);
    }

    [Fact]
    public void Two_canvases_may_each_use_the_same_node_id_and_only_the_unknown_one_is_refused()
    {
        var body = Graph([Node("n1", "gone.block")]);
        var container = Node("container", "workflow.container");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode("n1"), container], [Edge("e1", "n1", "container")]);

        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), WorkflowContainerShape());

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.BlockTypeUnknown && d.NodeId == "n1");
        result.Diagnostics.Single().CanvasPath.Should().Equal(new CanvasPathSegment("container", "Body"));
    }

    [Fact]
    public void A_definition_whose_nodes_deserialised_as_null_compiles_instead_of_throwing()
    {
        var graph = new WorkflowDefinition { Nodes = null!, Edges = [] };

        var result = WorkflowCompiler.Compile(graph, Catalogue(), CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void BoundShapes_refuses_a_mutation_that_would_otherwise_poison_every_empty_result()
    {
        var graph = Graph([StartNode(), Node("n1", "gone.block")], [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(), CanvasSignature.Empty);

        var mutable = (IDictionary<string, BlockShape>)result.BoundShapes;
        var act = () => mutable.Add("x", Shape("x"));

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Compile_refuses_a_null_signature_rather_than_silently_defaulting_to_empty()
    {
        var graph = Graph([StartNode()]);

        var act = () => WorkflowCompiler.Compile(graph, Catalogue(), null!);

        act.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("signature");
    }

    private static BlockShape WorkflowContainerShape() => Shape("workflow.container",
        subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] },
        subCanvasNames: ["Body"]);
}
