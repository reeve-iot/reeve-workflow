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

public sealed class ObjectToTextTests
{
    private const string Payload = """{"obj": {"b": 1, "a": "x"}, "items": [1, 2], "name": "n"}""";

    [Fact]
    public void A_wire_from_a_Json_output_into_a_String_input_without_a_projection_does_not_compile()
    {
        var diagnostics = Compile(Graph("""{"Text":{"type":"direct","sourceOutputName":"Doc"}}""")).Diagnostics;

        diagnostics.Select(d => $"{d.RuleId}@{d.NodeId}.{d.PortName}").Should().Equal("json-projection-invalid@sink.Text");
    }

    [Theory]
    [InlineData("expr:${previous.src.Doc}")]
    [InlineData("expr:\"doc: \" + ${previous.src.Doc}")]
    [InlineData("expr:toString(${previous.src.Doc})")]
    public void An_expression_that_reads_a_Json_value_as_text_does_not_compile(string value)
    {
        var diagnostics = Compile(Graph("{}", JsonSerializer.Serialize(new { Text = value }))).Diagnostics;

        diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.ExpressionTypeMismatch && d.NodeId == "sink");
    }

    [Theory]
    [InlineData("$.obj")]
    [InlineData("$.items")]
    [InlineData("$")]
    public async Task An_object_or_an_array_projected_into_a_String_input_fails_the_receiver_with_narrowing_and_never_reaches_it_as_text(string path)
    {
        var (outputs, seen) = await RunAsync(Graph(Projection(path, mode: null), sinkPolicy: "EmitError"));

        seen.Should().NotContainKey("sink");
        var error = (Dictionary<string, object?>)outputs["sink"]["_error"]!;
        error["code"].Should().Be(WorkflowErrorCodes.JsonProjectionFailed);
        error["category"].Should().Be("narrowing");
    }

    [Theory]
    [InlineData("$.obj", """{"a":"x","b":1}""")]
    [InlineData("$.items", "[1,2]")]
    [InlineData("$.name", "\"n\"")]
    public async Task A_projection_in_jsonText_mode_is_the_explicit_route_to_a_nodes_JSON_text(string path, string text)
    {
        var (_, seen) = await RunAsync(Graph(Projection(path, mode: "jsonText")));

        seen["sink"]["Text"].Should().Be(text);
    }

    [Fact]
    public async Task JsonPathText_is_the_explicit_route_in_an_expression()
    {
        var config = JsonSerializer.Serialize(new { Text = "expr:jsonPathText(${previous.src.Doc}, \"$.obj\", \"String\")" });

        var (_, seen) = await RunAsync(Graph("{}", config));

        seen["sink"]["Text"].Should().Be("""{"a":"x","b":1}""");
    }

    [Theory]
    [InlineData("dictionary")]
    [InlineData("list")]
    [InlineData("json-object")]
    [InlineData("json-array")]
    public async Task A_block_that_writes_an_object_or_an_array_on_a_String_output_fails_and_the_String_input_it_feeds_never_receives_it(
        string kind)
    {
        object written = kind switch
        {
            "dictionary" => new Dictionary<string, object?> { ["a"] = 1L },
            "list" => new List<object?> { 1L, "two" },
            "json-object" => JsonDocument.Parse("""{"a":1}""").RootElement.Clone(),
            _ => JsonDocument.Parse("[1]").RootElement.Clone(),
        };

        var (outputs, seen) = await RunAsync(Graph("""{"Text":{"type":"direct","sourceOutputName":"Text"}}""", srcPolicy: "EmitError"), written);

        seen["sink"].Should().NotContainKey("Text");
        ((Dictionary<string, object?>)outputs["src"]["_error"]!)["code"].Should().Be("block-output-type-mismatch");
    }

    private static string Projection(string path, string? mode) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["Text"] = new Dictionary<string, object>
        {
            ["type"] = "direct",
            ["sourceOutputName"] = "Doc",
            ["projection"] = mode is null ? new Dictionary<string, object> { ["path"] = path } : new Dictionary<string, object> { ["path"] = path, ["mode"] = mode },
        },
    });

    private static string Graph(string mappings, string sinkConfig = "{}", string? srcPolicy = null, string? sinkPolicy = null) => $$$"""
        {"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
        {"id":"src","blockTypeId":"src","position":{"x":120,"y":0},"configuration":{}{{{Policy(srcPolicy)}}}},
        {"id":"sink","blockTypeId":"sink","position":{"x":240,"y":0},"configuration":{{{sinkConfig}}}{{{Policy(sinkPolicy)}}}}],
        "edges":[{"id":"e0","sourceNodeId":"in","targetNodeId":"src","mappings":{}},
        {"id":"e1","sourceNodeId":"src","targetNodeId":"sink","mappings":{{{mappings}}}}]}
        """;

    private static string Policy(string? policy) => policy is null ? "" : $$""","errorPolicy":{"policy":"{{policy}}"}""";

    private static WorkflowDefinition Read(string json) =>
        StoredWorkflowDocument.Read(json, out var fault)?.Definition ?? throw new InvalidOperationException(fault!.Describe());

    private static RegisteredWorkflowBlocks Blocks(Dictionary<string, IReadOnlyDictionary<string, object?>> seen, object? text) => new(
    [
        new TestFactory("src", seen, [],
            [new BlockPortDefinition("Doc", "Doc", PortValueType.Of("Json?"), false), new BlockPortDefinition("Text", "Text", PortValueType.String, false)],
            text),
        new TestFactory("sink", seen, [new BlockPortDefinition("Text", "Text", PortValueType.String, false)], [], null),
    ]);

    private static CompilationResult Compile(string json) =>
        WorkflowCompiler.Compile(Read(json), new CatalogueBlockShapeSource(Blocks([], null)), CanvasSignature.Empty);

    private static async Task<(Dictionary<string, IReadOnlyDictionary<string, object?>> Outputs, Dictionary<string, IReadOnlyDictionary<string, object?>> Seen)>
        RunAsync(string json, object? text = null)
    {
        var seen = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        var definition = Read(json);
        var blocks = Blocks(seen, text);
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
            NullHelperResolver.Instance, new WorkflowGraphExecutorOptions());
        var parameters = new Dictionary<string, object?>();
        var outputs = await executor.ExecuteGraphAsync(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["in"] = parameters },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new NoServices(), [], [], WorkflowTraceMode.Full,
            debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: parameters, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None, debugHook: null, debugCounter: null, stateExecutionId: null,
            mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(result.Program!));
        return (outputs, seen);
    }

    private sealed class TestFactory(
        string id, Dictionary<string, IReadOnlyDictionary<string, object?>> seen, BlockPortDefinition[] inputs, BlockPortDefinition[] outputs,
        object? text) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public IWorkflowBlockPlugin Create() => new Recording(id, seen, text);
    }

    private sealed class Recording(string id, Dictionary<string, IReadOnlyDictionary<string, object?>> seen, object? text) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            seen[id] = inputs;
            if (id != "src")
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
            var written = new Dictionary<string, object?> { ["Doc"] = JsonDocument.Parse(Payload).RootElement.Clone() };
            if (text is not null)
                written["Text"] = text;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(written);
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
