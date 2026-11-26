using System.Text.Json.Nodes;
using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunOutputSanitizationTests
{
    [Fact]
    public async Task A_run_result_stored_on_the_execution_log_masks_nested_output_values()
    {
        var payload = JsonNode.Parse("{\"deep\":[{\"secret\":\"hidden\",\"label\":\"visible\"}]}")!;
        var world = Producing(("payload", payload));
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.WorkflowId, null, new Dictionary<string, object?>());

        var outputsJson = world.Journal.Logs.Values.Should().ContainSingle().Subject.OutputsJson;
        outputsJson.Should().NotBeNull();
        var output = JsonNode.Parse(outputsJson!)!;
        output["payload"]!["deep"]![0]!["secret"]!.GetValue<string>().Should().Be("***");
        output["payload"]!["deep"]![0]!["label"]!.GetValue<string>().Should().Be("visible");
        world.Journal.Logs.Values.Single().ResultOutputsJson.Should().BeNull("ordinary runs are not retained as raw parent payloads");
    }

    [Fact]
    public async Task A_run_result_masks_secrets_through_deep_mixed_containers()
    {
        JsonNode nested = new JsonObject { ["secret"] = "hidden", ["sibling"] = "visible" };
        for (var i = 0; i < 12; i++)
            nested = new JsonObject { ["layer"] = new JsonArray(nested) };
        var world = Producing(("payload", nested));
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.WorkflowId, null, new Dictionary<string, object?>());

        var output = JsonNode.Parse(world.Journal.Logs.Values.Single().OutputsJson!)!["payload"]!;
        for (var i = 0; i < 12; i++)
            output = output["layer"]![0]!;
        output["secret"]!.GetValue<string>().Should().Be("***");
        output["sibling"]!.GetValue<string>().Should().Be("visible");
    }

    [Fact]
    public async Task A_dispatched_run_stores_raw_untruncated_parent_outputs_separately_from_its_display_result()
    {
        const string graph = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"src","blockTypeId":"test.produce"},{"id":"out","blockTypeId":"WorkflowOutput"}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"src","mappings":{}},{"id":"out-0","sourceNodeId":"src","targetNodeId":"out","mappings":{"auth":{"type":"direct","sourceOutputName":"auth","projection":{"path":"$"}}}},{"id":"out-1","sourceNodeId":"start","targetNodeId":"out","mappings":{"note":{"type":"direct","sourceOutputName":"note"}}}]}""";
        var world = new RunLifecycleWorld(definitionJson: graph, inputParametersJson: """[{"name":"note","type":"String"}]""",
            outputParametersJson: """[{"name":"auth","type":"Json"},{"name":"note","type":"String"}]""");
        world.AdditionalBlocks = id => id == "test.produce" ? new ProducingFactory([("auth", JsonNode.Parse("{\"token\":\"t-raw\"}"))]) : null;
        using var engine = world.Engine();
        var note = new string('n', 20_000);

        var immediate = await engine.ExecuteAsync(world.WorkflowId, null, new Dictionary<string, object?> { ["note"] = note },
            parentExecutionId: Guid.NewGuid());

        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        var display = JsonNode.Parse(log.OutputsJson!)!;
        display["auth"]!["token"]!.GetValue<string>().Should().Be("***");
        display["note"]!.GetValue<string>().Should().EndWith("...[truncated]");
        var parent = JsonNode.Parse(log.ResultOutputsJson!)!;
        parent["auth"]!["token"]!.GetValue<string>().Should().Be("t-raw");
        parent["note"]!.GetValue<string>().Should().Be(note);
        JsonSerializer.SerializeToNode(immediate["auth"])!["token"]!.GetValue<string>().Should().Be("t-raw");
        immediate["note"].Should().Be(note);
    }

    [Fact]
    public async Task An_unwritable_dispatched_result_uses_the_safe_display_fallback()
    {
        const string graph = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"unwritable","blockTypeId":"test.unwritable"},{"id":"out","blockTypeId":"WorkflowOutput"}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"unwritable","mappings":{}},{"id":"om-0","sourceNodeId":"unwritable","targetNodeId":"out","mappings":{"payload":{"type":"direct","sourceOutputName":"payload"}}}]}""";
        var world = new RunLifecycleWorld(definitionJson: graph, inputParametersJson: "[]", outputParametersJson: """[{"name":"payload","type":"Cyclic"}]""");
        world.AdditionalBlocks = id => id == "test.unwritable" ? new UnwritableFactory() : null;
        world.CompilationOptions = CyclicOptions;
        using var engine = world.Engine();
        await engine.ExecuteAsync(world.WorkflowId, null, new Dictionary<string, object?>(), parentExecutionId: Guid.NewGuid());

        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        log.ResultOutputsJson.Should().BeNull("an unwritable raw copy is never stored");
        JsonNode.Parse(log.OutputsJson!)!["payload"]!.GetValue<string>().Should().Be("[unserializable]");
    }

    [Fact]
    public async Task A_resumed_run_result_masks_nested_previous_outputs()
    {
        const string graph = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"source","blockTypeId":"PayloadSource"},{"id":"sleep","blockTypeId":"Hold","configuration":{"DurationMs":60000}},{"id":"out","blockTypeId":"WorkflowOutput"}],"edges":[{"id":"a","sourceNodeId":"start","targetNodeId":"source","mappings":{}},{"id":"b","sourceNodeId":"source","targetNodeId":"sleep","mappings":{}},{"id":"om-0","sourceNodeId":"source","targetNodeId":"out","mappings":{"payload":{"type":"direct","sourceOutputName":"payload","projection":{"path":"$"}}}}]}""";
        var world = new RunLifecycleWorld(definitionJson: graph, inputParametersJson: "[]", outputParametersJson: """[{"name":"payload","type":"Json"}]""");
        using var engine = world.Engine();
        var parked = world.Parked(parentExecutionId: Guid.NewGuid(), blockId: "sleep");
        var original = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(parked.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
        var previous = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["start"] = new Dictionary<string, object?> { ["payload"] = "input" },
            ["source"] = new Dictionary<string, object?>
            {
                ["payload"] = new Dictionary<string, object?>
                {
                    ["deep"] = new object?[] { new Dictionary<string, object?> { ["token"] = "hidden", ["label"] = "visible" } }
                }
            }
        };
        var payload = new WorkflowSuspensionStatePayload
        {
            VariablesJson = original.VariablesJson,
            PreviousOutputsJson = JsonSerializer.Serialize(previous, WorkflowGraphHelpers.JsonOptions),
            TriggerEnvelopeJson = original.TriggerEnvelopeJson,
            ExecutionStateId = original.ExecutionStateId,
            InputNodeId = "start"
        };
        var context = new ResumeContext
        {
            SuspensionId = parked.SuspensionId,
            ExecutionLogId = parked.ExecutionLogId,
            WorkflowId = parked.WorkflowId,
            BlockId = "sleep",
            Kind = WorkflowSuspensionKind.Sleep,
            OutcomeJson = "{\"durationMs\":10}",
            StatePayloadJson = JsonSerializer.Serialize(payload, WorkflowGraphHelpers.JsonOptions),
            SuspendedAtUtc = parked.SuspendedAtUtc
        };

        (await world.Resumer(coordinators: engine.Coordinators, maxPayloadValueBytes: 1_024)
            .ResumeFromSuspensionAsync(context, CancellationToken.None))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        var outputsJson = world.Journal.Logs[parked.ExecutionLogId].OutputsJson;
        outputsJson.Should().NotBeNull();
        var output = JsonNode.Parse(outputsJson!)!;
        output.ToJsonString().Should().Contain("token");
        output["payload"]!["deep"]![0]!["token"]!.GetValue<string>().Should().Be("***");
        output["payload"]!["deep"]![0]!["label"]!.GetValue<string>().Should().Be("visible");
        var raw = world.Journal.Logs[parked.ExecutionLogId].ResultOutputsJson;
        raw.Should().NotBeNull();
        JsonNode.Parse(raw!)!["payload"]!["deep"]![0]!["token"]!.GetValue<string>().Should().Be("hidden");
    }

    [Fact]
    public async Task A_resumed_root_run_does_not_keep_a_raw_result_copy()
    {
        const string graph = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"source","blockTypeId":"PayloadSource"},{"id":"sleep","blockTypeId":"Hold","configuration":{"DurationMs":60000}},{"id":"out","blockTypeId":"WorkflowOutput"}],"edges":[{"id":"a","sourceNodeId":"start","targetNodeId":"source","mappings":{}},{"id":"b","sourceNodeId":"source","targetNodeId":"sleep","mappings":{}},{"id":"om-0","sourceNodeId":"source","targetNodeId":"out","mappings":{"payload":{"type":"direct","sourceOutputName":"payload","projection":{"path":"$"}}}}]}""";
        var world = new RunLifecycleWorld(definitionJson: graph, inputParametersJson: "[]", outputParametersJson: """[{"name":"payload","type":"Json"}]""");
        using var engine = world.Engine();
        var parked = world.Parked(blockId: "sleep");
        var original = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(parked.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
        var previous = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["start"] = new Dictionary<string, object?> { ["payload"] = "input" },
            ["source"] = new Dictionary<string, object?> { ["payload"] = new Dictionary<string, object?> { ["token"] = "hidden" } }
        };
        var payload = new WorkflowSuspensionStatePayload
        {
            VariablesJson = original.VariablesJson,
            PreviousOutputsJson = JsonSerializer.Serialize(previous, WorkflowGraphHelpers.JsonOptions),
            TriggerEnvelopeJson = original.TriggerEnvelopeJson,
            ExecutionStateId = original.ExecutionStateId,
            InputNodeId = "start"
        };
        var context = new ResumeContext
        {
            SuspensionId = parked.SuspensionId,
            ExecutionLogId = parked.ExecutionLogId,
            WorkflowId = parked.WorkflowId,
            BlockId = "sleep",
            Kind = WorkflowSuspensionKind.Sleep,
            OutcomeJson = "{\"durationMs\":10}",
            StatePayloadJson = JsonSerializer.Serialize(payload, WorkflowGraphHelpers.JsonOptions),
            SuspendedAtUtc = parked.SuspendedAtUtc
        };

        (await world.Resumer(coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(context, CancellationToken.None))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        world.Journal.Logs[parked.ExecutionLogId].ResultOutputsJson.Should().BeNull(
            "the state row did not verify that another run dispatched this root run");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_resumed_run_with_no_run_state_keeps_a_raw_result_exactly_when_its_payload_names_a_parent(bool hasParent)
    {
        const string graph = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"source","blockTypeId":"PayloadSource"},{"id":"sleep","blockTypeId":"Hold","configuration":{"DurationMs":60000}},{"id":"out","blockTypeId":"WorkflowOutput"}],"edges":[{"id":"a","sourceNodeId":"start","targetNodeId":"source","mappings":{}},{"id":"b","sourceNodeId":"source","targetNodeId":"sleep","mappings":{}},{"id":"om-0","sourceNodeId":"source","targetNodeId":"out","mappings":{"payload":{"type":"direct","sourceOutputName":"payload","projection":{"path":"$"}}}}]}""";
        var world = new RunLifecycleWorld(definitionJson: graph, inputParametersJson: "[]", outputParametersJson: """[{"name":"payload","type":"Json"}]""");
        using var engine = world.Engine();
        var parked = world.Parked(blockId: "sleep");
        var original = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(parked.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
        var payload = new WorkflowSuspensionStatePayload
        {
            VariablesJson = original.VariablesJson,
            PreviousOutputsJson = """{"start":{"payload":"input"},"source":{"payload":{"token":"hidden"}}}""",
            TriggerEnvelopeJson = original.TriggerEnvelopeJson,
            ExecutionStateId = null,
            InputNodeId = "start",
            ParentExecutionId = hasParent ? Guid.NewGuid() : null
        };
        var context = new ResumeContext
        {
            SuspensionId = parked.SuspensionId,
            ExecutionLogId = parked.ExecutionLogId,
            WorkflowId = parked.WorkflowId,
            BlockId = "sleep",
            Kind = WorkflowSuspensionKind.Sleep,
            OutcomeJson = "{\"durationMs\":10}",
            StatePayloadJson = JsonSerializer.Serialize(payload, WorkflowGraphHelpers.JsonOptions),
            SuspendedAtUtc = parked.SuspendedAtUtc
        };

        (await world.Resumer(coordinators: engine.Coordinators, withRunState: false)
            .ResumeFromSuspensionAsync(context, CancellationToken.None))
            .Should().Be(WorkflowResumeOutcome.Resumed);

        var raw = world.Journal.Logs[parked.ExecutionLogId].ResultOutputsJson;
        if (hasParent)
            JsonNode.Parse(raw!)!["payload"]!["token"]!.GetValue<string>().Should().Be("hidden");
        else
            raw.Should().BeNull("no run started this one");
    }

    private static RunLifecycleWorld Producing(params (string Name, object? Value)[] outputs)
    {
        var mappings = string.Join(",", outputs.Select(o =>
            "\"" + o.Name + "\":{\"type\":\"direct\",\"sourceOutputName\":\"" + o.Name + "\",\"projection\":{\"path\":\"$\"}}"));
        var graph = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"src","blockTypeId":"test.produce"},{"id":"out","blockTypeId":"WorkflowOutput"}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"src","mappings":{}},{"id":"out-0","sourceNodeId":"src","targetNodeId":"out","mappings":{"""
            + mappings + "}}]}";
        var parameters = "[" + string.Join(",", outputs.Select(o => "{\"name\":\"" + o.Name + "\",\"type\":\"Json\"}")) + "]";
        var world = new RunLifecycleWorld(definitionJson: graph, inputParametersJson: "[]", outputParametersJson: parameters);
        world.AdditionalBlocks = id => id == "test.produce" ? new ProducingFactory(outputs) : null;
        return world;
    }

    internal sealed class ProducingFactory((string Name, object? Value)[] outputs) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "test.produce";
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => string.Empty;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [.. outputs.Select(o => new BlockPortDefinition(o.Name, o.Name, PortValueType.Json, false))];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new Plugin(outputs);

        private sealed class Plugin((string Name, object? Value)[] outputs) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
                Task.FromResult<IReadOnlyDictionary<string, object?>>(outputs.ToDictionary(o => o.Name, o => (object?)JsonSerializer.SerializeToElement(o.Value)));
        }
    }

    private sealed class UnwritableFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => "test.unwritable";
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => string.Empty;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [new BlockPortDefinition("payload", "payload", PortValueType.Of("Cyclic"), false)];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new Plugin();

        private sealed class Plugin : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
                CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(
                    new Dictionary<string, object?> { ["payload"] = Cyclic() });
        }
    }

    private sealed class CyclicValue
    {
        public CyclicValue? Self { get; set; }
    }

    private static CyclicValue Cyclic()
    {
        var value = new CyclicValue();
        value.Self = value;
        return value;
    }

    private static readonly CompilationOptions CyclicOptions = new()
    {
        TypeRegistry = new WorkflowTypeRegistry(
        [
            new WorkflowTypeRecord("Cyclic", typeof(CyclicValue))
            {
                JsonTypeInfo = StrictJson.Info(typeof(CyclicValue)),
                Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
            },
        ]),
    };
}
