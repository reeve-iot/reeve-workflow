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

public sealed class OutputGateRunTests
{
    private static readonly PortValueType Int = PortValueType.Int;

    private static Block Producer(string id, (string Name, PortValueType Type)[] outputs, params (string Key, object? Value)[] writes) =>
        B(id, outputs: outputs, run: (_, _) => writes.ToDictionary(w => w.Key, w => w.Value));

    private static Block Sink(string id = "sink") => B(id, inputs: [("In", Int, true)]);

    [Fact]
    public async Task A_value_off_its_outputs_term_fails_the_producer_with_the_code_and_the_receiver_never_sees_it()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("bad", "bad", null), ("sink", "sink", null)), Wire("bad", "sink", ("V", "In")));
        definition.Nodes.Single(n => n.Id == "bad").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);

        var outputs = await RunAsync(definition, seen, Producer("bad", [("V", Int)], ("V", "five")),
            B("sink", inputs: [("In", Int, false)]));

        seen.Inputs["sink"].Should().NotContainKey("In", "the refused value never reaches a reader");
        var error = (Dictionary<string, object?>)outputs["bad"]["_error"]!;
        error["code"].Should().Be("block-output-type-mismatch");
        ((string)error["message"]!).Should().Contain("output 'V' of node 'bad'").And.Contain("String is not a value of 'Int'");
    }

    [Fact]
    public async Task Null_on_a_non_nullable_output_fails_the_producer_and_is_a_value_on_a_nullable_one()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("p", "p", null), ("q", "q", null)));
        definition.Nodes.Single(n => n.Id == "p").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);

        var outputs = await RunAsync(definition, seen,
            Producer("p", [("V", Int)], ("V", null)),
            Producer("q", [("V", PortValueType.Of("Int?"))], ("V", null)));

        ((Dictionary<string, object?>)outputs["p"]["_error"]!)["code"].Should().Be("block-output-type-mismatch");
        outputs["q"].Should().ContainKey("V").WhoseValue.Should().BeNull();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task A_non_finite_number_on_a_Number_output_fails_the_producer(double written)
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("p", "p", null)));
        definition.Nodes.Single(n => n.Id == "p").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);

        var outputs = await RunAsync(definition, seen, Producer("p", [("N", PortValueType.Number)], ("N", written)));

        var error = (Dictionary<string, object?>)outputs["p"]["_error"]!;
        error["code"].Should().Be("block-output-type-mismatch");
        ((string)error["message"]!).Should().Contain("output 'N' of node 'p'").And.Contain("non-finite number");
    }

    [Fact]
    public async Task A_JSON_null_element_on_a_Json_output_fails_the_producer()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("p", "p", null)));
        definition.Nodes.Single(n => n.Id == "p").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);
        using var nothing = JsonDocument.Parse("null");

        var outputs = await RunAsync(definition, seen, Producer("p", [("J", PortValueType.Json)], ("J", nothing.RootElement.Clone())));

        var error = (Dictionary<string, object?>)outputs["p"]["_error"]!;
        error["code"].Should().Be("block-output-type-mismatch");
        ((string)error["message"]!).Should().Contain("output 'J' of node 'p'");
    }

    [Fact]
    public async Task A_key_the_node_does_not_declare_is_dropped_and_an_int_on_Int_arrives_as_a_long()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("p", "p", null), ("sink", "sink", null)), Wire("p", "sink", ("V", "In")));

        var outputs = await RunAsync(definition, seen,
            Producer("p", [("V", Int)], ("V", 5), ("Junk", "x")),
            Sink());

        outputs["p"].Keys.Should().Equal("V");
        outputs["p"]["V"].Should().BeOfType<long>().Which.Should().Be(5L);
        seen.Inputs["sink"]["In"].Should().BeOfType<long>();
    }

    [Fact]
    public async Task An_output_the_compile_dropped_is_never_written()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("loop", "generic", null)));

        var outputs = await RunAsync(definition, seen, B("generic", outputs: [("Res", PortValueType.OfDeclaration("Collection<$R>"))],
            run: (_, _) => new Dictionary<string, object?> { ["Res"] = new List<object?>() }, typeParameters: [new WorkflowTypeParameter("R")]));

        outputs["loop"].Should().NotContainKey("Res", "nothing binds $R and nothing reads Res, so the compile dropped it");
    }

    [Fact]
    public async Task A_default_output_is_checked_like_any_output_and_reaches_its_reader_in_the_term()
    {
        var seen = new Seen();
        var definition = Definition(Nodes(("boom", "boom", null), ("sink", "sink", null)), Wire("boom", "sink", ("V", "In")));
        definition.Nodes.Single(n => n.Id == "boom").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault, """{"V":2}""");

        var outputs = await RunAsync(definition, seen,
            B("boom", outputs: [("V", Int)], run: (_, _) => throw new InvalidOperationException("the block failed")),
            Sink());

        outputs["boom"]["V"].Should().BeOfType<long>().Which.Should().Be(2L);
        seen.Inputs["sink"]["In"].Should().Be(2L);
    }

    [Fact]
    public void A_default_output_key_naming_no_output_is_wire_port_unknown()
    {
        var definition = Definition(Nodes(("boom", "boom", null)));
        definition.Nodes.Single(n => n.Id == "boom").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault, """{"V":2,"Nope":1}""");
        var blocks = Blocks(new Seen(), B("boom", outputs: [("V", Int)]));

        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);

        var unknown = result.Diagnostics.Where(d => d.RuleId == CompilationRules.WirePortUnknown).ToList();
        unknown.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d => d.NodeId == "boom" && d.PortName == "Nope");
    }

    [Fact]
    public async Task A_container_hands_its_body_an_int_on_Int_as_a_long()
    {
        var seen = new Seen();
        var definition = WithBody();

        await RunAsync(definition, seen, Container(("Index", 3), ("Name", "n")),
            B("probe", inputs: [("Index", Int, false), ("Name", PortValueType.String, false)]));

        seen.Inputs["probe"]["Index"].Should().BeOfType<long>().Which.Should().Be(3L);
        seen.Inputs["probe"]["Name"].Should().Be("n");
    }

    [Fact]
    public void A_key_the_body_does_not_declare_is_left_out_of_what_a_container_hands_it()
    {
        var definition = WithBody();
        var registered = Blocks(new Seen(), Container(), B("probe", inputs: [("Index", Int, false), ("Name", PortValueType.String, false)]));
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(registered), CanvasSignature.Empty);
        result.Diagnostics.Should().BeEmpty();
        var body = result.Program!.Canvases.Single(c => c.CanvasPath == CanvasPath.Root.Child("box", "Body"));

        var handed = ValueGate.SubCanvasInputs("box", "Body", body,
            new Dictionary<string, object?> { ["Index"] = 3, ["Name"] = "n", ["Junk"] = "x" });

        handed.Keys.Should().BeEquivalentTo(["Index", "Name"]);
        handed["Index"].Should().BeOfType<long>();
    }

    [Theory]
    [InlineData("Index", null, "null is not a value of 'Int'")]
    [InlineData("Name", 7, "Int32 is not a value of 'String'")]
    public async Task A_container_that_hands_null_or_a_wrong_type_to_a_body_input_fails_naming_the_sub_canvas_and_the_port(
        string key, object? value, string reason)
    {
        var seen = new Seen();
        var definition = WithBody();
        definition.Nodes.Single(n => n.Id == "box").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);

        var outputs = await RunAsync(definition, seen, Container((key, value)), B("probe", inputs: [("Index", Int, false), ("Name", PortValueType.String, false)]));

        var error = (Dictionary<string, object?>)outputs["box"]["_error"]!;
        error["code"].Should().Be("block-output-type-mismatch");
        ((string)error["message"]!).Should().Contain($"input '{key}' of sub-canvas 'Body' of node 'box'").And.Contain(reason);
        seen.Inputs.Should().NotContainKey("probe");
    }

    private static Block Container(params (string Key, object? Value)[] hands) =>
        B("container", run: (_, context) => context.SubCanvasExecutor!.ExecuteAsync(
                "Body", hands.ToDictionary(h => h.Key, h => h.Value), CancellationToken.None).GetAwaiter().GetResult(),
            subCanvasInputs: [("Index", Int), ("Name", PortValueType.String)]);

    private static WorkflowDefinition WithBody()
    {
        var body = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "bin", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "probe", BlockTypeId = "probe", Config = [] },
            ],
            Edges = [Wire("bin", "probe", ("Index", "Index"), ("Name", "Name"))],
        };
        var definition = Definition(Nodes(("box", "container", null)));
        definition.Nodes.Single(n => n.Id == "box").SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        return definition;
    }

    private sealed class Seen
    {
        public Dictionary<string, IReadOnlyDictionary<string, object?>> Inputs { get; } = [];
    }

    private static (string Id, string Block, object? Config)[] Nodes(params (string Id, string Block, object? Config)[] nodes) => nodes;

    private static WorkflowConnectionDefinition Wire(string source, string target, params (string From, string To)[] mappings) => new()
    {
        Id = $"{source}-{target}", SourceNodeId = source, TargetNodeId = target,
        Mappings = JsonSerializer.SerializeToElement(mappings.ToDictionary(m => m.To, m => new { type = "direct", sourceOutputName = m.From })),
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

    private static RegisteredWorkflowBlocks Blocks(Seen seen, params Block[] blocks) => new([.. blocks.Select(b => b.Factory(seen))]);

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(
        WorkflowDefinition definition, Seen seen, params Block[] blocks)
    {
        var registered = Blocks(seen, blocks);
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(registered), CanvasSignature.Empty);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var executor = new WorkflowGraphExecutor(
            registered, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
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

    private delegate IReadOnlyDictionary<string, object?> Run(IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context);

    private sealed class Block(
        string id, (string Name, PortValueType Type, bool Required)[] inputs, (string Name, PortValueType Type)[] outputs, Run? run,
        IReadOnlyList<WorkflowTypeParameter>? typeParameters, (string Name, PortValueType Type)[]? subCanvasInputs)
    {
        public IWorkflowBlockFactory Factory(Seen seen) => new TestFactory(
            id, seen, [.. inputs.Select(i => new BlockPortDefinition(i.Name, i.Name, i.Type, i.Required))],
            [.. outputs.Select(o => new BlockPortDefinition(o.Name, o.Name, o.Type, false))], typeParameters ?? [], run,
            [.. (subCanvasInputs ?? []).Select(i => new BlockPortDefinition(i.Name, i.Name, i.Type, false))]);
    }

    private static Block B(
        string id, (string Name, PortValueType Type, bool Required)[]? inputs = null, (string Name, PortValueType Type)[]? outputs = null,
        Run? run = null, IReadOnlyList<WorkflowTypeParameter>? typeParameters = null, (string Name, PortValueType Type)[]? subCanvasInputs = null) =>
        new(id, inputs ?? [], outputs ?? [], run, typeParameters, subCanvasInputs);

    private sealed class TestFactory(
        string id, Seen seen, BlockPortDefinition[] inputs, BlockPortDefinition[] outputs, IReadOnlyList<WorkflowTypeParameter> typeParameters,
        Run? run, BlockPortDefinition[] subCanvasInputs) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => typeParameters;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public IReadOnlyCollection<string> SubCanvasNames => subCanvasInputs.Length > 0 ? ["Body"] : [];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) => subCanvasInputs;
        public IWorkflowBlockPlugin Create() => new Recording(id, seen, run);
    }

    private sealed class Recording(
        string id, Seen seen, Run? run) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            seen.Inputs[id] = inputs;
            return Task.FromResult(run is null ? new Dictionary<string, object?>() : run(inputs, context));
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
