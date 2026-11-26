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

public sealed class WorkflowGraphExecutorStepBoundsTests
{
    private const string Step = "test.step";

    [Fact]
    public async Task A_walk_past_the_step_cap_records_its_first_steps_and_still_runs_every_node()
    {
        var steps = new List<WorkflowExecutionStep>();
        var ran = new List<string>();

        await Run(Chain("a", "b", "c", "d"), steps, ran, maxRecordedSteps: 2);

        ran.Should().Equal(["a", "b", "c", "d"], "the cap bounds what the run records, not what it does");
        steps.Select(s => s.NodeId).Should().Equal(["a", "b"]);
        steps.Select(s => s.SortOrder).Should().Equal([0, 1]);
    }

    [Fact]
    public async Task A_walk_under_the_step_cap_records_every_step()
    {
        var steps = new List<WorkflowExecutionStep>();

        await Run(Chain("a", "b", "c"), steps, [], maxRecordedSteps: 3);

        steps.Select(s => s.NodeId).Should().Equal(["a", "b", "c"]);
    }

    [Fact]
    public async Task The_step_that_failed_the_run_is_recorded_past_the_cap()
    {
        var steps = new List<WorkflowExecutionStep>();

        var act = () => Run(Chain("a", "b", "c", "fail"), steps, [], maxRecordedSteps: 2);

        await act.Should().ThrowAsync<WorkflowNodeExecutionException>();
        steps.Select(s => s.NodeId).Should().Equal(["a", "b", "fail"]);
        steps[^1].Status.Should().Be(WorkflowExecutionStepStatus.Failed);
    }

    [Fact]
    public async Task A_walk_past_the_cap_holds_no_more_log_entries_than_the_cap()
    {
        var entries = new List<WorkflowExecutionLogEntry>();
        var definition = Chain("fail-1", "fail-2", "fail-3", "fail-4");
        foreach (var node in definition.Nodes.Where(n => n.BlockTypeId == Step))
            node.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault, "{}");

        await Run(definition, [], [], maxRecordedSteps: 2, entries);

        entries.Should().HaveCount(2, "each caught failure logs a warning, and the list is held until the walk ends");
        entries.Select(e => e.NodeId).Should().Equal(["fail-1", "fail-2"]);
    }

    private static Task Run(WorkflowDefinition definition, List<WorkflowExecutionStep> steps, List<string> ran, int maxRecordedSteps,
        List<WorkflowExecutionLogEntry>? entries = null)
    {
        var registry = new Registry(ran);
        var executor = new WorkflowGraphExecutor(
            registry, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
            NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance,
            new WorkflowGraphExecutorOptions { MaxRecordedSteps = maxRecordedSteps });

        return executor.ExecuteGraphAsync(
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
            entries ?? [], steps, WorkflowTraceMode.Errors, debugMode: false, iterationIndex: null, callStackJson: null,
            depth: 0, visitedNodeIds: [], isInsideTryCatch: false, inputParametersForRun: null,
            triggerIdForRun: null, triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
            stateExecutionId: null, compiled: TestPrograms.Walk(definition, registry));
    }

    private static WorkflowDefinition Chain(params string[] ids)
    {
        var nodes = new List<WorkflowNodeDefinition>
        {
            new() { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }
        };
        nodes.AddRange(ids.Select(id => new WorkflowNodeDefinition { Id = id, BlockTypeId = Step }));
        var order = new[] { "start" }.Concat(ids).ToArray();
        return new WorkflowDefinition
        {
            Nodes = nodes,
            Edges = [.. order.Zip(order.Skip(1), (from, to) => new WorkflowConnectionDefinition { Id = $"{from}-{to}", SourceNodeId = from, TargetNodeId = to })]
        };
    }

    private sealed class Registry(List<string> ran) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId == Step ? new Factory(ran) : null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class Factory(List<string> ran) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Step;
        public string DisplayName => Step;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new Block(ran);
    }

    private sealed class Block(List<string> ran) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            ran.Add(context.NodeId!);
            if (context.NodeId!.StartsWith("fail", StringComparison.Ordinal))
                throw new InvalidOperationException("this block fails");
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
