using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using static ReeveWorkflow.Core.Tests.Execution.AdmissionGateWorld;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class StartArgumentAdmissionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static WorkflowTypeVocabulary Vocabulary => new WorkflowTypeRegistry([]).Current;

    private static CanvasSignature Signature(params (string Name, string Term, bool Required)[] parameters) =>
        new([.. parameters.Select(p => new PortShape(p.Name, PortValueType.Of(p.Term), p.Required))], []);

    [Fact]
    public void A_JSON_argument_is_read_as_its_parameters_term()
    {
        var signature = Signature(("Count", "Int", true), ("Ratio", "Number", true), ("Tags", "Collection<Int>", true),
            ("When", "DateTime", true), ("Doc", "Json", true), ("Name", "String?", true));
        var supplied = new Dictionary<string, object?>
        {
            ["Count"] = Json("3"), ["Ratio"] = Json("4"), ["Tags"] = Json("[1,2]"), ["When"] = Json("\"2026-10-13T10:00:00Z\""),
            ["Doc"] = Json("""{"a":1}"""), ["Name"] = Json("null"),
        };

        var outcome = WorkflowStartArguments.Check(signature, supplied, Vocabulary);

        outcome.Findings.Should().BeEmpty();
        outcome.Values["Count"].Should().BeOfType<long>().Which.Should().Be(3L);
        outcome.Values["Ratio"].Should().BeOfType<double>().Which.Should().Be(4.0);
        outcome.Values["Tags"].Should().BeAssignableTo<IReadOnlyList<long>>().Which.Should().Equal(1L, 2L);
        outcome.Values["When"].Should().BeOfType<DateTimeOffset>().Which.Should().Be(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
        outcome.Values["Doc"].Should().BeOfType<JsonElement>().Which.GetProperty("a").GetInt32().Should().Be(1);
        outcome.Values["Name"].Should().BeNull();
    }

    [Fact]
    public void A_CLR_argument_is_checked_against_its_terms_CLR_type_and_only_a_scalar_is_widened()
    {
        var signature = Signature(("Count", "Int", true), ("Tags", "Collection<Int>", true), ("Ratio", "Number", true));
        var tags = new List<long> { 1, 2 };
        var supplied = new Dictionary<string, object?> { ["Count"] = 3, ["Tags"] = tags, ["Ratio"] = 2.5f };

        var outcome = WorkflowStartArguments.Check(signature, supplied, Vocabulary);

        outcome.Findings.Should().BeEmpty();
        outcome.Values["Count"].Should().BeOfType<long>().Which.Should().Be(3L);
        outcome.Values["Tags"].Should().BeSameAs(tags);
        outcome.Values["Ratio"].Should().BeOfType<double>();
    }

    [Theory]
    [InlineData("Collection<Int>", "ints")]
    [InlineData("Collection<String>", "objects")]
    [InlineData("Int", "double-for-int")]
    [InlineData("Json", "node")]
    public void A_CLR_argument_that_is_not_a_value_of_its_term_is_a_mismatch_and_is_never_rebuilt(string term, string kind)
    {
        object argument = kind switch
        {
            "ints" => new[] { 1, 2 },
            "objects" => new List<object?> { "a", "b" },
            "double-for-int" => 1.5,
            _ => System.Text.Json.Nodes.JsonNode.Parse("{}")!,
        };

        var outcome = WorkflowStartArguments.Check(
            Signature(("P", term, true)), new Dictionary<string, object?> { ["P"] = argument }, Vocabulary);

        outcome.Findings.Should().ContainSingle().Which.Code.Should().Be(WorkflowStartArgumentCodes.TypeMismatch);
    }

    [Theory]
    [InlineData("Int", "1.5")]
    [InlineData("Int", "\"3\"")]
    [InlineData("Int", "null")]
    [InlineData("Number", "\"x\"")]
    [InlineData("String", "7")]
    [InlineData("Boolean", "1")]
    [InlineData("DateTime", "\"not a date\"")]
    [InlineData("Collection<Int>", "[1,\"x\"]")]
    [InlineData("Collection<Int>", "{}")]
    public void An_argument_that_is_not_a_value_of_its_term_is_a_type_mismatch_that_never_echoes_it(string term, string argument)
    {
        var outcome = WorkflowStartArguments.Check(
            Signature(("P", term, true)), new Dictionary<string, object?> { ["P"] = Json(argument) }, Vocabulary);

        var finding = outcome.Findings.Should().ContainSingle().Subject;
        (finding.Code, finding.Parameter).Should().Be((WorkflowStartArgumentCodes.TypeMismatch, "P"));
        finding.Message.Should().Contain(term).And.NotContain("not a date");
    }

    public sealed record Color(int R, int G, int B);

    private static WorkflowTypeVocabulary ColorVocabulary => new WorkflowTypeRegistry([new WorkflowTypeRecord("Color", typeof(Color))
    {
        Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
        JsonTypeInfo = StrictJson.Info(typeof(Color)),
    }]).Current;

    [Theory]
    [InlineData("Json", """{"a":{"k":1,"k":2}}""")]
    [InlineData("Collection<Json>", """[{"k":1,"k":2}]""")]
    [InlineData("Color", """{"R":1,"R":2,"G":0,"B":0}""")]
    [InlineData("Collection<Color>", """[{"R":1,"G":0,"B":0},{"R":1,"G":0,"G":1,"B":0}]""")]
    public void A_JSON_argument_that_repeats_a_member_name_is_a_type_mismatch_that_never_names_the_member(string term, string argument)
    {
        var outcome = WorkflowStartArguments.Check(
            Signature(("P", term, true)), new Dictionary<string, object?> { ["P"] = Json(argument) }, ColorVocabulary);

        var finding = outcome.Findings.Should().ContainSingle().Subject;
        (finding.Code, finding.Parameter).Should().Be((WorkflowStartArgumentCodes.TypeMismatch, "P"));
        finding.Message.Should().Contain("a repeated member name").And.NotContain("'k'");
    }

    [Fact]
    public void A_trigger_payload_field_that_repeats_a_member_name_is_a_mismatch_naming_the_key()
    {
        var trigger = new WorkflowExpressionNamespace("trigger", 1, new TriggerCheck(_ => "Json?"),
            (_, _) => throw new InvalidOperationException("no resolver"));
        var findings = new List<WorkflowValidationFinding>();

        var read = WorkflowStartArguments.ReadPayload(
            new Dictionary<string, object?> { ["doc"] = Json("""{"a":{"k":1,"k":2}}"""), ["fine"] = Json("""{"a":1}""") },
            trigger, Vocabulary, findings);

        read.Should().ContainKey("fine").And.NotContainKey("doc");
        var finding = findings.Should().ContainSingle().Subject;
        (finding.Code, finding.Parameter).Should().Be((WorkflowStartArgumentCodes.TypeMismatch, "trigger.doc"));
        finding.Message.Should().Contain("a repeated member name");
    }

    [Fact]
    public void A_required_parameter_left_out_is_missing_an_optional_one_is_not_and_a_stranger_is_unknown()
    {
        var signature = Signature(("Need", "Int", true), ("Maybe", "Int", false));

        var outcome = WorkflowStartArguments.Check(signature, new Dictionary<string, object?> { ["Stranger"] = Json("1") }, Vocabulary);

        outcome.Findings.Select(f => (f.Code, f.Parameter)).Should().BeEquivalentTo(
            [(WorkflowStartArgumentCodes.Missing, "Need"), (WorkflowStartArgumentCodes.Unknown, "Stranger")]);
        outcome.Values.Should().NotContainKey("Maybe");
    }

    [Fact]
    public void A_trigger_payload_field_is_read_into_the_term_the_namespace_answers_and_a_mismatch_names_the_key()
    {
        var trigger = new WorkflowExpressionNamespace("trigger", 1, new TriggerCheck(_ => "Int"),
            (_, _) => throw new InvalidOperationException("no resolver"));
        var findings = new List<WorkflowValidationFinding>();

        var read = WorkflowStartArguments.ReadPayload(
            new Dictionary<string, object?> { ["count"] = 3, ["wide"] = Json("7"), ["bad"] = "text" }, trigger, Vocabulary, findings);

        read["count"].Should().BeOfType<long>();
        read["wide"].Should().BeOfType<long>();
        var finding = findings.Should().ContainSingle().Subject;
        (finding.Code, finding.Parameter).Should().Be((WorkflowStartArgumentCodes.TypeMismatch, "trigger.bad"));
    }

    [Fact]
    public void A_Json_answer_serialises_a_CLR_value_keeps_a_present_null_and_drops_a_key_the_check_does_not_accept()
    {
        var trigger = new WorkflowExpressionNamespace("trigger", 1, new TriggerCheck(key => key == "secret" ? null : "Json?"),
            (_, _) => throw new InvalidOperationException("no resolver"));
        var findings = new List<WorkflowValidationFinding>();

        var read = WorkflowStartArguments.ReadPayload(new Dictionary<string, object?>
        {
            ["doc"] = new Dictionary<string, object?> { ["a"] = 1 }, ["nothing"] = null, ["secret"] = "x", [TriggerEnvelope.IdKey] = "id-1",
        }, trigger, Vocabulary, findings);

        findings.Should().BeEmpty();
        read["doc"].Should().BeOfType<JsonElement>().Which.GetProperty("a").GetInt32().Should().Be(1);
        read.Should().ContainKey("nothing").WhoseValue.Should().BeNull();
        read.Should().NotContainKey("secret");
        read[TriggerEnvelope.IdKey].Should().BeOfType<string>().Which.Should().Be("id-1",
            "the trigger id is read by String?, whatever the namespace answers for other keys");
    }

    [Fact]
    public void A_reserved_firedAt_is_held_as_its_DateTime_term_and_one_that_is_not_a_time_is_a_mismatch()
    {
        var trigger = new WorkflowExpressionNamespace("trigger", 1, new TriggerCheck(_ => "Json?"),
            (_, _) => throw new InvalidOperationException("no resolver"));
        var at = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(2));
        var findings = new List<WorkflowValidationFinding>();

        var read = WorkflowStartArguments.ReadPayload(
            new Dictionary<string, object?> { [TriggerEnvelope.FiredAtKey] = at }, trigger, Vocabulary, findings);
        WorkflowStartArguments.ReadPayload(
            new Dictionary<string, object?> { [TriggerEnvelope.FiredAtKey] = "t" }, trigger, Vocabulary, findings);

        read[TriggerEnvelope.FiredAtKey].Should().BeOfType<DateTimeOffset>().Which.Should().Be(at);
        var finding = findings.Should().ContainSingle().Subject;
        (finding.Code, finding.Parameter).Should().Be((WorkflowStartArgumentCodes.TypeMismatch, $"trigger.{TriggerEnvelope.FiredAtKey}"));
    }

    [Fact]
    public void With_no_trigger_namespace_no_payload_key_is_kept_because_no_expression_can_read_it()
    {
        var read = WorkflowStartArguments.ReadPayload(
            new Dictionary<string, object?> { ["temp"] = 21, [TriggerEnvelope.FiredAtKey] = "t" }, null, Vocabulary, []);

        read.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trigger_start_holds_the_converted_payload_in_the_envelope_the_run_reads()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)));
        var seen = new List<TriggerEnvelope?>();
        var trigger = TriggerNamespace(key => key == "secret" ? null : "Json?", seen);
        using var engine = world.Engine(new CompilationOptions { NamespaceRegistrations = [trigger] }, hostNamespaces: [trigger]);

        await engine.ExecuteAsync(id, new WorkflowFire
        {
            TriggerId = Guid.NewGuid(),
            Payload = new Dictionary<string, object?> { ["doc"] = new Dictionary<string, object?> { ["a"] = 1 }, ["secret"] = "x", ["nothing"] = null },
        }, null).WaitAsync(Patience);

        var payload = seen.Should().NotBeEmpty().And.Subject.First()!.Payload;
        payload["doc"].Should().BeOfType<JsonElement>().Which.GetProperty("a").GetInt32().Should().Be(1);
        payload.Should().ContainKey("nothing").WhoseValue.Should().BeNull();
        payload.Should().NotContainKey("secret");
    }

    [Fact]
    public async Task A_trigger_start_whose_payload_field_will_not_convert_is_refused_naming_the_key_and_leaves_a_row()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)));
        var trigger = TriggerNamespace(_ => "Int", []);
        using var engine = world.Engine(new CompilationOptions { NamespaceRegistrations = [trigger] }, hostNamespaces: [trigger]);

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id,
                new WorkflowFire { Payload = new Dictionary<string, object?> { ["n"] = "text" } }, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartFindingsException>()).Which;

        var finding = refusal.Findings.Should().ContainSingle().Subject;
        (finding.Code, finding.Parameter).Should().Be((WorkflowStartArgumentCodes.TypeMismatch, "trigger.n"));
        world.Executed.Should().BeEmpty();
        world.Journal.Logs.Values.Should().ContainSingle().Which.FailureReason.Should().Be(WorkflowFailureReasons.StartArgumentsInvalid);
    }

    [Fact]
    public async Task A_trigger_start_with_no_trigger_namespace_keeps_no_payload_key()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)));
        var seen = new List<TriggerEnvelope?>();
        var dock = new WorkflowExpressionNamespace("dock", 1, new TriggerCheck(_ => "Json?", "dock"), (scope, _) =>
        {
            seen.Add(scope.TriggerEnvelope);
            return Task.FromResult<IExpressionNamespaceResolver>(new NoResolver("dock"));
        });
        using var engine = world.Engine(new CompilationOptions { NamespaceRegistrations = [dock] }, hostNamespaces: [dock]);

        await engine.ExecuteAsync(id, new WorkflowFire
        {
            TriggerId = Guid.NewGuid(), Payload = new Dictionary<string, object?> { ["temp"] = 21 },
        }, null).WaitAsync(Patience);

        seen.Should().NotBeEmpty().And.Subject.First()!.Payload.Should().BeEmpty();
    }

    [Fact]
    public async Task A_start_with_the_wrong_arguments_is_refused_before_the_first_node_and_leaves_one_ended_row()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: """[{"name":"Count","type":"Int","isRequired":true}]""");
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null,
                new Dictionary<string, object?> { ["Count"] = Json("\"x\""), ["Stranger"] = Json("1") }).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartFindingsException>()).Which;

        refusal.Findings.Select(f => f.Code).Should().BeEquivalentTo(
            [WorkflowStartArgumentCodes.TypeMismatch, WorkflowStartArgumentCodes.Unknown]);
        refusal.Diagnostics.Select(d => d.RuleId).Should().BeEquivalentTo(refusal.Findings.Select(f => f.Code));
        world.Executed.Should().BeEmpty();
        var row = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        row.FailureReason.Should().Be(WorkflowFailureReasons.StartArgumentsInvalid);
    }

    [Fact]
    public async Task A_trigger_start_and_a_manual_start_are_both_checked()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: """[{"name":"Count","type":"Int","isRequired":true}]""");
        using var engine = world.Engine();
        var none = new Dictionary<string, object?>();

        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, none).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartFindingsException>().Where(e => e.Findings.Single().Code == WorkflowStartArgumentCodes.Missing);
        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, new WorkflowFire(), none).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartFindingsException>().Where(e => e.Findings.Single().Code == WorkflowStartArgumentCodes.Missing);
        world.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task The_run_receives_each_argument_in_its_parameters_CLR_type()
    {
        var world = new AdmissionGateWorld();
        world.SinkInputs.AddRange(
        [
            new BlockPortDefinition("Count", "Count", PortValueType.Int, true),
            new BlockPortDefinition("Ratio", "Ratio", PortValueType.Number, true),
            new BlockPortDefinition("Tags", "Tags", PortValueType.CollectionOf(PortValueType.Int), true),
            new BlockPortDefinition("When", "When", PortValueType.Of("DateTime"), true),
        ]);
        var id = world.AddWorkflow(SinkGraph("Count", "Ratio", "Tags", "When"), inputParametersJson: """
            [{"name":"Count","type":"Int","isRequired":true},{"name":"Ratio","type":"Number","isRequired":true},
             {"name":"Tags","type":"Collection<Int>","isRequired":true},{"name":"When","type":"DateTime","isRequired":true}]
            """);
        using var engine = world.Engine();

        await engine.ExecuteAsync(id, null, new Dictionary<string, object?>
        {
            ["Count"] = Json("3"), ["Ratio"] = Json("4"), ["Tags"] = Json("[1,2]"), ["When"] = Json("\"2026-10-13T10:00:00Z\""),
        }).WaitAsync(Patience);

        var received = world.Received["sink"];
        received["Count"].Should().BeOfType<long>().Which.Should().Be(3L);
        received["Ratio"].Should().BeOfType<double>().Which.Should().Be(4.0);
        received["Tags"].Should().BeAssignableTo<IReadOnlyList<long>>().Which.Should().Equal(1L, 2L);
        received["When"].Should().BeOfType<DateTimeOffset>();
    }

    [Fact]
    public async Task The_argument_validator_is_asked_after_the_check_with_the_host_typed_arguments_only()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: """
            [{"name":"Count","type":"Int","isRequired":true},{"name":"Box","type":"Crate","isRequired":true}]
            """);
        var validator = new ArgumentValidator();
        using var engine = world.Engine(CrateOptions, argumentValidator: validator);

        await engine.ExecuteAsync(id, null, new Dictionary<string, object?> { ["Count"] = Json("1"), ["Box"] = Json("""{"Id":"c-1"}""") })
            .WaitAsync(Patience);

        var asked = validator.Asked.Should().ContainSingle().Subject;
        asked.Start.WorkflowId.Should().Be(id);
        var argument = asked.Arguments.Should().ContainSingle().Subject;
        (argument.Parameter, argument.Term).Should().Be(("Box", "Crate"));
        argument.Value.Should().BeOfType<Crate_>().Which.Id.Should().Be("c-1");
    }

    [Fact]
    public async Task The_argument_validator_is_not_asked_when_the_check_failed()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: """[{"name":"Count","type":"Int","isRequired":true}]""");
        var validator = new ArgumentValidator();
        using var engine = world.Engine(argumentValidator: validator);

        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, new Dictionary<string, object?>()).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartFindingsException>();

        validator.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_finding_of_the_argument_validator_refuses_the_start_with_a_row()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: CrateParameter);
        var validator = new ArgumentValidator
        {
            Answer = _ => [new WorkflowValidationFinding("crate-too-big", "The crate is above the host's limit.") { Parameter = "Box" }],
        };
        using var engine = world.Engine(CrateOptions, argumentValidator: validator);

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null,
                new Dictionary<string, object?> { ["Box"] = Json("""{"Id":"c-1"}""") }).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartFindingsException>()).Which;

        refusal.Findings.Single().Code.Should().Be("crate-too-big");
        world.Executed.Should().BeEmpty();
        world.Journal.Logs.Values.Should().ContainSingle().Which.FailureReason.Should().Be(WorkflowFailureReasons.StartArgumentsInvalid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_throwing_or_silent_argument_validator_refuses_the_start_as_faulted_and_writes_no_row(bool throws)
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: CrateParameter);
        var validator = new ArgumentValidator { Answer = _ => throws ? throw new InvalidOperationException("secret detail") : null! };
        using var engine = world.Engine(CrateOptions, argumentValidator: validator);

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null,
                new Dictionary<string, object?> { ["Box"] = Json("""{"Id":"c-1"}""") }).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartRefusedException>()).Which;

        (refusal.Code, refusal.IsFault).Should().Be((WorkflowStartRefusalCodes.AdmissionFaulted, true));
        refusal.Message.Should().NotContain("secret detail");
        world.Executed.Should().BeEmpty();
        world.Journal.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_node_validator_is_asked_at_admission_with_each_nodes_configuration_and_literal()
    {
        var world = new AdmissionGateWorld();
        world.SinkInputs.Add(new BlockPortDefinition("Count", "Count", PortValueType.Int, false));
        var definition = JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "sink", blockTypeId = Sink, position = new { x = 0, y = 0 }, configuration = new { Note = "hi" } },
            },
            edges = new object[]
            {
                new { id = "e", sourceNodeId = "start", targetNodeId = "sink", mappings = new { Count = new { type = "literal", literalValue = 7 } } },
            },
        });
        var id = world.AddWorkflow(definition);
        var validator = new NodeValidator();
        using var engine = world.Engine(nodeValidator: validator);

        await engine.ExecuteAsync(id, null, null).WaitAsync(Patience);

        var request = validator.Asked.Should().ContainSingle().Subject;
        (request.WorkflowId, request.Phase).Should().Be((id, WorkflowValidationPhase.Admission));
        var node = request.Nodes.Single(n => n.NodeId == "sink");
        (node.BlockTypeId, node.CanvasPath).Should().Be((Sink, CanvasPath.Root));
        node.Configuration["Note"].GetString().Should().Be("hi");
        (node.Literals["Count"].Term, node.Literals["Count"].Value.GetInt32()).Should().Be(("Int", 7));
    }

    [Fact]
    public async Task A_finding_of_the_node_validator_refuses_the_start_with_a_row_and_a_diagnostic_naming_the_node()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)));
        var validator = new NodeValidator
        {
            Answer = _ => [new WorkflowValidationFinding("host-says-no", "Not this node.") { NodeId = "n" }],
        };
        using var engine = world.Engine(nodeValidator: validator);

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartFindingsException>()).Which;

        var diagnostic = refusal.Diagnostics.Should().ContainSingle().Subject;
        (diagnostic.RuleId, diagnostic.NodeId).Should().Be(("host-says-no", "n"));
        world.Executed.Should().BeEmpty();
        world.Journal.Logs.Values.Should().ContainSingle().Which.FailureReason.Should().Be(WorkflowFailureReasons.NodeValidationRefused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_throwing_or_silent_node_validator_refuses_the_start_as_faulted_and_writes_no_row(bool throws)
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)));
        var validator = new NodeValidator { Answer = _ => throws ? throw new InvalidOperationException("secret detail") : null! };
        using var engine = world.Engine(nodeValidator: validator);

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartRefusedException>()).Which;

        (refusal.Code, refusal.IsFault).Should().Be((WorkflowStartRefusalCodes.AdmissionFaulted, true));
        refusal.Message.Should().NotContain("secret detail");
        world.Journal.Logs.Should().BeEmpty();
    }

    [Fact]
    public void A_validator_finding_refuses_a_code_the_library_owns()
    {
        FluentActions.Invoking(() => new WorkflowValidationFinding(WorkflowStartArgumentCodes.Missing, "x"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new WorkflowValidationFinding(WorkflowEditorFindingCodes.HostValidatorFaulted, "x"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new WorkflowValidationFinding(" ", "x")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new WorkflowValidationFinding("host-code", " ")).Should().Throw<ArgumentException>();
        new WorkflowValidationFinding("host-code", "x").Code.Should().Be("host-code");
    }

    [Theory]
    [InlineData("manual", "missing")]
    [InlineData("manual", "unknown")]
    [InlineData("manual", "mismatch")]
    [InlineData("trigger", "missing")]
    [InlineData("trigger", "unknown")]
    [InlineData("trigger", "mismatch")]
    [InlineData("child", "missing")]
    [InlineData("child", "unknown")]
    [InlineData("child", "mismatch")]
    public async Task Each_start_path_refuses_each_argument_code_before_the_first_node(string path, string kind)
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: """[{"name":"Count","type":"Int","isRequired":true}]""");
        var version = world.Store.Workflows[id].CurrentVersion!.Id;
        using var engine = world.Engine();
        var (arguments, code) = kind switch
        {
            "missing" => (new Dictionary<string, object?>(), WorkflowStartArgumentCodes.Missing),
            "unknown" => (new Dictionary<string, object?> { ["Count"] = Json("1"), ["Stranger"] = Json("1") }, WorkflowStartArgumentCodes.Unknown),
            _ => (new Dictionary<string, object?> { ["Count"] = Json("\"x\"") }, WorkflowStartArgumentCodes.TypeMismatch),
        };

        var start = () =>
        {
            using var pin = path == "child"
                ? WorkflowRunPinScope.Push(new WorkflowRunPin { VersionId = version, Manifest = new Dictionary<Guid, Guid> { [id] = version }, CallDepth = 1 })
                : null;
            return engine.ExecuteAsync(id, path == "trigger" ? new WorkflowFire() : null, arguments,
                parentExecutionId: path == "child" ? Guid.NewGuid() : null);
        };

        var refusal = (await FluentActions.Awaiting(() => start().WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowStartFindingsException>()).Which;

        refusal.Findings.Single().Code.Should().Be(code);
        world.Executed.Should().BeEmpty();
        world.Journal.Logs.Values.Should().ContainSingle().Which.FailureReason.Should().Be(WorkflowFailureReasons.StartArgumentsInvalid);
    }

    [Fact]
    public async Task The_node_validator_judges_the_started_version_and_each_pin_at_a_root_and_a_child_only_its_own_reach()
    {
        var world = new AdmissionGateWorld();
        var grandchild = world.AddWorkflow(Graph(("n", Leaf, null)));
        var sibling = world.AddWorkflow(Graph(("n", Leaf, null)));
        var child = world.AddWorkflow(Graph(("call", Call, Calls(grandchild))));
        var root = world.AddWorkflow(Graph(("call", Call, Calls(child)), ("other", Call, Calls(sibling))));
        Guid VersionOf(Guid workflow) => world.Store.Workflows[workflow].CurrentVersion!.Id;
        var validator = new NodeValidator();
        using var engine = world.Engine(nodeValidator: validator);

        await engine.ExecuteAsync(root, null, null).WaitAsync(Patience);

        validator.Asked.Select(r => r.VersionId).Should().BeEquivalentTo(
            [VersionOf(root), VersionOf(child), VersionOf(grandchild), VersionOf(sibling)]);

        validator.Asked.Clear();
        using (WorkflowRunPinScope.Push(new WorkflowRunPin
        {
            VersionId = VersionOf(child),
            Manifest = new Dictionary<Guid, Guid>
            {
                [root] = VersionOf(root), [child] = VersionOf(child), [grandchild] = VersionOf(grandchild), [sibling] = VersionOf(sibling),
            },
            CallDepth = 1,
        }))
            await engine.ExecuteAsync(child, null, null, parentExecutionId: Guid.NewGuid()).WaitAsync(Patience);

        validator.Asked.Select(r => r.VersionId).Should().BeEquivalentTo([VersionOf(child), VersionOf(grandchild)]);
    }

    [Fact]
    public async Task A_child_start_under_a_node_validator_asks_the_helper_resolver_once_for_its_own_version()
    {
        var world = new AdmissionGateWorld();
        var child = world.AddWorkflow(Graph(("n", Leaf, new { v = "expr:${helper.x}" })));
        var version = world.Store.Workflows[child].CurrentVersion!.Id;
        var resolver = new CountingCheckResolver();
        var validator = new NodeValidator();
        using var engine = world.Engine(new CompilationOptions { HelperResolver = resolver }, nodeValidator: validator, helperResolver: resolver);
        using var pin = WorkflowRunPinScope.Push(new WorkflowRunPin
        {
            VersionId = version, Manifest = new Dictionary<Guid, Guid> { [child] = version }, CallDepth = 1,
        });

        await engine.ExecuteAsync(child, null, null, parentExecutionId: Guid.NewGuid()).WaitAsync(Patience);

        validator.Asked.Should().ContainSingle().Which.VersionId.Should().Be(version);
        resolver.CheckCalls.Should().Be(1);
    }

    private sealed class CountingCheckResolver : IHelperResolver
    {
        public int CheckCalls { get; private set; }

        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            CheckCalls++;
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(
                paths.ToDictionary(path => path, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Int")));
        }

        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(paths.ToDictionary(path => path, _ => (object?)1L));
    }

    [Fact]
    public async Task A_child_whose_parameters_cannot_be_read_is_refused_by_the_compile_and_runs_no_node()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: "not json");
        var version = world.Store.Workflows[id].CurrentVersion!.Id;
        using var engine = world.Engine();
        using var pin = WorkflowRunPinScope.Push(new WorkflowRunPin
        {
            VersionId = version, Manifest = new Dictionary<Guid, Guid> { [id] = version }, CallDepth = 1,
        });

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null, parentExecutionId: Guid.NewGuid()).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        refusal.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.SignatureUnreadable);
        world.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_version_whose_parameters_cannot_be_read_is_refused_by_the_compile_and_runs_no_node()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: "not json");
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        refusal.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.SignatureUnreadable);
        world.Executed.Should().BeEmpty();
    }

    private static string SinkGraph(params string[] parameters) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "sink", blockTypeId = Sink, position = new { x = 0, y = 0 }, configuration = new { } },
        },
        edges = new object[]
        {
            new
            {
                id = "e", sourceNodeId = "start", targetNodeId = "sink",
                mappings = parameters.ToDictionary(p => p, p => new { type = "direct", sourceOutputName = p }),
            },
        },
    });

    private const string CrateParameter = """[{"name":"Box","type":"Crate","isRequired":true}]""";

    private static CompilationOptions CrateOptions => new() { TypeRegistry = new WorkflowTypeRegistry([Crate]) };

    public sealed record Crate_(string Id);

    private static readonly WorkflowTypeRecord Crate = new("Crate", typeof(Crate_))
    {
        Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
        JsonTypeInfo = StrictJson.Info(typeof(Crate_)),
    };

    private static WorkflowExpressionNamespace TriggerNamespace(Func<string, string?> term, List<TriggerEnvelope?> seen) =>
        new("trigger", 1, new TriggerCheck(term), (scope, _) =>
        {
            seen.Add(scope.TriggerEnvelope);
            return Task.FromResult<IExpressionNamespaceResolver>(new NoResolver("trigger"));
        });

    private sealed class NoResolver(string name = "dock") : IExpressionNamespaceResolver
    {
        public string Namespace => name;

        public ResolveResult Resolve(string[] segments) => throw new InvalidOperationException("nothing reads it");
    }

    private sealed class TriggerCheck(Func<string, string?> term, string name = "trigger") : IExpressionNamespaceStaticCheck
    {
        public string Namespace => name;

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            term(segments[0]) is { } answer ? new(NamespacePathCheck.Yes, answer) : new(NamespacePathCheck.No, null);
    }

    private sealed class ArgumentValidator : IWorkflowStartArgumentValidator
    {
        public List<WorkflowStartArgumentsRequest> Asked { get; } = [];

        public Func<WorkflowStartArgumentsRequest, IReadOnlyList<WorkflowValidationFinding>> Answer { get; init; } = _ => [];

        public ValueTask<IReadOnlyList<WorkflowValidationFinding>> ValidateAsync(
            WorkflowStartArgumentsRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return ValueTask.FromResult(Answer(request));
        }
    }

    private sealed class NodeValidator : IWorkflowNodeValidator
    {
        public List<WorkflowNodeValidationRequest> Asked { get; } = [];

        public Func<WorkflowNodeValidationRequest, IReadOnlyList<WorkflowValidationFinding>> Answer { get; init; } = _ => [];

        public ValueTask<IReadOnlyList<WorkflowValidationFinding>> ValidateAsync(
            WorkflowNodeValidationRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return ValueTask.FromResult(Answer(request));
        }
    }
}
