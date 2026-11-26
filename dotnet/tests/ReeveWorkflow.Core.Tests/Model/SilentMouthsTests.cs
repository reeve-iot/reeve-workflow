using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class SilentMouthsTests
{
    private static readonly Guid Run = Guid.NewGuid();

    private static IBlockShapeSource Blocks() => Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
        Shape("produce", outputs: [("Value", PortValueType.String)]));

    [Fact]
    public void A_model_holding_output_mappings_is_refused_by_the_root_gate()
    {
        var graph = Graph(
            [StartNode(), Node("p", "produce"), Node("wo", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("e1", "start", "p"), Edge("e2", "p", "wo")]);
        graph.OutputMappings = [new() { SourceNodeId = "p", SourceOutput = "Nope", WorkflowOutput = "final" }];
        var gate = () => WorkflowGraphHelpers.ValidateRootCanvas(graph);
        gate.Should().Throw<WorkflowValidationException>().WithMessage("*outputMappings*output-mappings-unread*");
    }

    [Fact]
    public void A_sub_canvas_output_mapping_is_refused_by_the_reader()
    {
        const string stored = """{"nodes":[{"id":"parent","position":{"x":0,"y":0},"configuration":{},"subCanvases":{"Body":{"nodes":[],"edges":[],"outputMappings":[{"sourceNodeId":"b","sourceOutput":"Out","workflowOutput":"result"}]}}}],"edges":[]}""";
        StoredWorkflowDocument.Read(stored, out var refused).Should().BeNull();
        refused!.Rule.Should().Be(StoredDocumentRules.OutputMappingsUnread);
        refused.Location.Should().Be("/nodes/0/subCanvases/Body/outputMappings");
    }

    private static WorkflowConnectionDefinition Connection(string id, string? source, string? target)
    {
        var connection = (WorkflowConnectionDefinition)RuntimeHelpers.GetUninitializedObject(typeof(WorkflowConnectionDefinition));
        connection.Id = id;
        if (source is not null) connection.SourceNodeId = source;
        if (target is not null) connection.TargetNodeId = target;
        return connection;
    }

    public static TheoryData<string, bool> Ends() => new()
    {
        { "sourceNodeId", true },
        { "targetNodeId", false },
    };

    private static WorkflowDefinition Held(bool nullSource) => Graph(
        [StartNode(), Node("p", "produce"), Node("wo", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
        [Edge("e1", "start", "p"), Connection("broken", nullSource ? null : "p", nullSource ? "wo" : null)]);

    [Theory]
    [MemberData(nameof(Ends))]
    public void The_public_methods_taking_a_model_refuse_a_null_endpoint_naming_the_connection(string end, bool nullSource)
    {
        var graph = Held(nullSource);

        var calls = new (string Name, Action Call)[]
        {
            (nameof(WorkflowGraphHelpers.TopologicalSort), () => WorkflowGraphHelpers.TopologicalSort(graph)),
            (nameof(WorkflowGraphHelpers.ValidateStartAndReachability), () => WorkflowGraphHelpers.ValidateStartAndReachability(graph)),
            (nameof(WorkflowGraphHelpers.ValidateNoDuplicateInputMappings), () => WorkflowGraphHelpers.ValidateNoDuplicateInputMappings(graph)),
            (nameof(WorkflowGraphHelpers.ValidateRootCanvas), () => WorkflowGraphHelpers.ValidateRootCanvas(graph)),
            (nameof(WorkflowGraphHelpers.ValidateAllCanvases), () => WorkflowGraphHelpers.ValidateAllCanvases(graph)),
        };

        foreach (var (name, call) in calls)
            call.Should().Throw<WorkflowValidationException>(name).WithMessage($"*Connection 'broken'*null {end}*", name);
    }

    [Theory]
    [MemberData(nameof(Ends))]
    public void A_setter_refuses_null_naming_the_connection(string end, bool nullSource)
    {
        var connection = new WorkflowConnectionDefinition { Id = "broken" };

        Action assign = nullSource ? () => connection.SourceNodeId = null! : () => connection.TargetNodeId = null!;

        assign.Should().Throw<WorkflowValidationException>().WithMessage($"*Connection 'broken'*{char.ToUpperInvariant(end[0])}{end[1..]}*null*");
        connection.SourceNodeId.Should().BeEmpty();
        connection.TargetNodeId.Should().BeEmpty();
    }

    [Fact]
    public void An_absent_endpoint_binds_as_an_empty_string()
    {
        var connection = System.Text.Json.JsonSerializer.Deserialize<WorkflowConnectionDefinition>("""{"id":"e"}""");

        connection!.SourceNodeId.Should().BeEmpty();
        connection.TargetNodeId.Should().BeEmpty();
    }

    private sealed class NoServices : IServiceProvider
    {
        public static readonly NoServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
