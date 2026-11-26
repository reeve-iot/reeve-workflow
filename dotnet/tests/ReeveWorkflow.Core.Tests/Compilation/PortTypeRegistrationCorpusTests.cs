using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed partial class PortTypeRegistrationCorpusTests
{
    private class Shipment { }

    private sealed class ReeferShipment : Shipment { }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in Corpus.Value.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_backend_answers_its_column(string name)
    {
        var row = Corpus.Value[name];
        var expected = row.GetProperty("backend");
        switch (row.GetProperty("kind").GetString())
        {
            case "alias":
                Registers(row, () => StartedBy(row).WithAlias(
                    PortValueType.Of(row.GetProperty("alias").GetString()!), PortValueType.Of(row.GetProperty("target").GetString()!)));
                break;
            case "widening":
                Registers(row, () => StartedBy(row).With(
                    PortValueType.Of(row.GetProperty("from").GetString()!),
                    [.. row.GetProperty("widensTo").EnumerateArray().Select(t => PortValueType.Of(t.GetString()!))]));
                break;
            case "declare":
                Registers(row, () => StartedBy(row).Declaring(PortValueType.Of(row.GetProperty("type").GetString()!)));
                break;
            case "assignable":
                WorkflowTypeVocabulary.Builtin.IsAssignable(
                        PortValueType.Of(row.GetProperty("from").GetString()!), PortValueType.Of(row.GetProperty("to").GetString()!))
                    .Should().Be(expected.GetBoolean());
                break;
            case "constructor":
                var registering = TableWith(row);
                Registers(row, () => registering.WithConstructor(RecordOf(row.GetProperty("head").GetString()!, row.GetProperty("allowedArguments"))));
                break;
            case "row":
                var building = () => new WorkflowTypeRegistry(RowRecords(row));
                if (row.GetProperty("backend").GetString() == "accepts")
                    building.Should().NotThrow();
                else
                    building.Should().Throw<WorkflowTypeRegistrationException>().WithMessage(row.GetProperty("backendRefusal").GetString());
                break;
            case "reference":
                Registers(row);
                break;
            case "term":
                var vocabulary = TermVocabulary.Value;
                var refusal = vocabulary.RefusalOf(PortTerm.Parse(row.GetProperty("term").GetString(), PortTermGrammar.Document));
                if (row.GetProperty("backend").GetString() == "accepts")
                    refusal.Should().BeNull();
                else
                    $"{refusal?.Code}: {refusal?.Message}".Should().Be(row.GetProperty("backendRefusal").GetString());
                break;
            case "claim":
            case "literal":
                var refusals = (row.GetProperty("kind").GetString() == "claim" ? ClaimRecordOf(row) : LiteralRecordOf(row)) switch
                {
                    WorkflowTypeConstructorRecord constructor => constructor.Validate(),
                    WorkflowTypeRecord atom => atom.Validate(),
                    _ => throw new InvalidOperationException("claim record"),
                };
                if (expected.GetString() == "accepts")
                    refusals.Should().BeEmpty();
                else
                    refusals.Select(r => $"{r.Code}: {r.Message}").Should().ContainSingle().Which.Should().Match(row.GetProperty("backendRefusal").GetString());
                break;
            default:
                throw new InvalidOperationException($"kind '{row.GetProperty("kind")}'");
        }
    }

    private static void Registers(JsonElement row)
    {
        var registry = new WorkflowTypeRegistry();
        var records = row.GetProperty("records").EnumerateArray().ToList();
        foreach (var earlier in records[..^1])
            registry.Register(earlier.GetProperty("owner").GetString()!, [ReferenceRecordOf(earlier)]).Refusals.Should().BeEmpty();
        var last = records[^1];
        var result = registry.Register(last.GetProperty("owner").GetString()!, [ReferenceRecordOf(last)]);
        if (row.GetProperty("backend").GetString() == "accepts")
            result.Refusals.Should().BeEmpty();
        else
            result.Refusals.Select(r => $"{r.Code} ({r.Record}): {r.Message}").Should().ContainSingle().Which.Should().Match(row.GetProperty("backendRefusal").GetString());
    }

    private static WorkflowTypeRegistrationRecord ReferenceRecordOf(JsonElement record) => record.TryGetProperty("constructorName", out var name)
        ? RecordOf(name.GetString()!, record.GetProperty("allowedArguments"))
        : new WorkflowTypeRecord(record.GetProperty("atom").GetString()!, typeof(string))
        {
            WidensTo = [.. Each(record, "widensTo").Select(t => t.GetString()!)],
        };

    private static void Registers(JsonElement row, Func<PortTypeWideningTable> register)
    {
        if (row.GetProperty("backend").GetString() == "accepts")
            register.Should().NotThrow();
        else
            register.Should().Throw<ArgumentException>().WithMessage(row.GetProperty("backendRefusal").GetString());
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("widening")]
    [InlineData("declare")]
    [InlineData("constructor")]
    [InlineData("term")]
    [InlineData("row")]
    [InlineData("claim")]
    [InlineData("literal")]
    [InlineData("reference")]
    public void Each_registration_kind_has_an_accepted_and_a_refused_row(string kind) =>
        Corpus.Value.Values.Where(r => r.GetProperty("kind").GetString() == kind)
            .Select(r => r.GetProperty("backend").GetString()).Distinct().Order()
            .Should().Equal("accepts", "refuses");

    private static IEnumerable<WorkflowTypeRegistrationRecord> RowRecords(JsonElement row)
    {
        foreach (var constructor in Each(row, "constructors"))
            yield return RecordOf(constructor.GetProperty("name").GetString()!, constructor.GetProperty("allowedArguments"));
        var aliases = Each(row, "declaredAliases").ToList();
        foreach (var atom in Each(row, "declaredAtoms"))
        {
            var name = atom.GetString()!;
            yield return new WorkflowTypeRecord(name, name == "Shipment" ? typeof(Shipment) : typeof(string))
            {
                Aliases = [.. aliases.Where(a => a.GetProperty("target").GetString() == atom.GetString()).Select(a => a.GetProperty("alias").GetString()!)],
            };
        }
        var source = row.GetProperty("from").GetString()!;
        var sourceType = source switch
        {
            "ReeferShipment" => typeof(ReeferShipment),
            "Handle" => typeof(string),
            _ => typeof(string[]),
        };
        yield return new WorkflowTypeRecord(source, sourceType)
        {
            WidensTo = [.. row.GetProperty("widensTo").EnumerateArray().Select(t => t.GetString()!)],
        };
    }

    private static WorkflowTypeConstructorRecord RecordOf(string name, JsonElement allowedArguments) =>
        new(name, typeof(string)) { AllowedArguments = [.. allowedArguments.EnumerateArray().Select(a => a.GetString()!)] };

    private static PortTypeWideningTable TableWith(JsonElement row)
    {
        var table = PortTypeWideningTable.Builtin;
        foreach (var atom in Each(row, "declaredAtoms"))
            table = table.Declaring(PortValueType.Of(atom.GetString()!));
        foreach (var alias in Each(row, "declaredAliases"))
            table = table.WithAlias(PortValueType.Of(alias.GetProperty("alias").GetString()!), PortValueType.Of(alias.GetProperty("target").GetString()!));
        foreach (var widening in Each(row, "declaredWidenings"))
            table = table.With(PortValueType.Of(widening.GetProperty("from").GetString()!),
                [.. widening.GetProperty("widensTo").EnumerateArray().Select(t => PortValueType.Of(t.GetString()!))]);
        return WithConstructors(table, Each(row, "after"));
    }

    private static PortTypeWideningTable StartedBy(JsonElement row) =>
        WithConstructors(PortTypeWideningTable.Builtin, Each(row, "constructors"));

    private static PortTypeWideningTable WithConstructors(PortTypeWideningTable table, IEnumerable<JsonElement> records) =>
        records.Aggregate(table, (t, r) => t.WithConstructor(RecordOf(r.GetProperty("name").GetString()!, r.GetProperty("allowedArguments"))));

    private static IEnumerable<JsonElement> Each(JsonElement row, string property) =>
        row.TryGetProperty(property, out var found) && found.ValueKind == JsonValueKind.Array ? found.EnumerateArray() : [];

    private static readonly Lazy<PortTypeWideningTable> TermVocabulary = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PortTypeRegistrationCorpusPath()));
        var vocabulary = document.RootElement.GetProperty("termVocabulary");
        return WithConstructors(
            Each(vocabulary, "atoms").Aggregate(PortTypeWideningTable.Builtin, (t, a) => t.Declaring(PortValueType.Of(a.GetString()!))),
            Each(vocabulary, "constructors"));
    });

    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Corpus = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PortTypeRegistrationCorpusPath()));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.Clone(), StringComparer.Ordinal);
    });
}
