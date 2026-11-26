using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class PortLiteralVectorTests
{
    private static readonly Lazy<JsonDocument> File = new(() => JsonDocument.Parse(System.IO.File.ReadAllText(RepositoryFiles.LiteralCoercionPath())));

    [Fact]
    public void The_vocabulary_grades_each_vector_the_file_states()
    {
        var vocabulary = LiteralVectors.VocabularyOf(File.Value.RootElement);
        var wrong = new List<string>();
        var graded = 0;
        JsonElement json = default;
        foreach (var row in File.Value.RootElement.GetProperty("rows").EnumerateArray())
        {
            var type = PortValueType.Of(row.GetProperty("type").GetString()!);
            var expected = row.GetProperty("accepted").GetBoolean();
            var spelledAsText = row.TryGetProperty("text", out var text);
            var hasValue = spelledAsText || row.TryGetProperty("value", out json);
            if (spelledAsText)
                json = JsonDocument.Parse(text.GetString()!).RootElement;
            var clr = hasValue && !spelledAsText ? WorkflowGraphHelpers.JsonToValue(json) : null;

            if (!spelledAsText && vocabulary.IsLiteralOf(clr, type) != expected)
                wrong.Add($"{row} as a CLR value");
            if (vocabulary.IsLiteralOf(hasValue ? json : null, type) != expected)
                wrong.Add($"{row} as a JsonElement");
            graded++;
        }

        wrong.Should().BeEmpty();
        graded.Should().BeGreaterThan(150);
    }

    [Fact]
    public void The_shared_function_asks_the_forms_the_vocabulary_holds()
    {
        var vocabulary = LiteralVectors.VocabularyOf(File.Value.RootElement);
        foreach (var row in File.Value.RootElement.GetProperty("rows").EnumerateArray())
        {
            var type = PortValueType.Of(row.GetProperty("type").GetString()!);
            if (row.TryGetProperty("text", out _))
                continue;
            var value = row.TryGetProperty("value", out var json) ? WorkflowGraphHelpers.JsonToValue(json) : null;
            var canonical = PortValueType.Of(vocabulary.Table.Canonicalize(PortTerm.Parse(type.Name, PortTermGrammar.Document)).ToString());

            PortLiteralCoercion.IsLiteralOf(value, canonical, vocabulary.LiteralFormOf)
                .Should().Be(row.GetProperty("accepted").GetBoolean(), $"{row}");
        }
    }

    [Theory]
    [InlineData("$T", 5)]
    [InlineData("Collection<$T>", null)]
    [InlineData("$T?", "x")]
    public void A_type_that_names_a_parameter_is_graded_when_the_parameter_is_bound(string type, object? value)
    {
        var vocabulary = LiteralVectors.VocabularyOf(File.Value.RootElement);
        var declared = PortValueType.OfDeclaration(type);

        vocabulary.IsLiteralOf(value, declared).Should().BeTrue();
        PortLiteralCoercion.IsLiteralOf(value, declared, vocabulary.LiteralFormOf).Should().BeFalse("the shared function cannot grade an open term");
    }

    [Fact]
    public void The_library_declares_a_form_for_each_of_its_atoms_and_none_for_Collection()
    {
        var vocabulary = LiteralVectors.VocabularyOf(File.Value.RootElement);

        vocabulary.LiteralFormOf("Boolean").Should().Be(new WorkflowLiteralForm(WorkflowLiteralKind.Boolean));
        vocabulary.LiteralFormOf("int").Should().Be(new WorkflowLiteralForm(WorkflowLiteralKind.Int));
        vocabulary.LiteralFormOf("Number").Should().Be(new WorkflowLiteralForm(WorkflowLiteralKind.Number));
        vocabulary.LiteralFormOf("String").Should().Be(new WorkflowLiteralForm(WorkflowLiteralKind.String));
        vocabulary.LiteralFormOf("DateTime").Should().Be(new WorkflowLiteralForm(WorkflowLiteralKind.String, WorkflowLiteralFormat.DateTime));
        vocabulary.LiteralFormOf("Json").Should().Be(new WorkflowLiteralForm(WorkflowLiteralKind.Json));
        vocabulary.LiteralFormOf("Collection").Should().BeNull();
        vocabulary.Types.Single(t => t.Name == "Ref").Literal.Should().Be(new WorkflowLiteralForm(WorkflowLiteralKind.String, WorkflowLiteralFormat.Uuid));
        vocabulary.Types.Single(t => t.Name == "Scene").Literal.Should().BeNull();
        vocabulary.Constructors.Single(c => c.Name == "Gauge").Literal.Should().Be(new WorkflowLiteralForm(WorkflowLiteralKind.Json));
        vocabulary.Constructors.Single(c => c.Name == "Collection").Literal.Should().BeNull();
    }

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo StringInfo =
        StrictJson.Info(typeof(string));

    [Fact]
    public void A_literal_form_without_a_JsonTypeInfo_is_port_type_registration_invalid()
    {
        var form = new WorkflowLiteralForm(WorkflowLiteralKind.String);

        new WorkflowTypeRecord("Stamp", typeof(string)) { Literal = form }.Validate().Should().ContainSingle().Which.Should().Be(
            new WorkflowRegistrationRefusal(
                WorkflowRegistrationCodes.PortTypeRegistrationInvalid, "Stamp",
                "Port type 'Stamp' cannot be registered: a literal form needs a JsonTypeInfo to read its literals."));
        new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Boolean"], Literal = form }.Validate().Should().ContainSingle().Which
            .Message.Should().Be("Port type constructor 'Gauge' cannot be registered: a literal form needs a JsonTypeInfo to read its literals.");
        var registering = () => new WorkflowTypeRegistry([new WorkflowTypeRecord("Stamp", typeof(string)) { Literal = form }]);
        registering.Should().Throw<WorkflowTypeRegistrationException>().Which.Refusals.Should().ContainSingle()
            .Which.Code.Should().Be(WorkflowRegistrationCodes.PortTypeRegistrationInvalid);
    }

    public static TheoryData<WorkflowLiteralForm, string> RefusedForms() => new()
    {
        { new WorkflowLiteralForm((WorkflowLiteralKind)99), "outside String, Boolean, Int, Number and Json" },
        { new WorkflowLiteralForm(WorkflowLiteralKind.String, (WorkflowLiteralFormat)99), "outside Uuid and DateTime" },
        { new WorkflowLiteralForm(WorkflowLiteralKind.Int, WorkflowLiteralFormat.Uuid), "applies only to a form graded as String, not Int" },
        { new WorkflowLiteralForm(WorkflowLiteralKind.Json, WorkflowLiteralFormat.DateTime), "applies only to a form graded as String, not Json" },
    };

    [Theory]
    [MemberData(nameof(RefusedForms))]
    public void A_record_with_a_form_outside_the_closed_set_is_port_type_registration_invalid(WorkflowLiteralForm form, string why)
    {
        var atom = new WorkflowTypeRecord("Stamp", typeof(string)) { Literal = form, JsonTypeInfo = StringInfo }.Validate();
        var constructor = new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Boolean"], Literal = form, JsonTypeInfo = StringInfo }.Validate();

        foreach (var refusals in new[] { atom, constructor })
        {
            refusals.Should().ContainSingle().Which.Should().Match<WorkflowRegistrationRefusal>(r =>
                r.Code == WorkflowRegistrationCodes.PortTypeRegistrationInvalid && r.Message.Contains(why));
        }

        var registering = () => new WorkflowTypeRegistry([new WorkflowTypeRecord("Stamp", typeof(string)) { Literal = form, JsonTypeInfo = StringInfo }]);
        registering.Should().Throw<WorkflowTypeRegistrationException>().Which.Refusals.Should().ContainSingle()
            .Which.Code.Should().Be(WorkflowRegistrationCodes.PortTypeRegistrationInvalid);
    }

    [Theory]
    [InlineData(WorkflowLiteralKind.String, null)]
    [InlineData(WorkflowLiteralKind.String, WorkflowLiteralFormat.Uuid)]
    [InlineData(WorkflowLiteralKind.String, WorkflowLiteralFormat.DateTime)]
    [InlineData(WorkflowLiteralKind.Boolean, null)]
    [InlineData(WorkflowLiteralKind.Int, null)]
    [InlineData(WorkflowLiteralKind.Number, null)]
    [InlineData(WorkflowLiteralKind.Json, null)]
    public void Every_form_of_the_closed_set_registers(WorkflowLiteralKind kind, WorkflowLiteralFormat? format)
    {
        var form = new WorkflowLiteralForm(kind, format);

        new WorkflowTypeRecord("Stamp", typeof(string)) { Literal = form, JsonTypeInfo = StringInfo }.Validate().Should().BeEmpty();
        new WorkflowTypeRegistry([new WorkflowTypeRecord("Stamp", typeof(string)) { Literal = form, JsonTypeInfo = StringInfo }]).Current.LiteralFormOf("Stamp")
            .Should().Be(form);
    }

    [Fact]
    public void A_plugins_form_leaves_with_its_plugin()
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register("plugin:p", [new WorkflowTypeRecord("Stamp", typeof(string)) { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String), JsonTypeInfo = StringInfo }]);
        registry.Current.IsLiteralOf("x", PortValueType.Of("Stamp")).Should().BeTrue();

        registry.Unregister("plugin:p").Should().BeTrue();

        registry.Current.LiteralFormOf("Stamp").Should().BeNull();
        registry.Current.IsLiteralOf("x", PortValueType.Of("Stamp")).Should().BeFalse("a type that is not registered has no form");
    }
}
