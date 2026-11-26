using System.Collections;
using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionDifferentialTests
{
    private static readonly WorkflowTypeVocabulary Vocabulary = WorkflowTypeVocabulary.Builtin;
    private static readonly ExpressionVectorScopes Scopes = ExpressionVectorScopes.Load();

    private static readonly IReadOnlyDictionary<string, string[]> Failures = Rows().ToDictionary(r => r.Id, r => r.Failures);

    private static IEnumerable<(string Id, string[] Failures)> Rows()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.ExpressionFunctionsPath()));
        return [.. document.RootElement.GetProperty("rows").EnumerateArray().Select(row => (
            row.GetProperty("id").GetString()!,
            row.GetProperty("failures").EnumerateArray().Select(f => f.GetString()!).ToArray()))];
    }

    [Fact]
    public void Every_value_the_evaluator_answers_is_inside_the_term_the_typer_inferred()
    {
        var typed = 0;
        var evaluated = 0;
        var failed = new List<string>();
        foreach (var expression in ExpressionGrid.Grid())
        {
            var result = ExpressionTyper.Type(expression, Scopes.Typing("workflow"));
            if (result.Tree is not { } tree)
            {
                result.Refusals.Should().NotBeEmpty(expression);
                continue;
            }

            typed++;
            foreach (var node in Nodes(tree))
            {
                if (!Closed(node.Term))
                    failed.Add($"{expression} => the term {node.Term} names a type parameter or Any");
                if (!ReferenceEquals(node, tree) && TryEvaluate(node, out var inner))
                    Grade(expression, node.Term, inner, failed);
            }

            try
            {
                var value = TypedExpressionEvaluator.Evaluate(tree, Scopes.Evaluation("workflow"));
                evaluated++;
                Grade(expression, tree.Term, value, failed);
            }
            catch (ExpressionEvaluationException ex)
            {
                var declared = Overloads(tree).SelectMany(id => Failures[id]).ToHashSet();
                if (ex.Category is null || !declared.Contains(ex.Category))
                    failed.Add($"{expression} => {ex.Category}: {ex.Message}; declared {string.Join(",", declared)}");
            }
        }

        failed.Should().BeEmpty();
        typed.Should().BeGreaterThan(2000);
        evaluated.Should().BeGreaterThan(typed / 2);
    }

    private static void Grade(string expression, PortTerm term, object? value, List<string> failed)
    {
        var spec = Spec.Mismatch(term, value);
        if (spec is not null)
            failed.Add($"{expression} => {value ?? "null"} is not a {term}: {spec}");
        else if (term.Kind != PortTermKind.Null && ExpressionNamespaceValues.Hold(term, value, Vocabulary) is { Ok: false } held)
            failed.Add($"{expression} => the oracle admits {value ?? "null"} in {term} and the gate says '{held.Why}'");
    }

    public static TheoryData<string, string, bool> OffTermSamples() => new()
    {
        { "Collection<String?>", "strings-with-null", true },
        { "Collection<Int>", "ints-as-int32", false },
        { "Collection<Int>", "ints", true },
        { "Collection<Int?>", "nullable-ints", true },
        { "Collection<Int>", "nullable-ints", false },
        { "Int", "int32", true },
        { "Number", "infinity", false },
        { "String", "null", false },
        { "String?", "null", true },
        { "Json", "json-null", false },
        { "Json", "json-object", true },
    };

    [Theory]
    [MemberData(nameof(OffTermSamples))]
    public void The_gate_decides_a_value_off_a_term_and_the_oracle_accepts_what_it_admits(string term, string sample, bool inside)
    {
        object? value = sample switch
        {
            "strings-with-null" => new List<string?> { "a", null },
            "ints-as-int32" => new List<int> { 1 },
            "ints" => new List<long> { 1 },
            "nullable-ints" => new List<long?> { 1, null },
            "int32" => 5,
            "infinity" => double.PositiveInfinity,
            "null" => null,
            "json-null" => JsonDocument.Parse("null").RootElement.Clone(),
            _ => JsonDocument.Parse("{}").RootElement.Clone(),
        };
        var parsed = PortTerm.Parse(term, PortTermGrammar.Document);

        var held = ExpressionNamespaceValues.Hold(parsed, value, Vocabulary);
        held.Ok.Should().Be(inside, "the gate");
        if (held.Ok)
            (Spec.Mismatch(parsed, held.Value) is null).Should().BeTrue("the oracle on the admitted value");
    }

    [Fact]
    public void A_typed_tree_binds_every_operator_and_function_to_a_row_of_the_functions_file()
    {
        foreach (var expression in ExpressionGrid.Grid())
        {
            if (ExpressionTyper.Type(expression, Scopes.Typing("workflow")).Tree is { } tree)
                Overloads(tree).Should().OnlyContain(id => Failures.ContainsKey(id), expression);
        }
    }

    private static bool Closed(PortTerm term) => !term.ContainsParameter && PortTypeNameSites.ReservedName(term.ToString()) is null;

    private static bool TryEvaluate(TypedExpression node, out object? value)
    {
        try
        {
            value = TypedExpressionEvaluator.Evaluate(node, Scopes.Evaluation("workflow"));
            return true;
        }
        catch (ExpressionEvaluationException)
        {
            value = null;
            return false;
        }
    }

    private static IEnumerable<TypedExpression> Nodes(TypedExpression node) =>
        new[] { node }.Concat(Children(node).SelectMany(Nodes));

    private static IEnumerable<string> Overloads(TypedExpression node) =>
        (node is TypedOperation operation ? new[] { operation.Overload } : [])
        .Concat(Children(node).SelectMany(Overloads));

    private static IEnumerable<TypedExpression> Children(TypedExpression node) => node switch
    {
        TypedOperation operation => operation.Operands,
        TypedConversion conversion => [conversion.Operand],
        _ => [],
    };

    private static class Spec
    {
        public static string? Mismatch(PortTerm term, object? value)
        {
            if (term.Kind == PortTermKind.Null)
                return value is null ? null : $"the null term holds {value}";
            if (value is null)
                return term.IsNullable ? null : "null in a non-nullable term";
            var core = term.IsNullable ? term.WithoutNullable() : term;
            if (core.IsCollection)
            {
                var elementClr = Clr(core.Arguments[0]);
                if (!typeof(IReadOnlyList<>).MakeGenericType(elementClr).IsInstanceOfType(value))
                    return $"{value.GetType().Name} is no IReadOnlyList<{elementClr.Name}>";
                var index = 0;
                foreach (var element in (IEnumerable)value)
                {
                    if (Mismatch(core.Arguments[0], element) is { } why)
                        return $"element {index}: {why}";
                    index++;
                }

                return null;
            }

            return core.Name switch
            {
                "Int" => value is long ? null : $"{value.GetType().Name} is no long",
                "Number" => value is double d ? (double.IsFinite(d) ? null : "a Number that is not finite") : $"{value.GetType().Name} is no double",
                "String" => value is string ? null : $"{value.GetType().Name} is no string",
                "Boolean" => value is bool ? null : $"{value.GetType().Name} is no bool",
                "DateTime" => value is DateTimeOffset ? null : $"{value.GetType().Name} is no DateTimeOffset",
                "Json" => value is JsonElement { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? null : "not a JSON value",
                _ => $"the term {core} is not one the grid can hold",
            };
        }

        private static Type Clr(PortTerm term)
        {
            var core = term.IsNullable ? term.WithoutNullable() : term;
            var type = core.IsCollection
                ? typeof(IReadOnlyList<>).MakeGenericType(Clr(core.Arguments[0]))
                : core.Name switch
                {
                    "Int" => typeof(long),
                    "Number" => typeof(double),
                    "String" => typeof(string),
                    "Boolean" => typeof(bool),
                    "DateTime" => typeof(DateTimeOffset),
                    "Json" => typeof(JsonElement),
                    _ => throw new InvalidOperationException($"The term {core} has no CLR type in the grid."),
                };
            return term.IsNullable && type.IsValueType ? typeof(Nullable<>).MakeGenericType(type) : type;
        }
    }
}
