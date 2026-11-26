using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionJsonConversionTests
{
    private sealed record Crate(string Id);

    private sealed record Loose(string Id);

    private sealed record Pair(string Zed, string Alpha);

    private static JsonTypeInfo CamelCase(Type type)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, AllowDuplicateProperties = false, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        return options.GetTypeInfo(type);
    }

    private static readonly WorkflowTypeVocabulary Vocabulary = new WorkflowTypeRegistry(
    [
        new WorkflowTypeRecord("Crate", typeof(Crate))
        {
            Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
            JsonTypeInfo = CamelCase(typeof(Crate)),
        },
        new WorkflowTypeRecord("Pair", typeof(Pair))
        {
            Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
            JsonTypeInfo = CamelCase(typeof(Pair)),
        },
        new WorkflowTypeRecord("Loose", typeof(Loose)),
    ]).Current;

    private static readonly Dictionary<string, (string Term, object? Value)> Paths = new()
    {
        ["c"] = ("Crate", new Crate("c-1")),
        ["none"] = ("Crate?", null),
        ["some"] = ("Crate?", new Crate("c-2")),
        ["p"] = ("Pair", new Pair("z", "a")),
        ["l"] = ("Loose", new Loose("l-1")),
        ["cs"] = ("Collection<Crate>", new List<Crate> { new("a"), new("b") }),
        ["ci"] = ("Collection<Int>", new List<long> { 1, 2 }),
    };

    private static (ExpressionTypingResult Typed, ExpressionEvaluationScope Evaluation) Type(string expression)
    {
        var probe = new Probe();
        var binding = new ExpressionNamespaceBinding(probe, 1, probe);
        var typing = new ExpressionTypingScope.Builder().Vocabulary(Vocabulary).Namespace(binding).Build();
        var evaluation = new ExpressionEvaluationScope.Builder().Vocabulary(Vocabulary).Namespace(binding).Build();
        return (ExpressionTyper.Type(expression, typing), evaluation);
    }

    private static object? Run(string expression)
    {
        var (typed, evaluation) = Type(expression);
        typed.Refusals.Should().BeEmpty(expression);
        return TypedExpressionEvaluator.Evaluate(typed.Tree!, evaluation);
    }

    [Fact]
    public void ToJsonText_writes_a_host_value_with_the_JsonTypeInfo_the_host_registered()
    {
        Run("toJsonText(${w.c})").Should().Be("{\"id\":\"c-1\"}");
        Run("toJsonText(${w.cs})").Should().Be("[{\"id\":\"a\"},{\"id\":\"b\"}]");
        Run("toJsonText(${w.ci})").Should().Be("[1,2]");
    }

    [Fact]
    public void ToJsonText_writes_a_host_record_with_its_keys_in_ordinal_order()
    {
        Run("toJsonText(${w.p})").Should().Be("{\"alpha\":\"a\",\"zed\":\"z\"}");
    }

    [Theory]
    [InlineData("toJsonText(${w.l})", "Loose")]
    [InlineData("toJsonText(${w.none})", "cannot be null")]
    [InlineData("toJsonText(${w.some})", "cannot be null")]
    [InlineData("toJsonText(${w.c}, ${w.c})", "toJsonText")]
    public void ToJsonText_refuses_a_term_that_is_not_JsonSerializable_or_can_be_null(string expression, string fragment)
    {
        var (typed, _) = Type(expression);

        typed.Tree.Should().BeNull();
        typed.Refusal!.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
        typed.Refusal.Message.Should().Contain(fragment);
    }

    [Fact]
    public void Coalesce_over_a_host_term_yields_the_value_when_present_and_the_default_when_not()
    {
        var (typed, _) = Type("${w.none} ?? ${w.c}");

        typed.Tree!.Term.ToString().Should().Be("Crate");
        Run("${w.none} ?? ${w.c}").Should().Be(new Crate("c-1"));
        Run("${w.some} ?? ${w.c}").Should().Be(new Crate("c-2"));
        Run("${w.some} ?? ${w.none}").Should().Be(new Crate("c-2"));
    }

    [Theory]
    [InlineData("${w.none} ?? ${w.l}")]
    [InlineData("${w.c} ?? ${w.c}")]
    public void Coalesce_refuses_a_left_that_cannot_be_null_and_terms_with_no_least_upper_bound(string expression)
    {
        var (typed, _) = Type(expression);

        typed.Tree.Should().BeNull();
        typed.Refusal!.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
        typed.Refusal.Message.Should().Contain("??");
    }

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("[1, 2]", "[1, 2]")]
    [InlineData("  7 ", "7")]
    [InlineData("\"x\"", "\"x\"")]
    [InlineData("true", "true")]
    public void ParseJson_gives_the_value_the_text_holds(string text, string raw)
    {
        var value = ExpressionOperations.ParseJson(text);

        value.Should().BeOfType<JsonElement>().Which.GetRawText().Should().Be(raw);
    }

    [Fact]
    public void ParseJson_gives_null_for_the_text_null()
    {
        ExpressionOperations.ParseJson("null").Should().BeNull();
        ExpressionOperations.ParseJson(" null\n").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("{'a':1}")]
    [InlineData("[1,]")]
    [InlineData("1 2")]
    [InlineData("undefined")]
    public void ParseJson_fails_with_category_json_parse_on_text_that_is_not_one_JSON_value(string text)
    {
        var failure = FluentActions.Invoking(() => ExpressionOperations.ParseJson(text))
            .Should().Throw<ExpressionOperationException>().Which;

        failure.Category.Should().Be("json-parse");
    }

    [Fact]
    public void ParseJson_refuses_text_with_an_unpaired_surrogate_instead_of_repairing_it()
    {
        var text = "\"" + '\ud800' + "\"";

        FluentActions.Invoking(() => ExpressionOperations.ParseJson(text))
            .Should().Throw<ExpressionOperationException>().Which.Category.Should().Be("json-parse");
    }

    [Fact]
    public void ParseJson_is_refused_as_a_whole_expression_because_a_Json_value_is_read_only_by_a_projection()
    {
        var (typed, _) = Type("parseJson(\"{}\")");

        typed.Tree.Should().BeNull();
        typed.Refusal!.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
        typed.Refusal.Message.Should().Contain("Json");
    }

    private sealed class Probe : IExpressionNamespaceResolver, IExpressionNamespaceStaticCheck
    {
        public string Namespace => "w";

        public ResolveResult Resolve(string[] segments) => ResolveResult.Resolved(Paths[segments[0]].Value);

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            Paths.TryGetValue(segments[0], out var path) ? new(NamespacePathCheck.Yes, path.Term) : NamespacePathCheckAnswer.No;

        public NamespacePathCheckAnswer Check(IReadOnlyList<string> segments) => CheckWithKeyType(segments);
    }
}
