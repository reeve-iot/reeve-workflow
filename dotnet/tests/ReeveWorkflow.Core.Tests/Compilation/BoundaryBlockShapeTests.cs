using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class BoundaryBlockShapeTests
{
    private static CatalogueBlockShapeSource HostCatalogue(params BlockShape[] shapes) =>
        new(new RegisteredWorkflowBlocks([.. shapes.Select(FakeFactory.For)]));

    [Fact]
    public void Boundary_nodes_compile_against_a_catalogue_that_registers_neither_id()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("e1", "start", "a"), Edge("e2", "a", "out")]);

        var result = WorkflowCompiler.Compile(graph, HostCatalogue(Shape("noop")), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
        var used = WorkflowCompiler.ShapesUsedBy(graph, HostCatalogue(Shape("noop")));
        used.Keys.Should().BeEquivalentTo(
            [WorkflowGraphHelpers.WorkflowInputBlockTypeId, WorkflowGraphHelpers.WorkflowOutputBlockTypeId, "noop"]);
        foreach (var boundary in used.Values.Where(s => s.BlockTypeId != "noop"))
        {
            boundary.Should().Match<BlockShape>(s => s.Inputs.Count == 0 && s.Outputs.Count == 0
                && s.ConfigurationFields.Count == 0 && s.Flags == default(BlockFlags) && s.SubCanvasNames.Count == 0
                && s.RunsWorkflowFrom == null && s.TypeParameters.Count == 0 && s.Source == null);
        }
    }

    [Fact]
    public void A_boundary_nodes_ports_are_the_frames_signature_and_nothing_else()
    {
        var graph = Graph(
            [StartNode(), Node("a", "deliver"), Node("b", "deliver"), Node("c", "read"),
             Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("e1", "start", "a", ("Name", "Depot")), Edge("e2", "start", "b", ("Missing", "Depot")),
             Edge("e3", "start", "c"), Edge("e4", "c", "out", ("Temperature", "Result")),
             Edge("e5", "a", "out"), Edge("e6", "b", "out")]);
        var signature = Signature(
            inputs: [("Name", PortValueType.String, true)], outputs: [("Result", PortValueType.String, true)]);

        var result = WorkflowCompiler.Compile(graph, HostCatalogue(
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)]),
            Shape("read", outputs: [("Temperature", PortValueType.Number)])), signature,
            new CompilationOptions { TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Depot", typeof(string))]) });

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.PortTypeMismatch && d.NodeId == "a" && d.PortName == "Depot");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.WirePortUnknown && d.NodeId == "start" && d.PortName == "Missing");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.PortTypeMismatch && d.NodeId == "out" && d.PortName == "Result");
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
    }

    [Fact]
    public void A_sub_canvas_boundary_node_binds_and_takes_its_ports_from_the_branch()
    {
        var body = Graph([StartNode("in"), Node("b", "deliver")], [Edge("e1", "in", "b", ("Item", "Depot"))]);
        var container = Node("container", "workflow.container");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "container")]);

        var result = WorkflowCompiler.Compile(graph, HostCatalogue(
            Shape("workflow.container",
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
                {
                    ["Body"] = [("Item", PortValueType.String, true)],
                },
                subCanvasNames: ["Body"]),
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)])), CanvasSignature.Empty,
            new CompilationOptions { TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Depot", typeof(string))]) });

        var only = result.Diagnostics.Should().ContainSingle().Which;
        only.Should().Match<CompilationDiagnostic>(d => d.RuleId == CompilationRules.PortTypeMismatch && d.NodeId == "b");
        only.CanvasPath.Should().Equal(new CanvasPathSegment("container", "Body"));
    }

    [Fact]
    public void A_shape_source_holding_a_boundary_id_is_not_asked_for_it()
    {
        var graph = Graph([StartNode()]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId,
            inputs: [("Seed", PortValueType.String, true)], configFields: [("Mode", ConfigFieldType.String, true)])),
            CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Another_spelling_of_a_boundary_id_is_not_the_boundary_and_needs_a_registration()
    {
        var graph = Graph([StartNode(), Node("n1", "workflowinput")], [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, HostCatalogue(), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.BlockTypeUnknown && d.NodeId == "n1");
    }
}
