using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class WorkflowCapabilityClaimTests
{
    private sealed record Crate(string Id);

    private sealed class Orderable : IComparable<Orderable>
    {
        public int CompareTo(Orderable? other) => 0;
    }

    private sealed class Comparing : IComparable<string>
    {
        public int CompareTo(string? other) => 0;
    }

    private sealed class Equal : IEquatable<Equal>
    {
        public bool Equals(Equal? other) => other is not null;
    }

    private readonly struct EqualStruct : IEquatable<EqualStruct>
    {
        public bool Equals(EqualStruct other) => true;
    }

    private readonly record struct RecordStruct(int Value);

    private sealed class Plain;

    private sealed class FailsOnUse;

    private sealed class FailingConverter : JsonConverter<FailsOnUse>
    {
        public int Uses { get; private set; }

        public override FailsOnUse Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Uses++;
            throw new JsonException("read");
        }

        public override void Write(Utf8JsonWriter writer, FailsOnUse value, JsonSerializerOptions options)
        {
            Uses++;
            throw new JsonException("write");
        }
    }

    private static HashSet<WorkflowCapability> Claim(params WorkflowCapability[] capabilities) => [.. capabilities];

    private static JsonTypeInfo Info(Type type, JsonConverter? converter = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Default) { AllowDuplicateProperties = false };
        if (converter is not null)
            options.Converters.Add(converter);
        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        return options.GetTypeInfo(type);
    }

    private static string[] Refused(WorkflowTypeRegistrationRecord record) => record switch
    {
        WorkflowTypeRecord atom => [.. atom.Validate().Select(r => $"{r.Code}:{r.Record}")],
        WorkflowTypeConstructorRecord constructor => [.. constructor.Validate().Select(r => $"{r.Code}:{r.Record}")],
        _ => throw new ArgumentException("unknown record"),
    };

    [Fact]
    public void A_record_that_claims_nothing_is_never_asked_anything()
    {
        Refused(new WorkflowTypeRecord("Anything", typeof(Plain))).Should().BeEmpty();
        Refused(new WorkflowTypeConstructorRecord("Box", typeof(Plain)) { AllowedArguments = ["Int"] }).Should().BeEmpty();
    }

    [Fact]
    public void Comparable_holds_for_a_type_that_implements_IComparable_of_itself_only()
    {
        Refused(new WorkflowTypeRecord("Pallet", typeof(Orderable)) { Claims = Claim(WorkflowCapability.Comparable) }).Should().BeEmpty();
        Refused(new WorkflowTypeRecord("Text", typeof(string)) { Claims = Claim(WorkflowCapability.Comparable) }).Should().BeEmpty();
        Refused(new WorkflowTypeRecord("Plain", typeof(Plain)) { Claims = Claim(WorkflowCapability.Comparable) })
            .Should().Equal("capability-claim-invalid:Plain");
        Refused(new WorkflowTypeRecord("Other", typeof(Comparing)) { Claims = Claim(WorkflowCapability.Comparable) })
            .Should().Equal("capability-claim-invalid:Other");
    }

    [Fact]
    public void Equatable_holds_for_IEquatable_of_itself_or_a_record_and_not_for_a_class_that_only_overrides_Equals()
    {
        foreach (var clr in new[] { typeof(Equal), typeof(EqualStruct), typeof(Crate), typeof(RecordStruct), typeof(string) })
            Refused(new WorkflowTypeRecord("Held", clr) { Claims = Claim(WorkflowCapability.Equatable) }).Should().BeEmpty(clr.Name);

        Refused(new WorkflowTypeRecord("Plain", typeof(Plain)) { Claims = Claim(WorkflowCapability.Equatable) })
            .Should().Equal("capability-claim-invalid:Plain");
    }

    [Fact]
    public void A_refused_claim_names_the_record_and_the_CLR_type()
    {
        var refusal = new WorkflowTypeRecord("Plain", typeof(Plain)) { Claims = Claim(WorkflowCapability.Comparable) }.Validate().Single();

        refusal.Message.Should().Contain("Plain").And.Contain("Comparable").And.Contain("IComparable<Plain>");
    }

    [Theory]
    [InlineData(WorkflowCapability.Iterable, "only Collection")]
    [InlineData(WorkflowCapability.Numeric, "only Int and Number")]
    public void A_host_may_not_claim_what_is_the_librarys(WorkflowCapability capability, string why)
    {
        var refusal = new WorkflowTypeRecord("Quantity", typeof(double)) { Claims = Claim(capability) }.Validate().Single();

        (refusal.Code, refusal.Record).Should().Be(("capability-claim-invalid", "Quantity"));
        refusal.Message.Should().Contain(capability.ToString()).And.Contain("the library's").And.Contain(why);
    }

    [Fact]
    public void A_value_outside_the_enum_is_not_a_capability()
    {
        var refusal = new WorkflowTypeRecord("Odd", typeof(string)) { Claims = Claim((WorkflowCapability)99) }.Validate().Single();

        refusal.Code.Should().Be("capability-claim-invalid");
        refusal.Message.Should().Contain("not a capability");
    }

    [Fact]
    public void JsonSerializable_holds_when_the_JsonTypeInfo_describes_the_CLR_type()
    {
        Refused(new WorkflowTypeRecord("Crate", typeof(Crate)) { Claims = Claim(WorkflowCapability.JsonSerializable), JsonTypeInfo = Info(typeof(Crate)) })
            .Should().BeEmpty();
        Refused(new WorkflowTypeRecord("Shipment", typeof(string)) { Claims = Claim(WorkflowCapability.JsonSerializable), JsonTypeInfo = Info(typeof(string)) })
            .Should().BeEmpty();
    }

    [Fact]
    public void JsonSerializable_without_a_serializer_is_a_record_defect_as_the_registration_rules_say()
    {
        var refusal = new WorkflowTypeRecord("Crate", typeof(Crate))
        {
            Claims = Claim(WorkflowCapability.JsonSerializable),
        }.Validate().Single();

        (refusal.Code, refusal.Record).Should().Be(("port-type-registration-invalid", "Crate"));
        refusal.Message.Should().Contain("no JsonTypeInfo");
    }

    [Fact]
    public void A_serializer_for_another_type_refuses_the_record_claimed_or_not()
    {
        foreach (var claims in new[] { Claim(WorkflowCapability.JsonSerializable), Claim() })
        {
            var refusal = new WorkflowTypeRecord("Crate", typeof(Crate)) { Claims = claims, JsonTypeInfo = Info(typeof(string)) }.Validate().Single();

            refusal.Code.Should().Be("port-type-registration-invalid");
            refusal.Message.Should().Be("Port type 'Crate' cannot be registered: its JsonTypeInfo describes String, not Crate.");
        }
    }

    [Fact]
    public void A_claim_is_checked_by_the_JsonTypeInfo_type_and_the_serializer_is_never_run()
    {
        var converter = new FailingConverter();
        var record = new WorkflowTypeRecord("Fragile", typeof(FailsOnUse))
        {
            Claims = Claim(WorkflowCapability.JsonSerializable), JsonTypeInfo = Info(typeof(FailsOnUse), converter),
        };

        new WorkflowTypeRegistry([record]).Current.Types.Select(t => t.Name).Should().Contain("Fragile");
        new WorkflowTypeRegistry().Register("plugin:a", [record]).Accepted.Should().BeTrue();
        converter.Uses.Should().Be(0);
    }

    [Fact]
    public void A_constructor_is_verified_against_its_erasure_and_names_itself()
    {
        var comparable = new WorkflowTypeConstructorRecord("Gauge", typeof(Plain)) { AllowedArguments = ["Int"], Claims = Claim(WorkflowCapability.Comparable) };
        var json = new WorkflowTypeConstructorRecord("Gauge", typeof(Crate))
        {
            AllowedArguments = ["Int"], Claims = Claim(WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable),
            JsonTypeInfo = Info(typeof(Crate)),
        };

        Refused(comparable).Should().Equal("capability-claim-invalid:Gauge");
        comparable.Validate().Single().Message.Should().StartWith("Port type constructor 'Gauge'");
        Refused(json).Should().BeEmpty();
        Refused(json with { JsonTypeInfo = Info(typeof(string)) }).Should().Equal("port-type-registration-invalid:Gauge");
        Refused(json with { Claims = Claim(), JsonTypeInfo = Info(typeof(string)) }).Should().Equal("port-type-registration-invalid:Gauge");
        Refused(json with { Claims = Claim(WorkflowCapability.Numeric) }).Should().Equal("capability-claim-invalid:Gauge");
    }

    [Fact]
    public void A_host_registration_with_a_claim_its_type_does_not_bear_out_stops_startup_naming_the_type()
    {
        var building = () => new WorkflowTypeRegistry(
            [new WorkflowTypeRecord("Good", typeof(string)), new WorkflowTypeRecord("Plain", typeof(Plain)) { Claims = Claim(WorkflowCapability.Equatable) }]);

        var thrown = building.Should().Throw<WorkflowTypeRegistrationException>().Which;

        thrown.Refusals.Should().ContainSingle().Which.Should().Match<WorkflowRegistrationRefusal>(r => r.Code == "capability-claim-invalid" && r.Record == "Plain");
        thrown.Message.Should().Contain("capability-claim-invalid (Plain)");
    }

    [Fact]
    public void A_plugin_registration_with_a_bad_claim_applies_nothing_and_leaves_the_generation()
    {
        var registry = new WorkflowTypeRegistry();

        var result = registry.Register("plugin:bt",
        [
            new WorkflowTypeRecord("Fine", typeof(string)),
            new WorkflowTypeRecord("Quantity", typeof(double)) { Claims = Claim(WorkflowCapability.Numeric) },
        ]);

        result.Accepted.Should().BeFalse();
        result.Refusals.Should().ContainSingle().Which.Should().Match<WorkflowRegistrationRefusal>(r => r.Code == "capability-claim-invalid" && r.Record == "Quantity");
        (registry.Generation, result.Generation).Should().Be((0L, 0L));
        registry.Current.Types.Select(t => t.Name).Should().NotContain("Fine");
    }

    [Fact]
    public void Verified_claims_are_what_a_term_implements_and_leave_with_the_owner()
    {
        var registry = new WorkflowTypeRegistry();
        var crate = PortTerm.Parse("Crate", PortTermGrammar.Document);
        registry.Register("plugin:bt",
        [
            new WorkflowTypeRecord("Crate", typeof(Crate))
            {
                Claims = Claim(WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable),
                JsonTypeInfo = Info(typeof(Crate)),
            },
        ]).Accepted.Should().BeTrue();
        var whileLoaded = registry.Current;

        registry.Unregister("plugin:bt").Should().BeTrue();

        whileLoaded.CapabilitiesOf(crate).Should().Equal(WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable);
        whileLoaded.IsAssignable(crate, PortTerm.Parse("Json", PortTermGrammar.Document)).Should().BeTrue();
        ((Action)(() => registry.Current.CapabilitiesOf(crate))).Should().Throw<ArgumentException>().WithMessage("port-type-unregistered*");
    }

    [Fact]
    public void A_type_that_claims_nothing_implements_nothing_whatever_its_CLR_type_implements()
    {
        var vocabulary = new WorkflowTypeRegistry([new WorkflowTypeRecord("Text", typeof(string)), new WorkflowTypeRecord("Id", typeof(Crate))]).Current;

        vocabulary.CapabilitiesOf(PortTerm.Parse("Text", PortTermGrammar.Document)).Should().BeEmpty("string is IComparable<string>, but nobody claimed it");
        vocabulary.CapabilitiesOf(PortTerm.Parse("Id", PortTermGrammar.Document)).Should().BeEmpty();
        vocabulary.IsAssignable(PortTerm.Parse("Text", PortTermGrammar.Document), PortTerm.Parse("Json", PortTermGrammar.Document))
            .Should().BeFalse("a host type is JsonSerializable only by a verified claim");
    }

    [Fact]
    public void A_changed_claim_changes_the_fingerprint_and_no_claim_leaves_it_as_it_was()
    {
        string Print(params WorkflowCapability[] claims) => new WorkflowTypeRegistry(
            [new WorkflowTypeRecord("Text", typeof(string)) { Claims = Claim(claims) }]).Current.Fingerprint();

        Print().Should().Be(Print());
        Print(WorkflowCapability.Equatable).Should().NotBe(Print());
        Print(WorkflowCapability.Equatable).Should().NotBe(Print(WorkflowCapability.Comparable));
        Print(WorkflowCapability.Equatable, WorkflowCapability.Comparable).Should().Be(Print(WorkflowCapability.Comparable, WorkflowCapability.Equatable));
        new WorkflowTypeRegistry().Current.Fingerprint().Should().Be(WorkflowTypeVocabulary.Builtin.Fingerprint());
    }

    [Fact]
    public void A_claim_of_Equatable_that_does_not_hold_gets_one_refusal_naming_it()
    {
        var refusals = new WorkflowTypeRecord("Reading", typeof(UnequatableReading))
        {
            Claims = Claim(WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable),
            JsonTypeInfo = Info(typeof(UnequatableReading)),
        }.Validate();

        refusals.Should().ContainSingle().Which.Message.Should().Contain("is not a record and does not implement IEquatable");
    }

    private sealed class UnequatableReading
    {
        public string Unit { get; set; } = "kg";
    }

    [Fact]
    public void A_call_names_every_record_it_refuses_whether_a_rule_or_a_claim_broke()
    {
        var result = new WorkflowTypeRegistry().Register("plugin:a",
        [
            new WorkflowTypeRecord("Flat", typeof(string)) { Aliases = ["Flat"] },
            new WorkflowTypeRecord("Plain", typeof(Plain)) { Claims = Claim(WorkflowCapability.Equatable) },
        ]);

        result.Accepted.Should().BeFalse();
        result.Refusals.Select(r => r.Record).Should().BeEquivalentTo(["Flat", "Plain"]);
    }
}
