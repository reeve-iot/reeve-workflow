using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class StandardBlocksOnAGraphTests
{
    [Theory]
    [InlineData(true, "then-ran")]
    [InlineData(false, "else-ran")]
    public async Task IfElse_runs_exactly_one_branch_of_a_real_graph(bool condition, string expected)
    {
        var probe = new Probe();
        var node = Container("IfElse", new()
        {
            ["Then"] = Branch("then-ran"),
            ["Else"] = Branch("else-ran")
        });
        node.Config = new Dictionary<string, JsonElement> { ["Condition"] = Json(condition) };

        await Run(probe, Root(node));

        probe.Marks.Should().Equal([expected]);
    }

    [Fact]
    public async Task IterateOver_runs_the_body_once_per_item_of_a_real_graph()
    {
        var probe = new Probe();
        var body = Branch("tick");
        body.Edges!.Single().Mappings = Json(new { });
        var node = Container("IterateOver", new() { ["Body"] = body });
        node.Config = new Dictionary<string, JsonElement>
        {
            ["Collection"] = JsonSerializer.SerializeToElement(new[] { "a", "b", "c" })
        };
        var root = Root(node);
        root.Edges!.Single().Mappings = Json(new { });

        await RunCompiled(probe, root);

        probe.Marks.Should().Equal(["tick", "tick", "tick"]);
    }

    [Fact]
    public async Task IterateOver_over_a_list_holding_a_NaN_fails_at_the_body_input_Item_and_runs_the_finite_items_before_it()
    {
        var probe = new Probe();
        var body = Branch("tick");
        body.Edges!.Single().Mappings = Json(new { });
        var node = Container("IterateOver", new() { ["Body"] = body });
        node.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);
        var producer = new WorkflowNodeDefinition { Id = "producer", BlockTypeId = ListProducerFactory.Id };
        var definition = new WorkflowDefinition
        {
            Nodes = [new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }, producer, node],
            Edges =
            [
                new WorkflowConnectionDefinition { Id = "e1", SourceNodeId = "start", TargetNodeId = "producer", Mappings = Json(new { }) },
                new WorkflowConnectionDefinition
                {
                    Id = "e2", SourceNodeId = "producer", TargetNodeId = node.Id,
                    Mappings = Json(new Dictionary<string, object> { ["Collection"] = new { type = "direct", sourceOutputName = "Numbers" } }),
                },
            ],
        };

        var outputs = await RunCompiled(probe, definition);

        var error = (Dictionary<string, object?>)outputs["container"]["_error"]!;
        error["code"].Should().Be("block-output-type-mismatch");
        ((string)error["message"]!).Should().Contain("input 'Item' of sub-canvas 'Body' of node 'container'").And.Contain("non-finite number");
        probe.Marks.Should().Equal(["tick"], "the first item is finite and the second is the NaN");
    }

    [Fact]
    public async Task ForLoop_stops_at_the_cap_it_was_given_rather_than_spinning()
    {
        var probe = new Probe();
        var body = Branch("tick");
        body.Edges!.Single().Mappings = Json(new { });
        var node = Container("ForLoop", new() { ["Body"] = body });
        node.Config = new Dictionary<string, JsonElement>
        {
            ["From"] = Json(0), ["To"] = Json(100), ["Step"] = Json(1), ["MaxIterations"] = Json(4)
        };

        var root = Root(node);
        root.Edges!.Single().Mappings = Json(new { });

        var run = () => RunCompiled(probe, root);

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>();
        probe.Marks.Should().HaveCount(4, "the cap is what stops it, and it stops after the fourth body");
    }

    [Fact]
    public async Task TryCatch_runs_Catch_for_a_failing_Try_and_Finally_either_way()
    {
        var probe = new Probe();
        var node = Container("TryCatch", new()
        {
            ["Try"] = Branch("try", thenThrow: true),
            ["Catch"] = Branch("caught"),
            ["Finally"] = Branch("finally")
        });

        var outputs = await Run(probe, Root(node));

        probe.Marks.Should().Equal(["try", "caught", "finally"]);
        outputs["container"]["CaughtError"].Should().Be(true);
        outputs["container"]["ErrorMessage"].Should().Be("probe failed");
    }

    [Fact]
    public async Task A_Rethrow_inside_Catch_still_lets_Finally_run_before_it_escapes()
    {
        var probe = new Probe();
        var node = Container("TryCatch", new()
        {
            ["Try"] = Branch("try", thenThrow: true),
            ["Catch"] = Branch("caught", thenRethrow: true),
            ["Finally"] = Branch("finally")
        });

        var run = () => Run(probe, Root(node));

        await run.Should().ThrowAsync<SubCanvasRethrowException>(
            "a rethrow escapes the node's error policy so an outer Try/Catch can still see it");
        probe.Marks.Should().Equal(["try", "caught", "finally"],
            "a Rethrow that skipped Finally would leak whatever Finally exists to release");
    }

    [Fact]
    public async Task An_overridden_id_is_the_block_the_walk_executes()
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks(o => o.Override<StandInHttpRequestBlockFactory>("HttpRequest"));
        var blocks = new RegisteredWorkflowBlocks(
            services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
        var definition = Root(new WorkflowNodeDefinition
        {
            Id = "container", BlockTypeId = "HttpRequest",
            Config = new Dictionary<string, JsonElement> { ["Url"] = Json("http://stand-in.test/") }
        });

        var outputs = await Executor(blocks).ExecuteGraphAsync(
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
            [], [], WorkflowTraceMode.Full, false, null, null, depth: 0, visitedNodeIds: [],
            isInsideTryCatch: false, inputParametersForRun: null, triggerIdForRun: null,
            triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
            compiled: TestPrograms.Walk(definition, blocks));

        outputs["container"]["Body"].Should().Be("stood in for",
            "a graph saved against 'HttpRequest' reaches the host's block, not the package's");
    }

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> Run(
        Probe probe, WorkflowDefinition definition)
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new ProbeBlockFactory(probe));
        var blocks = new RegisteredWorkflowBlocks(
            services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());

        return await Executor(blocks).ExecuteGraphAsync(
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
            [], [], WorkflowTraceMode.Full, false, null, null, depth: 0, visitedNodeIds: [],
            isInsideTryCatch: false, inputParametersForRun: null, triggerIdForRun: null,
            triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
            compiled: TestPrograms.Walk(definition, blocks));
    }

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunCompiled(Probe probe, WorkflowDefinition definition)
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new ProbeBlockFactory(probe));
        services.AddSingleton<IWorkflowBlockFactory>(new ListProducerFactory());
        var blocks = new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var parameters = new Dictionary<string, object?>();

        return await Executor(blocks).ExecuteGraphAsync(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["start"] = parameters },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
            [], [], WorkflowTraceMode.Full, false, null, null, depth: 0, visitedNodeIds: [],
            isInsideTryCatch: false, inputParametersForRun: parameters, triggerIdForRun: null,
            triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
            compiled: CompiledWalk.Root(result.Program!));
    }

    private static WorkflowGraphExecutor Executor(IWorkflowBlocks blocks, WorkflowGraphExecutorOptions? options = null) =>
        new(blocks, NullLoggerFactory.Instance, new NullWorkflowMetrics(), new NullWorkflowNodeObserver(),
            NullHelperResolver.Instance, options);

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static WorkflowNodeDefinition Container(
        string blockTypeId, Dictionary<string, WorkflowDefinition> subCanvases) => new()
    {
        Id = "container", BlockTypeId = blockTypeId, SubCanvases = subCanvases
    };

    private static WorkflowDefinition Root(WorkflowNodeDefinition node) => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            node
        ],
        Edges = [new WorkflowConnectionDefinition { Id = "e1", SourceNodeId = "start", TargetNodeId = node.Id }]
    };

    private static WorkflowDefinition Branch(string mark, bool thenThrow = false, bool thenRethrow = false) => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            new WorkflowNodeDefinition
            {
                Id = "probe",
                BlockTypeId = ProbeBlockFactory.Id,
                Config = new Dictionary<string, JsonElement>
                {
                    ["Mark"] = Json(mark), ["Throw"] = Json(thenThrow), ["Rethrow"] = Json(thenRethrow)
                }
            }
        ],
        Edges = [new WorkflowConnectionDefinition { Id = "e", SourceNodeId = "in", TargetNodeId = "probe" }]
    };

    private sealed class Probe
    {
        public List<string> Marks { get; } = [];
    }

    private sealed class StandInHttpRequestBlockFactory : HttpRequestBlockFactory
    {
        public override IWorkflowBlockPlugin Create() => new StandIn();

        private sealed class StandIn : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
                CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyDictionary<string, object?>>(
                    new Dictionary<string, object?> { ["Body"] = "stood in for" });
        }
    }

    private sealed class ProbeBlockFactory(Probe probe) : IWorkflowBlockFactory
    {
        public const string Id = "test.probe";

        public string BlockTypeId => Id;
        public string DisplayName => "Probe";
        public string Description => "Records that it ran, then optionally fails.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];

        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("Continue", "Continue", PortValueType.Boolean, false)];

        public IWorkflowBlockPlugin Create() => new ProbeBlock(probe);
    }

    private sealed class ListProducerFactory : IWorkflowBlockFactory
    {
        public const string Id = "test.list-producer";

        public string BlockTypeId => Id;
        public string DisplayName => "List producer";
        public string Description => "Writes a Collection<Number> holding a NaN.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];

        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("Numbers", "Numbers", PortValueType.Of("Collection<Number>"), false)];

        public IWorkflowBlockPlugin Create() => new ListProducer();

        private sealed class ListProducer : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyDictionary<string, object?>>(
                    new Dictionary<string, object?> { ["Numbers"] = new List<double> { 1.5, double.NaN } });
        }
    }

    private sealed class ProbeBlock(Probe probe) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
            CancellationToken cancellationToken)
        {
            probe.Marks.Add((string)inputs["Mark"]!);

            if (inputs.TryGetValue("Rethrow", out var rethrow) && rethrow is true)
                throw new SubCanvasRethrowException("probe rethrew");
            if (inputs.TryGetValue("Throw", out var fail) && fail is true)
                throw new InvalidOperationException("probe failed");

            return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["Continue"] = false });
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
