using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class WorkflowTypeRegistryTests
{
    private static WorkflowTypeRecord Atom(string name, Type? clr = null) => new(name, clr ?? typeof(string));

    private static PortTerm Term(string text) => PortTerm.Parse(text, PortTermGrammar.Document);

    private static WorkflowTypeRegistry Freight() => new(
    [
        Atom("Shipment"),
        Atom("ReeferShipment") with { WidensTo = ["Shipment"] },
        Atom("TankerShipment") with { WidensTo = ["Shipment"] },
        new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Boolean", "Number"] },
    ]);

    private static WorkflowTypeRegistry FreightWithGaugeErasure(Type erasure) => new(
    [
        Atom("Shipment"),
        Atom("ReeferShipment") with { WidensTo = ["Shipment"] },
        Atom("TankerShipment") with { WidensTo = ["Shipment"] },
        new WorkflowTypeConstructorRecord("Gauge", erasure) { AllowedArguments = ["Boolean", "Number"] },
    ]);

    private static string[] Refused(WorkflowTypeRegistrationResult result) => [.. result.Refusals.Select(r => $"{r.Code}:{r.Record}")];

    [Fact]
    public void A_registry_with_no_host_record_holds_the_six_library_atoms_and_the_collection_and_map_constructors_at_generation_0()
    {
        var registry = new WorkflowTypeRegistry();

        var types = registry.Current.Types;
        types.Select(t => (t.Name, t.Owner)).Should().BeEquivalentTo(
            new[] { "Boolean", "DateTime", "Int", "Json", "Number", "String" }.Select(n => (n, "library")));
        types.Single(t => t.Name == "Int").WidensTo.Should().Equal("Number");
        types.Where(t => t.Name != "Int").Select(t => t.WidensTo.Count).Should().OnlyContain(c => c == 0);
        registry.Current.Constructors.Should().BeEquivalentTo(
        [
            new RegisteredPortConstructor("Collection", "library", WorkflowTypeVariance.Covariant, null, null, [WorkflowCapability.Iterable]),
            new RegisteredPortConstructor("Map", "library", WorkflowTypeVariance.Covariant, null, null, [], Arity: 2, Key: "String"),
        ], o => o.WithStrictOrdering());
        registry.Generation.Should().Be(0);
    }

    [Theory]
    [InlineData("Boolean", typeof(bool))]
    [InlineData("Int", typeof(long))]
    [InlineData("Number", typeof(double))]
    [InlineData("String", typeof(string))]
    [InlineData("DateTime", typeof(DateTimeOffset))]
    [InlineData("Json", typeof(JsonElement))]
    [InlineData("Collection<Int>", typeof(IReadOnlyList<long>))]
    [InlineData("Collection<Collection<String>>", typeof(IReadOnlyList<IReadOnlyList<string>>))]
    [InlineData("Collection<Int?>", typeof(IReadOnlyList<long?>))]
    [InlineData("Collection<Int>?", typeof(IReadOnlyList<long>))]
    [InlineData("Int?", typeof(long?))]
    [InlineData("String?", typeof(string))]
    [InlineData("Json?", typeof(JsonElement?))]
    [InlineData("DateTime?", typeof(DateTimeOffset?))]
    [InlineData("Collection<DateTime?>", typeof(IReadOnlyList<DateTimeOffset?>))]
    [InlineData("int", typeof(long))]
    [InlineData("Shipment", typeof(string))]
    [InlineData("Collection<ReeferShipment>", typeof(IReadOnlyList<string>))]
    [InlineData("Gauge<Boolean>", typeof(Uri))]
    [InlineData("Collection<Gauge<Number>?>", typeof(IReadOnlyList<Uri>))]
    public void Each_closed_term_has_the_CLR_type_the_term_table_gives(string term, Type expected) =>
        FreightWithGaugeErasure(typeof(Uri)).Current.ClrTypeOf(Term(term)).Should().Be(expected);

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Collection<Unknown>")]
    [InlineData("Unknown<Int>")]
    public void A_term_naming_nothing_registered_has_no_CLR_type(string term) =>
        Freight().Current.ClrTypeOf(Term(term)).Should().BeNull();

    [Fact]
    public void The_null_literals_term_and_a_term_holding_a_parameter_have_no_CLR_type()
    {
        var vocabulary = Freight().Current;

        vocabulary.ClrTypeOf(PortTerm.Null).Should().BeNull();
        vocabulary.ClrTypeOf(PortTerm.Parse("Collection<$T>", PortTermGrammar.Declaration)).Should().BeNull();
    }

    [Fact]
    public void A_host_atom_is_published_with_its_owner_aliases_and_rows_and_a_constructor_with_its_arguments()
    {
        var registry = new WorkflowTypeRegistry(
        [
            Atom("Depot") with { Aliases = ["Yard", "store"] },
            Atom("BondedDepot") with { WidensTo = ["Depot"] },
            Atom("Handle") with { WidensTo = ["Gauge<Boolean>"] },
            new WorkflowTypeConstructorRecord("Gauge", typeof(string)) { AllowedArguments = ["Boolean"] },
        ]);

        var types = registry.Current.Types.ToDictionary(t => t.Name);

        (types["Depot"].Owner, types["Depot"].ClrType, types["Depot"].WidensTo).Should().Be(("host", typeof(string), types["Depot"].WidensTo));
        types["Depot"].WidensTo.Should().BeEmpty();
        types["Depot"].Aliases.Should().Equal("store", "Yard");
        types["BondedDepot"].WidensTo.Should().Equal("Depot");
        types["Handle"].WidensTo.Should().Equal("Gauge<Boolean>");
        registry.Current.Constructors.Select(c => (c.Name, c.Owner, c.Variance)).Should().Equal(
            ("Collection", "library", WorkflowTypeVariance.Covariant), ("Gauge", "host", WorkflowTypeVariance.Invariant),
            ("Map", "library", WorkflowTypeVariance.Covariant));
        registry.Current.Constructors.Single(c => c.Name == "Gauge").AllowedArguments.Should().Equal("Boolean");
    }

    [Fact]
    public void A_host_registration_that_is_refused_stops_construction_naming_each_record()
    {
        var building = () => new WorkflowTypeRegistry([Atom("Depot"), Atom("depot"), Atom("Any")]);

        var refusals = building.Should().Throw<WorkflowTypeRegistrationException>().Which.Refusals;

        refusals.Select(r => r.Record).Should().Contain("Any");
        refusals.Single(r => r.Record == "Any").Code.Should().Be("port-type-registration-invalid");
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(ValueType))]
    public void Startup_refuses_an_atom_whose_CLR_type_admits_every_value(Type clrType)
    {
        var building = () => new WorkflowTypeRegistry([Atom("Wildcard", clrType)]);

        var refusal = building.Should().Throw<WorkflowTypeRegistrationException>().Which.Refusals.Should().ContainSingle().Which;

        refusal.Code.Should().Be("port-type-registration-invalid");
        refusal.Record.Should().Be("Wildcard");
        refusal.Message.Should().Contain("admits every value");
    }

    [Fact]
    public void Startup_refuses_a_constructor_with_object_erasure()
    {
        var building = () => new WorkflowTypeRegistry([new WorkflowTypeConstructorRecord("Wildcard", typeof(object)) { AllowedArguments = ["Int"] }]);

        var refusal = building.Should().Throw<WorkflowTypeRegistrationException>().Which.Refusals.Should().ContainSingle().Which;

        refusal.Code.Should().Be("port-type-registration-invalid");
        refusal.Record.Should().Be("Wildcard");
        refusal.Message.Should().Contain("admits every value");
    }

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(ValueType))]
    public void Runtime_registration_refuses_an_atom_whose_CLR_type_admits_every_value(Type clrType)
    {
        var registry = new WorkflowTypeRegistry();
        var before = registry.Current;

        var result = registry.Register("plugin:wildcard", [Atom("AcceptedBeforeRefusal"), Atom("Wildcard", clrType)]);

        Refused(result).Should().Equal("port-type-registration-invalid:Wildcard");
        result.Refusals.Single().Message.Should().Contain("admits every value");
        registry.Generation.Should().Be(0);
        registry.Current.Should().BeSameAs(before);
    }

    [Fact]
    public void Runtime_registration_refuses_a_constructor_with_object_erasure_atomically()
    {
        var registry = new WorkflowTypeRegistry();
        var before = registry.Current;

        var result = registry.Register("plugin:wildcard", [Atom("AcceptedBeforeRefusal"), new WorkflowTypeConstructorRecord("Wildcard", typeof(object)) { AllowedArguments = ["Int"] }]);

        Refused(result).Should().Equal("port-type-registration-invalid:Wildcard");
        result.Refusals.Single().Message.Should().Contain("admits every value");
        registry.Generation.Should().Be(0);
        registry.Current.Should().BeSameAs(before);
    }

    [Theory]
    [InlineData("Any", "reserved")]
    [InlineData("float", "reserved")]
    [InlineData("Expression", "library's")]
    [InlineData("collection_of", "reserved")]
    [InlineData("Device<T>", "not a name")]
    [InlineData("", "not a name")]
    public void An_atom_record_with_a_name_no_one_may_take_is_invalid(string name, string why) =>
        Atom(name).Validate().Should().ContainSingle(r => r.Code == "port-type-registration-invalid" && r.Message.Contains(why, StringComparison.Ordinal));

    [Fact]
    public void An_atom_record_without_a_CLR_type_or_with_a_bad_alias_or_row_is_invalid()
    {
        new WorkflowTypeRecord("Depot", null!).Validate().Should().ContainSingle(r => r.Message.Contains("no CLR type", StringComparison.Ordinal));
        (Atom("Depot") with { Aliases = ["Any"] }).Validate().Should().ContainSingle(r => r.Message.Contains("alias 'Any'", StringComparison.Ordinal));
        (Atom("Depot") with { Aliases = ["depot"] }).Validate().Should().ContainSingle(r => r.Message.Contains("the name itself", StringComparison.Ordinal));
        (Atom("Depot") with { Aliases = ["a<b"] }).Validate().Should().ContainSingle(r => r.Message.Contains("not a plain name", StringComparison.Ordinal));
        (Atom("Depot") with { WidensTo = ["Int?"] }).Validate().Should().ContainSingle(r => r.Message.Contains("nullable", StringComparison.Ordinal));
        (Atom("Depot") with { WidensTo = ["Json"] }).Validate().Should().ContainSingle(r => r.Message.Contains("Json", StringComparison.Ordinal));
        (Atom("Depot") with { WidensTo = ["Collection<Json>"] }).Validate().Should().ContainSingle(r => r.Message.Contains("Json", StringComparison.Ordinal));
        (Atom("Depot") with { WidensTo = ["Float"] }).Validate().Should().ContainSingle(r => r.Message.Contains("reserved", StringComparison.Ordinal));
        (Atom("Depot") with { WidensTo = ["a b"] }).Validate().Should().ContainSingle(r => r.Message.Contains("not a closed term", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Int", "port-type-name-clash")]
    [InlineData("string", "port-type-name-clash")]
    [InlineData("Shipment", "port-type-name-clash")]
    [InlineData("gauge", "port-type-name-clash")]
    public void A_plugin_atom_taking_a_registered_name_is_refused_with_a_clash_and_nothing_applies(string name, string code)
    {
        var registry = Freight();
        var before = registry.Current;

        var result = registry.Register("plugin:bt", [Atom("Beacon"), Atom(name)]);

        Refused(result).Should().Equal($"{code}:{name}");
        (registry.Generation, registry.Current).Should().Be((0L, before));
        registry.Current.Types.Should().NotContain(t => t.Name == "Beacon", "one refused record refuses the call");
    }

    [Fact]
    public void A_plugin_alias_taking_a_registered_name_or_alias_is_a_clash()
    {
        var registry = Freight();

        Refused(registry.Register("plugin:a", [Atom("Beacon") with { Aliases = ["bool"] }])).Should().Equal("port-type-name-clash:Beacon");
        Refused(registry.Register("plugin:a", [Atom("Beacon") with { Aliases = ["TankerShipment"] }])).Should().Equal("port-type-name-clash:Beacon");
        Refused(registry.Register("plugin:a", [Atom("Beacon") with { Aliases = ["Pager"] }, Atom("Pager")])).Should().Equal("port-type-name-clash:Pager");
        registry.Generation.Should().Be(0);
    }

    [Fact]
    public void Two_plugins_cannot_both_take_a_name_but_one_may_after_the_other_unloads()
    {
        var registry = Freight();

        registry.Register("plugin:a", [Atom("Beacon")]).Accepted.Should().BeTrue();
        Refused(registry.Register("plugin:b", [Atom("Beacon")])).Should().Equal("port-type-name-clash:Beacon");
        registry.Unregister("plugin:a").Should().BeTrue();
        registry.Register("plugin:b", [Atom("Beacon")]).Accepted.Should().BeTrue();

        registry.Generation.Should().Be(3);
        registry.Current.Types.Single(t => t.Name == "Beacon").Owner.Should().Be("plugin:b");
    }

    [Theory]
    [InlineData("host")]
    [InlineData("library")]
    [InlineData("plugin:")]
    [InlineData("")]
    public void An_owner_other_than_plugin_and_an_id_is_an_ArgumentException(string owner)
    {
        var registry = Freight();

        ((Action)(() => registry.Register(owner, [Atom("Beacon")]))).Should().Throw<ArgumentException>().WithParameterName("owner");
        ((Action)(() => registry.Unregister(owner))).Should().Throw<ArgumentException>().WithParameterName("owner");
        registry.Generation.Should().Be(0);
    }

    [Fact]
    public void Generation_moves_by_one_for_an_accepted_registration_and_for_an_unregistration_only()
    {
        var registry = Freight();

        registry.Register("plugin:a", [Atom("Beacon")]).Generation.Should().Be(1);
        registry.Register("plugin:b", [Atom("Beacon")]).Generation.Should().Be(1, "a refused call leaves it");
        registry.Unregister("plugin:nobody").Should().BeFalse();
        registry.Generation.Should().Be(1);
        registry.Unregister("plugin:a").Should().BeTrue();
        registry.Generation.Should().Be(2);
        registry.Current.Generation.Should().Be(2);
        registry.Current.Types.Should().NotContain(t => t.Name == "Beacon");
    }

    [Fact]
    public void A_state_a_compile_holds_does_not_change_when_the_registry_does()
    {
        var registry = Freight();
        var held = registry.Current;

        registry.Register("plugin:a", [Atom("Beacon")]);

        held.Types.Should().NotContain(t => t.Name == "Beacon");
        held.Generation.Should().Be(0);
        registry.Current.Types.Should().Contain(t => t.Name == "Beacon");
    }

    [Fact]
    public void A_row_may_lead_to_a_type_registered_in_the_same_call_in_either_order()
    {
        var registry = Freight();

        var result = registry.Register("plugin:a", [Atom("Pallet") with { WidensTo = ["Crate"] }, Atom("Crate")]);

        result.Accepted.Should().BeTrue();
        registry.Current.IsAssignable(Term("Pallet"), Term("Crate")).Should().BeTrue();
    }

    [Theory]
    [InlineData("Nowhere", "leads nowhere")]
    [InlineData("Number", "library type")]
    [InlineData("Json", "Json")]
    [InlineData("Beacon", "to the type itself")]
    [InlineData("Gauge<Int>", "leads nowhere")]
    [InlineData("Gauge<Boolean>", "second row into the constructor")]
    public void A_row_that_cannot_stand_refuses_its_record_with_registration_invalid(string target, string why)
    {
        var registry = Freight();
        var rows = target == "Gauge<Boolean>" ? new[] { "Gauge<Boolean>", "Gauge<Number>" } : [target];

        var result = registry.Register("plugin:a", [Atom("Beacon") with { WidensTo = rows }]);

        result.Refusals.Should().ContainSingle(r => r.Code == "port-type-registration-invalid" && r.Record == "Beacon"
            && r.Message.Contains(why, StringComparison.Ordinal));
        registry.Generation.Should().Be(0);
    }

    [Fact]
    public void A_row_to_a_term_the_CLR_type_cannot_reach_is_refused_and_one_it_can_is_accepted()
    {
        var registry = Freight();

        var refused = registry.Register("plugin:a", [Atom("Beacon", typeof(int)) with { WidensTo = ["Shipment"] }]);
        var accepted = registry.Register("plugin:a", [Atom("Beacon", typeof(string)) with { WidensTo = ["Shipment"] }]);

        refused.Refusals.Should().ContainSingle(r => r.Message.Contains("not reference-assignable", StringComparison.Ordinal));
        accepted.Accepted.Should().BeTrue();
    }

    [Fact]
    public void A_record_in_the_call_is_still_held_to_its_rows()
    {
        var registry = new WorkflowTypeRegistry();

        var result = registry.Register("plugin:b", [Atom("Pallet") with { WidensTo = ["Crate"] }]);

        Refused(result).Should().Equal("port-type-registration-invalid:Pallet");
    }

    [Fact]
    public void A_row_into_a_collection_term_is_refused_whether_one_or_two()
    {
        foreach (var rows in new[] { new[] { "Collection<String>" }, ["Collection<String>", "Collection<String?>"] })
        {
            var build = () => new WorkflowTypeRegistry([Atom("Batch", typeof(string[])) with { WidensTo = rows }]);

            build.Should().Throw<WorkflowTypeRegistrationException>().WithMessage("*leads into a collection*");
        }
    }

    [Fact]
    public void Two_rows_from_one_atom_into_one_constructor_head_are_refused()
    {
        var build = () => new WorkflowTypeRegistry(
        [
            Gauge("Boolean", "Int"),
            Atom("Handle", typeof(string)) with { WidensTo = ["Gauge<Boolean>", "Gauge<Int>"] },
        ]);

        build.Should().Throw<WorkflowTypeRegistrationException>().WithMessage("*is a second row into the constructor 'Gauge'*");
    }

    [Theory]
    [InlineData("shipment")]
    [InlineData("Cargo")]
    public void A_row_target_not_spelled_canonically_is_refused(string target)
    {
        var build = () => new WorkflowTypeRegistry(
        [
            Atom("Shipment") with { Aliases = ["Cargo"] },
            Atom("ReeferShipment") with { WidensTo = [target] },
        ]);

        build.Should().Throw<WorkflowTypeRegistrationException>().WithMessage("*is not spelled canonically*");
    }

    [Fact]
    public void A_row_into_a_constructor_term_spelled_with_a_case_variant_is_refused()
    {
        var build = () => new WorkflowTypeRegistry(
            [Gauge("Boolean"), Atom("Handle") with { WidensTo = ["gauge<Boolean>"] }]);

        build.Should().Throw<WorkflowTypeRegistrationException>().WithMessage("*is not spelled canonically*");
    }

    [Fact]
    public void A_constructor_in_the_call_naming_an_alias_of_a_registered_type_is_still_refused()
    {
        var registry = new WorkflowTypeRegistry();
        registry.Register("plugin:a", [Atom("Box") with { Aliases = ["Crate"] }]);

        var result = registry.Register("plugin:b", [Gauge("Crate")]);

        Refused(result).Should().Equal("port-type-registration-invalid:Gauge");
    }

    [Fact]
    public void A_registry_is_safe_to_register_into_from_many_threads_and_ends_with_every_accepted_call_applied()
    {
        var registry = Freight();

        Parallel.For(0, 40, i => registry.Register($"plugin:p{i}", [Atom($"T{i}")]).Accepted.Should().BeTrue());

        registry.Generation.Should().Be(40);
        registry.Current.Types.Count(t => t.Name.StartsWith('T') && t.Name.Length <= 3 && char.IsDigit(t.Name[1])).Should().Be(40);
    }

    [Fact]
    public void Each_record_field_moves_the_vocabulary_fingerprint()
    {
        static string Print(params WorkflowTypeRegistrationRecord[] records) => new WorkflowTypeRegistry(records).Current.Fingerprint();
        var baseline = Print(Atom("Depot"));

        Print(Atom("Depot")).Should().Be(baseline, "the same records print the same");
        Print(Atom("Depot2")).Should().NotBe(baseline, "Name");
        Print(Atom("Depot", typeof(Uri))).Should().NotBe(baseline, "ClrType");
        Print(Atom("Depot") with { Aliases = ["Yard"] }).Should().NotBe(baseline, "Aliases");
        Print(Atom("Depot"), Atom("Pallet")).Should().NotBe(Print(Atom("Depot"), Atom("Pallet") with { WidensTo = ["Depot"] }), "WidensTo, plain");
        Print(Atom("Handle") with { WidensTo = ["Gauge<Boolean>"] }, Gauge("Boolean", "Number"))
            .Should().NotBe(Print(Atom("Handle") with { WidensTo = ["Gauge<Number>"] }, Gauge("Boolean", "Number")), "WidensTo, into a constructor term");
        Print(Atom("Handle"), Gauge("Boolean")).Should().NotBe(Print(Atom("Handle"), Gauge("Boolean", "Number")), "AllowedArguments");
        Print(Atom("Handle"), Gauge("Boolean")).Should().NotBe(Print(Atom("Handle"), Gauge("Boolean") with { Erasure = typeof(Uri) }), "Erasure");
        Print(Atom("Depot") with { JsonTypeInfo = StrictJson.Info(typeof(string)) })
            .Should().Be(baseline, "no verdict reads a serializer");
    }

    [Fact]
    public void A_public_compile_reads_the_registry_once()
    {
        var counting = new CountingRegistry(new WorkflowTypeRegistry());
        var ports = JsonSerializer.Serialize(new[] { new { name = "Items", type = "Int" }, new { name = "More", type = "String" } });
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = ports }), Node("b", "act")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Items", "In"))]);
        var catalogue = Catalogue(
            Shape(ReeveWorkflow.Core.Model.WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.source", hasDynamicPorts: true),
            Shape("act", inputs: [("In", PortValueType.Number, true)]));

        WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, new CompilationOptions { TypeRegistry = counting });

        counting.Reads.Should().Be(1, "one compile judges every term against one state");
    }

    private sealed class CountingRegistry(IWorkflowTypeRegistry inner) : IWorkflowTypeRegistry
    {
        public int Reads;
        public WorkflowTypeRegistrationResult Register(string owner, IReadOnlyCollection<WorkflowTypeRegistrationRecord> records) => inner.Register(owner, records);
        public bool Unregister(string owner) => inner.Unregister(owner);
        public long Generation => inner.Generation;
        public WorkflowTypeVocabulary Current { get { Interlocked.Increment(ref Reads); return inner.Current; } }
    }

    private static WorkflowTypeConstructorRecord Gauge(params string[] arguments) =>
        new("Gauge", typeof(string)) { AllowedArguments = arguments };

    [Fact]
    public void A_registration_moves_the_fingerprint_the_dependency_hash_reads()
    {
        var registry = Freight();
        var options = new CompilationOptions { TypeRegistry = registry };
        string Hash() => DependencyFingerprint.Compute([], options, NodePortEnvironment.Empty);
        var before = Hash();

        registry.Register("plugin:a", [Atom("Beacon")]);
        var during = Hash();
        registry.Unregister("plugin:a");

        during.Should().NotBe(before);
        Hash().Should().Be(before, "the state without the plugin is the state before it");
    }

    public static TheoryData<string[], string?> LubRows => new()
    {
        { ["Int", "Number"], "Number" },
        { ["Int", "Int?"], "Int?" },
        { ["Int", "null"], "Int?" },
        { ["Int?", "Number"], "Number?" },
        { ["Collection<Int>", "Collection<Number>"], "Collection<Number>" },
        { ["Collection<Int?>", "Collection<Number>"], "Collection<Number?>" },
        { ["Json", "Json?"], "Json?" },
        { ["Json", "null"], "Json?" },
        { ["Int", "Json"], null },
        { ["Int", "String"], null },
        { ["Gauge<Boolean>", "Gauge<Number>"], null },
        { ["ReeferShipment", "TankerShipment"], "Shipment" },
        { ["null"], null },
        { [], null },
        { ["null", "null"], null },
        { ["Shipment", "Shipment"], "Shipment" },
        { ["Collection<String>", "Collection<String>?"], "Collection<String>?" },
        { ["Collection<Collection<Int>>", "Collection<Collection<Number>>"], "Collection<Collection<Number>>" },
        { ["Collection<Collection<Int>>?", "Collection<Collection<Number?>>"], "Collection<Collection<Number?>>?" },
        { ["Collection<Int>", "Collection<String>"], null },
        { ["Collection<Int>", "Int"], null },
        { ["Collection<Json>"], "Collection<Json>" },
        { ["ReeferShipment", "Shipment"], "Shipment" },
        { ["ReeferShipment", "Shipment?"], "Shipment?" },
        { ["ReeferShipment", "Int"], null },
        { ["ReeferShipment", "ReeferShipment"], "ReeferShipment" },
        { ["Int", "Number", "Int"], "Number" },
        { ["Int", "Number", "String"], null },
        { ["Collection<ReeferShipment>", "Collection<TankerShipment>"], "Collection<Shipment>" },
        { ["Gauge<Boolean>", "Gauge<Boolean>?"], "Gauge<Boolean>?" },
    };

    [Theory]
    [MemberData(nameof(LubRows))]
    public void The_least_upper_bound_answers_each_row_of_the_table(string[] evidence, string? expected)
    {
        var terms = evidence.Select(e => e == "null" ? PortTerm.Null : Term(e));

        var join = Freight().Current.LeastUpperBound(terms);

        join?.ToString().Should().Be(expected);
        (join is null).Should().Be(expected is null);
    }

    [Fact]
    public void A_join_needs_exactly_one_least_candidate_and_rows_do_not_compose()
    {
        var registry = new WorkflowTypeRegistry(
        [
            Atom("Tiny") with { WidensTo = ["Small"] },
            Atom("Small") with { WidensTo = ["Big"] },
            Atom("Big"),
            Atom("Other") with { WidensTo = ["Small", "Big"] },
            Atom("Left") with { WidensTo = ["Big"] },
            Atom("Right") with { WidensTo = ["Big", "Wide"] },
            Atom("Wide"),
        ]);
        var vocabulary = registry.Current;

        vocabulary.LeastUpperBound([Term("Tiny"), Term("Small")])!.ToString().Should().Be("Small");
        vocabulary.LeastUpperBound([Term("Tiny"), Term("Big")]).Should().BeNull("Tiny does not feed Big: rows do not compose");
        vocabulary.LeastUpperBound([Term("Other"), Term("Small")])!.ToString().Should().Be("Small");
        vocabulary.LeastUpperBound([Term("Left"), Term("Right")])!.ToString().Should().Be("Big");
        vocabulary.IsAssignable(Term("Tiny"), Term("Big")).Should().BeFalse();
    }

    [Fact]
    public void The_join_of_an_alias_spelling_is_the_canonical_term()
    {
        Freight().Current.LeastUpperBound([Term("int"), Term("NUMBER")])!.ToString().Should().Be("Number");
    }

    [Fact]
    public void A_100000_deep_join_is_answered_on_a_256KB_stack_with_no_recursion()
    {
        var vocabulary = Freight().Current;
        static PortTerm Deep(string element) => PortTerm.Parse(
            string.Concat(Enumerable.Repeat("Collection<", 100_000)) + element + string.Concat(Enumerable.Repeat(">", 100_000)),
            PortTermGrammar.Document);
        string? join = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { join = vocabulary.LeastUpperBound([Deep("Int"), Deep("Number?")])?.ToString().Length.ToString(); }
            catch (Exception e) { failure = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        failure.Should().BeNull();
        join.Should().NotBeNull();
    }

    [Fact]
    public void The_registry_answers_every_term_vector()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PortTypeAssignabilityPath()));
        var terms = file.RootElement.GetProperty("terms");
        var vocabulary = terms.GetProperty("vocabulary");
        var library = new HashSet<string>(["Boolean", "Int", "Number", "String", "DateTime", "Json"], StringComparer.OrdinalIgnoreCase);
        var rows = vocabulary.GetProperty("rows").EnumerateArray().Select(r => (From: r[0].GetString()!, To: r[1].GetString()!)).ToList();
        var records = new List<WorkflowTypeRegistrationRecord>();
        foreach (var atom in vocabulary.GetProperty("atoms").EnumerateObject().Where(a => !library.Contains(a.Name)))
            records.Add(Claiming(Atom(atom.Name) with { WidensTo = [.. rows.Where(r => r.From == atom.Name).Select(r => r.To)] }, atom.Value));
        foreach (var constructor in vocabulary.GetProperty("constructors").EnumerateObject())
            records.Add(Claiming(new WorkflowTypeConstructorRecord(constructor.Name, typeof(string)) { AllowedArguments = ["Boolean"] }, constructor.Value));
        var registry = new WorkflowTypeRegistry(records).Current;

        var wrong = new List<string>();
        var graded = 0;
        foreach (var vector in terms.GetProperty("vectors").EnumerateArray())
        {
            var from = vector.GetProperty("from").ValueKind == JsonValueKind.Null ? PortTerm.Null : Term(vector.GetProperty("from").GetString()!);
            var to = Term(vector.GetProperty("to").GetString()!);
            graded++;
            var expected = vector.GetProperty("assignable").GetBoolean();
            if (registry.IsAssignable(from, to) != expected)
                wrong.Add($"{from} -> {to}: expected {expected}");
        }

        wrong.Should().BeEmpty();
        graded.Should().BeGreaterThan(100);
    }

    private static WorkflowTypeRecord Claiming(WorkflowTypeRecord record, JsonElement entry) => entry.GetProperty("json").GetBoolean()
        ? record with { Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable }, JsonTypeInfo = StringInfo }
        : record;

    private static WorkflowTypeConstructorRecord Claiming(WorkflowTypeConstructorRecord record, JsonElement entry) => entry.GetProperty("json").GetBoolean()
        ? record with { Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable }, JsonTypeInfo = StringInfo }
        : record;

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo StringInfo =
        StrictJson.Info(typeof(string));

    [Fact]
    public void The_wire_check_decides_every_pair_by_the_rules_and_Any_is_no_wildcard()
    {
        var vocabulary = Freight().Current;
        var any = PortValueType.Of("Any");

        vocabulary.IsAssignable(any, PortValueType.Of("Shipment")).Should().BeFalse("A7: neither is a wildcard");
        vocabulary.IsAssignable(PortValueType.Of("Collection<Int>"), PortValueType.CollectionOf(any)).Should().BeFalse("A5, then A7");
        vocabulary.IsAssignable(PortValueType.Int, PortValueType.Of("Int?")).Should().BeTrue("A2");
        vocabulary.IsAssignable(PortValueType.Of("Int?"), PortValueType.Int).Should().BeFalse("A3");
        vocabulary.IsAssignable(PortValueType.Number, PortValueType.Int).Should().BeFalse("the library has one row, Int to Number");
        vocabulary.IsAssignable(PortValueType.Of("ReeferShipment"), PortValueType.Of("Shipment")).Should().BeTrue("A6");
        vocabulary.IsAssignable(PortValueType.Of("Shipment"), PortValueType.Of("ReeferShipment")).Should().BeFalse("A7");
        vocabulary.IsAssignable(PortValueType.Of("bool"), PortValueType.Of("Boolean?")).Should().BeTrue("an alias reads as the name it spells");
    }
}
