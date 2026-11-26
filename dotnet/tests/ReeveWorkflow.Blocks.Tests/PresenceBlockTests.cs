using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class PresenceBlockTests
{
    private static WorkflowBlockContext Context(ISubCanvasExecutor? executor = null) => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Substitute.For<ILogger>(),
        Services = Substitute.For<IServiceProvider>(),
        SubCanvasExecutor = executor,
    };

    [Theory]
    [InlineData(5L, 9L, 5L)]
    [InlineData(0L, 9L, 0L)]
    [InlineData(null, 9L, 9L)]
    public async Task Coalesce_gives_Value_unless_it_is_null(object? value, object fallback, object expected)
    {
        var result = await new CoalesceBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = value, ["Default"] = fallback }, Context(), CancellationToken.None);

        result.Should().ContainKey("Result").WhoseValue.Should().Be(expected);
    }

    [Fact]
    public async Task Coalesce_keeps_a_present_falsy_value_and_gives_Default_for_an_absent_one()
    {
        var falsy = await new CoalesceBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = false, ["Default"] = true }, Context(), CancellationToken.None);
        var empty = await new CoalesceBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = "", ["Default"] = "d" }, Context(), CancellationToken.None);
        var absent = await new CoalesceBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Default"] = "d" }, Context(), CancellationToken.None);

        falsy["Result"].Should().Be(false);
        empty["Result"].Should().Be("");
        absent["Result"].Should().Be("d");
    }

    [Theory]
    [InlineData(3L, "Present")]
    [InlineData(null, "Absent")]
    public async Task IfPresent_runs_Present_for_a_value_and_Absent_for_a_null(object? value, string branch)
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(call => new Dictionary<string, object?> { ["Out"] = call.ArgAt<string>(0) });

        var result = await new IfPresentBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["Value"] = value }, Context(executor), CancellationToken.None);

        result["Out"].Should().Be(branch);
    }

    [Fact]
    public async Task IfPresent_runs_Absent_when_Value_has_no_key()
    {
        var executor = Substitute.For<ISubCanvasExecutor>();
        executor.ExecuteAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(call => new Dictionary<string, object?> { ["Out"] = call.ArgAt<string>(0) });

        var result = await new IfPresentBlock().ExecuteAsync(new Dictionary<string, object?>(), Context(executor), CancellationToken.None);

        result["Out"].Should().Be("Absent");
    }

    [Fact]
    public void The_two_blocks_are_in_the_standard_set_and_declare_what_the_specification_says()
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        var factories = services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>().ToDictionary(f => f.BlockTypeId);

        var coalesce = factories["Coalesce"];
        coalesce.Inputs.Select(p => (p.Name, p.Type.Name, p.IsRequired)).Should().Equal(("Value", "$T?", false), ("Default", "$T", true));
        coalesce.Outputs.Select(p => (p.Name, p.Type.Name)).Should().Equal(("Result", "$T"));

        var ifPresent = factories["IfPresent"];
        ifPresent.Inputs.Select(p => (p.Name, p.Type.Name, p.IsRequired)).Should().Equal(("Value", "$T?", false));
        ifPresent.Outputs.Should().BeEmpty("it has no fixed output");
        ifPresent.SupportsDeclaredOutputs.Should().BeTrue();
        ifPresent.SubCanvasNames.Should().Equal("Present", "Absent");
        ifPresent.GetSubCanvasInputs("Present").Select(p => (p.Name, p.Type.Name)).Should().Equal(("Value", "$T"));
        ifPresent.GetSubCanvasInputs("Absent").Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, 7L)]
    [InlineData(3, 3L)]
    public async Task Coalesce_on_a_graph_binds_T_to_Int_and_answers_a_long(object? written, long expected)
    {
        var definition = Root(
            Node("p", Producer.Id), Node("c", "Coalesce", new Dictionary<string, object?> { ["Default"] = 7 }),
            Wire("p", "c", ("V", "Value")));

        var outputs = await RunAsync(definition, new Marks(), written);

        outputs["c"]["Result"].Should().BeOfType<long>().Which.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "absent")]
    [InlineData(3, "present:3:Int64")]
    public async Task IfPresent_on_a_graph_hands_Present_the_value_in_the_term_the_nullability_was_taken_off(object? written, string expected)
    {
        var present = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "bin", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                Node("probe", MarkProbe.PresentId),
            ],
            Edges = [Wire("bin", "probe", ("Value", "Value"))],
        };
        var absent = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "ain", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                Node("probe", MarkProbe.AbsentId),
            ],
            Edges = [Wire("ain", "probe")],
        };
        var container = Node("ip", "IfPresent");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Present"] = present, ["Absent"] = absent };
        var definition = Root(Node("p", Producer.Id), container, Wire("p", "ip", ("V", "Value")));
        var marks = new Marks();

        await RunAsync(definition, marks, written);

        marks.Seen.Should().Equal(expected);
    }

    private static WorkflowNodeDefinition Node(string id, string blockTypeId, Dictionary<string, object?>? config = null) => new()
    {
        Id = id, BlockTypeId = blockTypeId,
        Config = (config ?? []).ToDictionary(c => c.Key, c => JsonSerializer.SerializeToElement(c.Value)),
    };

    private static WorkflowConnectionDefinition Wire(string source, string target, params (string From, string To)[] mappings) => new()
    {
        Id = $"{source}-{target}", SourceNodeId = source, TargetNodeId = target,
        Mappings = JsonSerializer.SerializeToElement(mappings.ToDictionary(m => m.To, m => new { type = "direct", sourceOutputName = m.From })),
    };

    private static WorkflowDefinition Root(WorkflowNodeDefinition producer, WorkflowNodeDefinition consumer, WorkflowConnectionDefinition wire) => new()
    {
        Nodes = [new WorkflowNodeDefinition { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }, producer, consumer],
        Edges = [Wire("in", producer.Id), wire],
    };

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(
        WorkflowDefinition definition, Marks marks, object? written)
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new Producer(written));
        services.AddSingleton<IWorkflowBlockFactory>(new MarkProbe(MarkProbe.PresentId, marks));
        services.AddSingleton<IWorkflowBlockFactory>(new MarkProbe(MarkProbe.AbsentId, marks));
        var blocks = new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
            NullHelperResolver.Instance, new WorkflowGraphExecutorOptions());
        var parameters = new Dictionary<string, object?>();
        return await executor.ExecuteGraphAsync(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["in"] = parameters },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new NoServices(), [], [], WorkflowTraceMode.Full,
            debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: parameters, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None, debugHook: null, debugCounter: null, stateExecutionId: null,
            mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(result.Program!));
    }

    private sealed class Marks
    {
        public List<string> Seen { get; } = [];
    }

    private sealed class Producer(object? written) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "test.producer";

        public string BlockTypeId => Id;
        public string DisplayName => "Producer";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new BlockPortDefinition("V", "V", PortValueType.Of("Int?"), false)];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["V"] = written });
    }

    private sealed class MarkProbe(string id, Marks marks) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string PresentId = "test.present";
        public const string AbsentId = "test.absent";

        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";

        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            id == PresentId ? [new BlockPortDefinition("Value", "Value", PortValueType.Int, false)] : [];

        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            marks.Seen.Add(id == PresentId ? $"present:{inputs["Value"]}:{inputs["Value"]?.GetType().Name}" : "absent");
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
