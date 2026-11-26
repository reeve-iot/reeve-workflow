using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class WideningTablePublicationTests
{
    private static readonly string[] Atoms =
    [
        "Depot", "DEPOT", "depot", "Stop", "Point", "Room", "String", "STRING", "string", "Int", "INT", "Number",
        "Float", "Json", "JSON", "Boolean", "BOOLEAN", "boolean", "Bool", "DateTime", "DATETIME", "Any", "ANY",
    ];

    private static readonly string[] AliasSpellings = ["dep", "BOOL", "bool", "Rm", "text", "DEP", "Pt"];

    [Fact]
    public void A_host_writing_a_built_in_in_capitals_publishes_the_built_in_spelling()
    {
        var table = PortTypeWideningTable.Builtin
            .With(PortValueType.Of("BOOLEAN"), [PortValueType.Of("Flag")])
            .With(PortValueType.Of("Flag"), [PortValueType.Of("STRING")]);

        table.KnownTypes.Select(t => t.Name).Should().Contain(["Boolean", "String"]).And.NotContain(["BOOLEAN", "STRING"]);
        table.WideningsOf(PortValueType.Of("Flag")).Select(t => t.Name).Should().Equal("String");
    }

    [Fact]
    public void A_name_the_host_writes_two_ways_is_published_in_the_spelling_the_compile_accepts()
    {
        var table = PortTypeWideningTable.Builtin
            .Declaring(PortValueType.Of("Parcel"))
            .With(PortValueType.Of("PARCEL"), []);

        var published = table.KnownTypes.Select(t => t.Name).Single(n => n.Equals("parcel", StringComparison.OrdinalIgnoreCase));

        published.Should().Be("PARCEL", "no alias fixes a spelling, so the ordinally smallest written one is the one Canonicalize answers");
        table.Canonicalize(PortValueType.Of(published)).Name.Should().Be(published);
        table.IsCanonical(PortValueType.Of(published)).Should().BeTrue();
    }

    [Fact]
    public void Every_name_published_is_its_own_canonical_spelling_in_every_generated_table()
    {
        var random = new Random(20260925);
        var checkedNames = 0;
        for (var i = 0; i < 400; i++)
        {
            var table = Generate(random);
            var known = table.KnownTypes;
            known.Select(t => t.Name).Should().OnlyHaveUniqueItems(n => n.ToUpperInvariant(), because: $"table {i}");
            foreach (var type in known)
            {
                table.Canonicalize(type).Name.Should().Be(type.Name, because: $"table {i} publishes it");
                foreach (var target in table.WideningsOf(type))
                    table.Canonicalize(target).Name.Should().Be(target.Name, because: $"table {i} publishes it as a target of {type.Name}");
                checkedNames++;
            }
        }

        checkedNames.Should().BeGreaterThan(400 * 6);
    }

    [Fact]
    public void Every_collection_name_published_has_its_atom_published_or_an_alias_the_adapter_refuses_by_name()
    {
        var random = new Random(20260926);
        var collections = 0;
        for (var i = 0; i < 400; i++)
        {
            var table = Generate(random);
            var plain = table.KnownTypes.Where(t => !t.Name.Contains('<')).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var type in table.KnownTypes.Where(t => t.Name.Contains('<')))
            {
                var atom = type.Name.Replace("Collection<", "", StringComparison.Ordinal).TrimEnd('>');
                (plain.Contains(atom) || table.AliasesOf(type).Count > 0)
                    .Should().BeTrue($"table {i} publishes {type.Name} with neither {atom} nor an alias");
                collections += table.AliasesOf(type).Count > 0 ? 1 : 0;
            }
        }

        collections.Should().BeGreaterThan(0, "an alias onto a collection is generated");
    }

    private static PortTypeWideningTable Generate(Random random)
    {
        var table = PortTypeWideningTable.Builtin;
        for (var k = random.Next(1, 9); k > 0; k--)
        {
            try
            {
                table = random.Next(3) switch
                {
                    0 => table.With(Name(random), [.. Enumerable.Range(0, random.Next(1, 3)).Select(_ => Name(random))]),
                    1 => table.WithAlias(PortValueType.Of(AliasSpellings[random.Next(AliasSpellings.Length)]), Name(random)),
                    _ => table.Declaring(Name(random)),
                };
            }
            catch (ArgumentException)
            {
            }
        }

        return table;
    }

    private static PortValueType Name(Random random)
    {
        var atom = Atoms[random.Next(Atoms.Length)];
        return PortValueType.Of(random.Next(10) switch
        {
            < 7 => atom,
            < 9 => $"Collection<{atom}>",
            _ => $"Collection<Collection<{atom}>>",
        });
    }
}
