using System.Text.Json;
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

public sealed class EnumConfigRunTests
{
    private const string BlockId = "test.enum-reader";

    public static TheoryData<string, bool> Expressions => new()
    {
        { "expr:\"Toggle\"", true },
        { "expr:\"Scene\"", true },
        { "expr:\"toggle\"", false },
        { "expr:\"Other\"", false },
        { "expr:1", false },
        { "expr:true", false },
        { "expr:1 + 1", false },
    };

    [Theory]
    [MemberData(nameof(Expressions))]
    public async Task An_expression_result_reaches_the_block_only_when_it_is_one_of_the_names(string expression, bool reaches)
    {
        var block = new Reader();
        var run = () => Execute(block, new Dictionary<string, JsonElement> { ["Kind"] = JsonSerializer.SerializeToElement(expression) });

        if (reaches)
        {
            await run();
            block.Seen.Should().Equal(expression["expr:\"".Length..^1]);
            return;
        }

        var failure = (await run.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which;
        failure.InnerException.Should().BeOfType<WorkflowConfigValueNotAllowedException>()
            .Which.Message.Should().StartWith("config_value_not_allowed:").And.Contain("'Kind'").And.Contain("'Toggle', 'Dim', 'Scene'");
        block.Seen.Should().BeEmpty("the block must never see a value outside the set, and nothing coerces it");
    }

    [Fact]
    public async Task A_failing_enum_check_fails_the_node_under_its_own_error_policy_like_any_block_failure()
    {
        var block = new Reader();
        var node = Node(new Dictionary<string, JsonElement> { ["Kind"] = JsonSerializer.SerializeToElement("expr:\"Other\"") });
        node.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault);

        await ExecuteNode(block, node);

        block.Seen.Should().BeEmpty("the node failed before the block ran, and ContinueWithDefault carried the run on");
    }

    [Theory]
    [InlineData("")]
    [InlineData("expr:\"\"")]
    public async Task An_empty_text_is_absent_and_never_reaches_the_block_as_a_name(string stored)
    {
        var block = new Reader();

        await Execute(block, new Dictionary<string, JsonElement> { ["Kind"] = JsonSerializer.SerializeToElement(stored) });

        block.Seen.Should().Equal(new string?[] { null });
    }

    [Fact]
    public async Task An_absent_value_reaches_the_block_as_it_does_for_a_string_field()
    {
        var block = new Reader();

        await Execute(block, new Dictionary<string, JsonElement>());

        block.Seen.Should().Equal(new string?[] { null });
    }

    [Fact]
    public async Task A_field_that_is_not_an_enum_is_not_checked()
    {
        var block = new Reader(ConfigFieldType.String);

        await Execute(block, new Dictionary<string, JsonElement> { ["Kind"] = JsonSerializer.SerializeToElement("expr:\"Other\"") });

        block.Seen.Should().Equal("Other");
    }

    [Theory]
    [InlineData("Dim", true)]
    [InlineData("Bogus", false)]
    [InlineData("dim", false)]
    public async Task A_wired_value_reaches_the_block_only_when_it_is_one_of_the_names(string wired, bool reaches)
    {
        var block = new Reader(withPort: true);
        var source = new Source(wired);

        await AssertWired(block, source, [], reaches);
    }

    [Theory]
    [InlineData("Dim", true)]
    [InlineData("Bogus", false)]
    public async Task The_same_check_holds_after_a_resume_when_the_wired_name_arrives_as_a_plain_string(string wired, bool reaches)
    {
        var block = new Reader(withPort: true);
        var source = new Source(wired);
        var restored = new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["src"] = new Dictionary<string, object?> { ["Value"] = wired } };

        await AssertWired(block, source, restored, reaches);
        source.Ran.Should().BeFalse("a node with restored outputs is not run again");
    }

    private static async Task AssertWired(
        Reader block, Source source, Dictionary<string, IReadOnlyDictionary<string, object?>> outputs, bool reaches)
    {
        var edge = new WorkflowConnectionDefinition
        {
            Id = "e", SourceNodeId = "src", TargetNodeId = "n1",
            Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Kind"] = new { type = "direct", sourceOutputName = "Value" } }),
        };
        var definition = new WorkflowDefinition
        {
            Nodes = [new() { Id = "src", BlockTypeId = Source.Id }, new() { Id = "n1", BlockTypeId = BlockId }],
            Edges = [edge],
        };
        var executor = new WorkflowGraphExecutor(new Registry(block, source), NullLoggerFactory.Instance, new Metrics(), new Observer(), NullHelperResolver.Instance);
        var run = () => executor.ExecuteGraphAsync(
            outputs, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Services.Instance, [], [], WorkflowTraceMode.Full, false, null, null,
            depth: 1, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None,
            compiled: TestPrograms.Walk(definition, new Registry(block, source)));

        if (reaches)
        {
            await run();
            block.Seen.Should().Equal("Dim");
            return;
        }

        var failure = (await run.Should().ThrowAsync<WorkflowNodeExecutionException>()).Which;
        failure.InnerException.Should().BeOfType<WorkflowConfigValueNotAllowedException>()
            .Which.Message.Should().StartWith("config_value_not_allowed:").And.Contain("'Kind'");
        block.Seen.Should().BeEmpty("the block must never see a wired value outside the set");
    }

    private static WorkflowNodeDefinition Node(Dictionary<string, JsonElement> config) =>
        new() { Id = "n1", BlockTypeId = BlockId, Config = config };

    private static Task Execute(Reader block, Dictionary<string, JsonElement> config) => ExecuteNode(block, Node(config));

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> ExecuteNode(Reader block, WorkflowNodeDefinition node)
    {
        var registry = new Registry(block);
        var executor = new WorkflowGraphExecutor(registry, NullLoggerFactory.Instance, new Metrics(), new Observer(), NullHelperResolver.Instance);
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        var definition = new WorkflowDefinition { Nodes = [node], Edges = [] };
        return await executor.ExecuteCompiledAsync(TestPrograms.Walk(definition, registry),
            outputs, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Services.Instance, [], [], WorkflowTraceMode.Full, false, null, null,
            depth: 1, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None);
    }

    private sealed class Reader(ConfigFieldType type = ConfigFieldType.Enum, bool withPort = false) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public List<string?> Seen { get; } = [];
        public string BlockTypeId => BlockId;
        public string DisplayName => "Reader";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => withPort ? [new("Kind", "Kind", PortValueType.String, false)] : [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
            [new("Kind", "Kind", type, false) { AllowedValues = type == ConfigFieldType.Enum ? ["Toggle", "Dim", "Scene"] : null }];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            Seen.Add(inputs.TryGetValue("Kind", out var value) ? value as string : null);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class Source(string value) : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "test.enum-source";
        public bool Ran { get; private set; }
        public string BlockTypeId => Id;
        public string DisplayName => "Source";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new("Value", "Value", PortValueType.String, false)];
        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => [];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            Ran = true;
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Value"] = value });
        }
    }

    private sealed class Registry(Reader block, Source? source = null) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == BlockId ? block : blockTypeId == Source.Id ? source : null;
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => source is null ? [block] : [block, source];
    }

    private sealed class Metrics : IWorkflowMetrics
    {
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class Observer : IWorkflowNodeObserver
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

    private sealed class Services : IServiceProvider
    {
        public static readonly Services Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
