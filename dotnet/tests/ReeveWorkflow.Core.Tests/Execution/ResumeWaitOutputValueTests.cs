using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ResumeWaitOutputValueTests
{
    [Fact]
    public async Task Seeded_trigger_data_reaches_a_string_port_as_json_text_only_through_a_jsonText_projection()
    {
        object? received = null;
        var blocks = new TestBlocks(new StringBodyFactory(value => received = value), new TriggerDataFactory());
        var direct = Definition(DurableSleepWorld.Direct("Body", "TriggerData"));
        var projected = Definition(new Dictionary<string, object>
        {
            ["Body"] = new { type = "direct", sourceOutputName = "TriggerData", projection = new { path = "$", mode = "jsonText" } },
        });

        WorkflowCompiler.Compile(direct, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty).Diagnostics
            .Select(d => $"{d.RuleId}@{d.NodeId}.{d.PortName}").Should().Equal("json-projection-invalid@request.Body");
        var program = WorkflowCompiler.Compile(projected, new CatalogueBlockShapeSource(blocks), CanvasSignature.Empty).Program!;
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance, NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance);

        await executor.ExecuteGraphAsync(
            new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["wait"] = new Dictionary<string, object?> { ["TriggerData"] = JsonDocument.Parse("""{"body": {"ok": true}}""").RootElement.Clone() }
            },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new EmptyServices(), [], [], WorkflowTraceMode.None,
            debugMode: false, iterationIndex: null, callStackJson: null, depth: 0, visitedNodeIds: [], isInsideTryCatch: false,
            inputParametersForRun: null, triggerIdForRun: null, triggerEnvelopeForRun: null,
            cancellationToken: CancellationToken.None,
            compiled: CompiledWalk.Root(program));

        received.Should().Be("{\"body\":{\"ok\":true}}");
    }

    private static WorkflowDefinition Definition(object mappings)
    {
        var json = $$$"""
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
            {"id":"wait","blockTypeId":"test.wait","position":{"x":120,"y":0},"configuration":{}},
            {"id":"request","blockTypeId":"test.request","position":{"x":240,"y":0},"configuration":{}}],
            "edges":[{"id":"start-wait","sourceNodeId":"start","targetNodeId":"wait","mappings":{}},
            {"id":"wait-request","sourceNodeId":"wait","targetNodeId":"request","mappings":{{{JsonSerializer.Serialize(mappings)}}}}]}
            """;
        return StoredWorkflowDocument.Read(json, out var fault)?.Definition ?? throw new InvalidOperationException(fault!.Describe());
    }

    [Fact]
    public void A_satisfied_wait_builder_keeps_trigger_data_with_a_repeated_key()
    {
        var context = new ResumeContext
        {
            SuspensionId = Guid.NewGuid(),
            ExecutionLogId = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            BlockId = "wait",
            Kind = WorkflowSuspensionKind.WaitForTrigger,
            OutcomeJson = """{"outcome":"satisfied","triggerData":{"nested":{"a":1,"a":2}}}""",
            StatePayloadJson = "{}",
            SuspendedAtUtc = DateTimeOffset.UtcNow
        };

        var output = WorkflowExecutionHelpers.BuildResumeBlockOutput(context);

        output["Outcome"].Should().Be("satisfied");
        output["TriggerData"].Should().BeOfType<JsonElement>().Which.GetRawText()
            .Should().Be("""{"nested":{"a":1,"a":2}}""");
        output["triggerData"].Should().BeSameAs(output["TriggerData"]);
    }

    [Theory]
    [InlineData("""{"outcome":"timed_out","triggerData":{"a":1},"lastEvaluation":true}""", null, null)]
    [InlineData("""{"outcome":"satisfied","triggerData":{"a":1},"lastEvaluation":true}""", """{"a":1}""", true)]
    [InlineData("""{"outcome":"satisfied","triggerData":null,"lastEvaluation":false}""", null, false)]
    public void A_resumed_wait_writes_trigger_data_as_json_and_last_evaluation_as_a_boolean_and_both_null_on_a_timeout(
        string outcome, string? triggerData, bool? lastEvaluation)
    {
        var output = WorkflowExecutionHelpers.BuildResumeBlockOutput(Context(outcome));

        if (triggerData is null)
            output["TriggerData"].Should().BeNull();
        else
            output["TriggerData"].Should().BeOfType<JsonElement>().Which.GetRawText().Should().Be(triggerData);
        output["LastEvaluation"].Should().Be(lastEvaluation);
    }

    [Theory]
    [InlineData(WorkflowSuspensionKind.WaitForTrigger)]
    [InlineData(WorkflowSuspensionKind.WaitTemplate)]
    public void A_timed_out_trigger_or_template_wait_resumes_with_trigger_data_and_last_evaluation_null(WorkflowSuspensionKind kind)
    {
        var output = WorkflowExecutionHelpers.BuildResumeBlockOutput(
            Context("""{"outcome":"timed_out","triggerData":{"a":1},"lastEvaluation":true}""", kind));

        output.Should().ContainKey("TriggerData").WhoseValue.Should().BeNull();
        output.Should().ContainKey("LastEvaluation").WhoseValue.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"outcome":"satisfied","lastEvaluation":{"nested":1}}""")]
    [InlineData("""{"outcome":3}""")]
    [InlineData("""not json""")]
    public void A_wait_outcome_that_does_not_read_is_refused_rather_than_resumed_as_a_timeout(string outcome)
    {
        var build = () => WorkflowExecutionHelpers.BuildResumeBlockOutput(Context(outcome));

        build.Should().Throw<JsonException>("a parked run whose outcome does not read is abandoned");
    }

    [Fact]
    public void A_trigger_wait_whose_data_names_lastEvaluation_resumes_without_reading_it()
    {
        var output = WorkflowExecutionHelpers.BuildResumeBlockOutput(
            Context("""{"outcome":"satisfied","triggerData":{"a":1},"lastEvaluation":{"nested":1}}""", WorkflowSuspensionKind.WaitForTrigger));

        output["Outcome"].Should().Be("satisfied");
        output.Should().NotContainKey("LastEvaluation");
    }

    private static ResumeContext Context(string outcomeJson, WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitTemplate) => new()
    {
        SuspensionId = Guid.NewGuid(),
        ExecutionLogId = Guid.NewGuid(),
        WorkflowId = Guid.NewGuid(),
        BlockId = "wait",
        Kind = kind,
        OutcomeJson = outcomeJson,
        StatePayloadJson = "{}",
        SuspendedAtUtc = DateTimeOffset.UtcNow
    };

    private sealed class TestBlocks(params IWorkflowBlockFactory[] factories) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => factories.FirstOrDefault(f => f.BlockTypeId == blockTypeId);
        public bool ContainsPrivilegedBlock(string? definitionJson) => false;
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => factories;
    }

    private sealed class TriggerDataFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => "test.wait";
        public string DisplayName => "test.wait";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new("TriggerData", "TriggerData", PortValueType.Of("Json?"), false)];
        public IWorkflowBlockPlugin Create() => throw new InvalidOperationException("A seeded node does not run.");
    }

    private sealed class StringBodyFactory(Action<object?> received) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "test.request";
        public string DisplayName => "test.request";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [new("Body", "Body", PortValueType.String, false)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new BodyBlock(received);
    }

    private sealed class BodyBlock(Action<object?> received) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            received(inputs["Body"]);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
