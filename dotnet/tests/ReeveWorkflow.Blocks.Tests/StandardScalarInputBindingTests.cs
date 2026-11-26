using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class StandardScalarInputBindingTests
{
    [Fact]
    public async Task Math_runs_when_Int_inputs_arrive_over_wires()
    {
        var definition = Root(
            [Parser("StringToInt", "6"), Node("math", "Math")],
            [Wire("start", "parse"), Wire("parse", "math", ("Result", "A"), ("Result", "B"))]);

        var outputs = await CompiledGraph.RunAsync(definition, CompiledGraph.Blocks());

        outputs["math"]["Result"].Should().Be(12.0);
    }

    [Fact]
    public async Task ForLoop_runs_when_its_Number_input_arrives_over_a_wire()
    {
        var loop = Node("loop", "ForLoop");
        loop.SubCanvases = new(StringComparer.Ordinal) { ["Body"] = EmptyBody() };
        var definition = Root(
            [Parser("StringToNumber", "2"), loop],
            [Wire("start", "parse"), Wire("parse", "loop", ("Result", "To"))]);

        var outputs = await CompiledGraph.RunAsync(definition, CompiledGraph.Blocks());

        outputs["loop"]["Iterations"].Should().Be(2L);
    }

    [Fact]
    public async Task WaitForDuration_runs_when_its_Int_input_arrives_over_a_wire()
    {
        var definition = Root(
            [Parser("StringToInt", "0"), Node("wait", "WaitForDuration")],
            [Wire("start", "parse"), Wire("parse", "wait", ("Result", "DurationMs"))]);

        var outputs = await CompiledGraph.RunAsync(definition, CompiledGraph.Blocks());

        outputs["wait"]["DurationMs"].Should().Be(0L);
    }

    [Theory]
    [InlineData("WaitForDuration", "DurationMs", "0")]
    [InlineData("ForLoop", "To", "1")]
    [InlineData("HttpRequest", "ThrowOnFailure", "true")]
    public void A_text_literal_on_a_scalar_input_is_refused_during_compilation(string blockTypeId, string port, string text)
    {
        var node = Node("node", blockTypeId);
        node.Config = new() { [port] = JsonSerializer.SerializeToElement(text) };
        if (blockTypeId == "ForLoop") node.SubCanvases = new(StringComparer.Ordinal) { ["Body"] = EmptyBody() };
        if (blockTypeId == "HttpRequest") node.Config["Url"] = JsonSerializer.SerializeToElement("https://example.com/api");
        var definition = Root([node], [Wire("start", "node")]);

        CompiledGraph.Refusals(definition, CompiledGraph.Blocks()).Should().Contain($"literal-port-value-invalid@node.{port}");
    }

    private static WorkflowDefinition Root(IReadOnlyList<WorkflowNodeDefinition> nodes, IReadOnlyList<WorkflowConnectionDefinition> edges) => new()
    {
        Nodes = [new() { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }, .. nodes],
        Edges = edges,
    };

    private static WorkflowNodeDefinition Parser(string blockTypeId, string value) => new()
    {
        Id = "parse", BlockTypeId = blockTypeId,
        Config = new() { ["Value"] = JsonSerializer.SerializeToElement(value) },
    };

    private static WorkflowNodeDefinition Node(string id, string blockTypeId) => new() { Id = id, BlockTypeId = blockTypeId };

    private static WorkflowDefinition EmptyBody() => new()
    {
        Nodes = [new() { Id = "input", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }],
        Edges = [],
    };

    private static WorkflowConnectionDefinition Wire(string source, string target, params (string From, string To)[] mappings) => new()
    {
        Id = $"{source}-{target}", SourceNodeId = source, TargetNodeId = target,
        Mappings = JsonSerializer.SerializeToElement(mappings.ToDictionary(
            mapping => mapping.To,
            mapping => (object)new { type = "direct", sourceOutputName = mapping.From })),
    };
}
