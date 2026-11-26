using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class InputBinderTests
{
    private static readonly PortValueType Int = PortValueType.Int;
    private static readonly PortValueType Number = PortValueType.Number;

    [Fact]
    public async Task An_Int_wire_an_Int_expr_result_and_an_integral_literal_reach_Number_inputs_as_doubles()
    {
        var seen = new Seen();
        var definition = Definition(
            Nodes(("src", "src", null), ("sink", "sink", new { Expr = "expr:${previous.src.V} + 1", Config = 7 })),
            Wire("src", "sink", ("V", "Wired")), Literal("in", "sink", "Literal", 6));

        await RunAsync(definition, Blocks(seen, Factory("src", [], [("V", Int)]),
            Factory("sink", [("Wired", Number, false), ("Expr", Number, false), ("Config", Number, false), ("Literal", Number, false)], [])));

        seen.Inputs["sink"]["Wired"].Should().BeOfType<double>().Which.Should().Be(5.0);
        seen.Inputs["sink"]["Expr"].Should().BeOfType<double>().Which.Should().Be(6.0);
        seen.Inputs["sink"]["Config"].Should().BeOfType<double>().Which.Should().Be(7.0);
        seen.Inputs["sink"]["Literal"].Should().BeOfType<double>().Which.Should().Be(6.0);
    }

    [Fact]
    public async Task A_collection_of_Int_into_a_collection_of_Number_is_rebuilt_element_by_element()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("src", "src", null), ("sink", "sink", null)), Wire("src", "sink", ("Values", "In")));

        await RunAsync(definition, Blocks(seen,
            Factory("src", [], [("Values", PortValueType.CollectionOf(Int))]),
            Factory("sink", [("In", PortValueType.CollectionOf(Number), true)], [])));

        seen.Inputs["sink"]["In"].Should().BeOfType<List<double>>().Which.Should().Equal(1.0, 2.0);
    }

    [Fact]
    public async Task A_block_reads_its_ports_closed_types_from_the_context()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("src", "src", null), ("sink", "sink", null)), Wire("src", "sink", ("Values", "In")));

        await RunAsync(definition, Blocks(seen,
            Factory("src", [], [("Values", PortValueType.CollectionOf(Int))]),
            Factory("sink", [("In", PortValueType.CollectionOf(Number), true)], [("Out", PortValueType.CollectionOf(Int))])));

        seen.Types["sink"]["In"].ClrType.Should().Be(typeof(IReadOnlyList<double>));
        seen.Types["sink"]["Out"].CreateList([3]).Should().BeOfType<List<long>>().Which.Should().Equal(3L);
    }

    [Fact]
    public async Task A_required_input_a_resolver_answered_stops_the_node_when_it_holds_no_value()
    {
        var seen = new Seen();
        var definition = Definition(
            Nodes(("src", "src", null), ("call", "call", new { WorkflowId = "child" })), Wire("src", "call", ("Maybe", "Count")));
        definition.Nodes.Single(n => n.Id == "call").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);
        var environment = new NodePortEnvironmentBuilder()
            .Add(CanvasPath.Root, "call", NodePortResolution.Resolved("child", new CanvasSignature([new PortShape("Count", Int, true)], [])))
            .Build();

        var outputs = await RunAsync(definition, Blocks(seen,
            Factory("src", [], [("Maybe", Int)]),
            Factory("call", [("WorkflowId", PortValueType.String, true)], [], resolvedElsewhere: true)), environment);

        seen.Inputs.Should().NotContainKey("call", "the node stops before its block runs");
        ((Dictionary<string, object?>)outputs["call"]["_error"]!)["code"].Should().Be("required-input-value-absent");
    }

    [Fact]
    public async Task A_value_off_its_producers_term_fails_the_producer_and_the_receiver_never_sees_it()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("bad", "bad", null), ("sink", "sink", null)), Wire("bad", "sink", ("V", "In")));
        definition.Nodes.Single(n => n.Id == "bad").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);

        var outputs = await RunAsync(definition, Blocks(seen,
            Factory("bad", [], [("V", Int)]), Factory("sink", [("In", Number, false)], [])));

        seen.Inputs["sink"].Should().NotContainKey("In", "the refused value never reaches a reader");
        var error = (Dictionary<string, object?>)outputs["bad"]["_error"]!;
        error["code"].Should().Be("block-output-type-mismatch");
        ((string)error["message"]!).Should().Contain("output 'V' of node 'bad'").And.Contain("String is not a value of 'Int'");
    }

    [Fact]
    public async Task A_default_output_reaches_its_reader_in_its_outputs_CLR_type()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("boom", "boom", null), ("sink", "sink", null)), Wire("boom", "sink", ("V", "In"), ("L", "List")));
        definition.Nodes.Single(n => n.Id == "boom").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault, """{"V":2,"L":[1,2]}""");

        await RunAsync(definition, Blocks(seen,
            Factory("boom", [], [("V", Int), ("L", PortValueType.CollectionOf(Int))]),
            Factory("sink", [("In", Int, true), ("List", PortValueType.CollectionOf(Int), true)], [])));

        seen.Inputs["sink"]["In"].Should().BeOfType<long>().Which.Should().Be(2L);
        seen.Inputs["sink"]["List"].Should().BeOfType<List<long>>().Which.Should().Equal(1L, 2L);
    }

    [Fact]
    public void A_value_off_its_term_on_a_root_output_fails_with_the_conversions_message()
    {
        var definition = Definition(Nodes(("src", "src", null), ("wo", WorkflowGraphHelpers.WorkflowOutputBlockTypeId, null)), Wire("src", "wo", ("V", "O")));
        var blocks = Blocks(new Seen(), Factory("src", [], [("V", Int)]));
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks),
            new CanvasSignature([], [new PortShape("O", PortValueType.Of("Json"), true)]));
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["src"] = new Dictionary<string, object?> { ["V"] = 1.5 } };

        FluentActions.Invoking(() => WorkflowExecutionHelpers.BuildResultOutputs(outputs, CompiledWalk.Root(result.Program!)))
            .Should().Throw<WorkflowNodeExecutionException>().WithMessage("Input 'O' of 'wo' from 'src': A Double is not a value of 'Int'.")
            .Where(e => e.NodeId == "wo").WithInnerException<PortConversionException>();
    }

    [Fact]
    public async Task A_present_null_wire_beats_a_configured_input()
    {
        var seen = new Seen();
        var definition = Definition(
            Nodes(("src", "src", null), ("sink", "sink", new { In = "configured" })),
            Wire("src", "sink", ("Null", "In")));

        await RunAsync(definition, Blocks(seen,
            Factory("src", [], [("Null", PortValueType.Of("String?"))]),
            Factory("sink", [("In", PortValueType.Of("String?"), false)], [])));

        seen.Inputs["sink"]["In"].Should().BeNull();
    }

    private sealed class Seen
    {
        public Dictionary<string, IReadOnlyDictionary<string, object?>> Inputs { get; } = [];
        public Dictionary<string, IReadOnlyDictionary<string, ClosedPortType>> Types { get; } = [];
    }

    private static (string Id, string Block, object? Config)[] Nodes(params (string Id, string Block, object? Config)[] nodes) => nodes;

    private static WorkflowConnectionDefinition Wire(string source, string target, params (string From, string To)[] mappings) => new()
    {
        Id = $"{source}-{target}", SourceNodeId = source, TargetNodeId = target,
        Mappings = JsonSerializer.SerializeToElement(mappings.ToDictionary(m => m.To, m => new { type = "direct", sourceOutputName = m.From })),
    };

    private static WorkflowConnectionDefinition Literal(string source, string target, string input, object value) => new()
    {
        Id = $"{source}-{target}-literal", SourceNodeId = source, TargetNodeId = target,
        Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [input] = new { type = "literal", literalValue = value } }),
    };

    private static WorkflowDefinition Definition((string Id, string Block, object? Config)[] nodes, params WorkflowConnectionDefinition[] edges)
    {
        var all = new List<WorkflowNodeDefinition> { new() { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId } };
        all.AddRange(nodes.Select(n => new WorkflowNodeDefinition
        {
            Id = n.Id, BlockTypeId = n.Block,
            Config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(n.Config ?? new { })),
        }));
        var wired = edges.ToList();
        foreach (var node in nodes.Where(n => wired.All(e => e.TargetNodeId != n.Id)))
            wired.Add(new WorkflowConnectionDefinition
            {
                Id = $"in-{node.Id}", SourceNodeId = "in", TargetNodeId = node.Id, Mappings = JsonSerializer.SerializeToElement(new { }),
            });
        return new WorkflowDefinition { Nodes = all, Edges = wired };
    }

    private static RegisteredWorkflowBlocks Blocks(Seen seen, params Func<Seen, IWorkflowBlockFactory>[] factories) =>
        new([.. factories.Select(f => f(seen))]);

    private static Func<Seen, IWorkflowBlockFactory> Factory(
        string id, (string Name, PortValueType Type, bool Required)[] inputs, (string Name, PortValueType Type)[] outputs,
        bool resolvedElsewhere = false) =>
        seen => new TestFactory(id, seen,
            [.. inputs.Select(i => new BlockPortDefinition(i.Name, i.Name, i.Type, i.Required))],
            [.. outputs.Select(o => new BlockPortDefinition(o.Name, o.Name, o.Type, false))], resolvedElsewhere);

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(
        WorkflowDefinition definition, RegisteredWorkflowBlocks blocks, NodePortEnvironment? environment = null)
    {
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty, environment: environment);
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

    private sealed class TestFactory(
        string id, Seen seen, BlockPortDefinition[] inputs, BlockPortDefinition[] outputs, bool resolvedElsewhere) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public bool PortsAreResolvedElsewhere => resolvedElsewhere;
        public string? RunsWorkflowFrom => resolvedElsewhere ? "WorkflowId" : null;
        public IWorkflowBlockPlugin Create() => new Recording(id, seen);
    }

    private sealed class Recording(string id, Seen seen) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            if (id == "boom")
                throw new InvalidOperationException("the block failed");
            seen.Inputs[context.NodeId!] = inputs;
            seen.Types[context.NodeId!] = context.Types;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(id switch
            {
                "src" => new Dictionary<string, object?> { ["V"] = 5L, ["Values"] = new List<long> { 1, 2 }, ["Null"] = null },
                "bad" => new Dictionary<string, object?> { ["V"] = "five" },
                _ => new Dictionary<string, object?>(),
            });
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
