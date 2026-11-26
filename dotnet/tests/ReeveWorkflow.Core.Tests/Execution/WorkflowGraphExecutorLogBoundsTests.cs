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

public sealed class WorkflowGraphExecutorLogBoundsTests
{
    private const int Limit = 4000;

    private const string Flaky = "test.flaky";

    [Fact]
    public void The_writer_cuts_one_character_past_the_column_and_keeps_the_column_itself()
    {
        var entries = new List<WorkflowExecutionLogEntry>();
        var node = new WorkflowNodeDefinition { Id = "n", BlockTypeId = "test.block" };

        WorkflowGraphExecutor.AddLogEntry(entries, Guid.NewGuid(), node, LogEntryLevel.Warning,
            new string('m', Limit), false, null, null);
        WorkflowGraphExecutor.AddLogEntry(entries, Guid.NewGuid(), node, LogEntryLevel.Warning,
            new string('m', Limit + 1), false, null, null);

        entries[0].Message.Should().HaveLength(Limit, "a line the column holds is written as it was composed");
        entries[1].Message.Should().HaveLength(Limit, "and a longer one is cut, at the column and not before it");
    }

    [Fact]
    public async Task A_retry_line_carrying_a_host_exception_message_is_cut_to_the_column_too()
    {
        var world = new World();
        var logEntries = new List<WorkflowExecutionLogEntry>();
        var said = new string('e', Limit + 1);
        var head = $"Retrying block '{Flaky}' (attempt 1/1) due to: ";

        await world.Run(FlakyOnce(), logEntries, said);

        var retry = logEntries.Should()
            .ContainSingle(e => e.Message.StartsWith("Retrying block", StringComparison.Ordinal)).Subject;

        retry.Message.Should().Be(head + said[..(Limit - head.Length)],
            "what survives is the line's own words and the head of what the host said, cut at the column");
    }

    private static WorkflowDefinition FlakyOnce() => new()
    {
        Nodes =
        [
            new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
            new WorkflowNodeDefinition
            {
                Id = "a",
                BlockTypeId = Flaky,
                Config = new Dictionary<string, JsonElement>
                {
                    ["retryCount"] = JsonSerializer.SerializeToElement(1),
                    ["retryBackoffSeconds"] = JsonSerializer.SerializeToElement(0),
                    ["timeoutSeconds"] = JsonSerializer.SerializeToElement(5)
                }
            }
        ],
        Edges = [new WorkflowConnectionDefinition { Id = "e1", SourceNodeId = "start", TargetNodeId = "a" }]
    };

    private sealed class World
    {
        private int attempts;

        public Task Run(WorkflowDefinition definition, List<WorkflowExecutionLogEntry> logEntries, string said)
        {
            var executor = new WorkflowGraphExecutor(
                new Registry(this, said), NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
                NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance);

            return executor.ExecuteGraphAsync(
                [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
                logEntries, [], WorkflowTraceMode.Full, debugMode: false, iterationIndex: null, callStackJson: null,
                depth: 0, visitedNodeIds: [], isInsideTryCatch: false, inputParametersForRun: null,
                triggerIdForRun: null, triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
                stateExecutionId: null, compiled: TestPrograms.Walk(definition, new Registry(this, said)));
        }

        public bool FirstTry() => attempts++ == 0;
    }

    private sealed class Registry(World world, string said) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == Flaky ? new Factory(world, said) : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class Factory(World world, string said) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Flaky;
        public string DisplayName => Flaky;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new FlakyBlock(world, said);
    }

    private sealed class FlakyBlock(World world, string said) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            if (world.FirstTry())
                throw new HttpRequestException(said);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
