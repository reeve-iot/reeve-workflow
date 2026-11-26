using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class WideningTableTests
{
    private static readonly PortValueType NestedCollection =
        PortValueType.CollectionOf(PortValueType.CollectionOf(PortValueType.Int));
    private static readonly PortValueType DoublyNestedCollection = PortValueType.CollectionOf(NestedCollection);

    [Fact]
    public void The_builtin_Int_widens_to_Number_and_not_back()
    {
        var table = PortTypeWideningTable.Builtin;

        table.IsAssignable(PortValueType.Int, PortValueType.Number).Should().BeTrue();
        table.IsAssignable(PortValueType.Number, PortValueType.Int).Should().BeFalse("the library has one row, Int to Number");
        table.IsAssignable(PortValueType.Number, PortValueType.Of("Float")).Should().BeFalse();
        table.IsAssignable(PortValueType.Int, PortValueType.Of("float")).Should().BeFalse();
    }

    [Fact]
    public void Any_is_no_wildcard_and_is_assignable_in_neither_direction()
    {
        var table = PortTypeWideningTable.Builtin;

        table.IsAssignable(PortValueType.Of("Any"), PortValueType.Of("Shipment")).Should().BeFalse();
        table.IsAssignable(PortValueType.Of("Shipment"), PortValueType.Of("Any")).Should().BeFalse();
    }

    [Fact]
    public void A_host_widening_is_added_to_the_builtins_and_does_not_replace_them()
    {
        var table = PortTypeWideningTable.Builtin.With(PortValueType.Of("BondedDepot"), [PortValueType.Of("Depot")]);

        table.IsAssignable(PortValueType.Of("BondedDepot"), PortValueType.Of("Depot")).Should().BeTrue();
        table.IsAssignable(PortValueType.Int, PortValueType.Number).Should().BeTrue();
    }

    [Fact]
    public void Two_unrelated_named_types_do_not_widen()
    {
        PortTypeWideningTable.Builtin
            .IsAssignable(PortValueType.Of("Depot"), PortValueType.Of("Carrier")).Should().BeFalse();
    }

    [Fact]
    public void A_collection_is_assignable_when_its_element_is()
    {
        PortTypeWideningTable.Builtin.IsAssignable(
            PortValueType.CollectionOf(PortValueType.Int),
            PortValueType.CollectionOf(PortValueType.Number)).Should().BeTrue();
    }

    [Fact]
    public void A_collection_of_Any_is_assignable_neither_to_nor_from_a_typed_one()
    {
        var table = PortTypeWideningTable.Builtin;

        table.IsAssignable(PortValueType.Of("Collection<Any>"), PortValueType.CollectionOf(PortValueType.Number)).Should().BeFalse();
        table.IsAssignable(PortValueType.CollectionOf(PortValueType.Number), PortValueType.Of("Collection<Any>")).Should().BeFalse();
    }

    [Fact]
    public void A_collection_and_a_scalar_do_not_widen_to_each_other()
    {
        var table = PortTypeWideningTable.Builtin;

        table.IsAssignable(PortValueType.CollectionOf(PortValueType.Int), PortValueType.String).Should().BeFalse();
        table.IsAssignable(PortValueType.String, PortValueType.CollectionOf(PortValueType.Int)).Should().BeFalse();
    }

    [Fact]
    public void The_bool_alias_canonicalises_to_Boolean_so_the_two_spellings_are_the_same_port_type()
    {
        var table = PortTypeWideningTable.Builtin;

        table.Canonicalize(PortValueType.Of("bool")).Should().Be(PortValueType.Boolean);
        table.IsAssignable(PortValueType.Of("bool"), PortValueType.Boolean).Should().BeTrue();
        table.IsAssignable(PortValueType.Boolean, PortValueType.Of("bool")).Should().BeTrue();
    }

    [Fact]
    public void An_alias_inside_a_collections_element_type_is_canonicalised_too()
    {
        var table = PortTypeWideningTable.Builtin;

        table.Canonicalize(PortValueType.CollectionOf(PortValueType.Of("bool")))
            .Should().Be(PortValueType.CollectionOf(PortValueType.Boolean));
        table.IsAssignable(
            PortValueType.CollectionOf(PortValueType.Of("bool")),
            PortValueType.CollectionOf(PortValueType.Boolean)).Should().BeTrue();
    }

    [Fact]
    public void A_host_registered_alias_canonicalises_the_same_way_a_builtin_one_does()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("depot"), PortValueType.Of("Depot"));

        table.Canonicalize(PortValueType.Of("depot")).Should().Be(PortValueType.Of("Depot"));
        table.IsAssignable(PortValueType.Of("depot"), PortValueType.Of("Depot")).Should().BeTrue();
    }

    [Fact]
    public void An_unknown_name_canonicalises_to_itself()
    {
        PortTypeWideningTable.Builtin.Canonicalize(PortValueType.Of("Depot")).Should().Be(PortValueType.Of("Depot"));
    }

    [Fact]
    public void Canonicalising_a_nested_collection_keeps_it_distinguishable_from_a_bare_one()
    {
        var table = PortTypeWideningTable.Builtin;

        table.Canonicalize(NestedCollection).Should().NotBe(PortValueType.CollectionOf(PortValueType.Int));
        table.Canonicalize(NestedCollection).Should().Be(NestedCollection);
    }

    [Fact]
    public void A_list_of_lists_does_not_widen_into_a_list_of_ints_either_way()
    {
        var table = PortTypeWideningTable.Builtin;

        table.IsAssignable(NestedCollection, PortValueType.CollectionOf(PortValueType.Int)).Should().BeFalse();
        table.IsAssignable(PortValueType.CollectionOf(PortValueType.Int), NestedCollection).Should().BeFalse();
    }

    [Fact]
    public void Assignability_across_a_matrix_of_nesting_depths_and_elements_matches_the_expected_answers()
    {
        var table = PortTypeWideningTable.Builtin;
        var collectionInt = PortValueType.CollectionOf(PortValueType.Int);
        var collectionNumber = PortValueType.CollectionOf(PortValueType.Number);
        var nestedInt = PortValueType.CollectionOf(PortValueType.CollectionOf(PortValueType.Int));
        var nestedNumber = PortValueType.CollectionOf(PortValueType.CollectionOf(PortValueType.Number));

        var cases = new (PortValueType From, PortValueType To, bool Expected)[]
        {
            (PortValueType.Int, PortValueType.Number, true),
            (collectionInt, collectionNumber, true),
            (PortValueType.Of("Collection<Any>"), collectionInt, false),
            (collectionInt, PortValueType.Of("Collection<Any>"), false),
            (NestedCollection, collectionInt, false),
            (collectionInt, NestedCollection, false),
            (DoublyNestedCollection, collectionInt, false),
            (nestedInt, collectionInt, false),
            (NestedCollection, NestedCollection, true),
            (nestedInt, nestedNumber, true),
            (PortValueType.String, collectionInt, false),
        };

        foreach (var (from, to, expected) in cases)
            table.IsAssignable(from, to).Should().Be(expected, because: $"{from.Name} -> {to.Name}");
    }

    [Fact]
    public void With_refuses_every_widening_whose_source_or_a_target_is_a_collection_at_any_depth()
    {
        string[] atoms = ["Origin", "Int", "Carrier"];
        var checkedPairs = 0;
        foreach (var fromAtom in atoms)
            foreach (var toAtom in atoms)
                for (var fromDepth = 0; fromDepth <= 2; fromDepth++)
                    for (var toDepth = 0; toDepth <= 2; toDepth++)
                    {
                        var from = Nest(PortValueType.Of(fromAtom), fromDepth);
                        var to = Nest(PortValueType.Of(toAtom == fromAtom ? "Waypoint" : toAtom), toDepth);
                        var with = () => PortTypeWideningTable.Builtin.With(from, [to]);
                        if (fromDepth > 0)
                            with.Should().Throw<ArgumentException>()
                                .WithMessage($"Port type '{from.Name}' cannot widen: a widening is stated between plain names, and a collection widens through its element.*")
                                .Which.ParamName.Should().Be("from");
                        else if (toDepth > 0)
                            with.Should().Throw<ArgumentException>()
                                .WithMessage($"Port type '{from.Name}' cannot widen to '{to.Name}': a widening is stated between plain names, and a collection widens through its element.*")
                                .Which.ParamName.Should().Be("targets");
                        else
                            with.Should().NotThrow($"{from.Name} -> {to.Name} is between plain names");
                        checkedPairs++;
                    }

        checkedPairs.Should().Be(81);
    }

    [Fact]
    public void With_refuses_a_collection_among_plain_targets_and_adds_none_of_them()
    {
        var with = () => PortTypeWideningTable.Builtin.With(
            PortValueType.Of("Origin"), [PortValueType.Of("Waypoint"), PortValueType.CollectionOf(PortValueType.Of("Waypoint"))]);

        with.Should().Throw<ArgumentException>().WithMessage("*'Origin' cannot widen to 'Collection<Waypoint>'*")
            .Which.ParamName.Should().Be("targets");
        PortTypeWideningTable.Builtin.IsAssignable(PortValueType.Of("Origin"), PortValueType.Of("Waypoint")).Should().BeFalse();
    }

    [Fact]
    public void A_collection_widens_exactly_when_its_element_does_in_every_generated_table()
    {
        var random = new Random(20260926);
        string[] atoms = ["Origin", "Waypoint", "Depot", "Int", "Number", "String"];
        PortValueType Atom(string a) => PortValueType.Of(a);
        var answers = new int[2];
        for (var i = 0; i < 200; i++)
        {
            var table = PortTypeWideningTable.Builtin;
            for (var k = random.Next(1, 6); k > 0; k--)
            {
                try
                {
                    table = table.With(Atom(atoms[random.Next(atoms.Length)]), [Atom(atoms[random.Next(atoms.Length)])]);
                }
                catch (ArgumentException)
                {
                }
            }

            foreach (var a in atoms)
                foreach (var b in atoms)
                {
                    var element = table.IsAssignable(Atom(a), Atom(b));
                    for (var depth = 1; depth <= 3; depth++)
                        table.IsAssignable(Nest(Atom(a), depth), Nest(Atom(b), depth))
                            .Should().Be(element, because: $"table {i}: {a} -> {b} at depth {depth}");
                    answers[element ? 1 : 0]++;
                }
        }

        answers.Should().OnlyContain(count => count > 200, "both answers are graded");
    }

    private static PortValueType Nest(PortValueType element, int depth) =>
        depth == 0 ? element : PortValueType.CollectionOf(Nest(element, depth - 1));

    [Fact]
    public void A_host_widening_on_an_existing_key_is_added_to_it_rather_than_replacing_it()
    {
        var table = PortTypeWideningTable.Builtin.With(PortValueType.Int, [PortValueType.Of("Depot")]);

        table.IsAssignable(PortValueType.Int, PortValueType.Of("Depot")).Should().BeTrue();
        table.IsAssignable(PortValueType.Int, PortValueType.Number).Should().BeTrue();
        table.IsAssignable(PortValueType.Number, PortValueType.Int).Should().BeFalse();
    }

    [Fact]
    public void Float_is_in_no_widening_row_in_either_direction()
    {
        var table = PortTypeWideningTable.Builtin;

        table.IsAssignable(PortValueType.Of("Float"), PortValueType.Number).Should().BeFalse();
        table.IsAssignable(PortValueType.Of("Float"), PortValueType.Int).Should().BeFalse();
        table.IsAssignable(PortValueType.Of("float"), PortValueType.Of("Collection<Number>")).Should().BeFalse();
        table.WideningsOf(PortValueType.Of("Float")).Should().BeEmpty();
        table.Canonicalize(PortValueType.Of("float")).Name.Should().Be("float");
        table.IsRegistered(PortValueType.Of("Float")).Should().BeFalse();
        table.IsRegistered(PortValueType.Of("Collection<float>")).Should().BeFalse();
    }

    [Fact]
    public void Any_is_no_member_of_the_vocabulary()
    {
        var table = PortTypeWideningTable.Builtin;

        table.IsRegistered(PortValueType.Of("Any")).Should().BeFalse();
        table.IsRegistered(PortValueType.Of("Collection<Any>")).Should().BeFalse();
        table.IsRegistered(PortValueType.Of("any")).Should().BeFalse();
        table.KnownTypes.Select(t => t.Name).Should().NotContain(["Any", "Collection<Any>", "Float"]);
        table.AliasesOf(PortValueType.Of("Any")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Float")]
    [InlineData("FLOAT")]
    [InlineData("Any")]
    [InlineData("any")]
    [InlineData("Any<Int>")]
    [InlineData("Gauge<Float>")]
    public void A_reserved_name_is_refused_by_every_registration_with_port_type_alias(string name)
    {
        var reserved = PortValueType.Of(name);

        var declare = () => PortTypeWideningTable.Builtin.Declaring(reserved);
        var from = () => PortTypeWideningTable.Builtin.With(reserved, [PortValueType.Number]);
        var onto = () => PortTypeWideningTable.Builtin.With(PortValueType.Int, [reserved]);
        var alias = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Quantity"), reserved);

        declare.Should().Throw<ArgumentException>().WithMessage("port-type-alias: *is a reserved name*").Which.ParamName.Should().Be("type");
        from.Should().Throw<ArgumentException>().WithMessage("port-type-alias: *is a reserved name*").Which.ParamName.Should().Be("from");
        onto.Should().Throw<ArgumentException>().WithMessage("port-type-alias: *is a reserved name*").Which.ParamName.Should().Be("targets");
        alias.Should().Throw<ArgumentException>().WithMessage("port-type-alias: *is a reserved name*").Which.ParamName.Should().Be("canonical");
    }

    [Fact]
    public void With_rejects_a_null_target_set_the_way_its_neighbours_guard_their_arguments()
    {
        var act = () => PortTypeWideningTable.Builtin.With(PortValueType.Number, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("targets");
    }

    [Fact]
    public void Default_is_refused_in_every_registration_position_naming_that_parameter()
    {
        var table = PortTypeWideningTable.Builtin;
        var none = default(PortValueType);

        table.Invoking(t => t.Declaring(none)).Should().Throw<ArgumentException>()
            .WithMessage("*default(PortValueType)*").WithParameterName("type");
        table.Invoking(t => t.With(none, [PortValueType.Int])).Should().Throw<ArgumentException>()
            .WithMessage("*default(PortValueType)*").WithParameterName("from");
        table.Invoking(t => t.With(PortValueType.Int, [PortValueType.Number, none])).Should().Throw<ArgumentException>()
            .WithMessage("*default(PortValueType)*").WithParameterName("targets");
        table.Invoking(t => t.WithAlias(none, PortValueType.Int)).Should().Throw<ArgumentException>()
            .WithMessage("*default(PortValueType)*").WithParameterName("alias");
        table.Invoking(t => t.WithAlias(PortValueType.Of("wid"), none)).Should().Throw<ArgumentException>()
            .WithMessage("*default(PortValueType)*").WithParameterName("canonical");
    }

    [Fact]
    public void The_readers_answer_default_the_way_their_documentation_says()
    {
        var table = PortTypeWideningTable.Builtin;
        var none = default(PortValueType);

        table.Invoking(t => t.Canonicalize(none)).Should().Throw<InvalidOperationException>();
        table.Invoking(t => t.IsAssignable(none, PortValueType.Int)).Should().Throw<InvalidOperationException>();
        table.Invoking(t => t.IsAssignable(PortValueType.Int, none)).Should().Throw<InvalidOperationException>();
        table.Invoking(t => t.WideningsOf(none)).Should().Throw<ArgumentException>().WithParameterName("from");
        table.Invoking(t => t.AliasesOf(none)).Should().Throw<ArgumentException>().WithParameterName("canonical");
    }

    [Fact]
    public void The_bare_word_collection_no_longer_names_any_type_the_library_seeds_since_the_prefix_is_reserved_notation()
    {
        var of = () => PortValueType.Of("collection");

        of.Should().Throw<PortTypeSyntaxException>()
            .Which.Error.Fault.Should().Be(PortTypeSyntaxFault.ReservedCollectionPrefix);
    }

    [Fact]
    public void KnownTypes_spells_every_builtin_canonically_and_never_by_one_of_its_aliases()
    {
        PortTypeWideningTable.Builtin.KnownTypes.Select(t => t.Name).Should().Equal(
            "Boolean", "DateTime", "Int", "Json", "Number", "String");
    }

    [Fact]
    public void KnownTypes_publishes_one_spelling_chosen_by_the_names_when_a_vocabulary_carries_two()
    {
        var table = PortTypeWideningTable.Builtin
            .With(PortValueType.Of("SrcA"), [PortValueType.Of("Unit")])
            .With(PortValueType.Of("SrcB"), [PortValueType.Of("UNIT")]);

        table.KnownTypes.Select(t => t.Name)
            .Where(n => n.Equals("Unit", StringComparison.OrdinalIgnoreCase))
            .Should().Equal("UNIT");
    }

    [Fact]
    public void KnownTypes_representative_is_the_ordinally_smallest_spelling_regardless_of_insertion_order()
    {
        string[] spellings = ["abc", "abC", "aBc", "aBC", "Abc", "ABC"];
        var sources = new[] { "Src1", "Src2", "Src3", "Src4", "Src5", "Src6" };

        foreach (var order in new[] { spellings, [.. spellings.Reverse()], [.. spellings.OrderBy(s => s, StringComparer.Ordinal)] })
        {
            var table = sources.Zip(order).Aggregate(PortTypeWideningTable.Builtin,
                (acc, pair) => acc.With(PortValueType.Of(pair.First), [PortValueType.Of(pair.Second)]));

            table.KnownTypes.Select(t => t.Name).Where(n => n.Equals("abc", StringComparison.OrdinalIgnoreCase))
                .Should().Equal("ABC");
        }
    }

    [Fact]
    public void A_case_variant_of_an_atom_declared_through_a_collection_alias_target_is_spelled_as_declared()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Bag"), PortValueType.Of("Collection<Foo>"));

        new[] { "Collection<foo>", "foo", "Bag" }.Select(n => table.Canonicalize(PortValueType.Of(n)).Name)
            .Should().Equal("Collection<Foo>", "Foo", "Collection<Foo>");
    }

    [Fact]
    public void A_widening_sources_own_name_is_published_even_though_it_has_no_alias()
    {
        var table = PortTypeWideningTable.Builtin.With(PortValueType.Of("BondedDepot"), [PortValueType.Of("Depot")]);

        table.KnownTypes.Should().Contain(PortValueType.Of("BondedDepot"));
    }

    [Fact]
    public void A_widening_targets_own_name_is_published_even_though_it_is_never_a_source()
    {
        var table = PortTypeWideningTable.Builtin.With(PortValueType.Of("Celsius"), [PortValueType.Of("Kelvin")]);

        table.KnownTypes.Should().Contain(PortValueType.Of("Kelvin"));
    }

    [Fact]
    public void An_aliased_canonical_spelling_wins_over_a_differently_cased_widening_key_for_the_same_type()
    {
        var table = PortTypeWideningTable.Builtin
            .With(PortValueType.Of("depot"), [PortValueType.Of("Carrier")])
            .WithAlias(PortValueType.Of("dep"), PortValueType.Of("Depot"));

        var names = table.KnownTypes.Select(t => t.Name).ToList();
        names.Should().Contain("Depot");
        names.Should().NotContain("depot");
    }

    [Fact]
    public void WideningsOf_and_AliasesOf_read_back_what_With_and_WithAlias_stored()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("depot"), PortValueType.Of("Depot"));

        table.WideningsOf(PortValueType.Int).Should().Equal([PortValueType.Number]);
        table.WideningsOf(PortValueType.Number).Should().BeEmpty();
        table.AliasesOf(PortValueType.Boolean).Should().Contain(PortValueType.Of("bool"));
        table.AliasesOf(PortValueType.Of("Depot")).Should().Equal([PortValueType.Of("depot")]);
    }

    [Fact]
    public void AliasesOf_returns_every_alias_for_a_type_in_a_stable_alphabetical_order()
    {
        PortTypeWideningTable.Builtin.AliasesOf(PortValueType.Boolean).Should().Equal(
            PortValueType.Of("bool"), PortValueType.Of("boolean"));
    }

    [Fact]
    public void The_reserved_wildcard_has_no_aliases()
    {
        PortTypeWideningTable.Builtin.AliasesOf(PortValueType.Of("Any")).Should().BeEmpty();
    }

    [Fact]
    public void An_alias_key_spelling_the_wildcard_is_refused_as_a_reserved_name()
    {
        var alias = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("ANY"), PortValueType.String);

        alias.Should().Throw<ArgumentException>().WithMessage("port-type-alias: Port type alias 'ANY' cannot be registered: Any is a reserved name*").Which.ParamName.Should().Be("alias");
    }

    [Fact]
    public void AliasesOf_returns_six_aliases_in_a_stable_alphabetical_order_however_they_were_registered()
    {
        var target = PortValueType.Of("Word");
        var table = PortTypeWideningTable.Builtin
            .WithAlias(PortValueType.Of("zulu"), target)
            .WithAlias(PortValueType.Of("mno"), target)
            .WithAlias(PortValueType.Of("bravo"), target)
            .WithAlias(PortValueType.Of("delta"), target)
            .WithAlias(PortValueType.Of("alpha"), target)
            .WithAlias(PortValueType.Of("echo"), target);

        table.AliasesOf(target).Should().Equal(
            PortValueType.Of("alpha"), PortValueType.Of("bravo"), PortValueType.Of("delta"),
            PortValueType.Of("echo"), PortValueType.Of("mno"), PortValueType.Of("zulu"));
    }

    [Fact]
    public void A_collections_element_widening_stays_one_way_the_same_as_the_scalar_it_wraps()
    {
        var table = PortTypeWideningTable.Builtin.With(PortValueType.Of("BondedDepot"), [PortValueType.Of("Depot")]);

        table.IsAssignable(
            PortValueType.CollectionOf(PortValueType.Of("BondedDepot")),
            PortValueType.CollectionOf(PortValueType.Of("Depot"))).Should().BeTrue();
        table.IsAssignable(
            PortValueType.CollectionOf(PortValueType.Of("Depot")),
            PortValueType.CollectionOf(PortValueType.Of("BondedDepot"))).Should().BeFalse();
    }

    [Fact]
    public void Declaring_refuses_a_collection_shaped_name_at_every_depth_naming_the_type_parameter()
    {
        string[] names = ["Collection<Point>", "Collection<point>", "Collection<Any>", "Collection<Collection<Any>>",
            "Collection<Float>", "Collection<bool>", "Collection<Collection<Collection<Widget>>>"];
        foreach (var name in names)
        {
            var declare = () => PortTypeWideningTable.Builtin.Declaring(PortValueType.Of(name));

            declare.Should().Throw<ArgumentException>()
                .WithMessage($"Port type '{name}' cannot be declared: only a plain name can be declared, and a collection of it is read through its element.*")
                .Which.ParamName.Should().Be("type");
        }
    }

    [Fact]
    public void A_host_type_that_collides_with_an_alias_targets_collection_is_refused()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("pts"), PortValueType.Of("Collection<Point>"));

        var collide = () => table.Declaring(PortValueType.Of("Point"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Point*Collection<Point>*");
    }

    [Fact]
    public void A_collision_between_two_casings_of_one_atom_is_refused()
    {
        var collide = () => PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("point"))
            .WithAlias(PortValueType.Of("pts"), PortValueType.Of("Collection<Point>"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Collection<Point>*point*");
    }

    [Fact]
    public void An_alias_onto_a_nested_collection_of_Any_is_refused_as_a_reserved_name()
    {
        var alias = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Nest"), PortValueType.Of("Collection<Collection<Any>>"));

        alias.Should().Throw<ArgumentException>().WithMessage("port-type-alias: *Collection<Collection<Any>>*is a reserved name*").WithParameterName("canonical");
    }

    [Fact]
    public void A_nested_collection_collides_with_a_collection_of_its_declared_element_too()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Widget"));

        var collide = () => table.WithAlias(PortValueType.Of("grid"), PortValueType.Of("Collection<Collection<Widget>>"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Collection<Collection<Widget>>*Widget*");
    }

    [Fact]
    public void The_nested_collision_is_refused_whichever_order_the_two_names_arrive_in()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("grid"), PortValueType.Of("Collection<Collection<Widget>>"));

        var collide = () => table.Declaring(PortValueType.Of("Widget"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Widget*Collection<Collection<Widget>>*");
    }

    [Fact]
    public void Declaring_a_type_a_second_time_is_a_harmless_no_op()
    {
        var declare = () => PortTypeWideningTable.Builtin
            .Declaring(PortValueType.Of("Point"))
            .Declaring(PortValueType.Of("Point"));

        declare.Should().NotThrow();
    }

    [Fact]
    public void With_declares_both_its_source_and_its_targets_so_a_later_collision_against_either_is_caught()
    {
        var table = PortTypeWideningTable.Builtin.With(PortValueType.Of("Origin"), [PortValueType.Of("Waypoint")]);

        var collideOnSource = () => table.WithAlias(PortValueType.Of("origins"), PortValueType.Of("Collection<Origin>"));
        var collideOnTarget = () => table.WithAlias(PortValueType.Of("route"), PortValueType.Of("Collection<Waypoint>"));

        collideOnSource.Should().Throw<ArgumentException>().WithMessage("*Collection<Origin>*Origin*");
        collideOnTarget.Should().Throw<ArgumentException>().WithMessage("*Collection<Waypoint>*Waypoint*");
    }

    [Fact]
    public void A_collection_of_a_builtin_scalar_collides_with_the_builtin_itself()
    {
        var collide = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("ints"), PortValueType.Of("Collection<Int>"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Collection<Int>*Int*");
    }

    [Fact]
    public void A_type_declared_with_no_widening_of_its_own_still_reaches_KnownTypes()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Widget"));

        table.KnownTypes.Should().Contain(PortValueType.Of("Widget"));
    }

    [Fact]
    public void An_alias_that_is_already_the_canonical_name_of_a_different_type_is_refused()
    {
        var collide = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.String, PortValueType.Int);

        collide.Should().Throw<ArgumentException>().WithMessage("*String*Int*");
    }

    [Fact]
    public void A_declared_host_type_with_no_self_alias_still_refuses_being_aliased_to_something_else()
    {
        var table = PortTypeWideningTable.Builtin.With(PortValueType.Of("Depot"), [PortValueType.Of("Carrier")]);

        var collide = () => table.WithAlias(PortValueType.Of("Depot"), PortValueType.Of("Carrier"));

        collide.Should().Throw<ArgumentException>()
            .WithMessage("Port type alias 'Depot' is already declared as its own type, and cannot also alias 'Carrier'.*");
    }

    [Fact]
    public void An_alias_that_resolves_to_Any_is_refused_so_it_cannot_bring_the_name_back()
    {
        var collide = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Foo"), PortValueType.Of("Any"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Foo*Any*");
    }

    [Fact]
    public void An_alias_that_resolves_to_a_collection_of_Any_is_refused_the_same_way_as_Any()
    {
        var collide = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Foo"), PortValueType.Of("Collection<Any>"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Foo*Collection*");
    }

    [Fact]
    public void An_alias_key_that_parses_as_a_collection_name_is_refused()
    {
        var collide = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.CollectionOf(PortValueType.Of("Foo")), PortValueType.Of("Bar"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Collection<Foo>*").Which.ParamName.Should().Be("alias");
    }

    [Fact]
    public void WithAlias_canonicalises_its_target_so_aliasing_onto_a_library_alias_resolves_in_one_hop()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("X"), PortValueType.Of("bool"));

        table.Canonicalize(PortValueType.Of("X")).Should().Be(PortValueType.Boolean);
        table.IsAssignable(PortValueType.Of("X"), PortValueType.Boolean).Should().BeTrue();
    }

    [Fact]
    public void WithAlias_declares_its_target_so_a_later_collision_against_it_is_caught()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("pt"), PortValueType.Of("Point"));

        var collide = () => table.WithAlias(PortValueType.Of("pts"), PortValueType.Of("Collection<Point>"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Collection<Point>*Point*");
    }

    [Fact]
    public void WithAlias_refuses_a_target_that_is_the_collection_companion_of_an_already_declared_name()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Point"));

        var collide = () => table.WithAlias(PortValueType.Of("x"), PortValueType.Of("Collection<Point>"));

        collide.Should().Throw<ArgumentException>().WithMessage("*Collection<Point>*Point*");
    }

    [Fact]
    public void Declaring_an_alias_spelling_for_a_different_type_is_refused()
    {
        var collide = () => PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("bool"));

        collide.Should().Throw<ArgumentException>().WithMessage("*bool*Boolean*");
    }

    [Fact]
    public void Withs_canonical_form_refusal_on_its_from_side_names_the_from_parameter()
    {
        var collide = () => PortTypeWideningTable.Builtin.With(PortValueType.Of("bool"), [PortValueType.Of("Widget")]);

        collide.Should().Throw<ArgumentException>().WithMessage("*bool*Boolean*").Which.ParamName.Should().Be("from");
    }

    [Fact]
    public void Withs_canonical_form_refusal_on_a_targets_element_names_the_targets_parameter()
    {
        var collide = () => PortTypeWideningTable.Builtin.With(PortValueType.Of("Widget"), [PortValueType.Of("bool")]);

        collide.Should().Throw<ArgumentException>().WithMessage("*bool*Boolean*").Which.ParamName.Should().Be("targets");
    }

    [Fact]
    public void Declaring_a_case_variant_of_a_builtins_own_canonical_name_is_not_refused()
    {
        var declare = () => PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("int"));

        declare.Should().NotThrow();
    }

    [Fact]
    public void Canonicalize_answers_a_case_variant_with_itself_which_is_what_lets_Declaring_accept_it()
    {
        PortTypeWideningTable.Builtin.Canonicalize(PortValueType.Of("int")).Should().Be(PortValueType.Of("int"));
    }

    [Fact]
    public void Aliasing_a_declared_type_to_itself_is_legal()
    {
        var table = PortTypeWideningTable.Builtin.With(PortValueType.Of("Depot"), [PortValueType.Of("Carrier")]);

        var alias = () => table.WithAlias(PortValueType.Of("Depot"), PortValueType.Of("Depot"));

        alias.Should().NotThrow();
    }

    [Fact]
    public void Re_registering_the_same_alias_to_the_same_target_is_legal()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("dep"), PortValueType.Of("Depot"));

        var alias = () => table.WithAlias(PortValueType.Of("dep"), PortValueType.Of("Depot"));

        alias.Should().NotThrow();
    }

    [Fact]
    public void Re_registering_the_same_alias_to_a_different_target_still_throws()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("dep"), PortValueType.Of("Depot"));

        var alias = () => table.WithAlias(PortValueType.Of("dep"), PortValueType.Of("Carrier"));

        alias.Should().Throw<ArgumentException>().WithMessage("*dep*Depot*Carrier*");
    }

    [Fact]
    public void A_row_between_collections_of_builtins_is_refused_and_their_elements_already_answer()
    {
        var collide = () => PortTypeWideningTable.Builtin.With(
            PortValueType.CollectionOf(PortValueType.Int), [PortValueType.CollectionOf(PortValueType.Number)]);

        collide.Should().Throw<ArgumentException>().WithMessage("*'Collection<Int>' cannot widen*").Which.Message.Should().NotContain("ename");
        PortTypeWideningTable.Builtin.IsAssignable(
            PortValueType.CollectionOf(PortValueType.Int), PortValueType.CollectionOf(PortValueType.Number)).Should().BeTrue();
    }

    [Fact]
    public void A_collision_reached_through_WithAlias_never_says_the_two_names_are_one_type()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Point"));

        var collide = () => table.WithAlias(PortValueType.Of("pts"), PortValueType.Of("Collection<Point>"));

        collide.Should().Throw<ArgumentException>().WithMessage("*one depth only*").Which.Message.Should().NotContain("one type");
    }

    [Fact]
    public void An_upward_collision_reached_through_With_names_the_from_parameter()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("origins"), PortValueType.Of("Collection<Origin>"));

        var collide = () => table.With(PortValueType.Of("Origin"), [PortValueType.Of("SomethingElse")]);

        collide.Should().Throw<ArgumentException>().WithMessage("*one depth only*").Which.Message.Should().NotContain("one type");
        collide.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("from");
    }

    [Fact]
    public void An_upward_collision_reached_through_Declaring_names_the_type_parameter()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("pts"), PortValueType.Of("Collection<Point>"));

        var collide = () => table.Declaring(PortValueType.Of("Point"));

        collide.Should().Throw<ArgumentException>().WithMessage("*one depth only*").Which.Message.Should().NotContain("one type");
        collide.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("type");
    }

    [Fact]
    public void An_upward_collision_on_a_Withs_targets_side_names_the_targets_parameter()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("route"), PortValueType.Of("Collection<Waypoint>"));

        var collide = () => table.With(PortValueType.Of("SomethingElse"), [PortValueType.Of("Waypoint")]);

        collide.Should().Throw<ArgumentException>().WithMessage("*one depth only*").Which.ParamName.Should().Be("targets");
    }

    [Fact]
    public void An_upward_collision_through_WithAlias_names_the_canonical_parameter()
    {
        var table = PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("origins"), PortValueType.Of("Collection<Origin>"));

        var collide = () => table.WithAlias(PortValueType.Of("y"), PortValueType.Of("Origin"));

        collide.Should().Throw<ArgumentException>().WithMessage("*one depth only*").Which.ParamName.Should().Be("canonical");
    }

    [Fact]
    public void Withs_shape_refusal_on_its_from_side_names_the_from_parameter_even_with_the_element_declared()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Point"));

        var collide = () => table.With(PortValueType.Of("Collection<Point>"), [PortValueType.Of("Widget")]);

        collide.Should().Throw<ArgumentException>().WithMessage("*'Collection<Point>' cannot widen:*").WithParameterName("from");
    }

    [Fact]
    public void Withs_shape_refusal_on_a_target_names_the_targets_parameter_even_with_the_element_declared()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Point"));

        var collide = () => table.With(PortValueType.Of("Widget"), [PortValueType.Of("Collection<Point>")]);

        collide.Should().Throw<ArgumentException>().WithMessage("*'Widget' cannot widen to 'Collection<Point>'*").WithParameterName("targets");
    }

    [Fact]
    public void WithAliass_collision_on_its_target_names_the_canonical_parameter()
    {
        var table = PortTypeWideningTable.Builtin.Declaring(PortValueType.Of("Point"));

        var collide = () => table.WithAlias(PortValueType.Of("x"), PortValueType.Of("Collection<Point>"));

        collide.Should().Throw<ArgumentException>().WithParameterName("canonical");
    }

    [Fact]
    public void The_growing_alias_shape_that_47766fd_stack_overflowed_on_is_refused_before_it_ever_reaches_WithAlias()
    {
        var of = () => PortValueType.Of("<A>");

        of.Should().Throw<PortTypeSyntaxException>()
            .Which.Error.Fault.Should().Be(PortTypeSyntaxFault.ExpectedTypeName);
    }

    [Fact]
    public void The_oscillating_alias_pair_shape_is_refused_before_it_ever_reaches_WithAlias()
    {
        var of = () => PortValueType.Of("<<B>>");

        of.Should().Throw<PortTypeSyntaxException>()
            .Which.Error.Fault.Should().Be(PortTypeSyntaxFault.ExpectedTypeName);
    }

    [Fact]
    public void WithAlias_refuses_a_growing_alias_shape_caught_by_DeclareOnes_own_target_check()
    {
        var alias = () => PortTypeWideningTable.Builtin.WithAlias(PortValueType.Of("Foo"), PortValueType.CollectionOf(PortValueType.Of("Foo")));

        alias.Should().Throw<ArgumentException>().WithMessage("*canonicalises to*Collection<Collection<Foo>>*");
    }

    [Fact]
    public void F3s_reproducer_that_used_to_reach_Withs_canonical_form_check_is_now_refused_at_Of()
    {
        var of = () => PortValueType.Of("Collection<<Foo>>");

        of.Should().Throw<PortTypeSyntaxException>()
            .Which.Error.Fault.Should().Be(PortTypeSyntaxFault.ExpectedTypeName);
    }
}
