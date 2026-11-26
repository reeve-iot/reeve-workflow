using BenchmarkDotNet.Attributes;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Benchmarks;

public partial class ExecutorBenchmarks
{
    private const string TypedTemplateText = "${parameter.threshold} > 10 && ${parameter.mode} == \"auto\"";
    private const string ColdExpression = "${parameter.number} + 5";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private ExpressionTypingScope? typingScope;
    private ExpressionEvaluationScope? evaluationScope;
    private TypedExpression? typedTemplate;

    private void SetupExpressions()
    {
        var parameters = new Dictionary<string, object?> { ["number"] = 42.0, ["threshold"] = 20.0, ["mode"] = "manual" };
        var terms = new Dictionary<string, PortTerm>
        {
            ["number"] = PortTerm.Atom("Number"), ["threshold"] = PortTerm.Atom("Number"), ["mode"] = PortTerm.Atom("String")
        };
        typingScope = new ExpressionTypingScope.Builder()
            .Parameters(name => terms.TryGetValue(name, out var term) ? ReadLookup.Typed(term) : ReadLookup.Missing("no such parameter"))
            .Build();
        evaluationScope = new ExpressionEvaluationScope.Builder()
            .Now(Now, Now)
            .Parameters(name => parameters.TryGetValue(name, out var value) ? (true, value) : (false, null))
            .Build();

        typedTemplate = TypeOrThrow(ExpressionParser.Parse(TypedTemplateText));
        var result = TypedExpressionEvaluator.Evaluate(typedTemplate, evaluationScope);
        Require(result is false, $"the typed template must evaluate to false, got {result ?? "null"}");

        Require(!ReferenceEquals(ExpressionParser.Parse(ColdExpression), ExpressionParser.Parse(ColdExpression)),
            "ExpressionParser.Parse must give a fresh tree each time, or the parse benchmarks measure the cache");
        var sum = ExpressionCold();
        Require(sum is 47.0, $"the cold expression must evaluate to 47, got {sum ?? "null"}");
    }

    private TypedExpression TypeOrThrow(ExpressionNode ast)
    {
        var typed = ExpressionTyper.Type(ast, typingScope!);
        return typed.Tree ?? throw new InvalidOperationException(
            "Benchmark setup assertion failed: the expression did not type: " + typed.Refusal?.Message);
    }

    [Benchmark(Description = "Scenario 5a: Expression parse (fresh)")]
    public ExpressionNode ExpressionParseFresh() => ExpressionParser.Parse("${parameter.number} + 1");

    [Benchmark(Description = "Scenario 5b: Expression evaluate (tree typed in setup, both variables resolve)")]
    public object? ExpressionEvaluateTyped() => TypedExpressionEvaluator.Evaluate(typedTemplate!, evaluationScope!);

    [Benchmark(Description = "Scenario 5c: Expression parse + type + evaluate (cold, no parse cache)")]
    public object? ExpressionCold() =>
        TypedExpressionEvaluator.Evaluate(TypeOrThrow(ExpressionParser.Parse(ColdExpression)), evaluationScope!);
}
