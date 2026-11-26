using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class ErrorPolicyChecksTests
{
    private static readonly IBlockShapeSource Blocks = Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("f", outputs: [("o", PortValueType.String)], source: "test://f"),
        Shape("text", inputs: [("e", PortValueType.String, false)]),
        Shape("own", outputs: [("_error", PortValueType.String)]),
        Shape("loop", subCanvasNames: ["Body"], subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }));

    private static WorkflowNodeDefinition With(WorkflowNodeDefinition node, NodeErrorPolicy? policy, bool route = false)
    {
        node.ErrorPolicy = policy is { } p ? new NodeErrorConfig(p, RouteToErrorPort: route) : null;
        return node;
    }

    private static CompilationResult Compile(params WorkflowNodeDefinition[] nodes) => Compile(nodes, [Edge("e0", "start", "a")]);

    private static CompilationResult Compile(WorkflowNodeDefinition[] nodes, WorkflowConnectionDefinition[] edges) =>
        WorkflowCompiler.Compile(Graph([StartNode(), .. nodes], edges), Blocks, CanvasSignature.Empty);

    [Theory]
    [InlineData("""{"policy":"Skip"}""")]
    [InlineData("\"Skip\"")]
    [InlineData("{}")]
    public void A_configuration_that_holds_an_error_policy_is_refused_whatever_it_holds(string value)
    {
        var node = Node("a", "f", System.Text.Json.JsonSerializer.Deserialize<object>("{\"errorPolicy\":" + value + "}"));

        var diagnostic = Compile(node).Diagnostics.Should().ContainSingle().Which;

        diagnostic.RuleId.Should().Be(CompilationRules.ErrorPolicyInConfiguration);
        diagnostic.NodeId.Should().Be("a");
        diagnostic.PortName.Should().Be("errorPolicy");
        diagnostic.CanvasPath.Should().BeEmpty();
        diagnostic.BlockSource.Should().Be("test://f");
        diagnostic.Message.Should().Contain("configuration").And.Contain("never reads");
    }

    [Fact]
    public void A_boundary_node_s_configuration_policy_is_refused_too_since_no_run_reads_it_either()
    {
        var graph = Graph([Node("start", WorkflowGraphHelpers.WorkflowInputBlockTypeId, new { errorPolicy = new { policy = "Skip" } })]);

        var diagnostics = WorkflowCompiler.Compile(graph, Blocks, CanvasSignature.Empty).Diagnostics;

        diagnostics.Select(d => d.RuleId).Should().Equal(CompilationRules.ErrorPolicyInConfiguration);
        diagnostics[0].Message.Should().Contain("remove the configuration key").And.NotContain("move it onto the node");
    }

    [Fact]
    public void A_key_of_another_case_is_a_configuration_value_like_any_other()
    {
        Compile(Node("a", "f", new { ErrorPolicy = new { policy = "Skip" } })).Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NodeErrorPolicy.FailWorkflow)]
    [InlineData(NodeErrorPolicy.ContinueWithDefault)]
    [InlineData(NodeErrorPolicy.EmitError)]
    [InlineData(NodeErrorPolicy.Skip)]
    public void A_route_to_the_error_port_is_refused_under_every_policy_and_false_is_not(NodeErrorPolicy policy)
    {
        var diagnostic = Compile(With(Node("a", "f"), policy, route: true)).Diagnostics.Should().ContainSingle().Which;
        diagnostic.RuleId.Should().Be(CompilationRules.ErrorPolicyRouteUnread);
        diagnostic.NodeId.Should().Be("a");

        Compile(With(Node("a", "f"), policy, route: false)).Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Both_forms_are_refused_below_the_root_with_the_branch_named()
    {
        Nested(Node("a", "f", new { errorPolicy = new { policy = "Skip" } })).Diagnostics
            .Select(d => (d.RuleId, d.NodeId, d.CanvasPath.Single().BranchName))
            .Should().Equal((CompilationRules.ErrorPolicyInConfiguration, "a", "Body"));

        Nested(With(Node("a", "f"), NodeErrorPolicy.Skip, route: true)).Diagnostics
            .Select(d => (d.RuleId, d.NodeId, d.CanvasPath.Single().BranchName))
            .Should().Equal((CompilationRules.ErrorPolicyRouteUnread, "a", "Body"));
    }

    [Theory]
    [InlineData(WorkflowGraphHelpers.WorkflowInputBlockTypeId, NodeErrorPolicy.FailWorkflow)]
    [InlineData(WorkflowGraphHelpers.WorkflowInputBlockTypeId, NodeErrorPolicy.EmitError)]
    [InlineData(WorkflowGraphHelpers.WorkflowOutputBlockTypeId, NodeErrorPolicy.FailWorkflow)]
    [InlineData(WorkflowGraphHelpers.WorkflowOutputBlockTypeId, NodeErrorPolicy.Skip)]
    public void An_error_policy_on_an_entry_or_exit_node_is_refused_whatever_the_policy_and_none_is_not(
        string blockTypeId, NodeErrorPolicy policy)
    {
        var boundary = Node("b", blockTypeId);
        var edges = blockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId ? Array.Empty<WorkflowConnectionDefinition>() : [Edge("e0", "start", "b")];
        CompilationResult Run(NodeErrorPolicy? withPolicy) => WorkflowCompiler.Compile(
            Graph([StartNode(), With(boundary, withPolicy)], edges), Blocks, CanvasSignature.Empty);

        var diagnostic = Run(policy).Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ErrorPolicyOnBoundaryNode).Which;
        diagnostic.NodeId.Should().Be("b");
        diagnostic.PortName.Should().Be("errorPolicy");
        diagnostic.CanvasPath.Should().BeEmpty();
        diagnostic.Message.Should().Contain("has no error policy");

        Run(null).Diagnostics.Select(d => d.RuleId).Should().NotContain(CompilationRules.ErrorPolicyOnBoundaryNode);
    }

    [Fact]
    public void An_error_policy_on_a_branch_s_entry_node_is_refused_with_the_branch_named()
    {
        var inner = Graph([With(StartNode(), NodeErrorPolicy.Skip), Node("a", "f")], [Edge("e0", "start", "a")]);
        var loop = Node("l", "loop");
        loop.SubCanvases = new() { ["Body"] = inner };

        Compile([loop], [Edge("e0", "start", "l")]).Diagnostics
            .Select(d => (d.RuleId, d.NodeId, d.CanvasPath.Single().BranchName))
            .Should().Equal((CompilationRules.ErrorPolicyOnBoundaryNode, "start", "Body"));
    }

    private static CompilationResult Nested(WorkflowNodeDefinition inside)
    {
        var inner = Graph([StartNode(), inside], [Edge("e0", "start", "a")]);
        var loop = Node("l", "loop");
        loop.SubCanvases = new() { ["Body"] = inner };
        return Compile([loop], [Edge("e0", "start", "l")]);
    }

    [Fact]
    public void A_configuration_policy_is_refused_on_a_canvas_that_holds_a_cycle()
    {
        var result = Compile(
            [Node("a", "f", new { errorPolicy = new { policy = "Skip" } }), Node("b", "f")],
            [Edge("e0", "start", "a"), Edge("e1", "a", "b"), Edge("e2", "b", "a")]);

        result.Diagnostics.Select(d => d.RuleId).Should().Contain(CompilationRules.Cycle)
            .And.Contain(CompilationRules.ErrorPolicyInConfiguration);
    }

    [Theory]
    [InlineData("$.code")]
    [InlineData("$.message")]
    public void Under_EmitError_a_projection_from_the_error_output_compiles(string path)
    {
        var result = Compile(
            [With(Node("a", "f"), NodeErrorPolicy.EmitError), Node("b", "text")],
            [Edge("e0", "start", "a"), ProjectionEdge("e1", "a", "b", "_error", "e", path)]);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void The_error_output_is_Json_and_a_direct_wire_from_it_is_refused()
    {
        var result = Compile(
            [With(Node("a", "f"), NodeErrorPolicy.EmitError), Node("b", "text")],
            [Edge("e0", "start", "a"), Edge("e1", "a", "b", ("_error", "e"))]);

        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.JsonProjectionInvalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(NodeErrorPolicy.FailWorkflow)]
    [InlineData(NodeErrorPolicy.ContinueWithDefault)]
    [InlineData(NodeErrorPolicy.Skip)]
    public void Under_any_other_policy_no_node_offers_an_error_output(NodeErrorPolicy? policy)
    {
        var result = Compile(
            [With(Node("a", "f"), policy), Node("b", "text")],
            [Edge("e0", "start", "a"), Edge("e1", "a", "b", ("_error", "e"))]);

        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.WirePortUnknown);
    }

    [Fact]
    public void A_workflow_input_node_offers_no_error_output_under_EmitError_since_it_runs_no_block()
    {
        var start = With(StartNode(), NodeErrorPolicy.EmitError);
        var graph = Graph([start, Node("b", "text")], [Edge("e0", "start", "b", ("_error", "e"))]);

        WorkflowCompiler.Compile(graph, Blocks, CanvasSignature.Empty).Diagnostics.Select(d => d.RuleId)
            .Should().BeEquivalentTo([CompilationRules.ErrorPolicyOnBoundaryNode, CompilationRules.WirePortUnknown]);
    }

    [Fact]
    public void A_block_s_own_output_of_that_name_keeps_its_type()
    {
        var result = Compile(
            [With(Node("a", "own"), NodeErrorPolicy.EmitError), Node("b", "text")],
            [Edge("e0", "start", "a"), Edge("e1", "a", "b", ("_error", "e"))]);

        result.Diagnostics.Should().BeEmpty();
    }
}
