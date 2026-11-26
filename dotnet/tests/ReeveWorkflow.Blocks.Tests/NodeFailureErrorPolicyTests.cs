using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class NodeFailureErrorPolicyTests
{
    public enum Cause { ExpressionDivision, ExpressionOverflow, ExpressionAbsentVariable, Math, Coded, Other, AbsentRequiredInput }

    private const string HostCode = "host.depot-closed";

    public static TheoryData<Cause, string, string?> Expected => new()
    {
        { Cause.ExpressionDivision, "expression-runtime-error", "division-by-zero" },
        { Cause.ExpressionOverflow, "expression-runtime-error", "overflow" },
        { Cause.ExpressionAbsentVariable, "expression-runtime-error", "value-absent" },
        { Cause.Math, "block-failed", null },
        { Cause.Coded, HostCode, null },
        { Cause.Other, "block-failed", null },
        { Cause.AbsentRequiredInput, "required-input-value-absent", null },
    };

    public static TheoryData<Cause, Type> Thrown => new()
    {
        { Cause.ExpressionDivision, typeof(ExpressionEvaluationException) },
        { Cause.ExpressionOverflow, typeof(ExpressionEvaluationException) },
        { Cause.ExpressionAbsentVariable, typeof(ExpressionEvaluationException) },
        { Cause.Math, typeof(DivideByZeroException) },
        { Cause.Coded, typeof(HostCodedException) },
        { Cause.Other, typeof(InvalidOperationException) },
        { Cause.AbsentRequiredInput, typeof(WorkflowRequiredInputAbsentException) },
    };

    [Theory]
    [MemberData(nameof(Expected))]
    public async Task EmitError_writes_the_error_object_with_its_code_and_category(Cause cause, string code, string? category)
    {
        var outputs = await RunAsync(new Probe(), Root(Failing(cause, new NodeErrorConfig(NodeErrorPolicy.EmitError))));

        var error = (Dictionary<string, object?>)outputs["n"]["_error"]!;
        error["code"].Should().Be(code);
        if (category is null)
            error.Should().NotContainKey("category");
        else
            error["category"].Should().Be(category);
        error["nodeId"].Should().Be("n");
        outputs["n"].Keys.Should().Equal(["_error"], "a failure is never a value, and Math has no Error output");
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public async Task ContinueWithDefault_writes_the_default_outputs_and_nothing_the_failure_made(Cause cause, string code, string? category)
    {
        _ = (code, category);
        var policy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault, """{"Result": 7}""");
        var outputs = await RunAsync(new Probe(), Root(Failing(cause, policy)));

        outputs["n"].Should().HaveCount(1);
        outputs["n"]["Result"].Should().Be(7);
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public async Task Skip_leaves_the_node_without_outputs_and_does_not_run_what_follows(Cause cause, string code, string? category)
    {
        _ = (code, category);
        var probe = new Probe();
        var outputs = await RunAsync(probe, Root(Failing(cause, new NodeErrorConfig(NodeErrorPolicy.Skip)), followedBy: true));

        outputs.Should().NotContainKey("n");
        outputs.Should().NotContainKey("after");
        probe.Marks.Should().NotContain("after");
    }

    [Theory]
    [MemberData(nameof(Thrown))]
    public async Task FailWorkflow_throws_the_node_failure_wrapping_what_the_node_threw(Cause cause, Type inner)
    {
        var probe = new Probe();
        var run = () => RunAsync(probe, Root(Failing(cause, new NodeErrorConfig(NodeErrorPolicy.FailWorkflow)), followedBy: true));

        var thrown = (await run.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which;
        thrown.NodeId.Should().Be("n");
        thrown.InnerException.Should().BeOfType(inner);
        probe.Marks.Should().NotContain("after");
    }

    [Theory]
    [MemberData(nameof(Expected))]
    public async Task A_try_catch_is_told_the_code_and_category_of_the_node_that_failed(Cause cause, string code, string? category)
    {
        var probe = new Probe();
        var catcher = new WorkflowNodeDefinition
        {
            Id = "catcher",
            BlockTypeId = CatcherFactory.Id,
            SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Try"] = Branch(Failing(cause, null)) }
        };

        await RunAsync(probe, Root(catcher));

        var error = probe.Reported.Should().ContainSingle().Subject;
        error.Code.Should().Be(code);
        error.Category.Should().Be(category);
        error.FailedNodeId.Should().Be("n");
    }

    [Fact]
    public async Task A_required_input_with_no_value_stops_the_node_before_its_block_runs()
    {
        var probe = new Probe();
        await RunAsync(probe, Root(Failing(Cause.AbsentRequiredInput, new NodeErrorConfig(NodeErrorPolicy.EmitError))));

        probe.Marks.Should().NotContain("needs-ran");
    }

    [Fact]
    public async Task A_null_is_a_value_and_does_not_stop_a_required_input()
    {
        var probe = new Probe();
        var node = new WorkflowNodeDefinition
        {
            Id = "n", BlockTypeId = NeedsFactory.Id,
            Config = new Dictionary<string, JsonElement> { ["Needed"] = JsonSerializer.SerializeToElement<string?>(null) }
        };

        await RunAsync(probe, Root(node));

        probe.Marks.Should().Contain("needs-ran");
    }

    [Fact]
    public async Task A_wire_from_a_node_that_wrote_no_such_output_leaves_the_required_input_absent()
    {
        var probe = new Probe();
        var producer = new WorkflowNodeDefinition { Id = "producer", BlockTypeId = MarkFactory.Id, Config = new() { ["Mark"] = Json("producer") } };
        var consumer = new WorkflowNodeDefinition
        {
            Id = "n", BlockTypeId = NeedsFactory.Id, ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError)
        };
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                producer, consumer
            ],
            Edges =
            [
                new WorkflowConnectionDefinition { Id = "e1", SourceNodeId = "start", TargetNodeId = "producer" },
                new WorkflowConnectionDefinition
                {
                    Id = "e2", SourceNodeId = "producer", TargetNodeId = "n",
                    Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["Needed"] = new { type = "direct", sourceOutputName = "Missing" } })
                }
            ]
        };

        var outputs = await RunAsync(probe, definition);

        ((Dictionary<string, object?>)outputs["n"]["_error"]!)["code"].Should().Be("required-input-value-absent");
        probe.Marks.Should().NotContain("needs-ran");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task A_coded_exception_with_a_blank_code_or_a_throwing_getter_is_block_failed(string? code)
    {
        var probe = new Probe { CodeToThrow = code };
        var outputs = await RunAsync(probe, Root(Failing(Cause.Coded, new NodeErrorConfig(NodeErrorPolicy.EmitError))));

        ((Dictionary<string, object?>)outputs["n"]["_error"]!)["code"].Should().Be("block-failed");
    }

    [Fact]
    public async Task A_value_outside_an_Enum_field_carries_the_code_its_message_leads_with()
    {
        var node = new WorkflowNodeDefinition
        {
            Id = "n", BlockTypeId = "Math",
            Config = new Dictionary<string, JsonElement> { ["A"] = Json(1), ["B"] = Json(2), ["Op"] = Json("Sqrt") },
            ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError)
        };

        var outputs = await RunAsync(new Probe(), Root(node));

        var error = (Dictionary<string, object?>)outputs["n"]["_error"]!;
        error["code"].Should().Be("config_value_not_allowed");
        ((string)error["message"]!).Should().StartWith("config_value_not_allowed:");
    }

    private static WorkflowNodeDefinition Failing(Cause cause, NodeErrorConfig? policy)
    {
        var node = cause switch
        {
            Cause.ExpressionDivision => ExpressionNode("expr: 1 / 0"),
            Cause.ExpressionOverflow => ExpressionNode("expr: 9223372036854775807 + 1"),
            Cause.ExpressionAbsentVariable => ExpressionNode("expr: ${previous.nobody.Value}"),
            Cause.Math => new WorkflowNodeDefinition
            {
                Id = "n", BlockTypeId = "Math",
                Config = new Dictionary<string, JsonElement> { ["A"] = Json(5), ["B"] = Json(0), ["Op"] = Json("Div") }
            },
            Cause.Coded => new WorkflowNodeDefinition { Id = "n", BlockTypeId = ThrowingFactory.Id, Config = ThrowConfig("coded") },
            Cause.Other => new WorkflowNodeDefinition { Id = "n", BlockTypeId = ThrowingFactory.Id, Config = ThrowConfig("other") },
            _ => new WorkflowNodeDefinition { Id = "n", BlockTypeId = NeedsFactory.Id },
        };
        node.ErrorPolicy = policy;
        return node;
    }

    private static WorkflowNodeDefinition ExpressionNode(string text) => new()
    {
        Id = "n", BlockTypeId = "Expression",
        Config = new Dictionary<string, JsonElement> { ["Expression"] = Json(text) }
    };

    private static Dictionary<string, JsonElement> ThrowConfig(string kind) => new() { ["Kind"] = Json(kind) };

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static WorkflowDefinition Root(WorkflowNodeDefinition node, bool followedBy = false)
    {
        var nodes = new List<WorkflowNodeDefinition>
        {
            new() { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            node,
        };
        var edges = new List<WorkflowConnectionDefinition>
        {
            new() { Id = "e1", SourceNodeId = "start", TargetNodeId = node.Id },
        };
        if (followedBy)
        {
            nodes.Add(new WorkflowNodeDefinition
            {
                Id = "after", BlockTypeId = MarkFactory.Id, Config = new Dictionary<string, JsonElement> { ["Mark"] = Json("after") }
            });
            edges.Add(new WorkflowConnectionDefinition { Id = "e2", SourceNodeId = node.Id, TargetNodeId = "after" });
        }

        return new WorkflowDefinition { Nodes = nodes, Edges = edges };
    }

    private static WorkflowDefinition Branch(WorkflowNodeDefinition node) => new()
    {
        Nodes = [new WorkflowNodeDefinition { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }, node],
        Edges = [new WorkflowConnectionDefinition { Id = "e", SourceNodeId = "in", TargetNodeId = node.Id }]
    };

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(
        Probe probe, WorkflowDefinition definition)
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new ThrowingFactory(probe));
        services.AddSingleton<IWorkflowBlockFactory>(new NeedsFactory(probe));
        services.AddSingleton<IWorkflowBlockFactory>(new MarkFactory(probe));
        services.AddSingleton<IWorkflowBlockFactory>(new CatcherFactory(probe));
        var blocks = new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, new NullWorkflowMetrics(), new NullWorkflowNodeObserver(), NullHelperResolver.Instance);

        var compiled = TestPrograms.Walk(definition, blocks, terms: new Dictionary<string, string> { ["nobody.Value"] = "Int" },
            nodeTypes: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["n"] = new Dictionary<string, string> { ["$T"] = "Int", ["Result"] = "Number" },
            });
        return await executor.ExecuteCompiledAsync(compiled,
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new EmptyServices(),
            [], [], WorkflowTraceMode.Full, false, null, null, depth: 0, visitedNodeIds: [],
            isInsideTryCatch: false, inputParametersForRun: null, triggerIdForRun: null,
            triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None);
    }

    private sealed class Probe
    {
        public List<string> Marks { get; } = [];
        public List<SubCanvasError> Reported { get; } = [];

        public string? CodeToThrow { get; init; } = HostCode;
    }

    public sealed class HostCodedException(string? code) : Exception("the depot is closed"), IWorkflowCodedException
    {
        public string Code => code ?? throw new InvalidOperationException("no code");
    }

    private abstract class Factory(string id) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => id;
        public string Category => "test";
        public virtual IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new BlockPortDefinition("Result", "Result", PortValueType.Number, false)];
        public virtual IReadOnlyCollection<string> SubCanvasNames => [];
        public abstract IWorkflowBlockPlugin Create();
    }

    private sealed class ThrowingFactory(Probe probe) : Factory(Id)
    {
        public const string Id = "test.throws";
        public override IWorkflowBlockPlugin Create() => new Throwing(probe);

        private sealed class Throwing(Probe probe) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
                (string?)inputs["Kind"] == "coded"
                    ? throw new HostCodedException(probe.CodeToThrow)
                    : throw new InvalidOperationException("the block's own failure");
        }
    }

    private sealed class NeedsFactory(Probe probe) : Factory(Id)
    {
        public const string Id = "test.needs";

        public override IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new BlockPortDefinition("Needed", "Needed", PortValueType.String, true)];

        public override IWorkflowBlockPlugin Create() => new Needs(probe);

        private sealed class Needs(Probe probe) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                probe.Marks.Add("needs-ran");
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
            }
        }
    }

    private sealed class MarkFactory(Probe probe) : Factory(Id)
    {
        public const string Id = "test.mark";
        public override IWorkflowBlockPlugin Create() => new Mark(probe);

        private sealed class Mark(Probe probe) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                probe.Marks.Add((string)inputs["Mark"]!);
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
            }
        }
    }

    private sealed class CatcherFactory(Probe probe) : Factory(Id)
    {
        public const string Id = "test.catcher";
        public override IReadOnlyCollection<string> SubCanvasNames => ["Try"];
        public override IWorkflowBlockPlugin Create() => new Catcher(probe);

        private sealed class Catcher(Probe probe) : IWorkflowBlockPlugin
        {
            public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                var result = await context.SubCanvasExecutor!.TryExecuteAsync("Try", inputs, cancellationToken);
                if (result.Error is { } error)
                    probe.Reported.Add(error);
                return new Dictionary<string, object?>();
            }
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
