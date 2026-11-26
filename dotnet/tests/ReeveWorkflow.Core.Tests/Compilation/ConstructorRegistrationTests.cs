using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class ConstructorRegistrationTests
{
    private static WorkflowTypeConstructorRecord Gauge { get; } =
        new("Gauge", typeof(string)) { AllowedArguments = ["Boolean", "Number", "String"] };

    private static PortTypeWideningTable Table => PortTypeWideningTable.Builtin.WithConstructor(Gauge);

    private static PortTerm Term(string text) => PortTerm.Parse(text, PortTermGrammar.Document);

    [Fact]
    public void A_valid_record_has_no_refusal()
    {
        Gauge.Validate().Should().BeEmpty();
    }

    [Theory]
    [InlineData("Collection", "built-in constructor")]
    [InlineData("collectionOf", "reserved")]
    [InlineData("ANY", "Any is a reserved name")]
    [InlineData("Float", "Float is a reserved name")]
    [InlineData("expression", "Expression is the library's")]
    [InlineData("a b", "not a name")]
    [InlineData("", "not a name")]
    public void A_name_no_host_may_take_is_refused_with_the_registration_code(string name, string why)
    {
        var refusals = new WorkflowTypeConstructorRecord(name, typeof(string)) { AllowedArguments = ["Int"] }.Validate();

        refusals.Should().ContainSingle().Which.Should().Match<WorkflowRegistrationRefusal>(r =>
            r.Code == "port-type-registration-invalid" && r.Record == name && r.Message.Contains(why));
    }

    [Fact]
    public void A_record_without_an_erasure_or_without_allowed_arguments_says_so_for_each()
    {
        var refusals = new WorkflowTypeConstructorRecord("Gauge", null!).Validate();

        refusals.Select(r => r.Message).Should().Satisfy(
            m => m.Contains("names no erasure type"), m => m.Contains("lists no allowed argument"));
        new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = null! }.Validate()
            .Should().ContainSingle().Which.Message.Should().Contain("lists no allowed argument");
    }

    [Theory]
    [InlineData("$T", "is not a closed term")]
    [InlineData("Int??", "is not a closed term")]
    [InlineData("Collection<", "is not a closed term")]
    [InlineData("Json", "holds Json")]
    [InlineData("Json?", "holds Json")]
    [InlineData("Collection<json>", "holds Json")]
    public void An_allowed_argument_must_be_a_closed_term_without_Json(string argument, string why)
    {
        var refusals = new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Boolean", argument] }.Validate();

        refusals.Should().ContainSingle().Which.Message.Should().Contain($"allowed argument '{argument}' ").And.Contain(why);
    }

    [Fact]
    public void The_table_refuses_an_invalid_record_with_its_code_and_every_reason()
    {
        var record = new WorkflowTypeConstructorRecord("Collection", typeof(string)) { AllowedArguments = [] };

        var registering = () => PortTypeWideningTable.Builtin.WithConstructor(record);

        registering.Should().Throw<ArgumentException>()
            .WithMessage("port-type-registration-invalid: *built-in constructor*port-type-registration-invalid: *lists no allowed argument*");
    }

    [Fact]
    public void A_registered_constructor_is_listed_and_found_whatever_the_case()
    {
        var table = Table.WithConstructor(new("Probe", typeof(string)) { AllowedArguments = ["Int"] });

        table.Constructors.Select(c => c.Name).Should().Equal("Gauge", "Probe");
        table.TryGetConstructor("gAUGE", out var found).Should().BeTrue();
        found.Should().BeSameAs(Gauge);
        table.TryGetConstructor("Latch", out _).Should().BeFalse();
        PortTypeWideningTable.Builtin.Constructors.Should().BeEmpty("registering returns a new table and leaves the builtins alone");
    }

    [Theory]
    [InlineData("Json")]
    [InlineData("bool")]
    [InlineData("Boolean")]
    public void A_constructor_named_like_a_registered_type_is_a_name_clash(string name)
    {
        var registering = () => Table.WithConstructor(new(name, typeof(string)) { AllowedArguments = ["Int"] });

        registering.Should().Throw<ArgumentException>().WithMessage(
            $"port-type-name-clash: Port type constructor '{name}' cannot be registered: a port type of that name is already registered.*");
    }

    [Fact]
    public void A_second_constructor_of_the_same_name_in_any_case_is_a_name_clash()
    {
        var registering = () => Table.WithConstructor(new("GAUGE", typeof(string)) { AllowedArguments = ["Int"] });

        registering.Should().Throw<ArgumentException>().WithMessage(
            "port-type-name-clash: *'GAUGE'*a constructor of that name is already registered.*");
    }

    [Fact]
    public void A_type_declared_after_a_constructor_cannot_take_its_name()
    {
        var table = Table;

        new Action[]
        {
            () => table.Declaring(PortValueType.Of("gauge")),
            () => table.With(PortValueType.Of("Gauge"), [PortValueType.Number]),
            () => table.With(PortValueType.Of("Crate"), [PortValueType.Of("Gauge")]),
            () => table.WithAlias(PortValueType.Of("gauge"), PortValueType.Number),
            () => table.WithAlias(PortValueType.Of("Reading"), PortValueType.Of("Gauge")),
        }.Should().AllSatisfy(a =>
            a.Should().Throw<ArgumentException>().WithMessage("port-type-name-clash: *a constructor of that name is already registered.*"));
    }

    [Theory]
    [InlineData("Gauge<Boolean>")]
    [InlineData("Gauge<Number>")]
    [InlineData("Gauge<String>")]
    [InlineData("Gauge<Boolean>?")]
    [InlineData("gauge<String>")]
    [InlineData("Collection<Gauge<Boolean>?>")]
    public void A_term_over_a_registered_constructor_and_an_allowed_argument_is_registered(string term)
    {
        Table.RefusalOf(Term(term)).Should().BeNull();
    }

    [Theory]
    [InlineData("Widget<Boolean>", "'Widget' is not a registered type constructor")]
    [InlineData("Collection<Widget<Boolean>>", "'Widget' is not a registered type constructor")]
    [InlineData("Gauge<Int>", "'Gauge' does not admit the argument 'Int'")]
    [InlineData("Gauge<Boolean?>", "'Gauge' does not admit the argument 'Boolean?'")]
    [InlineData("Collection<Gauge<Int>>", "'Gauge' does not admit the argument 'Int'")]
    [InlineData("Gauge", "'Gauge' is not a registered type")]
    public void A_term_over_an_unregistered_head_or_a_disallowed_argument_is_port_type_unregistered(string term, string message)
    {
        Table.RefusalOf(Term(term)).Should().Be(new PortTermRefusal("port-type-unregistered", message));
    }

    [Fact]
    public void A_type_parameter_argument_is_admitted_for_a_registered_head_and_judged_when_bound()
    {
        var declared = PortTerm.Parse("Gauge<$T>", PortTermGrammar.Declaration);
        var unregistered = PortTerm.Parse("Widget<$T>", PortTermGrammar.Declaration);

        Table.RefusalOf(declared).Should().BeNull();
        Table.RefusalOf(unregistered)!.Code.Should().Be("port-type-unregistered");
    }

    [Fact]
    public void A_parameter_nested_inside_an_argument_is_not_admitted()
    {
        var table = PortTypeWideningTable.Builtin.WithConstructor(new("Gauge", typeof(string)) { AllowedArguments = ["Collection<Int>"] });

        table.RefusalOf(PortTerm.Parse("Gauge<Collection<$T>>", PortTermGrammar.Declaration))
            .Should().Be(new PortTermRefusal("port-type-unregistered", "'Gauge' does not admit the argument 'Collection<$T>'"));
    }

    [Theory]
    [InlineData("Any", "names the reserved name 'Any'")]
    [InlineData("Collection<float>", "names the reserved name 'Float'")]
    public void A_reserved_allowed_argument_is_refused_by_the_record(string argument, string why)
    {
        new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = [argument] }.Validate()
            .Should().ContainSingle().Which.Message.Should().Contain(why);
    }

    [Theory]
    [InlineData("bool", "'bool' is spelled 'Boolean'")]
    [InlineData("Collection<STRING>", "'STRING' is spelled 'String'")]
    public void A_non_canonical_allowed_argument_is_refused_by_the_table_with_the_registration_code(string argument, string why)
    {
        var registering = () => PortTypeWideningTable.Builtin.WithConstructor(
            new("Gauge", typeof(string)) { AllowedArguments = [argument] });

        registering.Should().Throw<ArgumentException>().WithMessage(
            $"port-type-registration-invalid: Port type constructor 'Gauge' cannot be registered: allowed argument '{argument}' is not canonical: {why}.*");
    }

    [Fact]
    public void A_type_added_after_a_constructor_is_named_as_the_thing_refused()
    {
        var declaring = () => Table.Declaring(PortValueType.Of("Gauge"));
        var aliasing = () => Table.WithAlias(PortValueType.Of("Gauge"), PortValueType.Number);

        declaring.Should().Throw<ArgumentException>().WithMessage("port-type-name-clash: Port type 'Gauge' cannot be registered:*");
        aliasing.Should().Throw<ArgumentException>().WithMessage("port-type-name-clash: Port type alias 'Gauge' cannot be registered:*");
    }

    [Fact]
    public void A_term_a_hundred_thousand_levels_deep_is_judged_without_recursing()
    {
        const int depth = 100_000;
        var deep = string.Concat(Enumerable.Repeat("Collection<", depth)) + "Gauge<Boolean>" + new string('>', depth);
        var refused = string.Concat(Enumerable.Repeat("Collection<", depth)) + "Gauge<Int>" + new string('>', depth);

        Table.RefusalOf(Term(deep)).Should().BeNull();
        Table.RefusalOf(Term(refused))!.Message.Should().Be("'Gauge' does not admit the argument 'Int'");
    }

    [Fact]
    public void A_constructor_is_invariant_in_assignability_and_its_registered_terms_decide_nothing_else()
    {
        var vocabulary = new NoRowsVocabulary();
        var table = Table;
        bool Assignable(string from, string to) => PortTermAssignability.IsAssignable(Term(from), Term(to), vocabulary);

        table.RefusalOf(Term("Gauge<Number>")).Should().BeNull();
        Assignable("Gauge<Boolean>", "Gauge<Boolean>").Should().BeTrue("A1");
        Assignable("Gauge<Boolean>", "Gauge<Boolean>?").Should().BeTrue("A2");
        Assignable("Gauge<Boolean>", "Gauge<Boolean?>").Should().BeFalse("the argument's nullability is part of the term");
        Assignable("Gauge<Number>", "Gauge<Boolean>").Should().BeFalse();
        Assignable("Collection<Gauge<Boolean>>", "Collection<Gauge<Boolean?>>").Should().BeFalse("A5 steps into a collection, never into a host constructor");
        Assignable("Collection<Gauge<Boolean>>", "Collection<Gauge<Boolean>>").Should().BeTrue();
    }

    [Fact]
    public void A_stored_name_made_of_a_constructor_stays_unregistered_to_the_legacy_readers()
    {
        var table = Table;

        table.IsRegistered(PortValueType.Of("Gauge<Boolean>")).Should().BeFalse();
        table.IsRegistered(PortValueType.Of("Gauge")).Should().BeFalse();
        table.KnownTypes.Should().Equal(PortTypeWideningTable.Builtin.KnownTypes);
    }

    [Fact]
    public void Registering_a_constructor_changes_the_fingerprint_and_so_does_each_field_of_it()
    {
        var plain = PortTypeWideningTable.Builtin.Fingerprint();
        string Print(WorkflowTypeConstructorRecord record) => PortTypeWideningTable.Builtin.WithConstructor(record).Fingerprint();
        var baseline = Print(Gauge);

        baseline.Should().NotBe(plain);
        Print(Gauge with { AllowedArguments = ["Boolean", "Number"] }).Should().NotBe(baseline);
        Print(Gauge with { AllowedArguments = ["Boolean", "Number", "String?"] }).Should().NotBe(baseline);
        Print(Gauge with { Erasure = typeof(Uri) }).Should().NotBe(baseline);
        Print(Gauge with { Erasure = typeof(List<int>) }).Should().NotBe(Print(Gauge with { Erasure = typeof(List<long>) }), "generic arguments are part of the identity");
        Print(Gauge with { AllowedArguments = ["String", "Number", "Boolean"] }).Should().Be(baseline, "the set has no order");
        Print(Gauge with { Name = "GAUGE" }).Should().NotBe(baseline, "a case-only respelling changes what an old spelling grades as");
    }

    private sealed class NoRowsVocabulary : IPortTermVocabulary
    {
        public bool HasRow(PortTerm from, PortTerm to) => false;

        public bool IsJsonSerializable(PortTerm head) => true;
    }
}
