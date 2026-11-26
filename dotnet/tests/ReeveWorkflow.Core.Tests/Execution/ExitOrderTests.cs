using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ExitOrderTests
{
    [Fact]
    public void Compiled_root_and_body_exits_keep_document_order_when_only_the_edges_are_reordered()
    {
        static WorkflowDefinition Graph(bool reverseEdges)
        {
            var first = OutputWire("source", "first-exit", "first");
            var second = OutputWire("source", "second-exit", "second");
            var outputEdges = reverseEdges ? new[] { second, first } : new[] { first, second };
            var body = GraphCanvas(
                [StartNode("body-input"), Node("source", "source"), Node("first-exit", WorkflowGraphHelpers.WorkflowOutputBlockTypeId), Node("second-exit", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
                [Edge("body-start", "body-input", "source"), .. outputEdges]);
            var box = Node("box", "box");
            box.SubCanvases = new() { ["Body"] = body };
            var root = GraphCanvas(
                [StartNode(), Node("source", "source"), Node("first-exit", WorkflowGraphHelpers.WorkflowOutputBlockTypeId), Node("second-exit", WorkflowGraphHelpers.WorkflowOutputBlockTypeId), box],
                [Edge("root-start", "start", "source"), Edge("box-start", "start", "box"), .. outputEdges]);
            return root;
        }

        static Dictionary<string, object?> Result(WorkflowDefinition definition)
        {
            var boxShape = Shape("box", subCanvasOutputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]>
            {
                ["Body"] = [("result", PortValueType.String, false)],
            }, subCanvasNames: ["Body"]);
            var compilation = WorkflowCompiler.Compile(definition,
                Catalogue(Shape("source", outputs: [("first", PortValueType.String), ("second", PortValueType.String)]), boxShape),
                Signature(outputs: [("result", PortValueType.String, false)]));
            compilation.Diagnostics.Should().BeEmpty(string.Join("; ", compilation.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
            var root = CompiledWalk.Root(compilation.Program!);
            var values = new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["source"] = new Dictionary<string, object?> { ["first"] = "first", ["second"] = "second" },
            };
            var rootResult = WorkflowExecutionHelpers.BuildResultOutputs(values, root);
            var body = root.Child("box", "Body") ?? throw new InvalidOperationException("The compiled Body canvas is missing.");
            var bodyResult = WorkflowExecutionHelpers.BuildResultOutputs(values, body);
            rootResult["result"].Should().Be("second");
            return bodyResult;
        }

        Result(Graph(reverseEdges: false))["result"].Should().Be("second");
        Result(Graph(reverseEdges: true))["result"].Should().Be("second");
    }

    private static WorkflowDefinition GraphCanvas(List<WorkflowNodeDefinition> nodes, List<WorkflowConnectionDefinition> edges) =>
        new() { Nodes = nodes, Edges = edges };

    private static WorkflowConnectionDefinition OutputWire(string source, string target, string sourceOutput) => new()
    {
        Id = $"{source}-{target}", SourceNodeId = source, TargetNodeId = target,
        Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["result"] = new { type = "direct", sourceOutputName = sourceOutput },
        }),
    };
}
