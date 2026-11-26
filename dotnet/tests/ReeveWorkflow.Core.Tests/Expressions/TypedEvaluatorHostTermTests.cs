using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class TypedEvaluatorHostTermTests
{
    private sealed record Crate(string Id);

    private sealed class WritesNull;

    private sealed class WritesBadly;

    private sealed class NullWriter : JsonConverter<WritesNull>
    {
        public override WritesNull Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();

        public override void Write(Utf8JsonWriter writer, WritesNull value, JsonSerializerOptions options) => writer.WriteNullValue();
    }

    private sealed class BadWriter : JsonConverter<WritesBadly>
    {
        public override WritesBadly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new();

        public override void Write(Utf8JsonWriter writer, WritesBadly value, JsonSerializerOptions options) =>
            throw new InvalidOperationException("cannot write");
    }

    private static WorkflowTypeRecord Serializable(string name, Type type)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowDuplicateProperties = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new NullWriter(), new BadWriter() },
        };
        return new WorkflowTypeRecord(name, type)
        {
            Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
            JsonTypeInfo = options.GetTypeInfo(type),
        };
    }

    private static readonly WorkflowTypeVocabulary Vocabulary = new WorkflowTypeRegistry(
        [Serializable("Crate", typeof(Crate)), Serializable("Nulls", typeof(WritesNull)), Serializable("Bad", typeof(WritesBadly))]).Current;

    private static readonly Dictionary<string, (string Term, object? Value)> Paths = new()
    {
        ["c"] = ("Crate", new Crate("c-1")),
        ["none"] = ("Crate?", null),
        ["some"] = ("Crate?", new Crate("c-2")),
        ["nulls"] = ("Nulls", new WritesNull()),
        ["bad"] = ("Bad", new WritesBadly()),
        ["cs"] = ("Collection<Crate>", new List<Crate> { new("a"), new("b") }),
    };

    private static (TypedExpression Tree, ExpressionEvaluationScope Scope) Setup(string expression, WorkflowTypeVocabulary evaluationVocabulary)
    {
        var host = new Host();
        var binding = new ExpressionNamespaceBinding(host, 1, host);
        var typing = new ExpressionTypingScope.Builder().Vocabulary(Vocabulary).Namespace(binding).Build();
        var typed = ExpressionTyper.Type(expression, typing);
        typed.Refusals.Should().BeEmpty(expression);
        return (typed.Tree!, new ExpressionEvaluationScope.Builder().Vocabulary(evaluationVocabulary).Namespace(binding).Build());
    }

    [Fact]
    public void A_value_whose_serializer_writes_json_null_breaks_the_claim_of_the_term_and_fails_naming_it()
    {
        var (tree, scope) = Setup("toJsonText(${w.nulls})", Vocabulary);

        FluentActions.Invoking(() => TypedExpressionEvaluator.Evaluate(tree, scope))
            .Should().Throw<ExpressionEvaluationException>().WithMessage("a value of 'Nulls' writes JSON null*");
    }

    [Fact]
    public void A_serializer_that_throws_fails_the_expression_and_keeps_its_exception()
    {
        var (tree, scope) = Setup("toJsonText(${w.bad})", Vocabulary);

        var failure = FluentActions.Invoking(() => TypedExpressionEvaluator.Evaluate(tree, scope))
            .Should().Throw<ExpressionEvaluationException>().Which;

        failure.Message.Should().Contain("writing a value of 'Bad' as JSON threw");
        failure.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void The_vocabulary_of_the_scope_that_evaluates_holds_the_value_and_not_the_one_the_tree_was_typed_in()
    {
        var (tree, scope) = Setup("${w.c}", WorkflowTypeVocabulary.Builtin);
        FluentActions.Invoking(() => TypedExpressionEvaluator.Evaluate(tree, scope))
            .Should().Throw<ExpressionEvaluationException>().WithMessage("*has no CLR type*");
        TypedExpressionEvaluator.Evaluate(tree, Setup("${w.c}", Vocabulary).Scope).Should().Be(new Crate("c-1"));
    }

    private sealed class Host : IExpressionNamespaceResolver, IExpressionNamespaceStaticCheck
    {
        public string Namespace => "w";

        public ResolveResult Resolve(string[] segments) => ResolveResult.Resolved(Paths[segments[0]].Value);

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            Paths.TryGetValue(segments[0], out var path) ? new(NamespacePathCheck.Yes, path.Term) : NamespacePathCheckAnswer.No;
    }
}
