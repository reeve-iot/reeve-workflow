using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowGraphHostNamespaceTests
{
    [Fact]
    public async Task A_namespace_the_host_registers_answers_during_a_run()
    {
        var seen = await ReadingAsync(Options(new KitchenDevices(), 2));

        seen.Should().Be(21.5, "a run evaluates the host's vocabulary, not an empty copy of it");
    }

    [Fact]
    public async Task The_host_is_asked_once_per_walk_inside_that_walks_own_scope()
    {
        var scopes = new List<IServiceProvider>();
        var services = new EmptyServices();
        var options = new WorkflowGraphExecutorOptions
        {
            ExpressionNamespaces =
            [
                new WorkflowExpressionNamespace("device", 2, new TestCheck("device"), (scope, _) =>
                {
                    scopes.Add(scope.Services);
                    return Task.FromResult<IExpressionNamespaceResolver>(new KitchenDevices());
                })
            ]
        };

        await ReadingAsync(options, services, nodes: 2);

        scopes.Should().ContainSingle().Which.Should().BeSameAs(services);
    }

    [Fact]
    public void A_host_cannot_register_a_library_namespace()
    {
        var act = () => Options(new FixedNow());

        act.Should().Throw<ArgumentException>()
            .WithMessage("*reserved by the library*");
    }

    [Fact]
    public async Task A_shared_registration_receives_the_run_scope()
    {
        ExpressionNamespaceScope? received = null;
        var executionId = Guid.NewGuid();
        var triggerId = Guid.NewGuid();
        var envelope = new TriggerEnvelope(DateTimeOffset.UnixEpoch, new Dictionary<string, object?> { ["source"] = "test" });
        var options = new WorkflowGraphExecutorOptions
        {
            ExpressionNamespaces =
            [
                new WorkflowExpressionNamespace("device", 2, new TestCheck("device"), (scope, _) =>
                {
                    received = scope;
                    return Task.FromResult<IExpressionNamespaceResolver>(new TestNamespace("device", 21.5));
                })
            ]
        };
        var registry = new ReadingRegistry();
        var executor = new WorkflowGraphExecutor(
            registry, NullLoggerFactory.Instance, new SilentMetrics(), NullWorkflowNodeObserver.Instance,
            NullHelperResolver.Instance, options);

        var graph = Graph(1, "expr:${device.kitchen.temperature}");
        await executor.ExecuteCompiledAsync(TestPrograms.Walk(graph, registry, namespaces: options.ExpressionNamespaces),
            [], Guid.NewGuid(), executionId, Guid.NewGuid(),
            new EmptyServices(), [], [], WorkflowTraceMode.None, false, null, null,
            depth: 1, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: triggerId, triggerEnvelopeForRun: envelope,
            cancellationToken: CancellationToken.None);

        registry.Seen.Should().ContainSingle().Which.Should().Be(21.5d);
        received.Should().NotBeNull();
        received!.ExecutionId.Should().Be(executionId);
        received.TriggerId.Should().Be(triggerId);
        received.TriggerEnvelope.Should().BeSameAs(envelope);
    }

    [Fact]
    public async Task A_registered_resolver_receives_its_key_alone()
    {
        string[]? received = null;
        var options = new WorkflowGraphExecutorOptions
        {
            ExpressionNamespaces =
            [
                new WorkflowExpressionNamespace("device", 2, new TestCheck("device"), (_, _) =>
                    Task.FromResult<IExpressionNamespaceResolver>(new RecordingResolver(segments =>
                    {
                        received = segments;
                        return ResolveResult.Resolved(21.5);
                    })))
            ]
        };

        await ReadingAsync(options);

        received.Should().Equal("kitchen", "temperature");
    }

    private static WorkflowGraphExecutorOptions Options(IExpressionNamespaceResolver resolver, int keySegments = 1) => new()
    {
        ExpressionNamespaces =
        [
            new WorkflowExpressionNamespace(resolver.Namespace, keySegments, new TestCheck(resolver.Namespace),
                (_, _) => Task.FromResult(resolver))
        ]
    };

    private static async Task<object?> ReadingAsync(
        WorkflowGraphExecutorOptions options,
        IServiceProvider? services = null,
        int nodes = 1,
        string expression = "expr:${device.kitchen.temperature}")
    {
        var registry = new ReadingRegistry();
        var executor = new WorkflowGraphExecutor(
            registry, NullLoggerFactory.Instance, new SilentMetrics(),
            NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance, options);

        var graph = Graph(nodes, expression);
        await executor.ExecuteCompiledAsync(TestPrograms.Walk(graph, registry, namespaces: options.ExpressionNamespaces),
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            services ?? new EmptyServices(), [], [], WorkflowTraceMode.None, false, null, null,
            depth: 1, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None);

        return registry.Seen.Should().NotBeEmpty().And.Subject.First();
    }

    private static WorkflowDefinition Graph(int nodes, string expression) => new()
    {
        Nodes = [.. Enumerable.Range(1, nodes).Select(i => new WorkflowNodeDefinition
        {
            Id = $"n{i}",
            BlockTypeId = ReadingRegistry.BlockTypeId,
            Config = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["reading"] = System.Text.Json.JsonSerializer.SerializeToElement(expression)
            }
        })],
        Edges = []
    };

    private sealed class KitchenDevices : IExpressionNamespaceResolver
    {
        public string Namespace => "device";

        public ResolveResult Resolve(string[] segments) =>
            segments is ["kitchen", "temperature"]
                ? ResolveResult.Resolved(21.5)
                : ResolveResult.Failed();
    }

    private sealed class FixedNow : IExpressionNamespaceResolver
    {
        public string Namespace => "now";

        public ResolveResult Resolve(string[] segments) =>
            ResolveResult.Resolved(1999.0);
    }

    private sealed record TestCheck(string Namespace, string Term = "Number") : IExpressionNamespaceStaticCheck
    {
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, Term);
    }

    private sealed class RecordingResolver(Func<string[], ResolveResult> resolve) : IExpressionNamespaceResolver
    {
        public string Namespace => "device";
        public ResolveResult Resolve(string[] segments) => resolve(segments);
    }

    private sealed class ReadingRegistry : IWorkflowBlocks
    {
        public const string BlockTypeId = "test.reading";

        public List<object?> Seen { get; } = [];

        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == BlockTypeId ? new ReadingFactory(Seen) : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [new ReadingFactory(Seen)];
    }

    private sealed class ReadingFactory(List<object?> seen) : IWorkflowBlockFactory
    {
        public string BlockTypeId => ReadingRegistry.BlockTypeId;
        public string DisplayName => "Reading";
        public string Description => "Records what the walk resolved for one expression.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new ReadingBlock(seen);
    }

    private sealed class ReadingBlock(List<object?> seen) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            seen.Add(inputs.TryGetValue("reading", out var reading) ? reading : "<absent>");
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class SilentMetrics : IWorkflowMetrics
    {
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed record TestNamespace(string Namespace, double Value) : IExpressionNamespaceResolver
    {
        public ResolveResult Resolve(string[] segments) =>
            ResolveResult.Resolved(Value);
    }
}
