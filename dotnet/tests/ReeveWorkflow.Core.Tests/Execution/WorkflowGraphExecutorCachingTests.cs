using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowGraphExecutorCachingTests
{
    private const string Flaky = "test.flaky";

    [Fact]
    public async Task Two_nodes_sharing_one_policy_each_log_their_own_retries()
    {
        var world = new World();
        var logEntries = new List<WorkflowExecutionLogEntry>();

        await world.Run(TwoFlakyNodes(), logEntries);

        var retries = logEntries.Where(e => e.Message.StartsWith("Retrying block", StringComparison.Ordinal)).ToList();
        retries.Select(e => e.NodeId).Should().BeEquivalentTo(["a", "b"],
            "each node retried once, and each line belongs to the node that retried");
    }

    [Fact]
    public async Task A_second_run_over_the_same_policy_still_logs_its_own_retries()
    {
        var world = new World();
        await world.Run(TwoFlakyNodes(), []);

        world.ForgetAttempts();
        var logEntries = new List<WorkflowExecutionLogEntry>();
        await world.Run(TwoFlakyNodes(), logEntries);

        logEntries.Where(e => e.Message.StartsWith("Retrying block", StringComparison.Ordinal))
            .Select(e => e.NodeId).Should().BeEquivalentTo(["a", "b"]);
    }

    private static WorkflowDefinition TwoFlakyNodes()
    {
        var config = new Dictionary<string, JsonElement>
        {
            ["retryCount"] = JsonSerializer.SerializeToElement(1),
            ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0),
            ["timeoutSeconds"] = JsonSerializer.SerializeToElement(5)
        };
        return new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition { Id = "a", BlockTypeId = Flaky, Config = config },
                new WorkflowNodeDefinition { Id = "b", BlockTypeId = Flaky, Config = config }
            ],
            Edges =
            [
                new WorkflowConnectionDefinition { Id = "e1", SourceNodeId = "start", TargetNodeId = "a" },
                new WorkflowConnectionDefinition { Id = "e2", SourceNodeId = "a", TargetNodeId = "b" }
            ]
        };
    }

    private sealed class World
    {
        private readonly Dictionary<string, int> attempts = new(StringComparer.Ordinal);
        private readonly WorkflowGraphExecutor executor;

        public World() => executor = new WorkflowGraphExecutor(
            new Registry(this), NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
            NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance);

        public void ForgetAttempts() => attempts.Clear();

        public Task Run(WorkflowDefinition definition, List<WorkflowExecutionLogEntry> logEntries)
        {
            return executor.ExecuteGraphAsync(
                [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
                logEntries, [], WorkflowTraceMode.Full, debugMode: false, iterationIndex: null, callStackJson: null,
                depth: 0, visitedNodeIds: [], isInsideTryCatch: false, inputParametersForRun: null,
                triggerIdForRun: null, triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
                stateExecutionId: null, compiled: TestPrograms.Walk(definition, new Registry(this)));
        }

        public bool FirstTryOf(string nodeId)
        {
            attempts.TryGetValue(nodeId, out var seen);
            attempts[nodeId] = seen + 1;
            return seen == 0;
        }
    }

    private sealed class Registry(World world) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == Flaky ? new Factory(world) : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class Factory(World world) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Flaky;
        public string DisplayName => Flaky;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new FlakyBlock(world);
    }

    private sealed class FlakyBlock(World world) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            if (world.FirstTryOf(context.NodeId))
                throw new HttpRequestException("the far end hung up");
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
