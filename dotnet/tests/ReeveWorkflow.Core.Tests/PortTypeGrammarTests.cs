using FluentAssertions;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Tests;

public class PortTypeGrammarTests
{
    public static IEnumerable<object[]> WellFormed => new List<object[]>
    {
        new object[] { "Int", 0, "Int" },
        new object[] { "Collection<Int>", 1, "Int" },
        new object[] { "Collection<Collection<Any>>", 2, "Any" },
        new object[] { "Collection<any>", 1, "any" },
        new object[] { "_x9", 0, "_x9" },
        new object[] { "Collectible", 0, "Collectible" }
    };

    [Theory]
    [MemberData(nameof(WellFormed))]
    public void A_well_formed_name_parses_to_its_term(string text, int depth, string element)
    {
        PortValueType.TryParse(text, out var type, out var error).Should().BeTrue();
        error.Should().BeNull();
        type.IsCollection.Should().Be(depth > 0);
        var unwrapped = type;
        for (var i = 0; i < depth; i++) unwrapped = unwrapped.ElementType;
        unwrapped.Name.Should().Be(element);
    }

    public static IEnumerable<object[]> Malformed => new List<object[]>
    {
        new object[] { null!, PortTypeSyntaxFault.Absent, 0 },
        new object[] { "", PortTypeSyntaxFault.Empty, 0 },
        new object[] { "Pt ", PortTypeSyntaxFault.IllegalCharacter, 2 },
        new object[] { "Foo Bar", PortTypeSyntaxFault.IllegalCharacter, 3 },
        new object[] { "Reeve.Device", PortTypeSyntaxFault.IllegalCharacter, 5 },
        new object[] { "Collection< Int >", PortTypeSyntaxFault.IllegalCharacter, 11 },
        new object[] { "\u0000", PortTypeSyntaxFault.IllegalCharacter, 0 },
        new object[] { "\ud800", PortTypeSyntaxFault.IllegalCharacter, 0 },
        new object[] { "<X>", PortTypeSyntaxFault.ExpectedTypeName, 0 },
        new object[] { "Collection<>", PortTypeSyntaxFault.ExpectedTypeName, 11 },
        new object[] { "Collection<", PortTypeSyntaxFault.ExpectedTypeName, 11 },
        new object[] { "Collection", PortTypeSyntaxFault.CollectionWithoutBracket, 0 },
        new object[] { "Collection<Collection>", PortTypeSyntaxFault.CollectionWithoutBracket, 11 },
        new object[] { "CollectionInt", PortTypeSyntaxFault.ReservedCollectionPrefix, 0 },
        new object[] { "collection<Int>", PortTypeSyntaxFault.ReservedCollectionPrefix, 0 },
        new object[] { "Collections", PortTypeSyntaxFault.ReservedCollectionPrefix, 0 },
        new object[] { "Collection<Int", PortTypeSyntaxFault.MissingClose, 14 },
        new object[] { "Map<K,V>", PortTypeSyntaxFault.MapKey, 4 },
        new object[] { "Int>", PortTypeSyntaxFault.UnexpectedClose, 3 },
        new object[] { "Collection<Int>>", PortTypeSyntaxFault.UnexpectedClose, 15 }
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_name_is_refused_with_its_fault_and_position(string? text, PortTypeSyntaxFault fault, int position)
    {
        PortValueType.TryParse(text, out var type, out var error).Should().BeFalse();
        type.IsDefault.Should().BeTrue();
        error!.Fault.Should().Be(fault);
        error.Position.Should().Be(position);
    }

    public static IEnumerable<object[]> BracketAndPrefixNames => new List<object[]>
    {
        new object[] { "Foo>Bar", PortTypeSyntaxFault.UnexpectedClose, 3 },
        new object[] { "X<Y", PortTypeSyntaxFault.MissingClose, 3 },
        new object[] { "Z>W", PortTypeSyntaxFault.UnexpectedClose, 1 },
        new object[] { "List<Item>>", PortTypeSyntaxFault.UnexpectedClose, 10 },
        new object[] { "Multi<A><B>", PortTypeSyntaxFault.UnexpectedOpen, 8 },
        new object[] { "Trailing>>>", PortTypeSyntaxFault.UnexpectedClose, 8 },
        new object[] { "Weird<<>>", PortTypeSyntaxFault.ExpectedTypeName, 6 },
        new object[] { "Dict<K,Dict<K,V>>", PortTypeSyntaxFault.IllegalCharacter, 6 },
        new object[] { "<A>", PortTypeSyntaxFault.ExpectedTypeName, 0 },
        new object[] { "<<B>>", PortTypeSyntaxFault.ExpectedTypeName, 0 },
        new object[] { "<<Foo>>", PortTypeSyntaxFault.ExpectedTypeName, 0 },
        new object[] { "CollectionCollection<\t>", PortTypeSyntaxFault.ReservedCollectionPrefix, 0 },
        new object[] { "Collection<Collection<\t>>", PortTypeSyntaxFault.IllegalCharacter, 22 },
        new object[] { "CollectionCollection ", PortTypeSyntaxFault.IllegalCharacter, 20 },
        new object[] { "CollectionCollection< >", PortTypeSyntaxFault.ReservedCollectionPrefix, 0 },
        new object[] { "Collection<Collection< >>", PortTypeSyntaxFault.IllegalCharacter, 22 },
        new object[] { "CollectionCollection<　>", PortTypeSyntaxFault.ReservedCollectionPrefix, 0 },
        new object[] { "Collection<Collection<　>>", PortTypeSyntaxFault.IllegalCharacter, 22 },
        new object[] { "Collection \t ", PortTypeSyntaxFault.IllegalCharacter, 10 },
        new object[] { "Collection< \t >", PortTypeSyntaxFault.IllegalCharacter, 11 },
        new object[] { "CollectionCollection \t ", PortTypeSyntaxFault.IllegalCharacter, 20 },
        new object[] { "CollectionCollection< \t >", PortTypeSyntaxFault.ReservedCollectionPrefix, 0 },
        new object[] { "Collection<Collection< \t >>", PortTypeSyntaxFault.IllegalCharacter, 22 }
    };

    [Theory]
    [MemberData(nameof(BracketAndPrefixNames))]
    public void A_name_with_misplaced_brackets_or_whitespace_is_refused_at_parse(string text, PortTypeSyntaxFault fault, int position) =>
        A_malformed_name_is_refused_with_its_fault_and_position(text, fault, position);

    [Theory]
    [InlineData("Pt ", "white space")]
    [InlineData("Pt\u0001", "a control character")]
    [InlineData("Gr\u00f6\u00dfe", "a non-ASCII character")]
    [InlineData("\ud800", "a non-ASCII character")]
    [InlineData("A,B", "','")]
    public void An_illegal_character_is_named_by_its_class_in_the_hint(string text, string hint)
    {
        PortValueType.TryParse(text, out _, out var error).Should().BeFalse();
        error!.Hint.Should().Be(hint);
    }

    [Fact]
    public void Of_throws_PortTypeSyntaxException_pinning_the_same_fault()
    {
        var act = () => PortValueType.Of("CollectionInt");

        act.Should().Throw<PortTypeSyntaxException>()
            .Which.Error.Fault.Should().Be(PortTypeSyntaxFault.ReservedCollectionPrefix);
    }

    [Fact]
    public void Of_null_throws_ArgumentNullException_not_a_syntax_error()
    {
        var act = () => PortValueType.Of(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("CollectionInt")]
    [InlineData("CollectionAny")]
    [InlineData("CollectionFloat")]
    [InlineData("Collectionany")]
    public void A_reserved_collection_prefix_hints_to_write_the_bracketed_form(string text)
    {
        PortValueType.TryParse(text, out _, out var error).Should().BeFalse();
        error!.Fault.Should().Be(PortTypeSyntaxFault.ReservedCollectionPrefix);
        error.Hint.Should().Be("write Collection<T> with the type of its elements");
    }

    [Theory]
    [InlineData("Collection")]
    [InlineData("Collection<Collection>")]
    [InlineData("Collection>")]
    public void A_missing_bracket_hints_to_write_Collection_of_the_element_type(string text)
    {
        PortValueType.TryParse(text, out _, out var error).Should().BeFalse();
        error!.Fault.Should().Be(PortTypeSyntaxFault.CollectionWithoutBracket);
        error.Hint.Should().Be("write Collection<T> with the type of its elements");
    }

    [Fact]
    public void A_100000_deep_collection_parses_on_a_256KB_stack_with_no_recursion()
    {
        var deep = string.Concat(Enumerable.Repeat("Collection<", 100_000)) + "Int" + string.Concat(Enumerable.Repeat(">", 100_000));
        PortValueType? actual = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try { actual = PortValueType.Of(deep); }
            catch (Exception e) { failure = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        failure.Should().BeNull();
        actual!.Value.IsCollection.Should().BeTrue();
    }

    public static IEnumerable<object[]> Terms => new List<object[]>
    {
        new object[] { "Int?", PortTermGrammar.Document, "?Int" },
        new object[] { "Collection<Int?>", PortTermGrammar.Document, "Collection(?Int)" },
        new object[] { "Collection<Int>?", PortTermGrammar.Document, "?Collection(Int)" },
        new object[] { "Collection<Int?>?", PortTermGrammar.Document, "?Collection(?Int)" },
        new object[] { "Gauge<Collection<Json?>>?", PortTermGrammar.Document, "?Gauge(Collection(?Json))" },
        new object[] { "Expression<Boolean>", PortTermGrammar.Document, "Expression(Boolean)" },
        new object[] { "$T", PortTermGrammar.Declaration, "$T" },
        new object[] { "$T?", PortTermGrammar.Declaration, "?$T" },
        new object[] { "Collection<$T?>?", PortTermGrammar.Declaration, "?Collection(?$T)" },
        new object[] { "Expression<$T>", PortTermGrammar.Declaration, "Expression($T)" },
        new object[] { "$Collection", PortTermGrammar.Declaration, "$Collection" },
        new object[] { "Collection<$collectionX?>", PortTermGrammar.Declaration, "Collection(?$collectionX)" },
    };

    [Theory]
    [MemberData(nameof(Terms))]
    public void A_term_is_read_to_its_shape_and_written_back_as_it_was_read(string text, PortTermGrammar grammar, string shape)
    {
        var term = PortTerm.Parse(text, grammar);

        Shape(term).Should().Be(shape);
        term.ToString().Should().Be(text);
        PortTerm.Parse(term.ToString(), grammar).Should().Be(term);
    }

    public static IEnumerable<object[]> RefusedTerms => new List<object[]>
    {
        new object[] { "$T", PortTermGrammar.Document, PortTypeSyntaxFault.IllegalCharacter, 0 },
        new object[] { "Collection<$T>", PortTermGrammar.Document, PortTypeSyntaxFault.IllegalCharacter, 11 },
        new object[] { "Int??", PortTermGrammar.Document, PortTypeSyntaxFault.DoubleNullable, 4 },
        new object[] { "Collection<Int??>", PortTermGrammar.Document, PortTypeSyntaxFault.DoubleNullable, 15 },
        new object[] { "Collection<Int>??", PortTermGrammar.Document, PortTypeSyntaxFault.DoubleNullable, 16 },
        new object[] { "?", PortTermGrammar.Document, PortTypeSyntaxFault.ExpectedTypeName, 0 },
        new object[] { "Int?Foo", PortTermGrammar.Document, PortTypeSyntaxFault.IllegalCharacter, 4 },
        new object[] { "Gauge<>", PortTermGrammar.Document, PortTypeSyntaxFault.ExpectedTypeName, 6 },
        new object[] { "Gauge<Int", PortTermGrammar.Document, PortTypeSyntaxFault.MissingClose, 9 },
        new object[] { "Collection?", PortTermGrammar.Document, PortTypeSyntaxFault.CollectionWithoutBracket, 0 },
        new object[] { "collection<Int>?", PortTermGrammar.Document, PortTypeSyntaxFault.ReservedCollectionPrefix, 0 },
        new object[] { "$", PortTermGrammar.Declaration, PortTypeSyntaxFault.ExpectedTypeName, 1 },
        new object[] { "$$T", PortTermGrammar.Declaration, PortTypeSyntaxFault.IllegalCharacter, 1 },
        new object[] { "$1", PortTermGrammar.Declaration, PortTypeSyntaxFault.IllegalCharacter, 1 },
        new object[] { "$T<X>", PortTermGrammar.Declaration, PortTypeSyntaxFault.UnexpectedOpen, 2 },
        new object[] { "$T??", PortTermGrammar.Declaration, PortTypeSyntaxFault.DoubleNullable, 3 },
    };

    [Theory]
    [MemberData(nameof(RefusedTerms))]
    public void A_term_the_grammar_refuses_carries_its_fault_and_position(
        string text, PortTermGrammar grammar, PortTypeSyntaxFault fault, int position)
    {
        PortTerm.TryParse(text, grammar, out var term, out var error).Should().BeFalse();

        term.Should().BeNull();
        error!.Fault.Should().Be(fault);
        error.Position.Should().Be(position);
    }

    [Theory]
    [InlineData("Int??", "'Int??' is not a port type name: a second '?' after '?' at 4.")]
    [InlineData("Gauge<>", "'Gauge<>' is not a port type name: a type name was expected at 6.")]
    public void A_new_fault_reads_in_the_words_the_editor_gives_it(string text, string message)
    {
        PortValueType.TryParse(text, out _, out var error).Should().BeFalse();
        error!.Message.Should().Be(message);
    }

    [Fact]
    public void A_stored_name_reads_nullable_and_constructor_sentences_but_never_a_parameter()
    {
        PortValueType.TryParse("Collection<Int?>?", out var nullableList, out _).Should().BeTrue();
        PortValueType.TryParse("Gauge<Boolean>", out _, out _).Should().BeTrue();
        PortValueType.TryParse("$T", out _, out var error).Should().BeFalse();

        nullableList.IsCollection.Should().BeFalse("a list or null is not a list");
        PortValueType.Of("Collection<Int?>").IsCollection.Should().BeTrue();
        PortValueType.Of("Collection<Int?>").ElementType.Name.Should().Be("Int?");
        error!.Fault.Should().Be(PortTypeSyntaxFault.IllegalCharacter);
    }

    [Fact]
    public void Terms_compare_names_ignoring_ASCII_case_and_a_parameter_exactly()
    {
        PortTerm.Parse("Collection<int?>", PortTermGrammar.Document)
            .Should().Be(PortTerm.Parse("Collection<INT?>", PortTermGrammar.Document));
        PortTerm.Parse("Int", PortTermGrammar.Document).Should().NotBe(PortTerm.Parse("Int?", PortTermGrammar.Document));
        PortTerm.Parse("$T", PortTermGrammar.Declaration).Should().NotBe(PortTerm.Parse("$t", PortTermGrammar.Declaration));
    }

    [Fact]
    public void Making_a_nullable_term_nullable_again_gives_one_question_mark()
    {
        var once = PortTerm.Parse("Int?", PortTermGrammar.Document);

        once.AsNullable().Should().BeSameAs(once);
        PortTerm.Parse("Int", PortTermGrammar.Document).AsNullable().Should().Be(once);
        PortTerm.Null.AsNullable().Should().BeSameAs(PortTerm.Null);
    }

    [Fact]
    public void A_100000_deep_nullable_constructor_chain_parses_and_writes_on_a_256KB_stack()
    {
        var deep = string.Concat(Enumerable.Repeat("Gauge<", 100_000)) + "Int?" + string.Concat(Enumerable.Repeat(">?", 100_000));
        string? written = null;
        bool? equal = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var term = PortTerm.Parse(deep, PortTermGrammar.Document);
                written = term.ToString();
                equal = term.Equals(PortTerm.Parse(deep, PortTermGrammar.Document));
            }
            catch (Exception e) { failure = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        failure.Should().BeNull();
        written.Should().Be(deep);
        equal.Should().BeTrue();
    }

    private static string Shape(PortTerm term)
    {
        var prefix = new System.Text.StringBuilder();
        var suffix = new System.Text.StringBuilder();
        for (var node = term; node is not null; node = node.LastArgument)
        {
            if (node.IsNullable) prefix.Append('?');
            prefix.Append(node.Kind == PortTermKind.Parameter ? "$" : "").Append(node.Name);
            if (node.Arguments.Count > 0)
            {
                prefix.Append('(');
                foreach (var key in node.Arguments.SkipLast(1)) prefix.Append(key.Name).Append(',');
                suffix.Append(')');
            }
        }
        return prefix.Append(suffix).ToString();
    }
}
