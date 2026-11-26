using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class ErrorPolicyImportCheckTests
{
    private static readonly IBlockShapeSource Blocks = Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("f", source: "test://f"),
        Shape("loop", subCanvasNames: ["Body"], subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }));

    private static WorkflowNodeDefinition Failing(object? configuration, bool route)
    {
        var node = Node("a", "f", configuration);
        node.ErrorPolicy = route ? new NodeErrorConfig(NodeErrorPolicy.Skip, RouteToErrorPort: true) : null;
        return node;
    }

    private static WorkflowDefinition At(int depth, WorkflowNodeDefinition node)
    {
        var canvas = Graph([StartNode(), node], [Edge("e0", "start", node.Id)]);
        for (var level = depth; level > 0; level--)
        {
            var loop = Node("l" + level, "loop");
            loop.SubCanvases = new() { ["Body"] = canvas };
            canvas = Graph([StartNode(), loop], [Edge("e0", "start", loop.Id)]);
        }

        return canvas;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Each_form_is_refused_as_the_compiler_refuses_it_on_every_canvas(int depth)
    {
        foreach (var configured in new object?[] { null, new { errorPolicy = new { policy = "Skip" } }, new { ErrorPolicy = 1 } })
        foreach (var route in new[] { false, true })
        {
            var definition = At(depth, Failing(configured, route));

            var check = PortTypeNameCheck.Check(Stored(definition), null, null);
            var compiled = WorkflowCompiler.Compile(definition, Blocks, CanvasSignature.Empty).Diagnostics;

            var because = $"depth {depth}, configuration {System.Text.Json.JsonSerializer.Serialize(configured)}, route {route}";
            if (route)
            {
                check.Unread.Should().BeEmpty(because);
                check.Unreadable.Should().ContainSingle(because).Which.Message.Should().Contain("routeToErrorPort key no run reads");
                check.IsWellFormed.Should().BeFalse(because);
                compiled.Should().Contain(d => d.RuleId == CompilationRules.ErrorPolicyRouteUnread, because);
                continue;
            }

            check.Unread.Select(d => (d.RuleId, d.NodeId, d.PortName, d.Message, Path(d)))
                .Should().Equal(compiled.Select(d => (d.RuleId, d.NodeId, d.PortName, d.Message, Path(d))), because);
            check.Unread.Should().NotContain(d => d.BlockSource != null, because);
            check.IsWellFormed.Should().Be(compiled.Count == 0, because);
            check.Diagnostics.Should().Equal(check.Unread, because);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void A_policy_on_an_entry_node_is_refused_by_the_import_check_as_the_compiler_refuses_it(int depth)
    {
        var definition = At(depth, Failing(null, route: false));
        for (WorkflowDefinition? canvas = definition; canvas is not null;
            canvas = canvas.Nodes.FirstOrDefault(n => n.SubCanvases is not null)?.SubCanvases?["Body"])
        {
            foreach (var entry in canvas.Nodes.Where(n => n.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId))
                entry.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.Skip);
        }

        var check = PortTypeNameCheck.Check(Stored(definition), null, null);
        var compiled = WorkflowCompiler.Compile(definition, Blocks, CanvasSignature.Empty).Diagnostics;

        check.Unread.Should().HaveCount(depth + 1).And.OnlyContain(d => d.RuleId == CompilationRules.ErrorPolicyOnBoundaryNode);
        check.Unread.Select(d => (d.NodeId, Path(d))).Should().Equal(
            compiled.Where(d => d.RuleId == CompilationRules.ErrorPolicyOnBoundaryNode).Select(d => (d.NodeId, Path(d))));
        check.IsWellFormed.Should().BeFalse();
    }

    [Fact]
    public void Each_message_says_how_to_fix_it()
    {
        var configured = PortTypeNameCheck.Check(Stored(At(0, Failing(new { errorPolicy = new { policy = "Skip" } }, route: false))), null, null);
        configured.Unread.Select(d => d.RuleId).Should().Equal(CompilationRules.ErrorPolicyInConfiguration);
        configured.Unread[0].Message.Should().Contain("move it onto the node as the node's own 'errorPolicy'").And.Contain("remove the configuration key");

        var routed = PortTypeNameCheck.Check(Stored(At(0, Failing(null, route: true))), null, null);
        routed.Unreadable.Should().ContainSingle().Which.Message.Should().Contain("a routeToErrorPort key no run reads").And.Contain("error-policy-route-unread");
    }

    [Fact]
    public void A_node_whose_block_no_catalogue_is_read_for_is_checked_too()
    {
        var node = Node("a", "not.in.any.catalogue", new { errorPolicy = new { policy = "Skip" } });

        PortTypeNameCheck.Check(Stored(At(1, node)), null, null).Unread
            .Should().ContainSingle().Which.CanvasPath.Should().Equal(new CanvasPathSegment("l1", "Body"));
    }

    private static string Path(CompilationDiagnostic d) => string.Join("/", d.CanvasPath.Select(s => s.NodeId + "." + s.BranchName));
}
