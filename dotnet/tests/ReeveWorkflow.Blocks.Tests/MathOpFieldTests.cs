using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class MathOpFieldTests
{
    private static readonly string[] Names = ["Add", "Sub", "Mul", "Div", "Mod", "Pow", "Min", "Max"];

    [Fact]
    public void Op_is_an_Enum_field_of_the_eight_names_defaulting_to_Add()
    {
        var factory = new MathBlockFactory();

        var op = factory.ConfigurationSchema.Single(f => f.Name == "Op");

        op.Type.Should().Be(ConfigFieldType.Enum);
        op.AllowedValues.Should().Equal(Names);
        op.DefaultValue.Should().Be("Add");
        op.Required.Should().BeFalse();
    }

    [Fact]
    public void Op_is_not_a_port()
    {
        var factory = new MathBlockFactory();

        factory.Inputs.Select(p => p.Name).Should().Equal("A", "B");
        factory.Outputs.Select(p => p.Name).Should().Equal("Result");
    }

    [Fact]
    public void The_catalogue_publishes_Op_as_an_Enum_field_and_no_Op_input()
    {
        var described = WorkflowBlockDescription.Describe(new MathBlockFactory());

        described.Inputs.Select(p => p.Name).Should().Equal("A", "B");
        var op = described.ConfigurationSchema.Single(f => f.Name == "Op");
        (op.Type, op.DefaultValue).Should().Be(("Enum", "Add"));
        op.AllowedValues.Should().Equal(Names);
    }

    [Fact]
    public void The_block_registers_under_the_declaration_rules()
    {
        var read = () => BlockShape.From(new MathBlockFactory());

        read.Should().NotThrow();
    }

    public static TheoryData<string, object> Results() => new()
    {
        { "Add", 10.0 }, { "Sub", 4.0 }, { "Mul", 21.0 }, { "Div", 7.0 / 3.0 }, { "Mod", 1.0 }, { "Pow", 343.0 }, { "Min", 3.0 }, { "Max", 7.0 },
    };

    [Theory]
    [MemberData(nameof(Results))]
    public async Task A_node_applies_the_operation_its_configuration_names(string op, object expected)
    {
        var outputs = await CompiledGraph.RunAsync(Root(Math(("Op", Json(op)))), CompiledGraph.Blocks());

        outputs["math"]["Result"].Should().Be(expected);
    }

    [Fact]
    public async Task A_node_without_an_Op_adds()
    {
        var outputs = await CompiledGraph.RunAsync(Root(Math()), CompiledGraph.Blocks());

        outputs["math"]["Result"].Should().Be(10.0);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("add")]
    [InlineData("ADD")]
    [InlineData(" Add")]
    public async Task A_value_outside_the_names_fails_the_node_with_config_value_not_allowed(string stored)
    {
        var run = () => Run(Math(("Op", Json(stored))));

        var failure = (await run.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which;
        failure.InnerException.Should().BeOfType<WorkflowConfigValueNotAllowedException>()
            .Which.Message.Should().StartWith("config_value_not_allowed:").And.Contain("'Op'").And.Contain("'Add'").And.Contain("'Max'");
    }

    [Fact]
    public async Task The_failure_follows_the_nodes_error_policy_like_any_block_failure()
    {
        var node = Math(("Op", Json("Bogus")));
        node.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault);

        var run = () => Run(node);

        await run.Should().NotThrowAsync("the node failed before the block ran, and ContinueWithDefault carried the run on");
    }

    [Theory]
    [InlineData("literal")]
    [InlineData("direct")]
    public void A_stored_mapping_into_Op_is_a_wire_to_a_port_that_is_gone(string type)
    {
        var mapping = type == "literal"
            ? new Dictionary<string, object?> { ["type"] = type, ["literalValue"] = "Add" }
            : new Dictionary<string, object?> { ["type"] = type, ["sourceOutputName"] = "Result" };
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "math", BlockTypeId = "Math", Config = Config(("A", Json(7)), ("B", Json(3))) },
                new WorkflowNodeDefinition { Id = "sum", BlockTypeId = "Math", Config = Config(("A", Json(7)), ("B", Json(3))) },
            ],
            Edges =
            [
                new() { Id = "e0", SourceNodeId = "start", TargetNodeId = "math", Mappings = Json(new Dictionary<string, object>()) },
                new() { Id = "e1", SourceNodeId = "math", TargetNodeId = "sum",
                    Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Op"] = mapping }) },
            ],
        };

        var diagnostics = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(Blocks()), CanvasSignature.Empty).Diagnostics
            .Select(d => $"{d.RuleId}@{d.NodeId}.{d.PortName}");

        diagnostics.Should().Equal("wire-port-unknown@sum.Op");
    }

    [Theory]
    [InlineData("Sub", false)]
    [InlineData("sub", true)]
    [InlineData("Other", true)]
    [InlineData("", false)]
    public void A_stored_Op_in_the_configuration_is_graded_against_the_names_ignoring_nothing(string op, bool refused)
    {
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "math", BlockTypeId = "Math", Config = Config(("A", Json(7)), ("B", Json(3)), ("Op", Json(op))) },
            ],
            Edges = [new() { Id = "e0", SourceNodeId = "start", TargetNodeId = "math", Mappings = Json(new Dictionary<string, object>()) }],
        };

        var diagnostics = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(Blocks()), CanvasSignature.Empty).Diagnostics
            .Select(d => $"{d.RuleId}@{d.NodeId}.{d.PortName}");

        diagnostics.Should().Equal(refused ? ["literal-port-value-invalid@math.Op"] : []);
    }

    private static WorkflowNodeDefinition Math(params (string Key, JsonElement Value)[] extra) =>
        new() { Id = "math", BlockTypeId = "Math", Config = Config([("A", Json(7)), ("B", Json(3)), .. extra]) };

    private static Dictionary<string, JsonElement> Config(params (string Key, JsonElement Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    private static WorkflowDefinition Root(WorkflowNodeDefinition node) => new()
    {
        Nodes = [new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }, node],
        Edges = [new() { Id = "e0", SourceNodeId = "start", TargetNodeId = node.Id, Mappings = Json(new Dictionary<string, object>()) }],
    };

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> Run(WorkflowNodeDefinition node)
    {
        var definition = Root(node);
        var blocks = Blocks();
        return await new WorkflowGraphExecutor(blocks, NullLoggerFactory.Instance, new NullWorkflowMetrics(),
                new NullWorkflowNodeObserver(), NullHelperResolver.Instance)
            .ExecuteCompiledAsync(TestPrograms.Walk(definition, blocks),
                [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
                [], [], WorkflowTraceMode.Full, false, null, null, depth: 0, visitedNodeIds: [],
                isInsideTryCatch: false, inputParametersForRun: null, triggerIdForRun: null,
                triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None);
    }

    private static RegisteredWorkflowBlocks Blocks()
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        return new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
