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

public sealed class ProjectionBinderTests
{
    private const string Payload = """{"a": 1, "items": [1, 2], "name": "n"}""";

    [Fact]
    public async Task A_projection_delivers_its_node_in_the_inputs_CLR_type()
    {
        var seen = await RunAsync(("A", "Int", true, "$.a", null));

        seen["sink"]["A"].Should().BeOfType<long>().Which.Should().Be(1L);
    }

    [Fact]
    public async Task A_collection_projection_is_converted_to_the_inputs_term()
    {
        var seen = await RunAsync(("Values", "Collection<Number>", true, "$.items[*]", null));

        seen["sink"]["Values"].Should().BeOfType<List<double>>().Which.Should().Equal(1.0, 2.0);
    }

    [Fact]
    public async Task Nothing_found_for_a_nullable_input_leaves_it_absent()
    {
        var seen = await RunAsync(("B", "Int?", false, "$.b", null));

        seen["sink"].Should().NotContainKey("B");
    }

    [Fact]
    public async Task An_absent_source_key_is_absent_at_the_wire()
    {
        var seen = await RunAsync(("B", "Int?", false, "$", "Missing"));

        seen["sink"].Should().NotContainKey("B");
    }

    [Theory]
    [InlineData("$.b", "Int", "value-absent")]
    [InlineData("$.name", "Int", "narrowing")]
    public async Task A_failed_projection_fails_the_receiver_under_its_policy(string path, string term, string category)
    {
        var outputs = await RunWithOutputsAsync(policy: NodeErrorPolicy.EmitError, ("In", term, true, path, null));

        var error = (Dictionary<string, object?>)outputs["sink"]["_error"]!;
        error["code"].Should().Be(WorkflowErrorCodes.JsonProjectionFailed);
        error["category"].Should().Be(category);
        error["message"].Should().BeOfType<string>().Which.Should().Contain($"the path '{path}'")
            .And.NotContain(WorkflowErrorCodes.JsonProjectionFailed, "_error.code carries the code");
    }

    [Fact]
    public async Task A_projection_whose_path_multiplies_its_nodes_past_the_budget_fails_the_receiver_as_too_large()
    {
        var path = "$" + string.Concat(Enumerable.Repeat("[" + string.Join(",", Enumerable.Repeat("*", 200)) + "]", 2));

        var outputs = await RunWithOutputsAsync(policy: NodeErrorPolicy.EmitError, ("In", "Collection<Int>", true, path, null));

        var error = (Dictionary<string, object?>)outputs["sink"]["_error"]!;
        error["code"].Should().Be(WorkflowErrorCodes.JsonProjectionFailed);
        error["category"].Should().Be("too-large");
        error["message"].Should().BeOfType<string>().Which.Should().Contain($"the path '{path}' takes more than");
    }

    [Fact]
    public async Task The_source_keeps_its_Json_value_and_nothing_else_is_written()
    {
        var outputs = await RunWithOutputsAsync(policy: null, ("A", "Int", true, "$.a", null));

        outputs["src"].Keys.Should().BeEquivalentTo(["Doc"]);
        outputs.Keys.Should().BeEquivalentTo(["in", "src", "sink"]);
    }

    [Fact]
    public async Task A_replay_from_the_recorded_source_value_evaluates_the_projection_again()
    {
        var wire = ("A", "Int", true, "$.a", (string?)null);
        var fresh = await RunAsync(wire);
        var recorded = PersistedValueCodec.ForDisplay(
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["src"] = new Dictionary<string, object?> { ["Doc"] = JsonDocument.Parse(Payload).RootElement } }));
        var seen = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

        await RunCoreAsync(null, seen, [wire], recorded["src"]);

        seen.Should().NotContainKey("src", "a replay does not run the recorded source again");
        seen["sink"]["A"].Should().Be(1L).And.Be(fresh["sink"]["A"]);
    }

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(
        params (string Input, string Term, bool Required, string Path, string? From)[] wires)
    {
        var seen = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        await RunCoreAsync(null, seen, wires);
        return seen;
    }

    private static Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunWithOutputsAsync(
        NodeErrorPolicy? policy, params (string Input, string Term, bool Required, string Path, string? From)[] wires) =>
        RunCoreAsync(policy, [], wires);

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunCoreAsync(
        NodeErrorPolicy? policy, Dictionary<string, IReadOnlyDictionary<string, object?>> seen,
        (string Input, string Term, bool Required, string Path, string? From)[] wires,
        IReadOnlyDictionary<string, object?>? recordedSource = null)
    {
        var mappings = wires.ToDictionary(w => w.Input, w => (object)new
        {
            type = "direct", sourceOutputName = w.From ?? "Doc", projection = new { path = w.Path },
        });
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new() { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new() { Id = "src", BlockTypeId = "src" },
                new() { Id = "sink", BlockTypeId = "sink", ErrorPolicy = policy is { } p ? new NodeErrorConfig(p) : null },
            ],
            Edges =
            [
                new() { Id = "e0", SourceNodeId = "in", TargetNodeId = "src", Mappings = JsonSerializer.SerializeToElement(new { }) },
                new() { Id = "e1", SourceNodeId = "src", TargetNodeId = "sink", Mappings = JsonSerializer.SerializeToElement(mappings) },
            ],
        };
        var blocks = new RegisteredWorkflowBlocks(
        [
            new TestFactory("src", seen, [], [new BlockPortDefinition("Doc", "Doc", PortValueType.Of("Json?"), false),
                new BlockPortDefinition("Missing", "Missing", PortValueType.Of("Json?"), false)]),
            new TestFactory("sink", seen, [.. wires.Select(w => new BlockPortDefinition(w.Input, w.Input, PortValueType.Of(w.Term), w.Required))], []),
        ]);
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
            NullHelperResolver.Instance, new WorkflowGraphExecutorOptions());
        var parameters = new Dictionary<string, object?>();
        return await executor.ExecuteGraphAsync(
            recordedSource is null
                ? new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["in"] = parameters }
                : new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["in"] = parameters, ["src"] = recordedSource },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new NoServices(), [], [], WorkflowTraceMode.Full,
            debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: parameters, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None, debugHook: null, debugCounter: null, stateExecutionId: null,
            mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(result.Program));
    }

    private sealed class TestFactory(
        string id, Dictionary<string, IReadOnlyDictionary<string, object?>> seen, BlockPortDefinition[] inputs, BlockPortDefinition[] outputs)
        : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public IWorkflowBlockPlugin Create() => new Recording(id, seen);
    }

    private sealed class Recording(string id, Dictionary<string, IReadOnlyDictionary<string, object?>> seen) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            seen[context.NodeId!] = inputs;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(id == "src"
                ? new Dictionary<string, object?> { ["Doc"] = JsonDocument.Parse(Payload).RootElement.Clone() }
                : new Dictionary<string, object?>());
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
