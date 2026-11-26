using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class TypedEvaluatorHostValueTests
{
    private static ExpressionEvaluationException Failure(string term, Func<object?> answer, string expression = "${host.k}", bool helper = false)
    {
        var typing = new ExpressionTypingScope.Builder();
        var evaluation = new ExpressionEvaluationScope.Builder();
        var probe = new Probe(term, answer);
        if (helper)
        {
            typing.Helper(new Dictionary<HelperPath, NamespacePathCheckAnswer> { [new HelperPath(["k"])] = new(NamespacePathCheck.Yes, term) });
            evaluation.Helper(new Dictionary<HelperPath, object?>());
        }
        else
        {
            var binding = new ExpressionNamespaceBinding(probe, 1, probe);
            typing.Namespace(binding);
            evaluation.Namespace(binding);
        }

        var tree = ExpressionTyper.Type(expression, typing.Build()).Tree
            ?? throw new InvalidOperationException("The expression has no term.");
        return FluentActions.Invoking(() => TypedExpressionEvaluator.Evaluate(tree, evaluation.Build()))
            .Should().Throw<ExpressionEvaluationException>().Which;
    }

    private static object? Value(string term, Func<object?> answer, string expression = "${host.k}")
    {
        var typing = new ExpressionTypingScope.Builder();
        var evaluation = new ExpressionEvaluationScope.Builder();
        var probe = new Probe(term, answer);
        var binding = new ExpressionNamespaceBinding(probe, 1, probe);
        typing.Namespace(binding);
        evaluation.Namespace(binding);
        return TypedExpressionEvaluator.Evaluate(ExpressionTyper.Type(expression, typing.Build()).Tree!, evaluation.Build());
    }

    [Fact]
    public void A_null_element_of_a_collection_of_a_non_nullable_term_is_not_walked()
    {
        Value("Collection<String>", () => new List<string?> { "a", null }).Should().BeAssignableTo<IReadOnlyList<string?>>()
            .Which.Should().Equal("a", null);
    }

    [Fact]
    public void A_non_finite_number_is_off_the_term_number_and_a_list_of_them_is_not_walked()
    {
        var failure = Failure("Number", () => double.NaN);
        failure.Category.Should().Be("narrowing");
        failure.MissingPath.Should().Be("host.k");
        failure.Message.Should().Contain("${host.k}").And.Contain("Number");
        Failure("Number", () => double.PositiveInfinity).Category.Should().Be("narrowing");
        Value("Collection<Number>", () => new List<double> { 1.5, double.NaN }).Should().BeAssignableTo<IReadOnlyList<double>>();
        Failure("Collection<Number>", () => new List<object?> { 1.5 }).Category.Should().Be("narrowing");
    }

    [Fact]
    public void A_null_element_of_a_collection_of_a_nullable_term_is_in_it()
    {
        Value("Collection<Int?>", () => new List<long?> { 1, null }).Should().BeAssignableTo<IReadOnlyList<long?>>().Which.Should().Equal(1, null);
        Value("Collection<String?>", () => new List<string?> { null }).Should().BeAssignableTo<IReadOnlyList<string?>>().Which.Should().Equal((string?)null);
    }

    [Fact]
    public void A_resolver_that_throws_fails_the_read_and_keeps_the_exception()
    {
        var failure = Failure("Int", () => throw new InvalidOperationException("the device is gone"));

        failure.Category.Should().BeNull();
        failure.Code.Should().Be("expression-runtime-error");
        failure.Message.Should().Contain("${host.k}").And.Contain("the device is gone");
        failure.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void A_helper_missing_from_the_read_snapshot_fails_with_value_absent()
    {
        var failure = Failure("Int", () => throw new InvalidOperationException("no helper"), "${helper.k}", helper: true);

        failure.Category.Should().Be("value-absent");
        failure.Code.Should().Be("expression-runtime-error");
    }

    [Fact]
    public void A_resolver_that_throws_an_evaluation_exception_is_wrapped_too_and_the_code_is_never_null()
    {
        var thrown = new ExpressionEvaluationException("the host's own", 0);

        var failure = Failure("Int", () => throw thrown);

        failure.Should().NotBeSameAs(thrown);
        failure.InnerException.Should().BeSameAs(thrown);
        failure.Code.Should().Be("expression-runtime-error");
        thrown.Code.Should().Be("expression-runtime-error");
    }

    [Fact]
    public void The_code_of_an_evaluation_failure_does_not_depend_on_a_category()
    {
        new ExpressionEvaluationException("x", 0).Code.Should().Be("expression-runtime-error");
        new ExpressionEvaluationException("x", 0, category: "overflow").Code.Should().Be("expression-runtime-error");
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void A_json_null_or_undefined_answer_reads_as_null()
    {
        const string read = "jsonPath(${host.k}, \"$\", \"Int?\")";

        Value("Json?", () => Element("null"), read).Should().BeNull();
        Value("Json?", () => default(JsonElement), read).Should().BeNull();
        Value("Json", () => Element("null"), read).Should().BeNull();
        Value("Json?", () => Element("""{"a":1}"""), "jsonPath(${host.k}, \"$.a\", \"Int\")").Should().Be(1L);
    }

    [Fact]
    public void A_non_nullable_json_path_over_a_json_null_fails_with_value_absent()
    {
        var failure = Failure("Json", () => Element("null"), "jsonPath(${host.k}, \"$\", \"Int\")");

        failure.Category.Should().Be("value-absent");
    }

    [Fact]
    public void A_path_the_check_accepted_and_the_resolver_cannot_find_fails_with_value_absent()
    {
        var gone = new Gone();
        var hostBinding = new ExpressionNamespaceBinding(gone, 1, gone);
        var typing = new ExpressionTypingScope.Builder().Namespace(hostBinding)
            .Helper(new Dictionary<HelperPath, NamespacePathCheckAnswer> { [new HelperPath(["k"])] = new(NamespacePathCheck.Yes, "Int") }).Build();
        var evaluation = new ExpressionEvaluationScope.Builder().Namespace(hostBinding).Helper(new Dictionary<HelperPath, object?>()).Build();

        foreach (var (expression, path) in new[] { ("${host.k}", "host.k"), ("${helper.k}", "helper.k") })
        {
            var tree = ExpressionTyper.Type(expression, typing).Tree!;
            var failure = FluentActions.Invoking(() => TypedExpressionEvaluator.Evaluate(tree, evaluation))
                .Should().Throw<ExpressionEvaluationException>().Which;

            failure.Category.Should().Be("value-absent", expression);
            failure.MissingPath.Should().Be(path, expression);
        }
    }

    [Fact]
    public void A_cancellation_inside_a_resolver_propagates_and_is_not_a_failed_read()
    {
        FluentActions.Invoking(() => Value("Int", () => throw new OperationCanceledException()))
            .Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void An_Int_answer_keeps_its_term_and_compares_with_a_Number()
    {
        Value("Int", () => 5L, "${host.k} + 1").Should().Be(6L);
        Value("Int", () => 5L, "${host.k} == 5.0").Should().Be(true);
        Value("Int", () => 5L, "${host.k} == 5.5").Should().Be(false);
    }

    private sealed record Pallet(string Id);

    [Fact]
    public void A_run_holds_a_value_to_the_vocabulary_the_compile_read_whatever_else_is_registered()
    {
        var compiled = new WorkflowTypeRegistry([new WorkflowTypeRecord("Pallet", typeof(Pallet))]);
        var other = new WorkflowTypeRegistry();

        ExpressionNamespaceValues.VocabularyOf(new ServiceCollection().BuildServiceProvider()).Should().BeNull();
        ExpressionNamespaceValues.VocabularyOf(new ServiceCollection()
                .AddSingleton(new CompilationOptions { TypeRegistry = compiled }).BuildServiceProvider())
            .Should().BeSameAs(compiled.Current);
        ExpressionNamespaceValues.VocabularyOf(new ServiceCollection()
                .AddSingleton<IWorkflowTypeRegistry>(other)
                .AddSingleton(new CompilationOptions { TypeRegistry = compiled }).BuildServiceProvider())
            .Should().BeSameAs(compiled.Current);
        ExpressionNamespaceValues.VocabularyOf(new ServiceCollection()
                .AddSingleton<IWorkflowTypeRegistry>(other).BuildServiceProvider())
            .Should().BeSameAs(other.Current);
    }

    private sealed class Gone : IExpressionNamespaceResolver, IExpressionNamespaceStaticCheck, IHelperResolver
    {
        public string Namespace => "host";

        public ResolveResult Resolve(string[] segments) => ResolveResult.Failed();

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "Int");

        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(paths.ToDictionary(p => p, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Int")));
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
    }

    private sealed class Probe(string term, Func<object?> answer) : IExpressionNamespaceResolver, IExpressionNamespaceStaticCheck, IHelperResolver
    {
        public string Namespace => "host";

        public ResolveResult Resolve(string[] segments) => ResolveResult.Resolved(answer());

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, term);

        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(paths.ToDictionary(p => p, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, term)));
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(paths.ToDictionary(p => p, _ => answer()));
    }
}
