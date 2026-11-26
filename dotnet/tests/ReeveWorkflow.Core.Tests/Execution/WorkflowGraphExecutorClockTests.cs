using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Polly.Timeout;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowGraphExecutorClockTests
{
    private const string Hang = "test.hang";
    private const int DeadlineSeconds = 30;

    [Fact]
    public async Task A_node_deadline_fires_when_the_executors_clock_passes_it()
    {
        var world = new World(new FakeTimeProvider());

        var run = world.Start();
        await world.Entered();
        world.Clock.Advance(TimeSpan.FromSeconds(DeadlineSeconds - 1));
        run.IsCompleted.Should().BeFalse("a second of the deadline is still left on the executor's clock");

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        await TimedOut(run);
    }

    [Fact]
    public async Task A_second_executor_with_the_same_policy_keeps_its_own_clock()
    {
        var first = new World(new FakeTimeProvider());
        var firstRun = first.Start();
        await first.Entered();
        first.Clock.Advance(TimeSpan.FromSeconds(DeadlineSeconds));
        await TimedOut(firstRun);

        var second = new World(new FakeTimeProvider());
        var secondRun = second.Start();
        await second.Entered();
        second.Clock.Advance(TimeSpan.FromSeconds(DeadlineSeconds));
        await TimedOut(secondRun);
    }

    private static async Task TimedOut(Task run)
    {
        var act = () => run.WaitAsync(TimeSpan.FromSeconds(10));
        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().NotBeOfType<TimeoutException>("the run has to end on the fake clock, not on the guard");
        Chain(thrown.Which).Should().Contain(e => e is TimeoutRejectedException);
    }

    private static IEnumerable<Exception> Chain(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            yield return e;
    }

    private static WorkflowDefinition OneHangingNode() => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            new WorkflowNodeDefinition
            {
                Id = "a", BlockTypeId = Hang,
                Config = new Dictionary<string, JsonElement>
                {
                    ["timeoutSeconds"] = JsonSerializer.SerializeToElement(DeadlineSeconds)
                }
            }
        ],
        Edges = [new WorkflowConnectionDefinition { Id = "e1", SourceNodeId = "start", TargetNodeId = "a" }]
    };

    private sealed class World(FakeTimeProvider clock)
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeTimeProvider Clock => clock;

        public Task Entered() => entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task Start()
        {
            var executor = new WorkflowGraphExecutor(
                new Registry(this), NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
                NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance,
                new WorkflowGraphExecutorOptions { TimeProvider = clock });

            return executor.ExecuteGraphAsync(
                [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
                [], [], WorkflowTraceMode.Full, debugMode: false, iterationIndex: null, callStackJson: null,
                depth: 0, visitedNodeIds: [], isInsideTryCatch: false, inputParametersForRun: null,
                triggerIdForRun: null, triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
                stateExecutionId: null, compiled: TestPrograms.Walk(OneHangingNode(), new Registry(this)));
        }

        public void SignalEntered() => entered.TrySetResult();
    }

    private sealed class Registry(World world) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId == Hang ? new Factory(world) : null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class Factory(World world) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Hang;
        public string DisplayName => Hang;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => false;
        public IWorkflowBlockPlugin Create() => new HangingBlock(world);
    }

    private sealed class HangingBlock(World world) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            world.SignalEntered();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
