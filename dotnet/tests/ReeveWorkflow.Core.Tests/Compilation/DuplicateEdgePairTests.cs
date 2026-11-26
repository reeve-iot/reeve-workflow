using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class DuplicateEdgePairTests
{
    [Fact]
    public void The_compiler_names_two_edges_with_the_same_source_and_target()
    {
        var blocks = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("sink"));
        var start = StartNode();
        var sink = Node("sink", "sink");
        var graph = Graph([start, sink], [Wire("e1"), Wire("e2")]);

        var diagnostics = WorkflowCompiler.Compile(graph, blocks, CanvasSignature.Empty).Diagnostics;

        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.DuplicateEdgePair);

        WorkflowConnectionDefinition Wire(string id) => new()
        {
            Id = id, SourceNodeId = start.Id, TargetNodeId = sink.Id,
            Mappings = System.Text.Json.JsonSerializer.SerializeToElement(new Dictionary<string, object>()),
        };
    }
}
