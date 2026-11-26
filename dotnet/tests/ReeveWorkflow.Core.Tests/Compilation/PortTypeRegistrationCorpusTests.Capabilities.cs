using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed partial class PortTypeRegistrationCorpusTests
{
    private static readonly Lazy<JsonElement> CapabilitiesSection = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PortTypeRegistrationCorpusPath()));
        return document.RootElement.GetProperty("capabilities").Clone();
    });

    private sealed record CrateRef(string Id);

    private sealed class PalletRef : IComparable<PalletRef>
    {
        public int CompareTo(PalletRef? other) => 0;
    }

    private sealed class PlainRef;

    private sealed class HandleRef;

    private sealed record GaugeRef(string Shipment, string Key);

    private sealed record LatchRef(string Id);

    private static readonly Dictionary<string, Type> Fixtures = new(StringComparer.Ordinal)
    {
        ["Crate"] = typeof(CrateRef),
        ["Pallet"] = typeof(PalletRef),
        ["Plain"] = typeof(PlainRef),
        ["Handle"] = typeof(HandleRef),
        ["Gauge"] = typeof(GaugeRef),
        ["Latch"] = typeof(LatchRef),
    };

    private static HashSet<WorkflowCapability> ClaimsOf(JsonElement claims) =>
        [.. claims.EnumerateArray().Select(n => PortCapabilities.TryParse(n.GetString(), out var c) ? c : (WorkflowCapability)99)];

    private static WorkflowTypeVocabulary RegisteredHosts()
    {
        var records = new List<WorkflowTypeRegistrationRecord>();
        var vocabulary = CapabilitiesSection.Value.GetProperty("vocabulary");
        foreach (var atom in vocabulary.GetProperty("atoms").EnumerateObject())
        {
            var clr = Fixtures[atom.Name];
            var claims = ClaimsOf(atom.Value.GetProperty("implements"));
            records.Add(new WorkflowTypeRecord(atom.Name, clr)
            {
                Claims = claims,
                JsonTypeInfo = claims.Contains(WorkflowCapability.JsonSerializable) ? StrictJson.Info(clr) : null,
            });
        }

        foreach (var constructor in vocabulary.GetProperty("constructors").EnumerateObject())
        {
            var clr = Fixtures[constructor.Name];
            var claims = ClaimsOf(constructor.Value.GetProperty("implements"));
            records.Add(new WorkflowTypeConstructorRecord(constructor.Name, clr)
            {
                AllowedArguments = [.. constructor.Value.GetProperty("allowedArguments").EnumerateArray().Select(a => a.GetString()!)],
                Claims = claims,
                JsonTypeInfo = claims.Contains(WorkflowCapability.JsonSerializable) ? StrictJson.Info(clr) : null,
            });
        }

        return new WorkflowTypeRegistry(records).Current;
    }

    private static WorkflowTypeRegistrationRecord ClaimRecordOf(JsonElement row)
    {
        var clrName = row.GetProperty("clr").GetString();
        var clr = clrName switch
        {
            "string" => typeof(string),
            "guid" => typeof(Guid),
            "memoryStream" => typeof(System.IO.MemoryStream),
            "object" => typeof(object),
            _ => throw new InvalidOperationException($"CLR fixture '{clrName}'"),
        };
        var claims = ClaimsOf(row.GetProperty("claims"));
        var json = claims.Contains(WorkflowCapability.JsonSerializable);
        var head = row.GetProperty("head").GetString()!;
        var info = row.TryGetProperty("jsonTypeInfoOf", out var of)
            ? of.GetString() == "string" ? StrictJson.Info(typeof(string)) : throw new InvalidOperationException($"JsonTypeInfo fixture '{of}'")
            : json ? StrictJson.Info(clr) : null;
        return row.GetProperty("subject").GetString() == "constructor"
            ? new WorkflowTypeConstructorRecord(head, clr)
            {
                AllowedArguments = [.. row.GetProperty("allowedArguments").EnumerateArray().Select(a => a.GetString()!)],
                Claims = claims,
                JsonTypeInfo = info,
            }
            : new WorkflowTypeRecord(head, clr)
            {
                Claims = claims,
                JsonTypeInfo = info,
            };
    }

    private static WorkflowTypeRegistrationRecord LiteralRecordOf(JsonElement row)
    {
        var written = row.GetProperty("literal");
        var kind = Enum.TryParse<WorkflowLiteralKind>(written.GetProperty("gradedAs").GetString(), out var parsedKind) ? parsedKind : (WorkflowLiteralKind)99;
        WorkflowLiteralFormat? format = written.TryGetProperty("format", out var text)
            ? Enum.TryParse<WorkflowLiteralFormat>(text.GetString(), out var parsedFormat) ? parsedFormat : (WorkflowLiteralFormat)99
            : null;
        var form = new WorkflowLiteralForm(kind, format);
        var head = row.GetProperty("head").GetString()!;
        var info = row.TryGetProperty("jsonTypeInfo", out var given) && !given.GetBoolean() ? null : StrictJson.Info(typeof(string));
        return row.GetProperty("subject").GetString() == "constructor"
            ? new WorkflowTypeConstructorRecord(head, typeof(string))
            {
                AllowedArguments = [.. row.GetProperty("allowedArguments").EnumerateArray().Select(a => a.GetString()!)],
                Literal = form,
                JsonTypeInfo = info,
            }
            : new WorkflowTypeRecord(head, typeof(string)) { Literal = form, JsonTypeInfo = info };
    }

    private static PortTerm TermOf(string text) => PortTerm.Parse(text, PortTermGrammar.Document);

    private static IEnumerable<JsonElement> Vectors() => CapabilitiesSection.Value.GetProperty("vectors").EnumerateArray();

    private static IEnumerable<string> Names(JsonElement array) => array.EnumerateArray().Select(n => n.GetString()!);

    [Fact]
    public void The_vocabulary_is_the_five_capabilities_with_Iterable_carrying_an_Item()
    {
        var published = CapabilitiesSection.Value.GetProperty("capabilities").EnumerateArray()
            .Select(c => (Name: c.GetProperty("name").GetString()!,
                Associated: c.TryGetProperty("associated", out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToArray() : []))
            .ToList();

        published.Select(c => c.Name).Should().Equal(PortCapabilities.All.Select(PortCapabilities.NameOf));
        published.Select(c => c.Associated).Should().BeEquivalentTo(PortCapabilities.All.Select(PortCapabilities.AssociatedTypes), o => o.WithStrictOrdering());
        Names(CapabilitiesSection.Value.GetProperty("hostClaimable"))
            .Should().Equal(PortCapabilities.All.Where(PortCapabilities.HostMayClaim).Select(PortCapabilities.NameOf));
    }

    [Fact]
    public void A_parameter_is_constrained_by_the_capabilities_but_Iterable()
    {
        Names(CapabilitiesSection.Value.GetProperty("constraints")).Should().Equal(TypeParameterConstraints.Known);
        TypeParameterConstraints.Known.Should().NotContain("Iterable");
    }

    [Fact]
    public void Every_term_implements_exactly_the_capabilities_the_corpus_states()
    {
        var vocabulary = RegisteredHosts();
        var wrong = new List<string>();
        foreach (var vector in Vectors())
        {
            var text = vector.GetProperty("term").GetString()!;
            var expected = Names(vector.GetProperty("implements")).ToList();
            var actual = vocabulary.CapabilitiesOf(TermOf(text)).Select(PortCapabilities.NameOf).ToList();
            if (!actual.SequenceEqual(expected))
                wrong.Add($"{text}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");

            foreach (var capability in PortCapabilities.All)
                if (vocabulary.Implements(TermOf(text), capability) != expected.Contains(PortCapabilities.NameOf(capability)))
                    wrong.Add($"{text}: Implements({capability}) disagrees with CapabilitiesOf");
        }

        wrong.Should().BeEmpty();
        Vectors().Count().Should().BeGreaterThan(40);
    }

    [Fact]
    public void An_Iterable_term_names_its_Item_and_a_term_that_is_not_Iterable_names_none()
    {
        var vocabulary = RegisteredHosts();
        var wrong = new List<string>();
        foreach (var vector in Vectors())
        {
            var text = vector.GetProperty("term").GetString()!;
            var expected = vector.TryGetProperty("item", out var item) ? item.GetString() : null;
            var actual = vocabulary.IterableItemOf(TermOf(text))?.ToString();
            if (actual != expected)
                wrong.Add($"{text}: expected item {expected ?? "none"}, got {actual ?? "none"}");
            (expected is not null).Should().Be(Names(vector.GetProperty("implements")).Contains("Iterable"), text);
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void A_term_is_assignable_to_Json_exactly_when_it_implements_JsonSerializable()
    {
        var vocabulary = RegisteredHosts();
        var wrong = new List<string>();
        foreach (var vector in Vectors())
        {
            var term = TermOf(vector.GetProperty("term").GetString()!);
            var serializable = Names(vector.GetProperty("implements")).Contains("JsonSerializable");
            var json = TermOf(term.IsNullable ? "Json?" : "Json");
            if (vocabulary.IsAssignable(term, json) != serializable)
                wrong.Add($"{term} -> {json}: implements JsonSerializable {serializable}");
        }

        wrong.Should().BeEmpty();
    }

    [Fact]
    public void Membership_is_refused_for_every_term_that_names_something_unregistered()
    {
        var vocabulary = RegisteredHosts();
        var refused = Names(CapabilitiesSection.Value.GetProperty("refused")).ToList();
        refused.Should().NotBeEmpty();
        foreach (var text in refused)
        {
            var term = TermOf(text);
            ((Action)(() => vocabulary.Implements(term, WorkflowCapability.Iterable))).Should().Throw<ArgumentException>(text).WithMessage("port-type-unregistered*");
            ((Action)(() => vocabulary.CapabilitiesOf(term))).Should().Throw<ArgumentException>(text).WithMessage("port-type-unregistered*");
            ((Action)(() => vocabulary.IterableItemOf(term))).Should().Throw<ArgumentException>(text).WithMessage("port-type-unregistered*");
        }
    }

    [Fact]
    public void The_published_atoms_and_constructors_list_what_they_implement()
    {
        var vocabulary = RegisteredHosts();
        var hosts = CapabilitiesSection.Value.GetProperty("vocabulary");
        foreach (var atom in hosts.GetProperty("atoms").EnumerateObject())
            vocabulary.Types.Single(t => t.Name == atom.Name).Implements.Should().Equal(ClaimsOf(atom.Value.GetProperty("implements")).Order());
        foreach (var constructor in hosts.GetProperty("constructors").EnumerateObject())
            vocabulary.Constructors.Single(c => c.Name == constructor.Name).Implements.Should().Equal(ClaimsOf(constructor.Value.GetProperty("implements")).Order());

        vocabulary.Constructors.Single(c => c.Name == "Collection").Implements.Should().Equal(WorkflowCapability.Iterable);
        vocabulary.Types.Single(t => t.Name == "Int").Implements.Should().Equal(
            WorkflowCapability.Numeric, WorkflowCapability.Comparable, WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable);
        vocabulary.Types.Single(t => t.Name == "Json").Implements.Should().Equal(WorkflowCapability.JsonSerializable);
    }

    [Fact]
    public void A_term_holding_a_type_parameter_has_no_membership_to_ask()
    {
        var parameter = PortTerm.Parse("Collection<$T>", PortTermGrammar.Declaration);

        ((Action)(() => RegisteredHosts().Implements(parameter, WorkflowCapability.Equatable)))
            .Should().Throw<ArgumentException>().WithMessage("*closed terms*");
    }
}
