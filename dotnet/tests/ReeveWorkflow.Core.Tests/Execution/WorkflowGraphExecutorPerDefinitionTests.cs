using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowGraphExecutorPerDefinitionTests
{
    private const string Echo = "test.echo";

    [Fact]
    public async Task A_block_logs_under_one_category_however_many_runs_there_are_and_its_scope_names_the_run_and_the_node()
    {
        var logging = new RecordingLoggerFactory();
        var world = new World(logging);
        var definition = Chain();

        var first = await world.RunAsync(definition);
        var afterOne = logging.Categories.Count;
        var second = await world.RunAsync(definition);

        logging.Categories.Count.Should().Be(afterOne, "a second run names no logger the first did not");
        logging.Categories.Should().NotContain(category => category.Contains(first.ToString()) || category.Contains(second.ToString()));
        logging.Entries.Where(entry => entry.Message == "echo").Select(entry => (entry.Category, entry.Scope))
            .Should().BeEquivalentTo(new[]
            {
                ("Workflow.Block", Scope(first, "a")), ("Workflow.Block", Scope(first, "b")),
                ("Workflow.Block", Scope(second, "a")), ("Workflow.Block", Scope(second, "b"))
            });
    }

    [Fact]
    public async Task A_block_scope_printed_by_a_log_formatter_reads_as_the_run_and_the_node()
    {
        var logging = new RecordingLoggerFactory();
        var run = await new World(logging).RunAsync(Chain());

        logging.Entries.Where(entry => entry.Message == "echo").Select(entry => entry.ScopeText)
            .Should().Equal($"WorkflowExecutionId:{run} WorkflowNodeId:a", $"WorkflowExecutionId:{run} WorkflowNodeId:b");
    }

    [Fact]
    public async Task A_literal_on_an_edge_is_the_input_whatever_the_node_is_configured_with()
    {
        var definition = Chain();
        definition.Edges = [.. definition.Edges!.Select(edge => edge.Id == "start-a"
            ? new WorkflowConnectionDefinition
            {
                Id = "start-a", SourceNodeId = "start", TargetNodeId = "a",
                Mappings = JsonDocument.Parse("""{"say":{"type":"literal","literalValue":"from the edge"}}""").RootElement
            }
            : edge)];
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

        await new World(new RecordingLoggerFactory()).RunAsync(definition, outputs);

        outputs["a"]["said"].Should().Be("from the edge");
        outputs["b"]["said"].Should().Be("from the edge");
    }

    [Fact]
    public async Task A_literal_list_on_an_edge_is_a_list_of_its_own_on_every_walk_of_the_same_definition()
    {
        var definition = Chain();
        definition.Edges = [.. definition.Edges!.Select(edge => edge.Id == "start-a"
            ? new WorkflowConnectionDefinition
            {
                Id = "start-a", SourceNodeId = "start", TargetNodeId = "a",
                Mappings = JsonDocument.Parse("""{"items":{"type":"literal","literalValue":["x","y"]}}""").RootElement
            }
            : edge)];
        var world = new World(new RecordingLoggerFactory());
        var first = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        await world.RunAsync(definition, first);
        var given = (System.Collections.IList)first["a"]["given"]!;
        given.Add("added by a block");

        var second = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        await world.RunAsync(definition, second);

        var fresh = (System.Collections.IList)second["a"]["given"]!;
        fresh.Cast<object?>().Should().Equal("x", "y");
    }

    [Fact]
    public async Task A_nested_object_literal_is_fresh_on_each_walk()
    {
        var definition = Chain();
        definition.Edges = [.. definition.Edges!.Select(edge => edge.Id == "start-a"
            ? new WorkflowConnectionDefinition
            {
                Id = "start-a", SourceNodeId = "start", TargetNodeId = "a",
                Mappings = JsonDocument.Parse("""{"payload":{"type":"literal","literalValue":[{"value":"original"}]}}""").RootElement
            }
            : edge)];
        var world = new World(new RecordingLoggerFactory());
        var first = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        await world.RunAsync(definition, first);
        var firstPayload = (System.Collections.IList)first["a"]["payload"]!;
        ((Dictionary<string, object?>)firstPayload[0]!)["value"] = "mutated";

        var second = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        await world.RunAsync(definition, second);

        var secondPayload = (System.Collections.IList)second["a"]["payload"]!;
        ((Dictionary<string, object?>)secondPayload[0]!).Should().Contain("value", "original");
    }

    [Fact]
    public async Task A_wire_from_an_output_its_source_never_produced_leaves_the_configured_value()
    {
        var definition = Chain();
        definition.Nodes = [.. definition.Nodes.Select(node => node.Id == "b"
            ? new WorkflowNodeDefinition
            {
                Id = "b", BlockTypeId = Echo,
                Config = new Dictionary<string, JsonElement> { ["say"] = JsonSerializer.SerializeToElement("kept") }
            }
            : node)];
        definition.Edges = [.. definition.Edges!.Select(edge => edge.Id == "a-b"
            ? new WorkflowConnectionDefinition
            {
                Id = "a-b", SourceNodeId = "a", TargetNodeId = "b",
                Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["say"] = new { type = "direct", sourceOutputName = "nothing" } })
            }
            : edge)];
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

        await new World(new RecordingLoggerFactory()).RunAsync(definition, outputs);

        outputs["b"]["said"].Should().Be("kept");
    }

    [Fact]
    public async Task Two_edges_into_one_input_of_a_sub_canvas_node_give_it_the_value_of_the_later_edge()
    {
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                Saying("x", "first"),
                Saying("y", "second"),
                new WorkflowNodeDefinition { Id = "z", BlockTypeId = Echo }
            ],
            Edges =
            [
                new WorkflowConnectionDefinition { Id = "start-x", SourceNodeId = "start", TargetNodeId = "x" },
                new WorkflowConnectionDefinition { Id = "start-y", SourceNodeId = "start", TargetNodeId = "y" },
                Wire("x", "z"),
                Wire("y", "z")
            ]
        };
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>();

        await new World(new RecordingLoggerFactory()).RunAsync(definition, outputs, depth: 1);

        outputs["z"]["said"].Should().Be("second");
    }

    private static WorkflowNodeDefinition Saying(string id, string word) => new()
    {
        Id = id, BlockTypeId = Echo,
        Config = new Dictionary<string, JsonElement> { ["say"] = JsonSerializer.SerializeToElement(word) }
    };

    private static WorkflowConnectionDefinition Wire(string from, string to) => new()
    {
        Id = $"{from}-{to}", SourceNodeId = from, TargetNodeId = to,
        Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["say"] = new { type = "direct", sourceOutputName = "said" } })
    };

    private static Dictionary<string, object?> Scope(Guid executionId, string nodeId) => new()
    {
        ["WorkflowExecutionId"] = executionId,
        ["WorkflowNodeId"] = nodeId
    };

    private static WorkflowDefinition Chain() => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            new WorkflowNodeDefinition
            {
                Id = "a", BlockTypeId = Echo,
                Config = new Dictionary<string, JsonElement> { ["say"] = JsonSerializer.SerializeToElement("hello") }
            },
            new WorkflowNodeDefinition { Id = "b", BlockTypeId = Echo }
        ],
        Edges =
        [
            new WorkflowConnectionDefinition { Id = "start-a", SourceNodeId = "start", TargetNodeId = "a" },
            new WorkflowConnectionDefinition
            {
                Id = "a-b", SourceNodeId = "a", TargetNodeId = "b",
                Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["say"] = new { type = "direct", sourceOutputName = "said" } })
            }
        ]
    };

    private sealed class World(ILoggerFactory logging)
    {
        public async Task<Guid> RunAsync(
            WorkflowDefinition definition, Dictionary<string, IReadOnlyDictionary<string, object?>>? outputs = null,
            int depth = 0)
        {
            var executionId = Guid.NewGuid();
            var executor = new WorkflowGraphExecutor(
                new Registry(), logging, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance,
                NullHelperResolver.Instance);

            var walked = await executor.ExecuteGraphAsync(
                [], Guid.NewGuid(), executionId, Guid.NewGuid(), EmptyServices.Instance,
                [], [], WorkflowTraceMode.None, debugMode: false, iterationIndex: null, callStackJson: null,
                depth: depth, visitedNodeIds: [], isInsideTryCatch: false, inputParametersForRun: null,
                triggerIdForRun: null, triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
                stateExecutionId: null, compiled: TestPrograms.Walk(definition, new Registry()));
            if (outputs is not null)
                foreach (var (node, values) in walked)
                    outputs[node] = values;
            return executionId;
        }
    }

    private sealed class Registry : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId == Echo ? new Factory() : null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class Factory : IWorkflowBlockFactory
    {
        public string BlockTypeId => Echo;
        public string DisplayName => Echo;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new BlockPortDefinition("say", "say", PortValueType.String, false),
             new BlockPortDefinition("items", "items", PortValueType.Of("Collection<String>?"), false),
             new BlockPortDefinition("payload", "payload", PortValueType.Of("Json?"), false)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("said", "said", PortValueType.String, false),
             new BlockPortDefinition("given", "given", PortValueType.Of("Collection<String>?"), false),
             new BlockPortDefinition("payload", "payload", PortValueType.Of("Json?"), false)];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new EchoBlock();
    }

    private sealed class EchoBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            context.Logger.LogInformation("echo");
            var said = inputs.TryGetValue("say", out var value) ? value?.ToString() : null;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?>
                {
                    ["said"] = said, ["given"] = inputs.GetValueOrDefault("items"),
                    ["payload"] = inputs.GetValueOrDefault("payload")
                });
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly HashSet<string> categories = [];

        public List<(string Category, string Message, Dictionary<string, object?>? Scope, string? ScopeText)> Entries { get; } = [];

        public IReadOnlyCollection<string> Categories
        {
            get { lock (categories) return [.. categories]; }
        }

        public ILogger CreateLogger(string categoryName)
        {
            lock (categories) categories.Add(categoryName);
            return new Logger(this, categoryName);
        }

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Logger(RecordingLoggerFactory owner, string category) : ILogger
        {
            private readonly AsyncLocal<Dictionary<string, object?>?> scope = new();
            private readonly AsyncLocal<string?> scopeText = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                var previous = scope.Value;
                var previousText = scopeText.Value;
                scopeText.Value = state.ToString();
                scope.Value = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                    : state is IEnumerable<KeyValuePair<string, object>> objects
                        ? objects.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
                        : null;
                return new Restore(() => (scope.Value, scopeText.Value) = (previous, previousText));
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                    owner.Entries.Add((category, formatter(state, exception), scope.Value, scopeText.Value));
            }

            private sealed class Restore(Action restore) : IDisposable
            {
                public void Dispose() => restore();
            }
        }
    }
}
