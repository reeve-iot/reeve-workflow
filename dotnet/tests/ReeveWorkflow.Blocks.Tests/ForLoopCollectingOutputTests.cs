using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class ForLoopCollectingOutputTests
{
    private static string[] Rules(CompilationResult result) => [.. result.Diagnostics.Select(d => $"{d.RuleId} {d.NodeId}").Order(StringComparer.Ordinal)];

    [Theory]
    [InlineData("Int", "Collection<Int?>")]
    [InlineData("Int?", "Collection<Int?>")]
    [InlineData("Number", "Collection<Number?>")]
    public void A_declared_output_of_T_is_a_collection_of_T_nullable_and_never_a_doubly_nullable_one(string stored, string sinkTakes)
    {
        var result = Compile(Loop(stored, "Squares"), sinkTakes);

        Rules(result).Should().BeEmpty();
        result.Program!.Canvases.Single(c => c.CanvasPath.IsRoot).Nodes.Single(n => n.NodeId == "loop")
            .OutputTypes["Squares"].Term.ToString().Should().Be(sinkTakes);
    }

    [Fact]
    public void The_collected_output_cannot_be_read_as_a_collection_of_a_non_nullable_element()
    {
        Rules(Compile(Loop("Int", "Squares"), "Collection<Int>")).Should().Equal("port-type-mismatch sink");
    }

    [Fact]
    public void The_collected_output_cannot_be_read_as_another_element_type()
    {
        Rules(Compile(Loop("Int", "Squares"), "Collection<String?>")).Should().Equal("port-type-mismatch sink");
    }

    [Fact]
    public void The_body_exit_node_takes_T_under_the_key_the_sources_name()
    {
        var loop = Loop("String", "Squares");

        Rules(Compile(loop, "Collection<String?>")).Should().Equal("port-type-mismatch out");
    }

    [Theory]
    [InlineData("Json")]
    [InlineData("Json?")]
    [InlineData("Collection<Json>")]
    public void A_stored_type_holding_Json_is_port_type_json_position_on_the_node(string stored)
    {
        var rules = Rules(Compile(Loop(stored, "Squares"), "Collection<Int?>"));

        rules.Where(r => r.StartsWith("port-type-json-position", StringComparison.Ordinal)).Should().Equal("port-type-json-position loop");
    }

    [Fact]
    public void A_host_type_is_collected_as_a_collection_of_that_type_nullable()
    {
        var body = new WorkflowDefinition
        {
            Nodes =
            [
                new() { Id = "bin", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new() { Id = "hemit", BlockTypeId = HostEmit.Id },
                new() { Id = "out", BlockTypeId = WorkflowGraphHelpers.WorkflowOutputBlockTypeId },
            ],
            Edges = [Wire("bin", "hemit"), Wire("hemit", "out", ("D", "d"))],
        };
        var loop = new WorkflowNodeDefinition
        {
            Id = "loop", BlockTypeId = "ForLoop", Config = new() { ["To"] = JsonSerializer.SerializeToElement(2) },
            OutputPorts = [new() { Name = "Depots", DisplayName = "Depots", Type = "Depot", Sources = new() { ["Body"] = "d" } }],
            SubCanvases = new(StringComparer.Ordinal) { ["Body"] = body },
        };
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new HostEmit());
        services.AddSingleton<IWorkflowBlockFactory>(new Sink("Collection<Depot?>"));
        var blocks = new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
        var options = new CompilationOptions { TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Depot", typeof(string))]) };

        var result = WorkflowCompiler.Compile(Root(loop, "Collection<Depot?>", null), new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty, options);

        Rules(result).Should().BeEmpty();
        result.Program!.Canvases.Single(c => c.CanvasPath.IsRoot).Nodes.Single(n => n.NodeId == "loop")
            .OutputTypes["Depots"].Term.ToString().Should().Be("Collection<Depot?>");
    }

    [Fact]
    public void A_read_of_the_removed_Results_output_is_out_of_scope()
    {
        var definition = Root(Loop("Int", "Squares"), "Collection<Int?>", null);
        definition.Nodes.Single(n => n.Id == "sink").Config = new() { ["Seen"] = JsonSerializer.SerializeToElement("expr:${previous.loop.Results}") };
        definition.Edges = [Wire("in", "loop"), Wire("loop", "sink")];

        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(Blocks()), CanvasSignature.Empty);

        Rules(result).Should().Contain("expression-out-of-scope sink");
    }

    [Fact]
    public void A_stored_type_holding_Json_is_not_refused_on_a_container_that_does_not_collect()
    {
        var node = new WorkflowNodeDefinition
        {
            Id = "choose", BlockTypeId = "IfElse", Config = new() { ["Condition"] = JsonSerializer.SerializeToElement(true) },
            OutputPorts = [new() { Name = "Doc", DisplayName = "Doc", Type = "Json", Sources = new() { ["Then"] = "doc" } }],
            SubCanvases = new(StringComparer.Ordinal) { ["Then"] = Empty(), ["Else"] = Empty() },
        };
        var definition = new WorkflowDefinition
        {
            Nodes = [new() { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }, node],
            Edges = [Wire("in", "choose")],
        };

        Rules(WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(Blocks()), CanvasSignature.Empty))
            .Should().NotContain("port-type-json-position choose");
    }

    [Fact]
    public void A_declared_output_named_like_the_fixed_Iterations_output_is_refused()
    {
        Rules(Compile(Loop("Int", "Iterations"), "Collection<Int?>", wireOutput: "Iterations"))
            .Should().Contain("declared-output-shadows-static loop");
    }

    [Fact]
    public void Results_is_a_name_like_any_other_now_that_the_loop_has_no_such_output()
    {
        Rules(Compile(Loop("Int", "Results"), "Collection<Int?>", wireOutput: "Results")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Int")]
    [InlineData("Int?")]
    public async Task The_run_writes_one_element_per_iteration_in_order_and_null_where_the_body_left_the_output_absent(string stored)
    {
        var outputs = await Run(Loop(stored, "Squares"), "Collection<Int?>");

        outputs["loop"]["Squares"].Should().BeOfType<List<long?>>().Which.Should().Equal(0L, null, 4L);
        outputs["loop"]["Iterations"].Should().Be(3);
        outputs["sink"]["Text"].Should().Be("0,null,4");
    }

    [Fact]
    public async Task A_loop_that_runs_no_iteration_writes_an_empty_collection_the_next_node_can_read()
    {
        var outputs = await Run(Loop("Int", "Squares", to: 0), "Collection<Int?>");

        outputs["loop"]["Squares"].Should().BeOfType<List<long?>>().Which.Should().BeEmpty();
        outputs["sink"]["Text"].Should().Be("");
    }

    [Fact]
    public async Task Two_declared_outputs_are_collected_independently()
    {
        var loop = Loop("Int", "Squares");
        loop.OutputPorts!.Add(new() { Name = "Labels", DisplayName = "Labels", Type = "String", Sources = new() { ["Body"] = "label" } });
        loop.SubCanvases!["Body"] = Body(("sq", "V"), ("label", "Label"));

        var outputs = await Run(loop, "Collection<Int?>");

        outputs["loop"]["Squares"].Should().BeOfType<List<long?>>().Which.Should().Equal(0L, null, 4L);
        outputs["loop"]["Labels"].Should().BeOfType<List<string?>>().Which.Should().Equal("n0", "n1", "n2");
    }

    private static WorkflowNodeDefinition Loop(string stored, string name, int to = 3) => new()
    {
        Id = "loop", BlockTypeId = "ForLoop", Config = new() { ["To"] = JsonSerializer.SerializeToElement(to) },
        OutputPorts = [new() { Name = name, DisplayName = name, Type = stored, Sources = new() { ["Body"] = "sq" } }],
        SubCanvases = new(StringComparer.Ordinal) { ["Body"] = Body(("sq", "V")) },
    };

    private static WorkflowDefinition Body(params (string Key, string Output)[] exits) => new()
    {
        Nodes =
        [
            new() { Id = "bin", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            new() { Id = "emit", BlockTypeId = Emit.Id },
            new() { Id = "out", BlockTypeId = WorkflowGraphHelpers.WorkflowOutputBlockTypeId },
        ],
        Edges = [Wire("bin", "emit", ("Index", "Index")), Wire("emit", "out", [.. exits.Select(e => (e.Output, e.Key))])],
    };

    private static WorkflowDefinition Empty() => new()
    {
        Nodes = [new() { Id = "bin", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }],
        Edges = [],
    };

    private static WorkflowConnectionDefinition Wire(string source, string target, params (string From, string To)[] mappings) => new()
    {
        Id = $"{source}-{target}", SourceNodeId = source, TargetNodeId = target,
        Mappings = JsonSerializer.SerializeToElement(mappings.ToDictionary(m => m.To, m => new { type = "direct", sourceOutputName = m.From })),
    };

    private static WorkflowDefinition Root(WorkflowNodeDefinition loop, string sinkTakes, string? wireOutput)
    {
        var name = wireOutput ?? loop.OutputPorts![0].Name;
        return new WorkflowDefinition
        {
            Nodes =
            [
                new() { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                loop,
                new() { Id = "sink", BlockTypeId = Sink.Id },
            ],
            Edges = [Wire("in", "loop"), Wire("loop", "sink", (name, "Seen"))],
        };
    }

    private static RegisteredWorkflowBlocks Blocks(string sinkTakes = "Collection<Int?>")
    {
        var services = new ServiceCollection();
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory>(new Emit());
        services.AddSingleton<IWorkflowBlockFactory>(new Sink(sinkTakes));
        return new RegisteredWorkflowBlocks(services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>());
    }

    private static CompilationResult Compile(WorkflowNodeDefinition loop, string sinkTakes, string? wireOutput = null) =>
        WorkflowCompiler.Compile(Root(loop, sinkTakes, wireOutput), new CatalogueBlockShapeSource(Blocks(sinkTakes)), CanvasSignature.Empty);

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> Run(WorkflowNodeDefinition loop, string sinkTakes)
    {
        var definition = Root(loop, sinkTakes, null);
        var blocks = Blocks(sinkTakes);
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

    private sealed class Emit : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "test.emit";

        public string BlockTypeId => Id;
        public string DisplayName => "Emit";
        public string Description => "";
        public string Category => "test";

        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new BlockPortDefinition("Index", "Index", PortValueType.Int, false)];

        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
        [
            new BlockPortDefinition("V", "V", PortValueType.Int, false),
            new BlockPortDefinition("Label", "Label", PortValueType.String, false),
        ];

        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var index = Convert.ToInt64(inputs["Index"]);
            var written = new Dictionary<string, object?> { ["Label"] = $"n{index}" };
            if (index != 1)
                written["V"] = index * index;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(written);
        }
    }

    private sealed class HostEmit : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "test.hostemit";

        public string BlockTypeId => Id;
        public string DisplayName => "HostEmit";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new BlockPortDefinition("D", "D", PortValueType.Of("Depot"), false)];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["D"] = "depot" });
    }

    private sealed class Sink(string takes) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "test.sink";

        public string BlockTypeId => Id;
        public string DisplayName => "Sink";
        public string Description => "";
        public string Category => "test";

        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new BlockPortDefinition("Seen", "Seen", PortValueType.Of(takes), false)];

        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("Text", "Text", PortValueType.String, false)];

        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var items = ((System.Collections.IEnumerable)inputs["Seen"]!).Cast<object?>();
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["Text"] = string.Join(",", items.Select(i => i?.ToString() ?? "null")) });
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
