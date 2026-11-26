using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionTypingContractTests
{
    private static readonly ExpressionVectorScopes Scopes = ExpressionVectorScopes.Load();

    public static TheoryData<string> Ids()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Vectors())
            data.Add(vector.GetProperty("id").GetString()!);
        return data;
    }

    internal static List<JsonElement> Vectors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.ExpressionTypingPath()));
        return [.. document.RootElement.GetProperty("vectors").EnumerateArray().Select(v => v.Clone())];
    }

    [Fact]
    public void Every_vector_has_an_owner_this_test_grades_and_a_unique_id()
    {
        var vectors = Vectors();
        vectors.Select(v => v.GetProperty("owner").GetString()).Distinct().Should().BeEquivalentTo(["dbc.10", "dbc.10s", "dbc.9", "dbc.33", "4md.5", "4md.6"]);
        vectors.Select(v => v.GetProperty("id").GetString()).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void A_vector_is_typed_and_evaluated_as_the_file_says(string id)
    {
        var vector = Vectors().Single(v => v.GetProperty("id").GetString() == id);
        var scope = vector.GetProperty("scope").GetString()!;
        var result = ExpressionTyper.Type(vector.GetProperty("expression").GetString()!, Scopes.Typing(scope));

        if (vector.TryGetProperty("refusal", out var refusal))
        {
            GradeRefusal(vector, result, refusal.GetString()!);
            return;
        }

        result.Refusals.Should().BeEmpty(id);
        var tree = result.Tree!;
        tree.Term.ToString().Should().Be(vector.GetProperty("term").GetString(), id);
        if (vector.TryGetProperty("tree", out var shape))
            Shape(tree).Should().Equal(shape.EnumerateArray().Select(e => e.GetString()), id);

        if (vector.TryGetProperty("failure", out var failure))
        {
            var thrown = new Func<object?>(() => TypedExpressionEvaluator.Evaluate(tree, Scopes.Evaluation(scope)))
                .Should().Throw<ExpressionEvaluationException>(id).Which;
            thrown.Category.Should().Be(failure.GetString(), id);
            thrown.Code.Should().Be(TypedExpressionEvaluator.FailureCode);
        }
        else if (vector.TryGetProperty("value", out var value))
        {
            var actual = TypedExpressionEvaluator.Evaluate(tree, Scopes.Evaluation(scope));
            var expected = tree.Term.Kind == PortTermKind.Null ? null : ExpressionVectorScopes.Clr(tree.Term, value);
            TermValues.AreEqual(actual, expected).Should().BeTrue($"{id}: {actual ?? "null"} vs {expected ?? "null"}");
        }
    }

    [Fact]
    public void Every_overload_of_the_functions_file_occurs_in_a_vectors_tree()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vector in Vectors().Where(v => v.TryGetProperty("tree", out _)))
        {
            foreach (var entry in vector.GetProperty("tree").EnumerateArray())
                used.Add(entry.GetString()!);
        }

        ExpressionSignatures.All.Select(s => s.Id).Where(id => !used.Contains(id)).Should().BeEmpty();
    }

    [Fact]
    public void An_index_into_a_collection_is_a_field_access_refusal_naming_the_collection()
    {
        var result = ExpressionTyper.Type("${previous.n.ci[0]}", Scopes.Typing("workflow"));

        result.Tree.Should().BeNull();
        result.Refusal!.RuleId.Should().Be(CompilationRules.ExpressionFieldAccessInvalid);
        result.Refusal.Message.Should().Contain("Collection<Int>");
    }

    private static void GradeRefusal(JsonElement vector, ExpressionTypingResult result, string rule)
    {
        var id = vector.GetProperty("id").GetString();
        result.Tree.Should().BeNull(id);
        result.IsTyped.Should().BeFalse(id);
        result.Refusals.Should().NotBeEmpty(id);
        result.Refusal!.RuleId.Should().Be(rule, $"{id}: {result.Refusal.Message}");
        if (vector.TryGetProperty("messageContains", out var fragment))
            result.Refusal.Message.Should().Contain(fragment.GetString()!, id);
        if (vector.TryGetProperty("position", out var position))
            result.Refusal.Position.Should().Be(position.GetInt32(), id);
        if (vector.TryGetProperty("refusalCount", out var count))
            result.Refusals.Should().HaveCount(count.GetInt32(), id);
    }

    internal static List<string> Shape(TypedExpression node)
    {
        var shape = new List<string>();
        Walk(node, shape);
        return shape;
    }

    private static void Walk(TypedExpression node, List<string> shape)
    {
        switch (node)
        {
            case TypedOperation operation:
                shape.Add(operation.Overload);
                foreach (var operand in operation.Operands)
                    Walk(operand, shape);
                break;
            case TypedProjection projection:
                shape.Add(projection.Overload);
                Walk(projection.Subject, shape);
                break;
            case TypedConversion conversion:
                shape.Add(conversion.Kind switch
                {
                    TypedConversionKind.Widen => "widen",
                    TypedConversionKind.ToText => "to-text",
                    _ => "convert",
                });
                Walk(conversion.Operand, shape);
                break;
        }
    }
}
