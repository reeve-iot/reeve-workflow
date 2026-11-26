using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.JsonPath;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class RepeatedMemberNameTests
{
    private static JsonElement Element(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static ClosedPortType Closed(string term) =>
        WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse(term, PortTermGrammar.Document))!;

    private static readonly List<(string Name, string Text, JsonRepeat? Repeat)> Rows = ReadRows();

    private static List<(string, string, JsonRepeat?)> ReadRows()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.RepeatedMemberPath()));
        return [.. file.RootElement.GetProperty("rows").EnumerateArray().Select(row =>
        (
            row.GetProperty("name").GetString()!,
            row.GetProperty("text").GetString()!,
            row.GetProperty("repeat") is { ValueKind: JsonValueKind.Object } repeat
                ? new JsonRepeat(repeat.GetProperty("name").GetString(), repeat.GetProperty("path").GetString()!)
                : (JsonRepeat?)null))];
    }

    public static TheoryData<string> Repeating() => [.. Rows.Where(row => row.Repeat is not null).Select(row => row.Name)];

    public static TheoryData<string> Clean() => [.. Rows.Where(row => row.Repeat is null).Select(row => row.Name)];

    private static (string Text, JsonRepeat? Repeat) Row(string name) =>
        Rows.Where(row => row.Name == name).Select(row => (row.Text, row.Repeat)).Single();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [MemberData(nameof(Repeating))]
    public void A_repeat_is_found_with_its_member_and_its_object_in_document_order(string name)
    {
        var (text, repeat) = Row(name);

        JsonMembers.FindRepeat(Element(text)).Should().Be(repeat);
    }

    [Theory]
    [MemberData(nameof(Clean))]
    public void Names_that_differ_or_sit_in_different_objects_are_no_repeat(string name)
    {
        JsonMembers.FindRepeat(Element(Row(name).Text)).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Repeating))]
    public void Parsing_text_with_a_repeat_is_refused_as_a_repeat_whatever_its_spelling(string name)
    {
        var (text, repeat) = Row(name);

        var thrown = FluentActions.Invoking(() => JsonMembers.Parse(Bytes(text))).Should().Throw<JsonRepeatException>().Which;

        thrown.Repeat.Should().Be(repeat!.Value);
    }

    [Theory]
    [MemberData(nameof(Clean))]
    public void Parsing_text_with_no_repeat_gives_the_document(string name)
    {
        using var document = JsonMembers.Parse(Bytes(Row(name).Text));

        document.RootElement.ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"a":1,}""")]
    [InlineData("""{"a":1,"a":2""")]
    public void Malformed_text_is_still_not_JSON_rather_than_a_repeat(string text)
    {
        FluentActions.Invoking(() => JsonMembers.Parse(Bytes(text)))
            .Should().Throw<JsonException>().Which.Should().NotBeOfType<JsonRepeatException>();
    }

    [Theory]
    [MemberData(nameof(Repeating))]
    public void Every_entry_of_a_Json_value_refuses_the_same_text(string name)
    {
        var json = Element(Row(name).Text);

        var gate = ValueGate.Admit(Closed("Json?"), json, out _);
        var codec = Closed("Json").TryFromJson(json, out _, out var reason);
        var literal = WorkflowTypeVocabulary.Builtin.IsLiteralOf(json, PortValueType.Json);

        gate.Should().StartWith("a Json value holding a ");
        codec.Should().BeFalse();
        reason.Should().NotBeNull();
        literal.Should().BeFalse();
        FluentActions.Invoking(() => Closed("Json").FromJson(json)).Should().Throw<PersistedValueReadException>();
    }

    [Fact]
    public void A_reason_of_the_gate_or_the_codec_carries_no_member_name_and_no_path()
    {
        var json = Element("""{"secret":{"token":1,"token":2}}""");

        var gate = ValueGate.Admit(Closed("Json"), json, out _);
        Closed("Json").TryFromJson(json, out _, out var reason);

        (gate + reason).Should().NotContain("token").And.NotContain("secret").And.NotContain("$");
    }

    [Fact]
    public void A_Json_value_that_repeats_nothing_is_admitted_read_and_graded()
    {
        var json = Element("""{"a":{"k":1},"b":{"k":2}}""");

        ValueGate.Admit(Closed("Json"), json, out var admitted).Should().BeNull();
        admitted.Should().Be(json);
        Closed("Json").TryFromJson(json, out _, out _).Should().BeTrue();
        WorkflowTypeVocabulary.Builtin.IsLiteralOf(json, PortValueType.Json).Should().BeTrue();
    }

    [Fact]
    public void The_gate_reads_the_elements_of_a_collection_of_Json_and_a_nested_one()
    {
        var clean = Element("""{"k":1}""");
        var repeating = Element("""{"k":1,"k":2}""");

        ValueGate.Admit(Closed("Collection<Json>"), new List<JsonElement> { clean, repeating }, out _).Should().Contain("repeated member name");
        ValueGate.Admit(Closed("Collection<Collection<Json>>"), new List<IReadOnlyList<JsonElement>> { new[] { clean }, new[] { clean, repeating } }, out _)
            .Should().Contain("repeated member name");
        ValueGate.Admit(Closed("Collection<Json>"), new List<JsonElement> { clean, clean }, out _).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"m":{"a":1},"m":{"b":2}}""", "$.m", "Map<String,Int>")]
    [InlineData("""{"m":{"a":1,"a":2}}""", "$.m", "Map<String,Int>")]
    [InlineData("""{"a":1,"other":{"k":1,"k":2}}""", "$.a", "Int")]
    [InlineData("""{"a":1,"list":[{"x":[{"y":1,"y":2}]}]}""", "$.a", "Int")]
    public void A_projection_refuses_a_source_that_repeats_a_member_wherever_the_path_goes(string text, string path, string term)
    {
        var projection = new CompiledProjection(JsonPathQuery.Parse(path), false, Closed(term), null, WorkflowTypeVocabulary.Builtin);

        var thrown = FluentActions.Invoking(() => projection.TryBind(Element(text), "wire", out _))
            .Should().Throw<JsonProjectionException>().Which;

        thrown.Category.Should().Be(JsonProjectionException.Narrowing);
        thrown.Message.Should().StartWith("wire:").And.Contain("more than once").And.Contain("in the object at $");
    }

    [Fact]
    public void parseJson_refuses_text_that_repeats_a_member_name_and_says_which_and_where()
    {
        var failure = FluentActions.Invoking(() => ExpressionOperations.ParseJson("""{"outer":[{"a":1,"a":2}]}"""))
            .Should().Throw<ExpressionOperationException>().Which;

        failure.Category.Should().Be("json-parse");
        failure.Message.Should().Contain("'a'").And.Contain("$.outer[0]");
    }

    [Fact]
    public void parseJson_names_a_repeat_spelled_with_an_escape_as_a_repeat_and_not_as_text_that_is_not_JSON()
    {
        var failure = FluentActions.Invoking(() => ExpressionOperations.ParseJson("""{"a":1,"\u0061":2}"""))
            .Should().Throw<ExpressionOperationException>().Which;

        failure.Message.Should().Contain("more than once").And.Contain("'a'");
    }

    [Fact]
    public void A_default_output_text_that_repeats_an_output_name_does_not_read_and_says_which()
    {
        var outputs = WorkflowGraphExecutor.ReadDefaultOutputs("""{"Result":1,"Result":2}""", out var unparsed);

        outputs.Should().BeEmpty();
        unparsed.Should().Contain("'Result'").And.Contain("$");
    }

    [Theory]
    [InlineData("""{"maxAttempts":0,"maxAttempts":3}""")]
    [InlineData("""{"maxAttempts":3,"MaxAttempts":0}""")]
    public void A_retry_policy_that_repeats_a_name_is_refused_at_the_save_and_read_as_no_policy_by_a_run(string policy)
    {
        WorkflowRowSettings.RetryPolicyFault(policy)!.Reason.Should().Be(WorkflowRowSettings.RetryPolicyMalformed);

        WorkflowRunAttempt.ParseRetryPolicy(policy, WorkflowGraphHelpers.JsonOptions, NullLogger.Instance)
            .Should().BeNull("a case-insensitive reader would otherwise take one of the two spellings by their order");
    }

    [Fact]
    public void A_retry_policy_that_repeats_a_name_it_never_reads_is_refused_at_the_save_too()
    {
        WorkflowRowSettings.RetryPolicyFault("""{"maxAttempts":3,"note":{"k":1,"k":2}}""")!.Reason
            .Should().Be(WorkflowRowSettings.RetryPolicyMalformed);
    }

    [Theory]
    [InlineData("""{"mode":"EveryBlock","Mode":"Heartbeat","heartbeatSeconds":30}""")]
    [InlineData("""{"Mode":"Heartbeat","HeartbeatSeconds":30,"HeartbeatSeconds":60}""")]
    public void A_checkpoint_policy_that_repeats_a_name_is_not_read(string policy)
    {
        WorkflowCheckpointPolicy.Read(policy, WorkflowGraphHelpers.JsonOptions, out var mode).Should().NotBeNull();

        mode.Should().BeNull();
    }

    public sealed record Reading(int R);

    private static JsonSerializerOptions HostOptions(bool allowDuplicates, bool ignoreCase) => new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        AllowDuplicateProperties = allowDuplicates,
        PropertyNameCaseInsensitive = ignoreCase,
    };

    [Theory]
    [InlineData("""{"r":1,"R":2}""")]
    [InlineData("""{"R":1,"R":2}""")]
    public void A_reader_made_refusing_counts_a_name_in_a_second_casing_as_a_repeat(string text)
    {
        var options = JsonMembers.Refusing(HostOptions(allowDuplicates: true, ignoreCase: true));

        FluentActions.Invoking(() => JsonSerializer.Deserialize<Reading>(text, options)).Should().Throw<JsonException>();
        JsonSerializer.Deserialize<Reading>("""{"r":1}""", options).Should().Be(new Reading(1));
    }

    [Fact]
    public void A_host_record_read_case_insensitively_refuses_a_value_that_names_a_member_in_two_casings()
    {
        var info = HostOptions(allowDuplicates: false, ignoreCase: true).GetTypeInfo(typeof(Reading));
        var vocabulary = new WorkflowTypeRegistry([new WorkflowTypeRecord("Reading", typeof(Reading))
        {
            Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
            JsonTypeInfo = info,
        }]).Current;
        var closed = vocabulary.ClosedTypeOf(PortTerm.Parse("Reading", PortTermGrammar.Document))!;

        closed.TryFromJson(Element("""{"r":1,"R":2}"""), out _, out _).Should().BeFalse();
        closed.TryFromJson(Element("""{"r":1}"""), out var read, out _).Should().BeTrue();
        read.Should().Be(new Reading(1));
    }

    [Fact]
    public void A_record_whose_JsonTypeInfo_allows_a_repeated_member_is_refused_at_registration_naming_the_type_and_the_change()
    {
        var lax = HostOptions(allowDuplicates: true, ignoreCase: false).GetTypeInfo(typeof(Reading));
        var atom = new WorkflowTypeRecord("Reading", typeof(Reading)) { JsonTypeInfo = lax }.Validate();
        var constructor = new WorkflowTypeConstructorRecord("Gauge", typeof(Reading)) { AllowedArguments = ["Boolean"], JsonTypeInfo = lax }.Validate();

        foreach (var refusals in new[] { atom, constructor })
        {
            refusals.Should().ContainSingle().Which.Should().Match<WorkflowRegistrationRefusal>(r =>
                r.Code == WorkflowRegistrationCodes.PortTypeRegistrationInvalid
                && r.Message.Contains("Reading") && r.Message.Contains("AllowDuplicateProperties = false"));
        }

        FluentActions.Invoking(() => new WorkflowTypeRegistry([new WorkflowTypeRecord("Reading", typeof(Reading)) { JsonTypeInfo = lax }]))
            .Should().Throw<WorkflowTypeRegistrationException>();
    }

    [Fact]
    public void A_record_whose_JsonTypeInfo_refuses_a_repeated_member_registers_and_so_does_a_scalar_from_any_options()
    {
        var strict = HostOptions(allowDuplicates: false, ignoreCase: true).GetTypeInfo(typeof(Reading));
        var scalar = JsonSerializerOptions.Default.GetTypeInfo(typeof(string));

        new WorkflowTypeRecord("Reading", typeof(Reading)) { JsonTypeInfo = strict }.Validate().Should().BeEmpty();
        new WorkflowTypeConstructorRecord("Gauge", typeof(Reading)) { AllowedArguments = ["Boolean"], JsonTypeInfo = strict }.Validate().Should().BeEmpty();
        new WorkflowTypeRecord("Shipment", typeof(string)) { JsonTypeInfo = scalar }.Validate().Should().BeEmpty();
    }
}
