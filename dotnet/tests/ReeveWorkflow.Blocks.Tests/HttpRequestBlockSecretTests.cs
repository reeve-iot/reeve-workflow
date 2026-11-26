using System.Net;
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

public sealed partial class HttpRequestBlockSecretTests
{
    private const string Plaintext = "S3CR3T-plaintext-7f3a";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_literal_Protected_and_Reference_entry_is_revealed_with_its_site_and_sent_whether_it_is_configuration_or_an_edge_literal(bool onEdge)
    {
        var codec = new FakeCodec { ["enc:AAA"] = "plain-AAA", ["ref:vault:db#pw"] = "plain-ref" };
        var world = new World(codec);
        var headers = new Dictionary<string, string>
        {
            ["X-Token"] = "enc:AAA", ["X-Ref"] = "ref:vault:db#pw", ["Accept"] = "text/plain",
        };

        await world.RunAsync(onEdge ? World.LiteralEdge(headers) : World.Configured(headers));

        var sent = world.Sent.Should().ContainSingle().Which;
        sent["X-Token"].Should().Equal("plain-AAA");
        sent["X-Ref"].Should().Equal("plain-ref");
        sent["Accept"].Should().Equal("text/plain");
        codec.Revealed.Should().Equal(
            (new WorkflowSecretSite(world.WorkflowId, "", "http", "Headers", "X-Token"), "enc:AAA"),
            (new WorkflowSecretSite(world.WorkflowId, "", "http", "Headers", "X-Ref"), "ref:vault:db#pw"));
    }

    [Fact]
    public async Task An_entry_that_arrives_by_a_wire_is_sent_as_it_arrived_and_never_revealed()
    {
        var codec = new FakeCodec { ["enc:AAA"] = "plain-AAA" };
        var world = new World(codec);

        await world.RunAsync(World.Wired(new Dictionary<string, string> { ["X-Token"] = "enc:AAA", ["X-Ref"] = "ref:vault:db#pw" }));

        world.Sent.Should().ContainSingle().Which["X-Token"].Should().Equal("enc:AAA");
        world.Sent[0]["X-Ref"].Should().Equal("ref:vault:db#pw");
        codec.Revealed.Should().BeEmpty();
    }

