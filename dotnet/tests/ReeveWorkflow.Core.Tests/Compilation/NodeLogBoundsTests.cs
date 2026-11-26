using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class NodeLogBoundsTests
{
    private static readonly int Limit = WorkflowGraphHelpers.NodeLogTextLimit;

    private static readonly string Fits = new('n', Limit);
    private static readonly string OverTheLimit = new('n', Limit + 1);

    private static readonly IBlockShapeSource Blocks = Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("f", source: "test://f"),
        Shape("loop", subCanvasNames: ["Body"], subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }));

    private static CompilationResult Compile(params WorkflowNodeDefinition[] nodes) =>
        WorkflowCompiler.Compile(Graph([StartNode(), .. nodes], [Edge("e0", "start", nodes[0].Id)]), Blocks, CanvasSignature.Empty);

    [Fact]
    public void A_node_id_one_character_past_the_column_refuses_the_graph()
    {
        var diagnostic = Compile(Node(OverTheLimit, "f")).Diagnostics.Should().ContainSingle().Which;

        diagnostic.RuleId.Should().Be(CompilationRules.NodeIdTooLong);
        diagnostic.NodeId.Should().Be(OverTheLimit, "the refusal points at the node it is about");
        diagnostic.CanvasPath.Should().BeEmpty();
        diagnostic.Message.Should().Contain(Limit.ToString()).And.Contain("NodeId");
        diagnostic.Message.Should().Contain(OverTheLimit.Length.ToString(),
            "the message says how long the id is, so an author need not count it");
    }

    [Fact]
    public void A_node_id_at_the_column_refuses_nothing()
    {
        Compile(Node(Fits, "f")).Diagnostics.Should().BeEmpty("an id the column holds is a graph the run can record");
    }

    [Fact]
    public void A_block_type_id_one_character_past_the_column_refuses_the_graph()
    {
        var diagnostic = Compile(Node("a", OverTheLimit)).Diagnostics
            .Should().ContainSingle(d => d.RuleId == CompilationRules.BlockTypeIdTooLong).Which;

        diagnostic.NodeId.Should().Be("a", "the node is what the refusal is anchored to");
        diagnostic.Message.Should().Contain(Limit.ToString()).And.Contain("NodeType").And.Contain(OverTheLimit);
    }

    [Fact]
    public void A_block_type_id_at_the_column_refuses_nothing()
    {
        Compile(Node("a", "f")).Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Both_are_refused_below_the_root_with_the_branch_named()
    {
        var body = Graph([StartNode(), Node(OverTheLimit, OverTheLimit)],
            [Edge("e1", "start", OverTheLimit)]);
        var loop = Node("l", "loop");
        loop.SubCanvases = new() { ["Body"] = body };
        CanvasPathSegment[] insideTheBody = [new("l", "Body")];

        var diagnostics = WorkflowCompiler.Compile(
            Graph([StartNode(), loop], [Edge("e0", "start", "l")]), Blocks, CanvasSignature.Empty).Diagnostics;

        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodeIdTooLong)
            .And.Contain(d => d.RuleId == CompilationRules.BlockTypeIdTooLong);
        diagnostics.Where(d => d.RuleId is CompilationRules.NodeIdTooLong or CompilationRules.BlockTypeIdTooLong)
            .Should().OnlyContain(d => d.CanvasPath.SequenceEqual(insideTheBody));
    }
}
