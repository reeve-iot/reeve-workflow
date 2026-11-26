using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class RootOutputMappingCompilationTests
{
    private static IBlockShapeSource Blocks() => Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
        Shape("produce", outputs: [("Value", PortValueType.String)]),
        Shape("loop.for", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]));

    private static WorkflowOutputMappingDefinition Mapping(string source, string output) =>
        new() { SourceNodeId = source, SourceOutput = "Value", WorkflowOutput = output };

    [Fact]
    public void A_root_output_mapping_on_the_model_is_refused_as_unread()
    {
        var graph = Graph(
            [StartNode(), Node("p", "produce"), Node("wo", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("e1", "start", "p"), Edge("e2", "p", "wo", ("Value", "final"))]);
        graph.OutputMappings = [Mapping("p", "final")];

        var result = WorkflowCompiler.Compile(graph, Blocks(), Signature(outputs: [("other", PortValueType.String, false)]));

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.OutputMappingsUnread && d.CanvasPath.Count == 0);
    }

    [Fact]
    public void Each_undeclared_exit_wire_is_refused_by_the_name_it_writes()
    {
        var graph = Graph(
            [StartNode(), Node("p", "produce"), Node("q", "produce"), Node("wo", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("e1", "start", "p"), Edge("e0", "start", "q"), Edge("e2", "p", "wo", ("Value", "final"), ("Value", "extra")), Edge("e3", "q", "wo", ("Value", "echo"))]);

        var result = WorkflowCompiler.Compile(graph, Blocks(), Signature(outputs: [("final", PortValueType.String, true)]));

        result.Diagnostics.Should().OnlyContain(d => d.RuleId == CompilationRules.WirePortUnknown && d.NodeId == "wo")
            .And.Subject.Select(d => d.PortName).Should().BeEquivalentTo(["extra", "echo"]);
    }

    [Fact]
    public void A_sub_canvas_mapping_on_the_model_is_refused_as_unread_with_its_canvas_path()
    {
        var body = Graph([Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("b1", "produce")], [Edge("b0", "bstart", "b1")]);
        body.OutputMappings = [Mapping("b1", "result")];
        var loop = Node("loop", "loop.for");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Blocks(), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.OutputMappingsUnread && d.CanvasPath.Count == 1);
    }
}
