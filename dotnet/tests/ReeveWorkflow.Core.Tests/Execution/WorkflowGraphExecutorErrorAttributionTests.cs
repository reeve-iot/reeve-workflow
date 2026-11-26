using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowGraphExecutorErrorAttributionTests
{
    private const string FailingBlockTypeId = "test.failing";

    [Theory]
    [InlineData(true, "TryCatch")]
    [InlineData(false, null)]
    public async Task AFailureInsideATryCatchIsReportedAsCaught(bool isInsideTryCatch, string? expectedCaughtBy)
    {
        var metrics = new RecordingMetrics();
        var executor = Build(metrics);
        var steps = new List<WorkflowExecutionStep>();

        var run = () => executor.ExecuteGraphAsync(
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            EmptyServices.Instance, [], steps, WorkflowTraceMode.Full, false, null, null,
            depth: 1, visitedNodeIds: [], isInsideTryCatch: isInsideTryCatch,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None,
            compiled: TestPrograms.Walk(OneFailingNode(), new SingleBlockRegistry()));

        await run.Should().ThrowAsync<WorkflowNodeExecutionException>(
            "FailWorkflow is still the policy — being inside a TryCatch changes who is blamed, not what happens");

        metrics.NodeErrors.Should().ContainSingle().Which.Caught.Should().Be(isInsideTryCatch,
            "the caught flag is the metric's only record of whether anything was going to handle this");

        var errorJson = steps.Should().ContainSingle().Subject.ErrorJson;
        if (expectedCaughtBy is null)
            errorJson.Should().Contain("\"caughtBy\":null");
        else
            errorJson.Should().Contain($"\"caughtBy\":\"{expectedCaughtBy}\"");
    }

    private static WorkflowGraphExecutor Build(IWorkflowMetrics metrics) =>
        new(new SingleBlockRegistry(), NullLoggerFactory.Instance, metrics,
            new SilentObserver(), NullHelperResolver.Instance);

    private static WorkflowDefinition OneFailingNode() => new()
    {
        Nodes = [new WorkflowNodeDefinition { Id = "n1", BlockTypeId = FailingBlockTypeId }],
        Edges = []
    };

    private sealed class RecordingMetrics : IWorkflowMetrics
    {
        public List<(string BlockType, string Policy, bool Caught, string ExceptionType)> NodeErrors { get; } = [];

        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) =>
            NodeErrors.Add((blockType, policy, caught, exceptionType));

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class SilentObserver : IWorkflowNodeObserver
    {
        public Task NotifyNodeStartedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NotifyNodeCompletedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> outputs,
            long durationMs, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NotifyNodeFailedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, string error, long durationMs,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SingleBlockRegistry : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == FailingBlockTypeId ? new FailingBlockFactory() : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [new FailingBlockFactory()];
    }

    private sealed class FailingBlockFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => FailingBlockTypeId;
        public string DisplayName => "Always Fails";
        public string Description => "Throws on every attempt.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new FailingBlock();
    }

    private sealed class FailingBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("block failed");
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
