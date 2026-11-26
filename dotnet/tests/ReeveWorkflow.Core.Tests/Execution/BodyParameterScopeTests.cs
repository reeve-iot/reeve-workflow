using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class BodyParameterScopeTests
{
    private const string Forward = "test.forward";
    private const string Read = "test.read";

    [Fact]
    public async Task A_name_the_container_forwards_by_passthrough_but_did_not_receive_reads_null_in_the_body()
    {
        var seen = new List<IReadOnlyDictionary<string, object?>>();

        await WalkAsync(seen, passthrough: ["Mode"], readsName: "Mode");

        seen.Should().ContainSingle().Which.Should().ContainKey("v").WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task A_name_the_body_was_handed_reads_as_handed()
    {
        var seen = new List<IReadOnlyDictionary<string, object?>>();

        await WalkAsync(seen, passthrough: ["Mode"], readsName: "Mode", handed: new Dictionary<string, object?> { ["Mode"] = "away" });

        seen.Should().ContainSingle().Which["v"].Should().Be("away");
    }

    [Fact]
    public async Task A_name_the_container_does_not_forward_still_fails_the_read_with_value_absent()
    {
        var seen = new List<IReadOnlyDictionary<string, object?>>();

        var failure = await FluentActions.Awaiting(() => WalkAsync(seen, passthrough: [], readsName: "Mode"))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        Exception cause = failure.Which;
        while (cause.InnerException is not null)
            cause = cause.InnerException;
        cause.Should().BeOfType<ExpressionEvaluationException>().Which.Category.Should().Be("value-absent");
        seen.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unsupplied_optional_declared_branch_input_reads_null()
    {
        var seen = new List<IReadOnlyDictionary<string, object?>>();
        var blocks = new Blocks(seen, new Dictionary<string, object?>(), declaresOptionalMode: true);
        var body = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition
                {
                    Id = "read", BlockTypeId = Read,
                    Config = new Dictionary<string, JsonElement>
                    {
                        ["v"] = JsonSerializer.SerializeToElement("expr:${parameter.Mode}")
                    }
                }
            ],
            Edges = []
        };
        var definition = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId },
                new WorkflowNodeDefinition
                {
                    Id = "box", BlockTypeId = Forward,
                    SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body }
                }
            ],
            Edges = [new WorkflowConnectionDefinition { Id = "entry", SourceNodeId = "start", TargetNodeId = "box", Mappings = JsonDocument.Parse("{}").RootElement }]
        };
        var signature = new CanvasSignature([], []);
        var result = WorkflowCompiler.Compile(definition, new CatalogueBlockShapeSource(blocks), signature);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        var compiled = CompiledWalk.Root(result.Program!);
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
            NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance);

        await executor.ExecuteGraphAsync([], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new NoServices(), [], [],
            WorkflowTraceMode.None, false, null, null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None, compiled: compiled);

        seen.Should().ContainSingle().Which.Should().ContainKey("v").WhoseValue.Should().BeNull();
    }

    private static async Task WalkAsync(
        List<IReadOnlyDictionary<string, object?>> seen, List<string> passthrough, string readsName,
        IReadOnlyDictionary<string, object?>? handed = null)
    {
        var body = new WorkflowDefinition
        {
            Nodes =
            [
                new WorkflowNodeDefinition
                {
                    Id = "read", BlockTypeId = Read,
                    Config = new Dictionary<string, JsonElement> { ["v"] = JsonSerializer.SerializeToElement("expr:${parameter." + readsName + "}") }
                }
            ],
            Edges = []
        };
        var container = new WorkflowNodeDefinition
        {
            Id = "box", BlockTypeId = Forward, Passthrough = passthrough,
            SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body }
        };
        var blocks = new Blocks(seen, handed ?? new Dictionary<string, object?>());
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
            NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance, new WorkflowGraphExecutorOptions());
        var root = new WorkflowDefinition { Nodes = [container], Edges = [] };
        var compiled = TestPrograms.Walk(root, blocks, parameters: new Dictionary<string, string> { [readsName] = "String?" });

        await executor.ExecuteCompiledAsync(compiled,
            [], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new NoServices(), [], [], WorkflowTraceMode.None, false, null, null,
            depth: 1, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None);
    }

    private sealed class Blocks(
        List<IReadOnlyDictionary<string, object?>> seen,
        IReadOnlyDictionary<string, object?> handed,
        bool declaresOptionalMode = false) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId switch
        {
            Forward => new ForwardFactory(handed, declaresOptionalMode),
            Read => new ReadFactory(seen),
            _ => null
        };

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() =>
            [new ForwardFactory(handed, declaresOptionalMode), new ReadFactory(seen)];
    }

    private sealed class ForwardFactory(IReadOnlyDictionary<string, object?> handed, bool declaresOptionalMode = false) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Forward;
        public string DisplayName => "Forward";
        public string Description => "Calls its Body with the inputs it was handed, as a conditional does.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<string> SubCanvasNames => ["Body"];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) => declaresOptionalMode
            ? [new BlockPortDefinition("Mode", "Mode", PortValueType.String, false)]
            : [];
        public IWorkflowBlockPlugin Create() => new ForwardBlock(handed);
    }

    private sealed class ForwardBlock(IReadOnlyDictionary<string, object?> handed) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            context.SubCanvasExecutor!.ExecuteAsync("Body", handed, cancellationToken);
    }

    private sealed class ReadFactory(List<IReadOnlyDictionary<string, object?>> seen) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Read;
        public string DisplayName => "Read";
        public string Description => "Records the inputs it was handed.";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new BlockPortDefinition("v", "v", PortValueType.OfDeclaration("String?"), false)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new ReadBlock(seen);
    }

    private sealed class ReadBlock(List<IReadOnlyDictionary<string, object?>> seen) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            seen.Add(inputs);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
