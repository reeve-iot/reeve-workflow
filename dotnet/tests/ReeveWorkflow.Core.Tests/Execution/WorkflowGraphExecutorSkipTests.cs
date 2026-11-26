using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowGraphExecutorSkipTests
{
    [Fact]
    public async Task Skip_follows_empty_mapping_edges_transitively_and_leaves_other_nodes_running()
    {
        var blocks = new Registry();
        var graph = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "skip", BlockTypeId = "test.fail", ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.Skip) },
                new WorkflowNodeDefinition { Id = "next", BlockTypeId = "test.record" },
                new WorkflowNodeDefinition { Id = "after", BlockTypeId = "test.record" },
                new WorkflowNodeDefinition { Id = "independent", BlockTypeId = "test.record" },
            ],
            Edges =
            [
                EmptyEdge("skip-next", "skip", "next"),
                EmptyEdge("next-after", "next", "after"),
            ]
        };
        var executor = new WorkflowGraphExecutor(blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
            NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance);

        await executor.ExecuteGraphAsync([], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance,
            [], [], WorkflowTraceMode.None, false, null, null, depth: 1, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None, compiled: TestPrograms.Walk(graph, blocks));

        blocks.Ran.Should().BeEquivalentTo(["skip", "independent"],
            "the failed node's empty mapping edges still define its descendants");
    }

    private static WorkflowConnectionDefinition EmptyEdge(string id, string source, string target) => new()
    {
        Id = id, SourceNodeId = source, TargetNodeId = target, Mappings = null
    };

    private sealed class Registry : IWorkflowBlocks
    {
        public List<string> Ran { get; } = [];
        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId switch
        {
            "test.fail" => new Factory(blockTypeId, Ran, true),
            "test.record" => new Factory(blockTypeId, Ran, false),
            _ => null
        };
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class Factory(string blockTypeId, List<string> ran, bool fails) : IWorkflowBlockFactory
    {
        public string BlockTypeId => blockTypeId;
        public string DisplayName => blockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new Plugin(ran, fails);
    }

    private sealed class Plugin(List<string> ran, bool fails) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(IReadOnlyDictionary<string, object?> inputs,
            WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            ran.Add(context.NodeId!);
            return fails
                ? throw new InvalidOperationException("failed")
                : Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
