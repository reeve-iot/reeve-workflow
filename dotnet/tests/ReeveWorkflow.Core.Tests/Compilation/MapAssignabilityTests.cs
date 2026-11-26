using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class MapAssignabilityTests
{
    private static readonly WorkflowTypeVocabulary Vocabulary = WorkflowTypeVocabulary.Builtin;

    private static PortTerm Term(string text) => PortTerm.Parse(text, PortTermGrammar.Document);

    private sealed class PlainHandle : ICapabilityClaims
    {
        public bool Claims(PortTerm head, WorkflowCapability capability) =>
            !string.Equals(head.Name, "Handle", StringComparison.OrdinalIgnoreCase)
            && ((ICapabilityClaims)Vocabulary).Claims(head, capability);
    }

    private static string[] Implemented(string text) =>
        [.. PortCapabilities.All.Where(c => PortTermCapabilities.Implements(Term(text), c, new PlainHandle())).Select(PortCapabilities.NameOf)];

    [Theory]
    [InlineData("Map<String,Int>", new[] { "Equatable", "JsonSerializable" })]
    [InlineData("Map<String,Boolean>", new[] { "Equatable", "JsonSerializable" })]
    [InlineData("Map<String,Collection<Int>>", new[] { "Equatable", "JsonSerializable" })]
    [InlineData("Map<String,Map<String,String>>", new[] { "Equatable", "JsonSerializable" })]
    [InlineData("Collection<Map<String,Int>>", new[] { "Iterable", "Equatable", "JsonSerializable" })]
    [InlineData("Map<String,Int?>", new[] { "JsonSerializable" })]
    [InlineData("Map<String,Handle>", new string[0])]
    [InlineData("Map<String,Collection<Handle>>", new string[0])]
    [InlineData("Map<String,Int>?", new[] { "JsonSerializable" })]
    public void A_map_is_equatable_and_json_serializable_exactly_when_its_value_is_and_nothing_else(string term, string[] expected)
    {
        Implemented(term).Should().Equal(expected);
    }

    [Theory]
    [InlineData("Map<String,Int>")]
    [InlineData("Map<String,Collection<Int>>")]
    [InlineData("Map<String,Int>?")]
    public void A_map_is_not_iterable_so_it_names_no_item(string term)
    {
        PortTermCapabilities.Implements(Term(term), WorkflowCapability.Iterable, new PlainHandle()).Should().BeFalse();
        PortTermCapabilities.IterableItem(Term(term)).Should().BeNull();
    }

    [Fact]
    public void A_collection_of_maps_iterates_over_its_maps()
    {
        PortTermCapabilities.IterableItem(Term("Collection<Map<String,Int>>"))!.ToString().Should().Be("Map<String,Int>");
    }

    [Fact]
    public void A_map_feeds_neither_a_collection_nor_an_atom_and_nothing_feeds_a_map_but_a_map()
    {
        var wrong = new List<string>();
        foreach (var (from, to) in new[]
        {
            ("Map<String,Int>", "Collection<Int>"), ("Map<String,Int>", "Collection<Map<String,Int>>"), ("Collection<Int>", "Map<String,Int>"),
            ("Collection<Map<String,Int>>", "Map<String,Int>"), ("Int", "Map<String,Int>"), ("Map<String,Int>", "Int"),
            ("Map<String,Int>", "String"),
        })
        {
            if (Vocabulary.IsAssignable(Term(from), Term(to)))
                wrong.Add($"{from} -> {to}");
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void A_map_of_a_nullable_value_does_not_feed_a_map_of_the_value()
    {
        Vocabulary.IsAssignable(Term("Map<String,Int?>"), Term("Map<String,Int>")).Should().BeFalse();
        Vocabulary.IsAssignable(Term("Map<String,Int>"), Term("Map<String,Int?>")).Should().BeTrue();
    }

    [Fact]
    public void The_wire_check_by_name_reads_maps_as_the_terms_check_does()
    {
        Vocabulary.IsAssignable(PortValueType.Of("Map<String,Int>"), PortValueType.Of("Map<String,Number>")).Should().BeTrue();
        Vocabulary.IsAssignable(PortValueType.Of("Map<String,Number>"), PortValueType.Of("Map<String,Int>")).Should().BeFalse();
        Vocabulary.IsAssignable(PortValueType.Of("Map<string,int>"), PortValueType.Of("Map<String,Number>")).Should().BeTrue();
    }

    [Fact]
    public void A_map_joins_value_by_value_and_never_with_a_collection_or_an_atom()
    {
        Vocabulary.LeastUpperBound([Term("Map<String,Int>"), Term("Map<String,Number>")])!.ToString().Should().Be("Map<String,Number>");
        Vocabulary.LeastUpperBound([Term("Map<String,Int>"), PortTerm.Null])!.ToString().Should().Be("Map<String,Int>?");
        Vocabulary.LeastUpperBound([Term("Map<String,Int>?"), Term("Map<String,Int?>")])!.ToString().Should().Be("Map<String,Int?>?");
        Vocabulary.LeastUpperBound([Term("Map<String,Int>"), Term("Collection<Int>")]).Should().BeNull();
        Vocabulary.LeastUpperBound([Term("Map<String,Int>"), Term("Map<String,String>")]).Should().BeNull();
    }

    [Fact]
    public void A_map_nested_100000_levels_is_answered_on_a_256KB_stack_by_every_decision_but_the_join()
    {
        const int depth = 100_000;
        static PortTerm Deep(string value) => PortTerm.Parse(
            string.Concat(Enumerable.Repeat("Map<String,", depth)) + value + string.Concat(Enumerable.Repeat(">", depth)),
            PortTermGrammar.Document);
        object[]? answers = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var (ints, numbers) = (Deep("Int"), Deep("Number"));
                answers =
                [
                    Vocabulary.IsAssignable(ints, numbers),
                    Vocabulary.IsAssignable(numbers, ints),
                    Vocabulary.IsAssignable(ints, Term("Json")),
                    PortTermCapabilities.Implements(ints, WorkflowCapability.Equatable, new PlainHandle()),
                    PortTermCapabilities.Implements(Deep("Handle"), WorkflowCapability.JsonSerializable, new PlainHandle()),
                    PortConversion.Between(ints, numbers, _ => false).Kind,
                ];
            }
            catch (Exception e) { failure = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();

        failure.Should().BeNull();
        answers.Should().Equal(true, false, true, true, false, PortConversionKind.Elementwise);
    }
}
