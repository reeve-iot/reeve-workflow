using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ExpressionValueDeliveryTests
{
    public enum Form { Whole, Ternary, Concat, ToText }

    public enum Namespace { Previous, Parameter, Trigger }

    public enum Road { Configuration, Literal }

    public sealed record Kind(
        string Name, Func<object?> Make, bool Composite, object? Whole = null, string? Concat = null, string? ToText = null,
        bool Unrecordable = false)
    {
        public override string ToString() => Name;
    }

    private static readonly DateTimeOffset Instant = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    public static readonly Kind[] Kinds =
    [
        new("null", () => null, false, null, null, ""),
        new("string", () => "hi", false, "hi", "hi", "hi"),
        new("bool", () => true, false, true, "true", "true"),
        new("int", () => 5, false, 5d, "5", "5"),
        new("long", () => 42L, false, 42d, "42", "42"),
        new("double", () => 1.5, false, 1.5, "1.5", "1.5"),
        new("decimal", () => 2.25m, false, 2.25, "2.25", "2.25"),
        new("float", () => 0.5f, false, 0.5, "0.5", "0.5"),
        new("byte", () => (byte)7, false, 7d, "7", "7"),
        new("int128", () => (Int128)5, false, 5d, "5", "5"),
        new("datetimeoffset", () => Instant, false, Instant, "2026-09-26T10:00:00.0000000+00:00", "2026-09-26T10:00:00.0000000+00:00"),
        new("datetime", () => Instant.UtcDateTime, false, Instant, "2026-09-26T10:00:00.0000000+00:00", "2026-09-26T10:00:00.0000000+00:00"),
        new("guid", () => Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), false,
            "0f8fad5b-d9cb-469f-a165-70867728950e", "0f8fad5b-d9cb-469f-a165-70867728950e", "0f8fad5b-d9cb-469f-a165-70867728950e"),
        new("enum", () => DayOfWeek.Friday, false, "Friday", "Friday", "Friday"),
        new("timespan", () => TimeSpan.FromMinutes(5), false, "00:05:00", "00:05:00", "00:05:00"),
        new("char", () => 'x', false, "x", "x", "x"),
        new("bytes", () => new byte[] { 1, 2, 3 }, false, "AQID", "AQID", "AQID"),
        new("json-string", () => Element("\"s\""), false, "s", "s", "s"),
        new("json-number", () => Element("3"), false, 3d, "3", "3"),
        new("json-null", () => Element("null"), false, null, null, ""),
        new("object", () => new Dictionary<string, object?> { ["b"] = 1, ["a"] = "x", ["n"] = new Dictionary<string, object?> { ["z"] = true, ["y"] = null } }, true),
        new("empty-object", () => new Dictionary<string, object?>(), true),
        new("array", () => new List<object?> { 1, "two", null, new Dictionary<string, object?> { ["k"] = 1.5 } }, true),
        new("empty-array", () => new List<object?>(), true),
        new("int-array", () => new[] { 1, 2 }, true),
        new("strings", () => new List<string> { "a", "\"q\"", "ł<>&" }, true),
        new("instants", () => new List<DateTimeOffset> { Instant }, true),
        new("bools", () => new List<bool> { true, false }, true),
        new("doubles", () => new List<double> { 1.5, 2 }, true),
        new("decimals", () => new List<decimal> { 2.50m, 10m }, true),
        new("big-integers", () => new List<long> { 9007199254740993L, long.MinValue }, true),
        new("enums", () => new Dictionary<string, DayOfWeek> { ["d"] = DayOfWeek.Friday }, true),
        new("objects", () => new List<Dictionary<string, object?>> { new() { ["k"] = "v" } }, true),
        new("typed-map", () => new Dictionary<string, int> { ["two"] = 2, ["one"] = 1 }, true),
        new("json-object", () => Element("""{"b":[1,2.50,{"d":null}],"a":"x"}"""), true),
        new("json-array", () => Element("""[{"b":1,"a":2},"s"]"""), true),
        new("record", () => new Point(1, 2), true),
        new("class", () => new Box { Label = "l", Items = [1, 2] }, true),
        new("unrecordable", () => new Dictionary<string, object?> { ["x"] = double.NaN }, true, Unrecordable: true),
        new("unrecordable-record", () => new Reading(double.NaN), true, Unrecordable: true),
        new("ip-address", () => IPAddress.Parse("10.0.0.1"), true, Unrecordable: true),
        new("ip-address-in-map", () => new Dictionary<string, object?> { ["ip"] = IPAddress.Parse("10.0.0.1") }, true, Unrecordable: true),
        new("cycle", () => new Loop(), true, Unrecordable: true),
        new("cycle-in-map", () => { var map = new Dictionary<string, object?>(); map["self"] = map; return map; }, true, Unrecordable: true),
        new("type", () => typeof(int), true, Unrecordable: true),
        new("type-in-map", () => new Dictionary<string, object?> { ["t"] = typeof(int) }, true, Unrecordable: true),
        new("getter-throws-null-reference", () => new Throwing(() => new NullReferenceException()), true, Unrecordable: true),
        new("getter-throws-invalid-operation", () => new Throwing(() => new InvalidOperationException("state")), true, Unrecordable: true),
        new("getter-throws-its-own-in-map", () => new Dictionary<string, object?> { ["d"] = new Throwing(() => new DomainException()) }, true,
            Unrecordable: true),
    ];

    public sealed record Target(string Name, string BlockTypeId, bool ReadsText);

    public static readonly Target[] Targets =
    [
        new("String", "sink-String", true),
        new("Collection", "sink-Collection", false),
        new("Number", "sink-Number", false),
        new("DynamicString", "sink-Dynamic", true),
        new("DynamicJson", "sink-Dynamic", false),
        new("DeclaredNumberDynamicString", "sink-DeclaredAndDynamic", false),
    ];

    [Theory]
    [InlineData(Namespace.Parameter, Road.Configuration)]
    [InlineData(Namespace.Parameter, Road.Literal)]
    [InlineData(Namespace.Trigger, Road.Configuration)]
    [InlineData(Namespace.Trigger, Road.Literal)]
    public void The_compiler_refuses_exactly_the_expressions_that_read_json(Namespace ns, Road road)
    {
        var blocks = new Blocks(null);
        var options = new CompilationOptions { NamespaceRegistrations = NamespaceRegistrations(ns).ToArray() };
        var all = WorkflowCompiler.Compile(Document(ns, road), new CatalogueBlockShapeSource(blocks.Catalogue), Signature(ns), options).Diagnostics;
        var declared = all.Where(d => d.RuleId == CompilationRules.PortTypeJsonPosition).ToList();
        declared.Should().OnlyContain(d => d.Message.Contains("holds Json"));
        var diagnostics = all.Except(declared).ToList();

        var described = string.Join("; ", diagnostics.Select(d => d.RuleId + ": " + d.Message));
        diagnostics.Where(d => d.RuleId != CompilationRules.ExpressionTypeMismatch).Should().BeEmpty(described);
        if (ns == Namespace.Parameter)
            declared.Should().Contain(d => d.PortName == "p");
        IEnumerable<string> expressionNodes = ns != Namespace.Trigger
            ? []
            : Enum.GetValues<Form>().SelectMany(form => Targets.Select(target => Sink(form, target))).Order(StringComparer.Ordinal);
        diagnostics.Select(d => d.NodeId!).Distinct().Order(StringComparer.Ordinal).Should().Equal(expressionNodes, described);
    }

    [Fact]
    public async Task The_error_output_reaches_a_wire_through_a_projection_and_no_expression_result()
    {
        var blocks = new Blocks(null) { Fails = true };
        var json = """
            {"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"src","blockTypeId":"src","position":{"x":120,"y":0},"configuration":{},"errorPolicy":{"policy":"EmitError"}},
            {"id":"w","blockTypeId":"sink-String","position":{"x":600,"y":0},"configuration":{}}],
            "edges":[{"id":"e0","sourceNodeId":"in","targetNodeId":"src","mappings":{}},
            {"id":"e4","sourceNodeId":"src","targetNodeId":"w","mappings":{"e":{"type":"direct","sourceOutputName":"_error","projection":{"path":"$.code"}}}}]}
            """;
        var definition = StoredWorkflowDocument.Read(json, out var fault)?.Definition ?? throw new InvalidOperationException(fault!.Describe());
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks.Catalogue), CanvasSignature.Empty);
        result.Diagnostics.Should().BeEmpty();

        await RunAsync(definition, blocks, Namespace.Previous, null, CompiledWalk.Root(result.Program!));

        blocks.Seen["w"].Should().Be(WorkflowErrorCodes.BlockFailed);
    }

    [Fact]
    public void The_error_output_read_in_an_expression_is_a_json_value_the_typer_refuses()
    {
        var blocks = new Blocks(null) { Fails = true };
        var json = """
            {"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"src","blockTypeId":"src","position":{"x":120,"y":0},"configuration":{},"errorPolicy":{"policy":"EmitError"}},
            {"id":"s","blockTypeId":"sink-String","position":{"x":360,"y":0},"configuration":{"e":"expr:\"failed: \" + ${previous.src._error}"}}],
            "edges":[{"id":"e0","sourceNodeId":"in","targetNodeId":"src","mappings":{}},{"id":"e2","sourceNodeId":"src","targetNodeId":"s","mappings":{}}]}
            """;
        var definition = StoredWorkflowDocument.Read(json, out var fault)?.Definition ?? throw new InvalidOperationException(fault!.Describe());

        WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks.Catalogue), CanvasSignature.Empty)
            .Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
    }

    [Theory]
    [InlineData("datetimeoffset", "2026-09-26T10:00:00+00:00")]
    [InlineData("enum", "Friday")]
    [InlineData("guid", "0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("bytes", "AQID")]
    [InlineData("int", 5L)]
    [InlineData("decimal", 2.25)]
    [InlineData("bool", true)]
    public void A_scalar_payload_field_is_the_JSON_scalar_it_is_written_as(string kind, object expected)
    {
        var made = Kinds.Single(k => k.Name == kind).Make()!;

        WorkflowGraphHelpers.JsonToValue(ExpressionJsonValue.Recorded(made)).Should().Be(expected);
    }

    [Fact]
    public void The_canonical_text_sorts_keys_writes_no_whitespace_and_escapes_only_what_json_requires()
    {
        Canonical(Kinds.Single(k => k.Name == "object").Make()!).Should().Be("""{"a":"x","b":1,"n":{"y":null,"z":true}}""");
        Canonical(Kinds.Single(k => k.Name == "json-object").Make()!).Should().Be("""{"a":"x","b":[1,2.5,{"d":null}]}""");
        Canonical(Kinds.Single(k => k.Name == "strings").Make()!).Should().Be("""["a","\"q\"","ł<>&"]""");
        Canonical(Kinds.Single(k => k.Name == "class").Make()!).Should().Be("""{"Items":[1,2],"Label":"l"}""");
    }

    private static string Canonical(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), WorkflowGraphHelpers.JsonOptions);
        return Sorted(node)?.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Sorted(x?.DeepClone())).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
            v.TryGetValue<long>(out var integer) ? JsonValue.Create(integer) : JsonValue.Create(v.GetValue<double>()),
        _ => node?.DeepClone(),
    };

    private static string Source(Namespace ns) => ns switch
    {
        Namespace.Previous => "${previous.src.v}",
        Namespace.Parameter => "${parameter.p}",
        _ => "${trigger.t}",
    };

    private static string Expression(Form form, Namespace ns) => "expr:" + form switch
    {
        Form.Whole => Source(ns),
        Form.Ternary => $"true ? {Source(ns)} : null",
        Form.Concat => $"\"<\" + {Source(ns)} + \">\"",
        _ => $"toString({Source(ns)})",
    };

    private static string Sink(Form form, Target target) => $"x-{form}-{target.Name}";

    private static string Wire(Target target) => $"w-{target.Name}";

    private static string DynamicPorts(Target target) => target.Name switch
    {
        "DynamicString" => """[{"name":"e","type":"String"}]""",
        "DynamicJson" => """[{"name":"e","type":"Json"}]""",
        "DeclaredNumberDynamicString" => """[{"name":"e","type":"String"}]""",
        _ => "",
    };

    private static WorkflowDefinition Document(Namespace ns, Road road, string failingSinkPolicy = "Skip")
    {
        var nodes = new JsonArray
        {
            new JsonObject { ["id"] = "in", ["blockTypeId"] = "WorkflowInput", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() },
            new JsonObject { ["id"] = "src", ["blockTypeId"] = "src", ["position"] = new JsonObject { ["x"] = 120, ["y"] = 0 }, ["configuration"] = new JsonObject() },
        };
        var edges = new JsonArray { Edge("in", "src", null) };
        nodes.Add(new JsonObject { ["id"] = PlainReader, ["blockTypeId"] = "sink-String", ["position"] = new JsonObject { ["x"] = 240, ["y"] = 0 }, ["configuration"] = new JsonObject { ["e"] = "expr:${previous.src.s}" } });
        edges.Add(Edge("src", PlainReader, null));
        foreach (var target in Targets)
        {
            foreach (var form in Enum.GetValues<Form>())
            {
                var configuration = new JsonObject();
                if (DynamicPorts(target) is { Length: > 0 } ports)
                    configuration["InputPorts"] = ports;
                JsonObject? mappings = null;
                if (road == Road.Configuration)
                    configuration["e"] = Expression(form, ns);
                else
                    mappings = new JsonObject { ["e"] = new JsonObject { ["type"] = "literal", ["literalValue"] = Expression(form, ns) } };
                var sink = new JsonObject { ["id"] = Sink(form, target), ["blockTypeId"] = target.BlockTypeId, ["position"] = new JsonObject { ["x"] = 360, ["y"] = 0 }, ["configuration"] = configuration };
                sink["errorPolicy"] = new JsonObject { ["policy"] = failingSinkPolicy };
                nodes.Add(sink);
                edges.Add(Edge("src", Sink(form, target), mappings));
            }

            if (ns == Namespace.Trigger)
                continue;
            var twin = new JsonObject();
            if (DynamicPorts(target) is { Length: > 0 } twinPorts)
                twin["InputPorts"] = twinPorts;
            nodes.Add(new JsonObject { ["id"] = Wire(target), ["blockTypeId"] = target.BlockTypeId, ["position"] = new JsonObject { ["x"] = 480, ["y"] = 0 }, ["configuration"] = twin });
            var (from, output) = ns == Namespace.Previous ? ("src", "v") : ("in", WireParameter(target));
            edges.Add(Edge(from, Wire(target), new JsonObject { ["e"] = new JsonObject { ["type"] = "direct", ["sourceOutputName"] = output } }));
        }

        var json = new JsonObject { ["nodes"] = nodes, ["edges"] = edges }.ToJsonString();
        return StoredWorkflowDocument.Read(json, out var fault)?.Definition ?? throw new InvalidOperationException(fault!.Describe());
    }

    private static int edgeCount;

    private static JsonObject Edge(string from, string to, JsonObject? mappings) => new()
    {
        ["id"] = $"e{Interlocked.Increment(ref edgeCount)}",
        ["sourceNodeId"] = from,
        ["targetNodeId"] = to,
        ["mappings"] = mappings ?? new JsonObject(),
    };

    private static string WireParameter(Target target) => "p_" + target.Name;

    private static PortValueType WireParameterType(Target target) => target.Name switch
    {
        "String" or "DynamicString" => PortValueType.String,
        "Number" or "DeclaredNumberDynamicString" => PortValueType.Number,
        "Collection" => PortValueType.CollectionOf(PortValueType.String),
        _ => PortValueType.Json,
    };

    private static CanvasSignature Signature(Namespace ns) => ns == Namespace.Parameter
        ? new CanvasSignature(
            [new PortShape("p", PortValueType.Json, false), .. Targets.Select(t => new PortShape(WireParameter(t), WireParameterType(t), false))], [])
        : CanvasSignature.Empty;

    private static IEnumerable<WorkflowExpressionNamespace> NamespaceRegistrations(Namespace ns)
    {
        if (ns == Namespace.Trigger)
            yield return new WorkflowExpressionNamespace("trigger", 1, new TriggerNamespace(),
                (scope, _) => Task.FromResult<IExpressionNamespaceResolver>(new TriggerResolver(scope.TriggerEnvelope)));
    }

    private static async Task RunAsync(WorkflowDefinition definition, Blocks blocks, Namespace ns, object? value, CompiledWalk compiled)
    {
        var executorOptions = new WorkflowGraphExecutorOptions { ExpressionNamespaces = NamespaceRegistrations(ns).ToList() };
        var executor = new WorkflowGraphExecutor(
            blocks.Catalogue, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance, executorOptions);
        var parameters = Parameters(ns, value);
        var trigger = Trigger(ns, value);
        await executor.ExecuteCompiledAsync(compiled,
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["in"] = parameters },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NoServices.Instance, [], [], WorkflowTraceMode.Full,
            debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: parameters, triggerIdForRun: trigger is null ? null : TriggerId, triggerEnvelopeForRun: trigger,
            cancellationToken: CancellationToken.None);
    }

    private static readonly Guid TriggerId = Guid.NewGuid();

    private static Dictionary<string, object?> Parameters(Namespace ns, object? value) =>
        ns == Namespace.Parameter ? Targets.Select(WireParameter).Append("p").ToDictionary(name => name, _ => value) : new();

    private static TriggerEnvelope? Trigger(Namespace ns, object? value) =>
        ns == Namespace.Trigger ? new TriggerEnvelope(Instant, new Dictionary<string, object?> { ["t"] = value }) : null;

    internal static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public sealed record Point(int X, int Y);

    public sealed record Reading(double Value);

    public sealed class Loop
    {
        public Loop Self => this;
    }

    public sealed class Throwing(Func<Exception> make)
    {
        public int Value => throw make();
    }

    public sealed class DomainException() : Exception("a host's own");

    public sealed class Box
    {
        public string Label { get; init; } = "";
        public int[] Items { get; init; } = [];
    }

    private const string Plain = "hello";

    private const string PlainReader = "reads-only-s";

    private sealed class Blocks(object? value)
    {
        public Dictionary<string, object?> Seen { get; } = new(StringComparer.Ordinal);

        public bool Fails { get; init; }

        public IWorkflowBlocks Catalogue => new UncheckedWorkflowBlocks([
            new Factory("src", () => new Returning(this, new Dictionary<string, object?> { ["v"] = value, ["s"] = Plain }), [],
                [Port("v", PortValueType.Json), Port("s", PortValueType.String)]),
            Sink("sink-String", PortValueType.String),
            Sink("sink-Collection", PortValueType.CollectionOf(PortValueType.String)),
            Sink("sink-Number", PortValueType.Number),
            new Factory("sink-Dynamic", () => new Recording(this), [], [], dynamic: true),
            new Factory("sink-DeclaredAndDynamic", () => new Recording(this), [Port("e", PortValueType.Number)], [], dynamic: true),
        ]);

        private Factory Sink(string id, PortValueType type) => new(id, () => new Recording(this), [Port("e", type)], []);

        private static BlockPortDefinition Port(string name, PortValueType type) => new(name, name, type, false);

        private sealed class Recording(Blocks blocks) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                blocks.Seen[context.NodeId!] = inputs.GetValueOrDefault("e");
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
            }
        }

        private sealed class Returning(Blocks blocks, IReadOnlyDictionary<string, object?> outputs) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                blocks.Seen[context.NodeId!] = null;
                return blocks.Fails ? throw new InvalidOperationException("failed") : Task.FromResult(outputs);
            }
        }
    }

    private sealed class Factory(
        string id, Func<IWorkflowBlockPlugin> create, BlockPortDefinition[] inputs, BlockPortDefinition[] outputs, bool dynamic = false)
        : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs;
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs;
        public bool HasDynamicPorts => dynamic;
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => create();
    }

    private sealed class NoServices : IServiceProvider
    {
        public static readonly NoServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
    private sealed class TriggerResolver(TriggerEnvelope? envelope) : IExpressionNamespaceResolver
    {
        public string Namespace => "trigger";

        public ResolveResult Resolve(string[] segments)
        {
            if (segments.Length == 0 || envelope?.Payload == null)
                return ResolveResult.Failed();

            var current = (object?)envelope.Payload;
            foreach (var segment in segments)
            {
                if (current is Dictionary<string, object?> dict && dict.TryGetValue(segment, out var value))
                    current = value;
                else
                    return ResolveResult.Failed();
            }

            return ResolveResult.Resolved(current is null ? null : ExpressionJsonValue.Recorded(current));
        }
    }

    private sealed class TriggerNamespace : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "trigger";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => segments.Count == 0 ? NamespacePathCheckAnswer.No : new(NamespacePathCheck.Yes, "Json?");
    }
}
