using ReeveWorkflow.Core.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowGraphTimeZoneTests
{
    private static readonly TimeZoneInfo HalfPastFive =
        TimeZoneInfo.CreateCustomTimeZone("test/+0530", TimeSpan.FromMinutes(330), "Test", "Test");

    private static readonly TimeZoneInfo MinusThree =
        TimeZoneInfo.CreateCustomTimeZone("test/-0300", TimeSpan.FromHours(-3), "Test", "Test");

    [Fact]
    public async Task A_deployment_whose_zone_is_a_setting_needs_no_lookup()
    {
        var stamped = await StampAsync(new WorkflowGraphExecutorOptions { TimeZone = HalfPastFive });

        stamped.Should().EndWith("+05:30");
    }

    [Fact]
    public async Task A_host_whose_zone_is_a_lookup_gets_the_zone_it_looked_up()
    {
        var options = new WorkflowGraphExecutorOptions
        {
            TimeZone = HalfPastFive,
            ResolveTimeZone = (_, _) => Task.FromResult(MinusThree)
        };

        var stamped = await StampAsync(options);

        stamped.Should().EndWith("-03:00", "a resolved zone is the answer, not a second opinion beside the setting");
    }

    [Fact]
    public async Task The_lookup_happens_once_per_walk_inside_that_walks_own_scope()
    {
        var scopes = new List<IServiceProvider>();
        var services = new EmptyServices();
        var options = new WorkflowGraphExecutorOptions
        {
            ResolveTimeZone = (sp, _) =>
            {
                scopes.Add(sp);
                return Task.FromResult(MinusThree);
            }
        };

        await StampAsync(options, services, nodes: 2);

        scopes.Should().ContainSingle().Which.Should().BeSameAs(services);
    }

    private static async Task<string> StampAsync(
        WorkflowGraphExecutorOptions options, IServiceProvider? services = null, int nodes = 1)
    {
        var registry = new StampingRegistry();
        var executor = new WorkflowGraphExecutor(
            registry, NullLoggerFactory.Instance, new SilentMetrics(),
            NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance, options);

        var graph = Graph(nodes);
        await executor.ExecuteCompiledAsync(TestPrograms.Walk(graph, registry),
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            services ?? new EmptyServices(), [], [], WorkflowTraceMode.None, false, null, null,
            depth: 1, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None);

        return registry.Stamps.Should().NotBeEmpty().And.Subject.First();
    }

    private static WorkflowDefinition Graph(int nodes) => new()
    {
        Nodes = [.. Enumerable.Range(1, nodes).Select(i => new WorkflowNodeDefinition
        {
            Id = $"n{i}",
            BlockTypeId = StampingRegistry.BlockTypeId,
            Config = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["stamp"] = System.Text.Json.JsonSerializer.SerializeToElement("expr:${now.localiso}")
            }
        })],
        Edges = []
    };

    private sealed class StampingRegistry : IWorkflowBlocks
    {
        public const string BlockTypeId = "test.stamp";

        public List<string> Stamps { get; } = [];

        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == BlockTypeId ? new StampFactory(Stamps) : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [new StampFactory(Stamps)];
    }

    private sealed class StampFactory(List<string> stamps) : IWorkflowBlockFactory
    {
        public string BlockTypeId => StampingRegistry.BlockTypeId;
        public string DisplayName => "Stamp";
        public string Description => "Records what the walk resolved for a local-time expression.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new StampBlock(stamps);
    }

    private sealed class StampBlock(List<string> stamps) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            stamps.Add(inputs.TryGetValue("stamp", out var stamp) ? stamp?.ToString() ?? "" : "");
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
}