    [Fact]
    public async Task An_entry_that_a_projection_selected_is_sent_as_it_arrived_and_never_revealed()
    {
        var codec = new FakeCodec { ["enc:AAA"] = "plain-AAA", ["ref:vault:db#pw"] = "plain-ref" };
        var world = new World(codec);

        await world.RunAsync(World.Projected("""{"h":{"X-Token":"enc:AAA","X-Ref":"ref:vault:db#pw"}}"""));

        world.Sent.Should().ContainSingle().Which["X-Token"].Should().Equal("enc:AAA");
        world.Sent[0]["X-Ref"].Should().Equal("ref:vault:db#pw");
        codec.Revealed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_node_below_the_root_canvas_reveals_with_the_path_of_its_canvas_in_the_site()
    {
        var codec = new FakeCodec { ["enc:AAA"] = "plain-AAA" };
        var world = new World(codec);

        await world.RunAsync(World.InSubCanvas(new Dictionary<string, string> { ["X-Token"] = "enc:AAA" }));

        world.Sent.Should().ContainSingle().Which["X-Token"].Should().Equal("plain-AAA");
        codec.Revealed.Should().ContainSingle().Which.Site.Should().Be(
            new WorkflowSecretSite(world.WorkflowId, "tc/Try", "http", "Headers", "X-Token"));
    }

    [Fact]
    public async Task An_entry_that_an_expression_produced_is_sent_as_it_evaluated_and_never_revealed()
    {
        var codec = new FakeCodec { ["ref:vault:db#pw"] = "plain-ref" };
        var world = new World(codec);

        await world.RunAsync(World.Evaluated(new Dictionary<string, string> { ["X-Ref"] = "ref:vault:db#pw" }));

        world.Sent.Should().ContainSingle().Which["X-Ref"].Should().Equal("ref:vault:db#pw");
        codec.Revealed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_revealed_value_holding_a_control_character_fails_the_node_naming_the_key_only_and_sends_nothing()
    {
        var codec = new FakeCodec { ["enc:AAA"] = $"{Plaintext}\r\nX-Injected: b" };
        var world = new World(codec);

        var outputs = await world.RunAsync(World.Configured(
            new Dictionary<string, string> { ["X-Token"] = "enc:AAA" }, emitError: true));

        var error = (Dictionary<string, object?>)outputs["http"]["_error"]!;
        ((string)error["message"]!).Should().Be(
            "Input 'Headers' key 'X-Token' has a value with a character other than visible ASCII, a space or a tab, which a field value cannot hold.");
        world.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reveal_that_throws_fails_the_node_with_secret_unresolved_naming_input_and_key_and_sends_nothing()
    {
        var codec = new FakeCodec { Failure = new InvalidOperationException($"vault said {Plaintext}") };
        var world = new World(codec);

        var outputs = await world.RunAsync(World.Configured(
            new Dictionary<string, string> { ["X-Ok"] = "1", ["X-Token"] = "enc:AAA" }, emitError: true));

        var error = (Dictionary<string, object?>)outputs["http"]["_error"]!;
        error["code"].Should().Be("secret-unresolved");
        error["message"].Should().Be("Input 'Headers' key 'X-Token' holds a secret that could not be revealed.");
        world.Sent.Should().BeEmpty("a header whose reveal failed is never sent empty");
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("edge literal")]
    [InlineData("sub-canvas")]
    public void A_key_that_is_no_field_name_is_refused_at_compile_naming_the_position_and_never_the_key(string where)
    {
        var world = new World(new FakeCodec());
        var pasted = new Dictionary<string, string> { ["Accept"] = "a", ["Authorization: Bearer x"] = "enc:AAA" };
        var scenario = where switch
        {
            "edge literal" => World.LiteralEdge(pasted),
            "sub-canvas" => World.InSubCanvas(pasted),
            _ => World.Configured(pasted),
        };

        var found = world.Diagnose(scenario).Where(d => d.RuleId == "secret-input-key-invalid").ToList();

        var diagnostic = found.Should().ContainSingle().Which;
        diagnostic.NodeId.Should().Be("http");
        diagnostic.PortName.Should().Be("Headers");
        diagnostic.Message.Should().Contain("Authorization...").And.NotContain("Bearer");
    }

    [Fact]
    public void A_field_name_and_a_map_that_a_wire_carries_are_not_refused_at_compile()
    {
        var world = new World(new FakeCodec());

        world.Diagnose(World.Configured(new Dictionary<string, string> { ["X-Api_Key.1"] = "enc:AAA" })).Should().NotContain(d => d.RuleId == "secret-input-key-invalid");
        world.Diagnose(World.Wired(new Dictionary<string, string> { ["Authorization: Bearer x"] = "v" })).Should().NotContain(d => d.RuleId == "secret-input-key-invalid");
    }

    [Fact]
    public async Task A_pasted_field_line_as_a_wired_key_puts_its_secret_in_neither_the_error_nor_the_recorded_trace()
    {
        var world = new World(codec: null);

        var act = async () => await world.RunAsync(World.Wired(new Dictionary<string, string> { ["Authorization: Bearer x"] = "" }));

        var failure = (await act.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which;
        failure.InnerException!.Message.Should().Be(
            "Input 'Headers' key 'Authorization...' is not a field name; a name is a token, and its character 14 is not a token character.");
        failure.ToString().Should().NotContain("Bearer x");
        var step = world.Steps.Single(candidate => candidate.NodeId == "http");
        step.InputsJson.Should().NotBeNull().And.NotContain("Bearer x");
        $"{step.ErrorMessage}{step.ErrorStackTrace}{step.ErrorJson}".Should().NotContain("Bearer x");
    }

    [Fact]
    public async Task With_no_codec_the_values_are_sent_as_written()
    {
        var world = new World(codec: null);

        await world.RunAsync(World.Configured(new Dictionary<string, string> { ["X-Token"] = "enc:AAA", ["X-Ref"] = "ref:vault:db#pw" }));

        world.Sent.Should().ContainSingle().Which["X-Token"].Should().Equal("enc:AAA");
        world.Sent[0]["X-Ref"].Should().Equal("ref:vault:db#pw");
    }

    [Fact]
    public async Task Reveal_answers_an_entry_only_when_it_equals_the_one_the_node_stored_at_that_key()
    {
        var codec = new FakeCodec { ["enc:AAA"] = "plain-AAA", ["enc:FOREIGN"] = "plain-foreign" };
        var world = new World(codec);
        IReadOnlyDictionary<string, string>? answer = null;
        world.Probe = async (inputs, context) =>
            answer = await context.Secrets.RevealAsync("Headers", new Dictionary<string, string>
            {
                ["X-Token"] = "enc:AAA", ["X-Foreign"] = "enc:FOREIGN", ["X-Swapped"] = "enc:FOREIGN", ["X-Plain"] = "p",
            }, CancellationToken.None);

        await world.RunProbeAsync(new Dictionary<string, string> { ["X-Token"] = "enc:AAA", ["X-Swapped"] = "enc:AAA", ["X-Plain"] = "p" });

        answer.Should().Equal(new Dictionary<string, string>
        {
            ["X-Token"] = "plain-AAA", ["X-Foreign"] = "enc:FOREIGN", ["X-Swapped"] = "enc:FOREIGN", ["X-Plain"] = "p",
        });
        codec.Revealed.Select(r => r.Site.Key).Should().Equal("X-Token");
    }

    [Fact]
    public async Task Reveal_leaves_an_input_that_does_not_declare_SecretValues_as_it_is()
    {
        var codec = new FakeCodec { ["enc:AAA"] = "plain-AAA" };
        var world = new World(codec);
        IReadOnlyDictionary<string, string>? answer = null;
        world.Probe = async (inputs, context) =>
            answer = await context.Secrets.RevealAsync("Other", new Dictionary<string, string> { ["X-Token"] = "enc:AAA" }, CancellationToken.None);

        await world.RunProbeAsync(new Dictionary<string, string> { ["X-Token"] = "enc:AAA" }, other: new Dictionary<string, string> { ["X-Token"] = "enc:AAA" });

        answer.Should().Equal(new Dictionary<string, string> { ["X-Token"] = "enc:AAA" });
        codec.Revealed.Should().BeEmpty();
    }

    [Fact]
    public async Task The_revealed_plaintext_is_in_no_exception_message_node_error_log_or_recorded_trace()
    {
        var captured = new List<string>();
        var codec = new FakeCodec { ["enc:AAA"] = Plaintext };

        foreach (var variant in new[] { "sent", "refused", "throws" })
        {
            var logger = new CollectingLogger();
            var world = new World(variant == "throws"
                ? new FakeCodec { Failure = new InvalidOperationException($"vault said {Plaintext}") }
                : variant == "refused" ? new FakeCodec { ["enc:AAA"] = $"{Plaintext}\r\nX: y" } : codec) { Logger = logger };
            var headers = new Dictionary<string, string> { ["X-Token"] = "enc:AAA" };

            var outputs = await world.RunAsync(World.Configured(headers, emitError: true));
            world.Sent.Count.Should().Be(variant == "sent" ? 1 : 0);

            captured.Add(JsonSerializer.Serialize(outputs.ToDictionary(o => o.Key, o => o.Value.ToDictionary(e => e.Key, e => e.Value))));
            captured.AddRange(world.Steps.SelectMany(s => new[] { s.InputsJson, s.OutputsJson, s.ErrorMessage, s.ErrorStackTrace, s.ErrorJson }).OfType<string>());
            captured.AddRange(world.Logs.Select(l => l.Message));
            captured.AddRange(logger.Lines);
        }

        captured.Should().NotBeEmpty();
        captured.Where(text => text.Contains(Plaintext, StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_reveal_that_throws_is_logged_by_exception_type_and_entry_without_the_message_or_the_value()
    {
        var codec = new FakeCodec { Failure = new InvalidOperationException($"vault said {Plaintext}") };
        var logger = new CollectingLogger();
        var world = new World(codec) { Logger = logger };

        await world.RunAsync(World.Configured(new Dictionary<string, string> { ["X-Token"] = "enc:AAA" }, emitError: true));

        var line = logger.Lines.Should().ContainSingle().Which;
        line.Should().Contain("System.InvalidOperationException").And.Contain("X-Token").And.Contain("http");
        line.Should().NotContain(Plaintext).And.NotContain("vault said").And.NotContain("enc:AAA");
    }

    [Fact]
    public async Task Reveal_refuses_an_entry_with_no_value_naming_the_key()
    {
        var world = new World(new FakeCodec());
        Exception? refused = null;
        world.Probe = async (inputs, context) =>
        {
            try
            {
                await context.Secrets.RevealAsync("Headers", new Dictionary<string, string> { ["X-Gone"] = null! }, CancellationToken.None);
            }
            catch (Exception exception)
            {
                refused = exception;
            }
        };

        await world.RunProbeAsync(new Dictionary<string, string> { ["X-Gone"] = "p" });

        refused.Should().BeOfType<ArgumentException>().Which.Message.Should().Be("Input 'Headers' key 'X-Gone' has no value.");
    }

    [Fact]
    public void A_field_prints_its_name_and_never_its_value()
    {
        var field = Activator.CreateInstance(
            typeof(HttpRequestBlock).GetNestedType("Field", System.Reflection.BindingFlags.NonPublic)!, "X-Token", Plaintext)!;

        field.ToString().Should().Contain("X-Token").And.NotContain(Plaintext);
    }

    private sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
    }

    private sealed class FakeCodec : IWorkflowSecretCodec
    {
        private readonly Dictionary<string, string> plain = new(StringComparer.Ordinal);

        public Exception? Failure { get; init; }
        public List<(WorkflowSecretSite Site, string Stored)> Revealed { get; } = [];

        public string this[string stored] { set => plain[stored] = value; }

        public WorkflowSecretForm Classify(string value) =>
            value.StartsWith("enc:", StringComparison.Ordinal) ? WorkflowSecretForm.Protected
            : value.StartsWith("ref:", StringComparison.Ordinal) ? WorkflowSecretForm.Reference
            : WorkflowSecretForm.Plain;

        public ValueTask<string> ProtectAsync(WorkflowSecretSite site, string plaintext, CancellationToken cancellationToken) =>
            ValueTask.FromResult("enc:" + plaintext);

        public ValueTask AdmitReferencesAsync(
            WorkflowSecretSave save, IReadOnlyList<string> bound, IReadOnlyList<string> unbound, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<string> RevealAsync(WorkflowSecretSite site, string stored, CancellationToken cancellationToken)
        {
            Revealed.Add((site, stored));
            return Failure is not null ? throw Failure : ValueTask.FromResult(plain[stored]);
        }
    }

    private sealed record Scenario(
        Dictionary<string, JsonElement> Config, object EdgeMapping, Dictionary<string, string> Source, bool EmitError,
        bool Nested = false, IWorkflowBlockFactory? Producer = null);

    private sealed class World(FakeCodec? codec)
    {
        public Guid WorkflowId { get; } = Guid.NewGuid();
        public List<Dictionary<string, string[]>> Sent { get; } = [];
        public List<WorkflowExecutionStep> Steps { get; } = [];
        public List<WorkflowExecutionLogEntry> Logs { get; } = [];
        public CollectingLogger? Logger { get; init; }
        public Func<IReadOnlyDictionary<string, object?>, WorkflowBlockContext, Task>? Probe { get; set; }
        public IWorkflowDebugHook? DebugHook { get; init; }
        public IWorkflowNodeObserver NodeObserver { get; init; } = NullWorkflowNodeObserver.Instance;

        public static Scenario Configured(Dictionary<string, string> headers, bool emitError = false) =>
            new(Config(JsonSerializer.SerializeToElement(headers)), new { }, [], emitError);

        public static Scenario LiteralEdge(Dictionary<string, string> headers) =>
            new(Config(null), new { Headers = new { type = "literal", literalValue = headers } }, [], false);

        public static Scenario Wired(Dictionary<string, string> source, bool emitError = false) =>
            new(Config(null), new { Headers = new { type = "direct", sourceOutputName = "Out" } }, source, emitError);

        public static Scenario InSubCanvas(Dictionary<string, string> headers) =>
            new(Config(JsonSerializer.SerializeToElement(headers)), new { }, [], false, Nested: true);

        public static Scenario Projected(string document) =>
            new(Config(null), new { Headers = new { type = "direct", sourceOutputName = "Doc", projection = new { path = "$.h" } } },
                [], false, Producer: new JsonSource(document));

        public static Scenario Evaluated(Dictionary<string, string> source) =>
            new(Config(JsonSerializer.SerializeToElement("expr:${previous.src.Out}")), new { }, source, false);

        private static Dictionary<string, JsonElement> Config(JsonElement? headers)
        {
            var config = new Dictionary<string, JsonElement> { ["Url"] = JsonSerializer.SerializeToElement("https://example.com/api") };
            if (headers is { } value)
                config["Headers"] = value;
            return config;
        }

        public Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(Scenario scenario)
        {
            var http = new WorkflowNodeDefinition { Id = "http", BlockTypeId = "HttpRequest", Config = scenario.Config };
            if (scenario.EmitError)
                http.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);
            return RunAsync(http, scenario, new SendingFactory(Sent));
        }

        public async Task RunProbeAsync(Dictionary<string, string> headers, Dictionary<string, string>? other = null, NodeErrorPolicy? policy = null)
        {
            var node = new WorkflowNodeDefinition
            {
                Id = "http", BlockTypeId = ProbeFactory.Id,
                Config = new()
                {
                    ["Headers"] = JsonSerializer.SerializeToElement(headers),
                    ["Other"] = JsonSerializer.SerializeToElement(other ?? []),
                },
            };
            if (policy is { } chosen)
                node.ErrorPolicy = new NodeErrorConfig(chosen);
            await RunAsync(node, new Scenario([], new { }, [], false), new ProbeFactory(() => Probe!));
        }

        private async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync(
            WorkflowNodeDefinition node, Scenario scenario, IWorkflowBlockFactory factory)
        {
            var producer = scenario.Producer ?? new MapSource(scenario.Source);
            var blocks = Blocks(factory, producer);
            var compiled = WorkflowCompiler.Compile(Definition(node, scenario, producer), new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty);
            compiled.Diagnostics.Should().BeEmpty(string.Join("; ", compiled.Diagnostics.Select(d => $"{d.RuleId}: {d.Message}")));
            var services = new ServiceCollection();
            if (codec is not null)
                services.AddSingleton<IWorkflowSecretCodec>(codec);
            if (Logger is not null)
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<BlockSecrets>>(new TypedLogger(Logger));
            return await new WorkflowGraphExecutor(blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
                    NodeObserver, NullHelperResolver.Instance, new WorkflowGraphExecutorOptions())
                .ExecuteGraphAsync(
                    new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["start"] = new Dictionary<string, object?>() },
                    WorkflowId, Guid.NewGuid(), Guid.NewGuid(), services.BuildServiceProvider(), Logs, Steps, WorkflowTraceMode.Full,
                    debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
                    inputParametersForRun: new Dictionary<string, object?>(), triggerIdForRun: null, triggerEnvelopeForRun: null,
                    cancellationToken: CancellationToken.None, debugHook: DebugHook, debugCounter: null, stateExecutionId: null,
                    mayParkSleep: false, parameterSignature: null, compiled: CompiledWalk.Root(compiled.Program!));
        }

        public IReadOnlyList<CompilationDiagnostic> Diagnose(Scenario scenario, params string[] passthrough)
        {
            var http = new WorkflowNodeDefinition { Id = "http", BlockTypeId = "HttpRequest", Config = scenario.Config, Passthrough = [.. passthrough] };
            var producer = scenario.Producer ?? new MapSource(scenario.Source);
            return WorkflowCompiler.Compile(Definition(http, scenario, producer), new CatalogueBlockShapeSource(Blocks(new SendingFactory(Sent), producer)), CanvasSignature.Empty).Diagnostics;
        }

        private static WorkflowDefinition Definition(WorkflowNodeDefinition node, Scenario scenario, IWorkflowBlockFactory producer)
        {
            var definition = new WorkflowDefinition
            {
                Nodes =
                [
                    new() { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                    new() { Id = "src", BlockTypeId = producer.BlockTypeId },
                    node,
                ],
                Edges =
                [
                    new() { Id = "e0", SourceNodeId = "start", TargetNodeId = "src", Mappings = JsonSerializer.SerializeToElement(new { }) },
                    new() { Id = "e1", SourceNodeId = "src", TargetNodeId = "http", Mappings = JsonSerializer.SerializeToElement(scenario.EdgeMapping) },
                ],
            };
            if (scenario.Nested)
            {
                definition = new WorkflowDefinition
                {
                    Nodes =
                    [
                        new() { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                        new()
                        {
                            Id = "tc", BlockTypeId = "TryCatch",
                            SubCanvases = new() { ["Try"] = new WorkflowDefinition { Nodes = [node], Edges = [] } },
                        },
                    ],
                    Edges =
                    [
                        new() { Id = "e0", SourceNodeId = "start", TargetNodeId = "tc", Mappings = JsonSerializer.SerializeToElement(new { }) },
                    ],
                };
            }

            return definition;
        }

        private static RegisteredWorkflowBlocks Blocks(IWorkflowBlockFactory under, IWorkflowBlockFactory source)
        {
            var services = new ServiceCollection();
            services.AddStandardWorkflowBlocks();
            var standard = services.BuildServiceProvider().GetServices<IWorkflowBlockFactory>()
                .Where(f => f.BlockTypeId != under.BlockTypeId);
            return new RegisteredWorkflowBlocks(standard.Append(under).Append(source));
        }
    }

    private sealed class TypedLogger(CollectingLogger inner) : Microsoft.Extensions.Logging.ILogger<BlockSecrets>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class SendingFactory(List<Dictionary<string, string[]>> sent) : HttpRequestBlockFactory
    {
        public override IWorkflowBlockPlugin Create() => new SendingBlock(sent);
    }

    private sealed class SendingBlock(List<Dictionary<string, string[]>> sent) : HttpRequestBlock
    {
        protected override HttpClientLease ResolveHttpClient(WorkflowBlockContext context) =>
            new(new HttpClient(new Recorder(sent)), CallerOwnsClient: true);
    }

    private sealed class Recorder(List<Dictionary<string, string[]>> sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent.Add(request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        }
    }

    private sealed class MapSource(Dictionary<string, string>? entries = null) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "MapSource";
        public string BlockTypeId => Id;
        public string DisplayName => Id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new("Out", "Out", PortValueType.MapOf(PortValueType.String), false)];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["Out"] = context.Types["Out"].CreateMap((entries ?? []).Select(e => new KeyValuePair<string, object?>(e.Key, e.Value))),
            });
    }

    private sealed class JsonSource(string document) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public string BlockTypeId => "JsonSource";
        public string DisplayName => BlockTypeId;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new("Doc", "Doc", PortValueType.Of("Json?"), false)];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?> { ["Doc"] = JsonDocument.Parse(document).RootElement.Clone() });
    }

    private sealed class ProbeFactory(Func<Func<IReadOnlyDictionary<string, object?>, WorkflowBlockContext, Task>> probe)
        : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "Probe";
        public string BlockTypeId => Id;
        public string DisplayName => Id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [
            new("Headers", "Headers", PortValueType.MapOf(PortValueType.String), false) { SecretValues = true },
            new("Other", "Other", PortValueType.MapOf(PortValueType.String), false),
        ];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => this;

        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await probe()(inputs, context);
            return new Dictionary<string, object?>();
        }
    }
}
