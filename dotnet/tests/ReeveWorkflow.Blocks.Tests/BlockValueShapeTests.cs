using System.Collections;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class BlockValueShapeTests
{
    public enum Road { Wire }

    public sealed record Row(string Name, int N);

    private sealed record Shape(Func<object?> Make, bool Resumed = false);

    private const string ItemsJson = """[{"Name":"a","N":1},{"Name":"b","N":2}]""";

    private static readonly Dictionary<string, Shape> Collections = new()
    {
        ["records"] = new(() => new List<Row> { new("a", 1), new("b", 2) }),
        ["record array"] = new(() => new[] { new Row("a", 1), new Row("b", 2) }),
        ["dictionaries"] = new(() => new List<object?> { Map("a", 1), Map("b", 2) }),
        ["read-only dictionaries"] = new(() => new List<IReadOnlyDictionary<string, object?>>
            { new ReadOnlyDictionary<string, object?>(Map("a", 1)), new ReadOnlyDictionary<string, object?>(Map("b", 2)) }),
        ["expando objects"] = new(() => new List<object> { Expando("a", 1), Expando("b", 2) }),
        ["read-only maps"] = new(() => new List<object> { new ReadOnlyMap(Map("a", 1)), new ReadOnlyMap(Map("b", 2)) }),
        ["hashtables"] = new(() => new List<object> { new Hashtable { ["Name"] = "a", ["N"] = 1 }, new Hashtable { ["Name"] = "b", ["N"] = 2 } }),
        ["json array"] = new(() => Element(ItemsJson)),
        ["list of json objects"] = new(() => Element(ItemsJson).EnumerateArray().Select(e => e.Clone()).ToList()),
        ["dictionaries with json members"] = new(() => new List<object?>
        {
            new Dictionary<string, object?> { ["Name"] = Element("\"a\""), ["N"] = Element("1") },
            new Dictionary<string, object?> { ["Name"] = Element("\"b\""), ["N"] = Element("2") },
        }),
        ["resumed records"] = new(() => new List<Row> { new("a", 1), new("b", 2) }, Resumed: true),
        ["resumed json array"] = new(() => Element(ItemsJson), Resumed: true),
    };

    private static readonly Dictionary<string, (Shape Shape, string Message)> RefusedCollections = new()
    {
        ["text"] = (new(() => "ab"), "Input 'Collection' must be a collection, not text."),
        ["json text"] = (new(() => ItemsJson), "Input 'Collection' must be a collection, not text."),
        ["json string"] = (new(() => Element("\"ab\"")), "Input 'Collection' must be a collection, not text."),
        ["object"] = (new(() => Map("a", 1)), "Input 'Collection' must be a collection, not an object."),
        ["json object"] = (new(() => Element("""{"Name":"a","N":1}""")), "Input 'Collection' must be a collection, not an object."),
        ["expando object"] = (new(() => Expando("a", 1)), "Input 'Collection' must be a collection, not an object."),
        ["read-only map"] = (new(() => new ReadOnlyMap(Map("a", 1))), "Input 'Collection' must be a collection, not an object."),
        ["resumed record"] = (new(() => new Row("a", 1), Resumed: true), "Input 'Collection' must be a collection, not an object."),
        ["record"] = (new(() => new Row("a", 1)), "Input 'Collection' is required and must be an IEnumerable."),
        ["number"] = (new(() => 5), "Input 'Collection' is required and must be an IEnumerable."),
    };

    private static readonly Dictionary<string, Shape> HeaderShapes = new()
    {
        ["string map"] = new(() => new Dictionary<string, string> { ["X-Key"] = "k", ["X-Num"] = "5" }),
        ["read-only string map"] = new(() => new ReadOnlyDictionary<string, string>(new Dictionary<string, string> { ["X-Key"] = "k", ["X-Num"] = "5" })),
    };

    private const string NotAMap = "Input 'Headers' must be a map of field names to values.";

    private static readonly Dictionary<string, (Shape Shape, string Message)> RefusedHeaders = new()
    {
        ["object map"] = (new(() => new Dictionary<string, object?> { ["X-Key"] = "k" }), NotAMap),
        ["json object"] = (new(() => Element("""{"X-Key":"k"}""")), NotAMap),
        ["json text"] = (new(() => """{"X-Key":"k"}"""), NotAMap),
        ["json array"] = (new(() => Element("""["X-Key: k"]""")), NotAMap),
        ["list of text"] = (new(() => new List<string> { "X-Key: k" }), NotAMap),
        ["record"] = (new(() => new Row("a", 1)), NotAMap),
        ["key that is no token"] = (new(() => new Dictionary<string, string> { ["X-Key"] = "k", ["X Key"] = "k" }),
            "Input 'Headers' key 'X...' is not a field name; a name is a token, and its character 2 is not a token character."),
        ["transport field"] = (new(() => new Dictionary<string, string> { ["Transfer-Encoding"] = "gzip" }),
            "Input 'Headers' key 'Transfer-Encoding' is a field the block takes from the Url, the Body or the connection, so an entry cannot set it."),
        ["value beyond ASCII"] = (new(() => new Dictionary<string, string> { ["X-Key"] = "k\u00a0" + "l" }),
            "Input 'Headers' key 'X-Key' has a value with a character other than visible ASCII, a space or a tab, which a field value cannot hold."),
    };

    public static TheoryData<string, Road> CollectionCases => Cross(Collections.Keys);
    public static TheoryData<string, Road> RefusedCollectionCases => Cross(RefusedCollections.Keys);
    public static TheoryData<string, Road> HeaderCases => Cross(HeaderShapes.Keys);
    public static TheoryData<string, Road> RefusedHeaderCases => Cross(RefusedHeaders.Keys);

    [Theory]
    [MemberData(nameof(CollectionCases))]
    public async Task IterateOver_hands_the_body_each_item_whatever_shape_the_collection_arrives_in(string shape, Road road)
    {
        var probe = new Probe();
        var target = new WorkflowNodeDefinition
        {
            Id = "target", BlockTypeId = "IterateOver",
            SubCanvases = new() { ["Body"] = Body() },
        };

        var outputs = await Run(target, "Collection", road, Collections[shape], probe);

        outputs["target"]["Results"].Should().BeOfType<List<string>>().Which.Should().BeEmpty("the body writes no Result");
        probe.Items.Select(Canonical).Should().Equal(["""{"N":1,"Name":"a"}""", """{"N":2,"Name":"b"}"""]);
    }

    [Theory]
    [MemberData(nameof(RefusedCollectionCases))]
    public async Task IterateOver_fails_the_node_naming_Collection_for_a_value_that_is_not_a_collection(string shape, Road road)
    {
        var probe = new Probe();
        var target = new WorkflowNodeDefinition
        {
            Id = "target", BlockTypeId = "IterateOver",
            SubCanvases = new() { ["Body"] = Body() },
        };
        var (value, message) = RefusedCollections[shape];

        var run = () => Run(target, "Collection", road, value, probe);

        (await run.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which.Should()
            .Match<WorkflowNodeExecutionException>(e => e.NodeId == "target" && e.Message == message);
        probe.Items.Should().BeEmpty("a refused collection runs no body at all");
    }

    [Theory]
    [MemberData(nameof(HeaderCases))]
    public async Task HttpRequest_sends_the_same_headers_from_any_map_of_text(string shape, Road road)
    {
        var probe = new Probe();
        var target = new WorkflowNodeDefinition
        {
            Id = "target", BlockTypeId = "HttpRequest",
            Config = new() { ["Url"] = Json("http://headers.test/") },
        };

        await Run(target, "Headers", road, HeaderShapes[shape], probe);

        probe.Sent.Should().ContainSingle().Which.Should().Equal(new Dictionary<string, string> { ["X-Key"] = "k", ["X-Num"] = "5" });
    }

    [Theory]
    [MemberData(nameof(RefusedHeaderCases))]
    public async Task HttpRequest_fails_the_node_naming_Headers_for_headers_it_cannot_send(string shape, Road road)
    {
        var probe = new Probe();
        var target = new WorkflowNodeDefinition
        {
            Id = "target", BlockTypeId = "HttpRequest",
            Config = new() { ["Url"] = Json("http://headers.test/") },
        };
        var (value, message) = RefusedHeaders[shape];

        var run = () => Run(target, "Headers", road, value, probe);

        (await run.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which.Should()
            .Match<WorkflowNodeExecutionException>(e => e.NodeId == "target" && e.Message == message);
        probe.Sent.Should().BeEmpty("a request whose headers are refused is never sent");
    }

    private static TheoryData<string, Road> Cross(IEnumerable<string> shapes)
    {
        var data = new TheoryData<string, Road>();
        foreach (var shape in shapes)
        foreach (var road in Enum.GetValues<Road>())
            data.Add(shape, road);
        return data;
    }

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> Run(
        WorkflowNodeDefinition target, string input, Road road, Shape shape, Probe probe)
    {
        var value = shape.Make();
        var executed = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        if (shape.Resumed)
        {
            var recorded = JsonSerializer.Serialize(new { src = new { v = value } }, WorkflowGraphHelpers.JsonOptions);
            executed["src"] = PersistedValueCodec.ForDisplay(recorded)["src"];
        }

        var edges = new List<WorkflowConnectionDefinition>
        {
            new() { Id = "e1", SourceNodeId = "start", TargetNodeId = "src" },
            new()
            {
                Id = "e2", SourceNodeId = "src", TargetNodeId = "target",
                Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [input] = new { type = "direct", sourceOutputName = "v" } }),
            },
        };

        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "src", BlockTypeId = SourceFactory.Id },
                target,
            ],
            Edges = edges,
        };
        var blocks = new UncheckedWorkflowBlocks(
        [
            new SourceFactory(value), new ProbeFactory(probe),
            new IterateOverBlockFactory(), new CapturingHttpRequestBlockFactory(probe),
        ]);
        var executor = new WorkflowGraphExecutor(blocks, NullLoggerFactory.Instance, new NullWorkflowMetrics(),
            new NullWorkflowNodeObserver(), NullHelperResolver.Instance);

        return await executor.ExecuteCompiledAsync(TestPrograms.Walk(definition, blocks, nodeTypes: new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["target"] = new Dictionary<string, string> { ["Results"] = "Collection<String>" },
            }),
            executed, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
            [], [], WorkflowTraceMode.Full, false, null, null, depth: 0, visitedNodeIds: [],
            isInsideTryCatch: false, inputParametersForRun: null, triggerIdForRun: null,
            triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None);
    }

    private static WorkflowDefinition Body() => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "in", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            new WorkflowNodeDefinition { Id = "probe", BlockTypeId = ProbeFactory.Id },
        ],
        Edges =
        [
            new WorkflowConnectionDefinition
            {
                Id = "e", SourceNodeId = "in", TargetNodeId = "probe",
                Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["Item"] = new { type = "direct", sourceOutputName = "Item" } }),
            },
        ],
    };

    private static string Canonical(object? value)
    {
        var node = JsonSerializer.SerializeToNode(value, WorkflowGraphHelpers.JsonOptions);
        return node is JsonObject o
            ? new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))).ToJsonString()
            : node?.ToJsonString() ?? "null";
    }

    private static Dictionary<string, object?> Map(string name, int n) => new() { ["Name"] = name, ["N"] = n };

    private static IDictionary<string, object?> Expando(string name, int n)
    {
        IDictionary<string, object?> expando = new ExpandoObject();
        expando["Name"] = name;
        expando["N"] = n;
        return expando;
    }

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class ReadOnlyMap(Dictionary<string, object?> entries) : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => entries[key];
        public IEnumerable<string> Keys => entries.Keys;
        public IEnumerable<object?> Values => entries.Values;
        public int Count => entries.Count;
        public bool ContainsKey(string key) => entries.ContainsKey(key);
        public bool TryGetValue(string key, out object? value) => entries.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Probe
    {
        public List<object?> Items { get; } = [];
        public List<Dictionary<string, string>> Sent { get; } = [];
    }

    private sealed class SourceFactory(object? value) : IWorkflowBlockFactory
    {
        public const string Id = "test.src";
        public string BlockTypeId => Id;
        public string DisplayName => "Source";
        public string Description => "Outputs the value under test.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new BlockPortDefinition("v", "v", PortValueType.Json, false)];
        public IWorkflowBlockPlugin Create() => new Block(value);

        private sealed class Block(object? value) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(IReadOnlyDictionary<string, object?> inputs,
                WorkflowBlockContext context, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["v"] = value });
        }
    }

    private sealed class ProbeFactory(Probe probe) : IWorkflowBlockFactory
    {
        public const string Id = "test.item-probe";
        public string BlockTypeId => Id;
        public string DisplayName => "Item probe";
        public string Description => "Records the Item it was handed.";
        public string Category => "test";
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T")];
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [new BlockPortDefinition("Item", "Item", PortValueType.OfDeclaration("$T"), false)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new Block(probe);

        private sealed class Block(Probe probe) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(IReadOnlyDictionary<string, object?> inputs,
                WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                probe.Items.Add(inputs.GetValueOrDefault("Item"));
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
            }
        }
    }

    private sealed class CapturingHttpRequestBlockFactory(Probe probe) : HttpRequestBlockFactory
    {
        public override IWorkflowBlockPlugin Create() => new Block(probe);

        private sealed class Block(Probe probe) : HttpRequestBlock
        {
            protected override HttpClientLease ResolveHttpClient(WorkflowBlockContext context) =>
                new(new HttpClient(new Capture(probe)), CallerOwnsClient: true);
        }

        private sealed class Capture(Probe probe) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                probe.Sent.Add(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
            }
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
