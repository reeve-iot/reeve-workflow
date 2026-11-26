using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ValueGateSitesTests
{
    private static readonly WorkflowTypeVocabulary Vocabulary = WorkflowTypeVocabulary.Builtin;

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static TheoryData<string, string, object?, bool> Rows() => new()
    {
        { "Null", "String", null, false },
        { "Null", "String?", null, true },
        { "Scalar normalisation", "Int", 7, true },
        { "Scalar normalisation", "Number", 1.5f, true },
        { "CLR type", "Number", 5L, false },
        { "CLR type", "Int", 5.0d, false },
        { "CLR type", "Collection<Int>", new List<long> { 1 }, true },
        { "CLR type", "Collection<Int>", new List<int> { 1 }, false },
        { "CLR type", "Collection<Int>", new List<long?> { 1 }, false },
        { "CLR type", "Collection<Int?>", new List<long?> { 1, null }, true },
        { "CLR type: a collection is not walked", "Collection<String>", new List<string?> { "a", null }, true },
        { "Number is a finite double", "Number", 2.5, true },
        { "Number is a finite double", "Number", double.NaN, false },
        { "Number is a finite double", "Number", double.PositiveInfinity, false },
        { "Number is a finite double", "Number?", double.NegativeInfinity, false },
        { "Number is a finite double", "Number", float.NaN, false },
        { "CLR type: a collection is not walked", "Collection<Number>", new List<double> { 1.5, double.NaN }, true },
        { "JSON kind", "Json", Element("""{"a":null}"""), true },
        { "JSON kind", "Json", Element("3"), true },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void The_gate_answers_each_row_of_the_table(string row, string term, object? value, bool admitted)
    {
        var closed = Vocabulary.ClosedTypeOf(PortTerm.Parse(term, PortTermGrammar.Document))!;

        (ValueGate.Admit(closed, value, out _) is null).Should().Be(admitted, row);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void A_resolvers_value_gets_the_gates_answer(string row, string term, object? value, bool admitted)
    {
        var held = ExpressionNamespaceValues.Hold(PortTerm.Parse(term, PortTermGrammar.Document), value, Vocabulary);

        held.Ok.Should().Be(admitted, row);
        (held.Why is null).Should().Be(admitted, row);
    }

    [Fact]
    public void A_JSON_start_argument_with_a_number_out_of_double_range_in_a_Number_collection_is_refused()
    {
        var signature = new CanvasSignature([new PortShape("P", PortValueType.Of("Collection<Number>"), true)], []);

        var outcome = WorkflowStartArguments.Check(signature, new Dictionary<string, object?> { ["P"] = Element("[1.5, 1e999]") }, Vocabulary);

        outcome.Findings.Should().ContainSingle().Which.Code.Should().Be(WorkflowStartArgumentCodes.TypeMismatch);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void An_in_process_start_argument_gets_the_gates_answer(string row, string term, object? value, bool admitted)
    {
        var signature = new CanvasSignature([new PortShape("P", PortValueType.Of(term), true)], []);

        var outcome = WorkflowStartArguments.Check(signature, new Dictionary<string, object?> { ["P"] = value }, Vocabulary);

        outcome.Findings.Count.Should().Be(admitted ? 0 : 1, row);
        if (!admitted)
            outcome.Findings.Single().Code.Should().Be(WorkflowStartArgumentCodes.TypeMismatch, row);
    }
}
