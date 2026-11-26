using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class AnyTermSourcesTests
{
    private static (string? Type, string Kind, IReadOnlyList<CompilationDiagnostic> Diagnostics) Read(
        WorkflowDefinition graph, IBlockShapeSource shapes, string nodeId, string port, NodePortEnvironment? environment = null)
    {
        var context = WorkflowCompiler.Run(graph, shapes, CanvasSignature.Empty, environment: environment);
        var outputs = context.Types[new NodeKey(CanvasPath.Root, nodeId)].Outputs ?? [];
        var type = outputs.FirstOrDefault(p => p.Name == port && p.State == PortState.Typed)?.Type;
        var kind = type is { } found
            ? ExpressionResolution.Lookup(found, optional: false, WorkflowTypeVocabulary.Builtin).Kind.ToString()
            : "no typed port";
        return (type?.Name, kind, context.Diagnostics);
    }

    private static WorkflowDefinition Reading(WorkflowNodeDefinition source, string port, params WorkflowConnectionDefinition[] into) => Graph(
        [StartNode(), source, Node("n1", "notify", new { Message = $"expr:${{previous.{source.Id}.{port}}} * true" })],
        [Edge("e1", "start", source.Id), .. into, Edge("e9", source.Id, "n1")]);

    private static BlockShape Notify => Shape("notify", inputs: [("Message", PortValueType.String, true)]);

    [Fact]
    public void A_config_array_through_a_passthrough_is_a_typed_read()
    {
        var graph = Reading(Node("h", "calc", new { xs = new[] { 1, 2 } }, passthrough: ["xs"]), "xs");
        var shapes = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("calc"), Notify);

        var (type, kind, diagnostics) = Read(graph, shapes, "h", "xs");

        (type, kind).Should().Be(("Collection<Int>", "Typed"));
        diagnostics.Should().Contain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal) && d.NodeId == "n1");
    }

    [Fact]
    public void A_mixed_list_literal_through_a_passthrough_is_refused_by_name()
    {
        var graph = Reading(Node("r", "relay", passthrough: ["Out"]), "Out", LiteralEdge("e2", "start", "r", "Out", new object[] { 1, "a" }));

        var (type, kind, diagnostics) = Read(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("relay"), Notify), "r", "Out");

        (type, kind).Should().Be(((string?)null, "no typed port"));
        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeUnresolved && d.NodeId == "r" && d.PortName == "Out");
        diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_host_shape_with_Any_on_a_port_is_refused_at_the_node()
    {
        var graph = Reading(Node("a", "hostcalc"), "Result");
        var host = BlockShape.Snapshot(FakeFactory.For(Shape("hostcalc", outputs: [("Result", PortValueType.Of("Any"))])));
        var shapes = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), host, Notify);

        var (type, kind, diagnostics) = Read(graph, shapes, "a", "Result");

        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeAlias && d.NodeId == "a" && d.PortName == "Result");
        (type, kind).Should().Be(((string?)null, "no typed port"));
        diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_callee_signature_holding_a_collection_of_Any_leaves_the_node_unresolved()
    {
        var graph = Reading(Node("sub", "workflow.run", new { WorkflowId = "child" }), "Result");
        var shapes = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)]),
            Notify);
        var environment = Environment((CanvasPath.Root, "sub", "child", [], [("Result", PortValueType.Of("Collection<Any>"))]));

        var (type, kind, diagnostics) = Read(graph, shapes, "sub", "Result", environment);

        (type, kind).Should().Be(((string?)null, "no typed port"));
        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeAlias && d.NodeId == "sub" && d.PortName == "Result");
        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodePortsUnresolved && d.NodeId == "sub");
        diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("Collection<Any>")]
    public void A_dynamic_port_typed_with_Any_is_refused_at_the_site(string written)
    {
        var source = Node("a", "dyn.source", new { OutputPorts = $$"""[{"name":"o","type":"{{written}}"}]""" });
        var graph = Reading(source, "o");
        var shapes = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("dyn.source", hasDynamicPorts: true, source: "plugin:dyn"), Notify);

        var (type, kind, diagnostics) = Read(graph, shapes, "a", "o");

        (type, kind).Should().Be(((string?)null, "no typed port"));
        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeAlias && d.NodeId == "a");
        diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_declared_output_typed_Any_is_refused_at_the_site()
    {
        var source = Node("a", "calc");
        source.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "o", DisplayName = "o", Type = "Any" }];
        var graph = Reading(source, "o");
        var shapes = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("calc", supportsDeclaredOutputs: true), Notify);

        var (type, kind, diagnostics) = Read(graph, shapes, "a", "o");

        (type, kind).Should().Be(((string?)null, "no typed port"));
        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeAlias && d.NodeId == "a");
        diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("Collection<Any>")]
    public void A_root_parameter_typed_Any_is_refused_at_the_parameter(string written)
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${parameter.p}" })],
            [Edge("e1", "start", "n1")]);
        var shapes = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Notify);

        var result = WorkflowCompiler.Compile(graph, shapes, Signature(inputs: [("p", PortValueType.Of(written), true)]));

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeAlias && d.PortName == "p");
        result.Diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal) || d.RuleId == CompilationRules.CompilationFault);
        result.Program.Should().BeNull();
    }

    [Fact]
    public void A_projection_typed_Any_is_refused_at_the_wire()
    {
        var graph = Graph(
            [StartNode(), Node("a", "jsonsource"), Node("b", "sink")],
            [Edge("e1", "start", "a"), ProjectionEdge("e2", "a", "b", "j", "v", "$.x", type: "Any")]);
        var shapes = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("jsonsource", outputs: [("j", PortValueType.Json)]),
            Shape("sink", inputs: [("v", PortValueType.OfDeclaration("$T"), true)], typeParameters: [new WorkflowTypeParameter("T")]));

        var result = WorkflowCompiler.Compile(graph, shapes, CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeAlias);
        result.Diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal) || d.RuleId == CompilationRules.CompilationFault);
        result.Program.Should().BeNull();
    }
}
