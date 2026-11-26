using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionFunctionsContractTests
{
    private sealed record Row(
        string Id, string Kind, string Name, string[] Parameters, string Result, string[] Failures, bool Variadic, string? Capability);

    private static List<Row> Rows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.ExpressionFunctionsPath()));
        return [.. document.RootElement.GetProperty("rows").EnumerateArray().Select(row => new Row(
            row.GetProperty("id").GetString()!,
            row.GetProperty("kind").GetString()!,
            row.GetProperty("name").GetString()!,
            [.. row.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()!)],
            row.GetProperty("result").GetString()!,
            [.. row.GetProperty("failures").EnumerateArray().Select(p => p.GetString()!)],
            row.TryGetProperty("variadic", out var variadic) && variadic.GetBoolean(),
            row.TryGetProperty("typeParameters", out var type) ? type.GetProperty("T").GetString() : null))];
    }

    [Fact]
    public void Row_ids_are_unique_and_every_failure_is_a_category_the_evaluator_throws()
    {
        var rows = Rows();
        rows.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        rows.SelectMany(r => r.Failures).Distinct().Should().BeSubsetOf(["overflow", "division-by-zero", "json-parse", "value-absent", "narrowing", "too-large"]);
        rows.Select(r => r.Kind).Distinct().Should().BeSubsetOf(["operator", "function"]);
    }

    [Fact]
    public void The_file_the_signature_table_and_the_implementations_hold_the_same_overloads()
    {
        var rows = Rows();
        var table = ExpressionSignatures.All;

        table.Select(s => s.Id).Should().Equal(rows.Select(r => r.Id));
        TypedExpressionCompiler.BoundOverloads.Concat(["json-path", "json-path-text"]).Should().BeEquivalentTo(rows.Select(r => r.Id));
        foreach (var (row, signature) in rows.Zip(table))
        {
            signature.Kind.ToString().ToLowerInvariant().Should().Be(row.Kind, row.Id);
            signature.Name.Should().Be(row.Name, row.Id);
            signature.Parameters.Should().Equal(row.Parameters, row.Id);
            signature.Result.Should().Be(row.Result, row.Id);
            signature.Failures.Should().Equal(row.Failures, row.Id);
            signature.Variadic.Should().Be(row.Variadic, row.Id);
            signature.Capability.Should().Be(row.Capability, row.Id);
        }
    }
}
