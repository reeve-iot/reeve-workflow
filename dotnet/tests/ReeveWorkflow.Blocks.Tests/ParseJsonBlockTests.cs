using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class ParseJsonBlockTests
{
    private static WorkflowBlockContext Context() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = NullLogger.Instance,
        Services = new NoServices(),
    };

    private static Task<IReadOnlyDictionary<string, object?>> Run(object? json) =>
        new ParseJsonBlock().ExecuteAsync(new Dictionary<string, object?> { ["Json"] = json }, Context(), CancellationToken.None);

    [Fact]
    public void ParseJson_takes_String_text_and_gives_a_nullable_Json_Value_and_a_Boolean_Success_with_no_Path()
    {
        var factory = new ParseJsonBlockFactory();

        factory.Inputs.Select(p => (p.Name, p.Type.Name, p.IsRequired)).Should().Equal(("Json", "String", true));
        factory.Outputs.Select(p => (p.Name, p.Type.Name)).Should().Equal(("Value", "Json?"), ("Success", "Boolean"));
        factory.ConfigurationSchema.Select(f => f.Name).Should().Equal("Json");
    }

    [Theory]
    [InlineData("""{"count": 3, "tags": ["a"]}""", JsonValueKind.Object, """{"count": 3, "tags": ["a"]}""")]
    [InlineData(" [1, 2] ", JsonValueKind.Array, "[1, 2]")]
    [InlineData("3", JsonValueKind.Number, "3")]
    [InlineData("\"s\"", JsonValueKind.String, "\"s\"")]
    [InlineData("true", JsonValueKind.True, "true")]
    public async Task ParseJson_writes_the_whole_document_as_a_JsonElement_and_Success_true(string text, JsonValueKind kind, string raw)
    {
        var result = await Run(text);

        result["Success"].Should().Be(true);
        var value = result["Value"].Should().BeOfType<JsonElement>().Subject;
        value.ValueKind.Should().Be(kind);
        value.GetRawText().Should().Be(raw);
    }

    [Fact]
    public async Task ParseJson_gives_a_null_Value_for_the_text_null()
    {
        var result = await Run("null");

        result["Success"].Should().Be(true);
        result.Should().ContainKey("Value").WhoseValue.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("1 2")]
    public async Task ParseJson_gives_Success_false_and_no_Value_key_for_text_that_is_not_JSON(string text)
    {
        var result = await Run(text);

        result["Success"].Should().Be(false);
        result.Should().NotContainKey("Value");
    }

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"outer":[{"k":true,"k":false}]}""")]
    [InlineData("""{"a":1,"\u0061":2}""")]
    public async Task ParseJson_gives_Success_false_and_no_value_for_text_that_repeats_a_member_name(string text)
    {
        var result = await Run(text);

        result["Success"].Should().Be(false);
        result.Should().NotContainKey("Value");
    }

    [Fact]
    public async Task ParseJson_gives_Success_false_for_text_with_an_unpaired_surrogate_instead_of_repairing_it()
    {
        var result = await Run("\"" + '\ud800' + "\"");

        result["Success"].Should().Be(false);
        result.Should().NotContainKey("Value");
    }

    [Fact]
    public async Task ParseJson_refuses_an_object_or_an_array_instead_of_writing_it_as_text()
    {
        await FluentActions.Invoking(() => Run(new Dictionary<string, object?> { ["a"] = 1L }))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Input 'Json' cannot be read as String: the value is an Object, not text.");
        await FluentActions.Invoking(() => Run(JsonSerializer.SerializeToElement(new[] { 1 })))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Input 'Json' cannot be read as String: the value is an Array, not text.");
        await FluentActions.Invoking(() => Run(true))
            .Should().ThrowAsync<ArgumentException>().WithMessage("Input 'Json' cannot be read as String: the value is a Boolean, not text.");
    }

    [Fact]
    public async Task ParseJson_fails_for_a_missing_Json()
    {
        await FluentActions.Invoking(() => new ParseJsonBlock().ExecuteAsync(new Dictionary<string, object?>(), Context(), CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*'Json'*required*");
    }

    [Fact]
    public void A_wire_from_Value_without_a_projection_or_a_mapping_into_Path_does_not_compile()
    {
        var blocks = CompiledGraph.Blocks(new Sink());
        var direct = CompiledGraph.Read(Graph("""{"Count":{"type":"direct","sourceOutputName":"Value"}}""", """{"Json":"{}"}"""));
        var path = CompiledGraph.Read(Graph(Projections, """{"Json":"{}"}""", """{"Path":{"type":"literal","literalValue":"$.count"}}"""));

        CompiledGraph.Refusals(direct, blocks).Should().Equal("json-projection-invalid@sink.Count");
        CompiledGraph.Refusals(path, blocks).Should().Equal("wire-port-unknown@parse.Path");
    }

    [Fact]
    public async Task On_a_graph_projection_wires_read_the_parsed_document_as_the_spec_example_does()
    {
        var blocks = CompiledGraph.Blocks(new Sink());
        var definition = CompiledGraph.Read(Graph(Projections, """{"Json":"{\"count\": 3, \"tags\": [\"a\"]}"}"""));

        var outputs = await CompiledGraph.RunAsync(definition, blocks);

        outputs["parse"]["Success"].Should().Be(true);
        outputs["sink"]["Count"].Should().Be(3.0);
        outputs["sink"]["Tags"].Should().BeAssignableTo<IReadOnlyList<string>>().Which.Should().Equal("a");
    }

    [Fact]
    public async Task On_a_graph_text_that_is_not_JSON_leaves_every_projection_wire_absent_and_the_run_going()
    {
        var blocks = CompiledGraph.Blocks(new Sink());
        var definition = CompiledGraph.Read(Graph(Projections, """{"Json":"not json"}"""));

        var outputs = await CompiledGraph.RunAsync(definition, blocks);

        outputs["parse"].Should().NotContainKey("Value");
        outputs["parse"]["Success"].Should().Be(false);
        outputs["sink"].Should().NotContainKey("Count").And.NotContainKey("Tags");
    }

    private const string Projections = """
        {"Count":{"type":"direct","sourceOutputName":"Value","projection":{"path":"$.count"}},
         "Tags":{"type":"direct","sourceOutputName":"Value","projection":{"path":"$.tags[*]"}}}
        """;

    private static string Graph(string mappings, string parseConfig, string parseMappings = "{}") => $$$"""
        {"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
        {"id":"parse","blockTypeId":"ParseJson","position":{"x":120,"y":0},"configuration":{{{parseConfig}}}},
        {"id":"sink","blockTypeId":"{{{Sink.Id}}}","position":{"x":240,"y":0},"configuration":{}}],
        "edges":[{"id":"e0","sourceNodeId":"in","targetNodeId":"parse","mappings":{{{parseMappings}}}},
        {"id":"e1","sourceNodeId":"parse","targetNodeId":"sink","mappings":{{{mappings}}}}]}
        """;

    private sealed class Sink : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "test.parse-json-sink";

        public string BlockTypeId => Id;
        public string DisplayName => "Sink";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [
            new BlockPortDefinition("Count", "Count", PortValueType.Number, false),
            new BlockPortDefinition("Tags", "Tags", PortValueType.Of("Collection<String>"), false),
        ];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
        [
            new BlockPortDefinition("Count", "Count", PortValueType.Number, false),
            new BlockPortDefinition("Tags", "Tags", PortValueType.Of("Collection<String>"), false),
        ];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(inputs.ToDictionary(p => p.Key, p => p.Value));
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
