using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class PersistedValueResumeTests
{
    private const string Stamp = "test.stamp";
    private const string Probe = "test.probe";

    private static readonly DateTimeOffset When = new(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task A_resumed_run_reads_each_output_in_its_terms_clr_type()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMinutes(1) };
        world.SuspensionSeam = world.Suspensions;
        var probed = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => new Dictionary<string, object?>
            {
                ["At"] = When, ["Count"] = 3.0, ["Tags"] = new List<string> { "a", "b" },
            }, outputs: Ports),
            Probe => new TypedFactory(id, inputs => { probed.Enqueue(inputs); return new Dictionary<string, object?>(); }, inputs: Ports),
            _ => null
        };
        var workflowId = world.AddRaw(Definition());
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        var inputs = probed.Should().ContainSingle().Which;
        inputs["At"].Should().BeOfType<DateTimeOffset>().Which.Should().Be(When);
        inputs["Count"].Should().BeOfType<double>().Which.Should().Be(3.0);
        inputs["Tags"].Should().BeAssignableTo<IReadOnlyList<string>>().Which.Should().Equal("a", "b");
    }

    [Fact]
    public async Task A_resumed_run_reads_a_map_as_a_map_keeping_its_entries_and_their_case()
    {
        BlockPortDefinition[] ports =
        [
            new("Meta", "Meta", PortValueType.MapOf(PortValueType.Int), true),
            new("Nested", "Nested", PortValueType.CollectionOf(PortValueType.MapOf(PortValueType.CollectionOf(PortValueType.Number))), true),
            new("Absent", "Absent", PortValueType.Of("Map<String,Int>?"), false),
        ];
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMinutes(1) };
        world.SuspensionSeam = world.Suspensions;
        var probed = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => new Dictionary<string, object?>
            {
                ["Meta"] = new Dictionary<string, long> { ["b"] = 2, ["X-A"] = 1, ["x-a"] = 3 },
                ["Nested"] = new List<IReadOnlyDictionary<string, IReadOnlyList<double>>>
                {
                    new Dictionary<string, IReadOnlyList<double>> { ["k"] = new List<double> { 1.5, 2 } },
                },
                ["Absent"] = null,
            }, outputs: ports),
            Probe => new TypedFactory(id, inputs => { probed.Enqueue(inputs); return new Dictionary<string, object?>(); }, inputs: ports),
            _ => null
        };
        var workflowId = world.AddRaw(Definition(ports));
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        var inputs = probed.Should().ContainSingle().Which;
        inputs["Meta"].Should().BeAssignableTo<IReadOnlyDictionary<string, long>>().Which
            .Should().Equal(new Dictionary<string, long> { ["b"] = 2, ["X-A"] = 1, ["x-a"] = 3 });
        var nested = inputs["Nested"].Should().BeAssignableTo<IReadOnlyList<IReadOnlyDictionary<string, IReadOnlyList<double>>>>().Which.Single();
        nested["k"].Should().Equal(1.5, 2.0);
        inputs["Absent"].Should().BeNull();
    }

    [Theory]
    [InlineData("At", "42")]
    [InlineData("Count", "\"three\"")]
    [InlineData("Tags", "[1]")]
    [InlineData("At", "null")]
    public async Task A_parked_run_whose_stored_value_does_not_read_as_its_term_is_abandoned(string port, string stored)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMinutes(1) };
        world.SuspensionSeam = world.Suspensions;
        var probed = 0;
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => new Dictionary<string, object?>
            {
                ["At"] = When, ["Count"] = 3.0, ["Tags"] = new List<string> { "a" },
            }, outputs: Ports),
            Probe => new TypedFactory(id, _ => { probed++; return new Dictionary<string, object?>(); }, inputs: Ports),
            _ => null
        };
        var workflowId = world.AddRaw(Definition());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var payload = System.Text.Json.Nodes.JsonNode.Parse(row.StatePayloadJson)!.AsObject();
        var outputs = System.Text.Json.Nodes.JsonNode.Parse(payload["PreviousOutputsJson"]!.GetValue<string>())!.AsObject();
        outputs["stamp"]![port] = System.Text.Json.Nodes.JsonNode.Parse(stored);
        payload["PreviousOutputsJson"] = outputs.ToJsonString();
        world.Suspensions.Rows[0] = row with { StatePayloadJson = payload.ToJsonString() };

        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);

        probed.Should().Be(0);
        world.Journal.Single(workflowId).ErrorMessage.Should().StartWith(WorkflowAbandonmentReasons.ResumeValueInvalid)
            .And.Contain("'stamp'").And.Contain($"'{port}'");
    }

    [Theory]
    [InlineData(NodeErrorPolicy.FailWorkflow)]
    [InlineData(NodeErrorPolicy.ContinueWithDefault)]
    public async Task A_resumed_output_the_gate_refuses_fails_the_node_through_its_policy(NodeErrorPolicy policy)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        var probed = 0;
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitForTrigger, "{}", DateTimeOffset.UtcNow.AddHours(1))),
                outputs: [new("TriggerData", "TriggerData", PortValueType.Json, false)]),
            Probe => new TypedFactory(id, _ => { probed++; return new Dictionary<string, object?>(); }),
            _ => null
        };
        var workflowId = world.AddRaw(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new
                {
                    id = "wait", blockTypeId = Stamp, position = new { x = 0, y = 0 }, configuration = new { },
                    errorPolicy = new { policy = policy.ToString() },
                },
                new { id = "probe", blockTypeId = Probe, position = new { x = 0, y = 0 }, configuration = new { } },
            },
            edges = new object[]
            {
                new { id = "a", sourceNodeId = "start", targetNodeId = "wait", mappings = new { } },
                new { id = "b", sourceNodeId = "wait", targetNodeId = "probe", mappings = new { } },
            },
        }));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;

        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            """{"outcome":"timed_out","_resumeReason":"timeout"}""", DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed,
            "the node fails inside the walk; the run is not abandoned");

        var log = world.Journal.Single(workflowId);
        if (policy == NodeErrorPolicy.FailWorkflow)
        {
            log.Status.Should().Be(WorkflowExecutionStatus.Failed);
            log.ErrorMessage.Should().NotStartWith(WorkflowAbandonmentReasons.ResumeValueInvalid).And.Contain("TriggerData");
            probed.Should().Be(0);
        }
        else
        {
            log.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
            probed.Should().Be(1, "the policy carried the run past the node");
        }
    }

    [Fact]
    public async Task A_host_term_with_its_own_converter_is_parked_in_that_converters_json_and_resumes_as_its_clr_value()
    {
        var world = SealWorld();
        world.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
        var probed = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => new Dictionary<string, object?> { ["Mark"] = new Seal("s-1") }, outputs: [SealPort]),
            Probe => new TypedFactory(id, inputs => { probed.Enqueue(inputs); return new Dictionary<string, object?>(); }, inputs: [SealPort]),
            _ => null
        };
        var workflowId = world.AddRaw(SealDefinition());
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(row.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
        System.Text.Json.Nodes.JsonNode.Parse(payload.PreviousOutputsJson)!["stamp"]!["Mark"]!.GetValue<string>().Should().Be("seal:s-1");

        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        probed.Should().ContainSingle().Which["Mark"].Should().BeOfType<Seal>().Which.Should().Be(new Seal("s-1"));
    }

    [Fact]
    public async Task A_host_term_with_its_own_converter_inside_a_container_is_kept_in_the_frame_in_that_converters_json()
    {
        var world = SealWorld();
        world.ParkWhenAtLeast = null;
        world.Blocks.LoopIterations = 1;
        var probed = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => new Dictionary<string, object?> { ["Mark"] = new Seal("s-2") }, outputs: [SealPort]),
            Probe => new TypedFactory(id, inputs => { probed.Enqueue(inputs); return new Dictionary<string, object?>(); }, inputs: [SealPort]),
            Wait => new LambdaFactory(id, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort]),
            _ => null
        };
        var workflowId = world.AddRaw(SealInALoop());
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(row.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
        var frame = WorkflowRunCheckpoint.Continuation(payload).Should().ContainSingle().Which;
        System.Text.Json.Nodes.JsonNode.Parse(frame.PreviousOutputsJson)!["stamp"]!["Mark"]!.GetValue<string>().Should().Be("seal:s-2");

        var context = (await world.Suspensions.TryResolveAsync(
            row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        probed.Should().ContainSingle().Which["Mark"].Should().BeOfType<Seal>().Which.Should().Be(new Seal("s-2"));
    }

    [Fact]
    public async Task A_DateTime_done_inside_a_container_reads_back_from_the_frame_as_a_DateTimeOffset()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.LoopIterations = 1;
        var probed = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => new Dictionary<string, object?>
            {
                ["At"] = When, ["Count"] = 3.0, ["Tags"] = new List<string> { "a", "b" },
            }, outputs: Ports),
            Probe => new TypedFactory(id, inputs => { probed.Enqueue(inputs); return new Dictionary<string, object?>(); }, inputs: Ports),
            Wait => new LambdaFactory(id, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort]),
            _ => null
        };
        var workflowId = world.AddRaw(InALoop(Ports.ToDictionary(p => p.Name, p => (object)new { type = "direct", sourceOutputName = p.Name })));
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var context = (await world.Suspensions.TryResolveAsync(
            row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        var inputs = probed.Should().ContainSingle().Which;
        inputs["At"].Should().BeOfType<DateTimeOffset>().Which.Should().Be(When);
        inputs["Count"].Should().BeOfType<double>().Which.Should().Be(3.0);
    }

    [Fact]
    public void A_trigger_payload_instant_reads_back_after_a_park_as_the_same_value_the_live_run_held()
    {
        var options = new CompilationOptions
        {
            NamespaceRegistrations =
            [
                new WorkflowExpressionNamespace("trigger", 1, new TriggerTerms(),
                    (_, _) => throw new InvalidOperationException("no resolver is opened here")),
            ],
        };
        var terms = new Dictionary<string, ClosedPortType>(StringComparer.Ordinal);
        var findings = new List<WorkflowValidationFinding>();
        var live = WorkflowStartArguments.ReadPayload(
            new Dictionary<string, object?> { ["at"] = When, ["count"] = 3 },
            WorkflowStartArguments.TriggerNamespace(options), options.Vocabulary, findings, terms);
        findings.Should().BeEmpty();
        var triggerId = Guid.NewGuid();

        var json = PersistedValueCodec.WriteTriggerEnvelope(triggerId, new TriggerEnvelope(When, live) { Terms = terms });
        var (readId, resumed) = PersistedValueCodec.ReadTriggerEnvelope(json, WorkflowStartArguments.PayloadTerms(options));

        readId.Should().Be(triggerId);
        live["at"].Should().BeOfType<DateTimeOffset>().Which.Should().Be(When);
        resumed!.Payload["at"].Should().BeOfType<DateTimeOffset>().Which.Should().Be(When);
        resumed.Payload["count"].Should().BeOfType(live["count"]!.GetType()).And.Be(live["count"]);
    }

    private sealed class TriggerTerms : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "trigger";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            segments switch
            {
                ["at"] => new(NamespacePathCheck.Yes, "DateTime"),
                ["count"] => new(NamespacePathCheck.Yes, "Int"),
                _ => NamespacePathCheckAnswer.No,
            };
    }

    private const string Wait = "test.wait";

    public sealed record Seal(string Code);

    private sealed class SealConverter : System.Text.Json.Serialization.JsonConverter<Seal>
    {
        public override Seal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is { } text && text.StartsWith("seal:", StringComparison.Ordinal)
                ? new Seal(text["seal:".Length..])
                : throw new JsonException("not a seal");

        public override void Write(Utf8JsonWriter writer, Seal value, JsonSerializerOptions options) =>
            writer.WriteStringValue("seal:" + value.Code);
    }

    private static readonly BlockPortDefinition[] DocOut = [new("Doc", "Doc", PortValueType.Json, true)];
    private static readonly BlockPortDefinition[] DocIn = [new("Count", "Count", PortValueType.Int, true)];

    private static string DocDefinition() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "stamp", blockTypeId = Stamp, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "pause", blockTypeId = DurableSleepWorld.SleepType, position = new { x = 0, y = 0 },
                configuration = new Dictionary<string, object?> { ["DurationMs"] = 600_000 },
            },
            new { id = "probe", blockTypeId = Probe, position = new { x = 0, y = 0 }, configuration = new { } },
        },
        edges = new object[]
        {
            new { id = "a", sourceNodeId = "start", targetNodeId = "stamp", mappings = new { } },
            new { id = "b", sourceNodeId = "stamp", targetNodeId = "pause", mappings = new { } },
            new { id = "c", sourceNodeId = "pause", targetNodeId = "probe", mappings = new { } },
            new
            {
                id = "d", sourceNodeId = "stamp", targetNodeId = "probe",
                mappings = new { Count = new { type = "direct", sourceOutputName = "Doc", projection = new { path = "$.m.a" } } },
            },
        },
    });

    private static (DurableSleepWorld World, Guid WorkflowId, Func<int> Probed, List<object?> Received) RepeatWorld(string docText)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMinutes(1) };
        world.SuspensionSeam = world.Suspensions;
        var probed = 0;
        var received = new List<object?>();
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => new Dictionary<string, object?> { ["Doc"] = JsonDocument.Parse(docText).RootElement.Clone() }, outputs: DocOut),
            Probe => new TypedFactory(id, inputs => { probed++; received.Add(inputs["Count"]); return new Dictionary<string, object?>(); }, inputs: DocIn),
            _ => null
        };
        return (world, world.AddRaw(DocDefinition()), () => probed, received);
    }

    [Fact]
    public async Task A_Json_that_repeats_no_member_name_is_the_same_value_before_the_park_and_after_the_resume()
    {
        var (world, workflowId, probed, received) = RepeatWorld("""{"m":{"a":7},"n":{"a":2}}""");
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), DateTimeOffset.UtcNow, default))!;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);
        probed().Should().Be(1);
        received.Should().Equal(7L);
    }

    [Fact]
    public async Task A_Json_that_repeats_a_member_name_fails_the_node_that_wrote_it_and_the_run_never_parks()
    {
        var (world, workflowId, probed, _) = RepeatWorld("""{"m":{"a":1,"a":2}}""");
        using var engine = world.Engine();

        await FluentActions.Invoking(() => engine.ExecuteAsync(workflowId, null, null))
            .Should().ThrowAsync<WorkflowNodeExecutionException>().WithMessage("*'Doc'*'stamp'*a repeated member name*");

        world.Suspensions.Rows.Should().BeEmpty();
        probed().Should().Be(0);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Failed);
    }

    [Fact]
    public async Task A_run_parked_by_8x_with_a_Json_that_repeats_a_member_name_is_abandoned_on_its_first_resume()
    {
        var (world, workflowId, probed, _) = RepeatWorld("""{"m":{"a":1}}""");
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var payload = System.Text.Json.Nodes.JsonNode.Parse(row.StatePayloadJson)!.AsObject();
        var outputs = System.Text.Json.Nodes.JsonNode.Parse(payload["PreviousOutputsJson"]!.GetValue<string>())!.AsObject();
        outputs["stamp"]!["Doc"] = "__REPEAT__";
        payload["PreviousOutputsJson"] = outputs.ToJsonString().Replace("\"__REPEAT__\"", """{"m":{"a":1,"a":2}}""", StringComparison.Ordinal);
        world.Suspensions.Rows[0] = row with { StatePayloadJson = payload.ToJsonString() };

        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);

        probed().Should().Be(0);
        world.Journal.Single(workflowId).ErrorMessage.Should().StartWith(WorkflowAbandonmentReasons.ResumeValueInvalid)
            .And.Contain("'Doc'").And.Contain("a repeated member name");
    }

    [Fact]
    public async Task A_wait_resumed_with_trigger_data_that_repeats_a_member_name_fails_the_node_as_the_first_run_would()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        var probed = 0;
        world.Blocks.Extra = id => id switch
        {
            Stamp => new TypedFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitForTrigger, "{}", DateTimeOffset.UtcNow.AddHours(1))),
                outputs: [new("TriggerData", "TriggerData", PortValueType.Of("Json?"), false)]),
            Probe => new TypedFactory(id, _ => { probed++; return new Dictionary<string, object?>(); }),
            _ => null
        };
        var workflowId = world.AddRaw(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "wait", blockTypeId = Stamp, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "probe", blockTypeId = Probe, position = new { x = 0, y = 0 }, configuration = new { } },
            },
            edges = new object[]
            {
                new { id = "a", sourceNodeId = "start", targetNodeId = "wait", mappings = new { } },
                new { id = "b", sourceNodeId = "wait", targetNodeId = "probe", mappings = new { } },
            },
        }));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;

        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            """{"outcome":"satisfied","triggerData":{"a":{"k":1,"k":2}}}""", DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        var log = world.Journal.Single(workflowId);
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().Contain("TriggerData").And.Contain("a repeated member name");
        probed.Should().Be(0);
    }

    private static readonly BlockPortDefinition SealPort = new("Mark", "Mark", PortValueType.Of("Seal"), true);

    private static DurableSleepWorld SealWorld()
    {
        var record = new WorkflowTypeRecord("Seal", typeof(Seal))
        {
            Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
            JsonTypeInfo = new JsonSerializerOptions(JsonSerializerOptions.Default) { Converters = { new SealConverter() } }.GetTypeInfo(typeof(Seal)),
        };
        var world = new DurableSleepWorld { CompilationOptions = new CompilationOptions { TypeRegistry = new WorkflowTypeRegistry([record]) } };
        world.SuspensionSeam = world.Suspensions;
        return world;
    }

    private static readonly Dictionary<string, object> MarkWire = new() { ["Mark"] = new { type = "direct", sourceOutputName = "Mark" } };

    private static string SealDefinition() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "stamp", blockTypeId = Stamp, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "pause", blockTypeId = DurableSleepWorld.SleepType, position = new { x = 0, y = 0 },
                configuration = new Dictionary<string, object?> { ["DurationMs"] = 600_000 },
            },
            new { id = "probe", blockTypeId = Probe, position = new { x = 0, y = 0 }, configuration = new { } },
        },
        edges = new object[]
        {
            new { id = "a", sourceNodeId = "start", targetNodeId = "stamp", mappings = new { } },
            new { id = "b", sourceNodeId = "stamp", targetNodeId = "pause", mappings = new { } },
            new { id = "c", sourceNodeId = "pause", targetNodeId = "probe", mappings = new { } },
            new { id = "d", sourceNodeId = "stamp", targetNodeId = "probe", mappings = MarkWire },
        },
    });

    private static string SealInALoop() => InALoop(MarkWire);

    private static string InALoop(Dictionary<string, object> wire) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.LoopType, position = new { x = 0, y = 0 }, configuration = new { },
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "stamp", blockTypeId = Stamp, position = new { x = 0, y = 0 }, configuration = new { } },
                            new
                            {
                                id = "wait", blockTypeId = Wait, position = new { x = 0, y = 0 },
                                configuration = new Dictionary<string, object> { [TemplateWaits.Input] = "expr:false" },
                            },
                            new { id = "probe", blockTypeId = Probe, position = new { x = 0, y = 0 }, configuration = new { } },
                        },
                        edges = new object[]
                        {
                            new { id = "in-stamp", sourceNodeId = "in", targetNodeId = "stamp", mappings = new { } },
                            new { id = "stamp-wait", sourceNodeId = "stamp", targetNodeId = "wait", mappings = new { } },
                            new { id = "wait-probe", sourceNodeId = "wait", targetNodeId = "probe", mappings = new { } },
                            new { id = "stamp-probe", sourceNodeId = "stamp", targetNodeId = "probe", mappings = wire },
                        },
                    },
                },
            },
        },
        edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } },
    });

    private static readonly BlockPortDefinition[] Ports =
    [
        new("At", "At", PortValueType.DateTime, true),
        new("Count", "Count", PortValueType.Number, true),
        new("Tags", "Tags", PortValueType.CollectionOf(PortValueType.String), true),
    ];

    private static string Definition(BlockPortDefinition[]? ports = null) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "stamp", blockTypeId = Stamp, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "pause", blockTypeId = DurableSleepWorld.SleepType, position = new { x = 0, y = 0 },
                configuration = new Dictionary<string, object?> { ["DurationMs"] = 600_000 },
            },
            new { id = "probe", blockTypeId = Probe, position = new { x = 0, y = 0 }, configuration = new { } },
        },
        edges = new object[]
        {
            new { id = "a", sourceNodeId = "start", targetNodeId = "stamp", mappings = new { } },
            new { id = "b", sourceNodeId = "stamp", targetNodeId = "pause", mappings = new { } },
            new { id = "c", sourceNodeId = "pause", targetNodeId = "probe", mappings = new { } },
            new
            {
                id = "d", sourceNodeId = "stamp", targetNodeId = "probe",
                mappings = (ports ?? Ports).ToDictionary(p => p.Name, p => (object)new { type = "direct", sourceOutputName = p.Name }),
            },
        },
    });

    private sealed class TypedFactory(
        string id, Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> body,
        IReadOnlyCollection<BlockPortDefinition>? inputs = null, IReadOnlyCollection<BlockPortDefinition>? outputs = null)
        : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs ?? [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs ?? [];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new Plugin(body);

        private sealed class Plugin(Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> body)
            : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
                Task.FromResult(body(inputs));
        }
    }
}
