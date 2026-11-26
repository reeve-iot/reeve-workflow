using System.Reflection;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ExpressionTyperHostTypesTests
{
    private sealed record Pallet(string Id);

    private sealed class Plain;

    private static readonly WorkflowTypeVocabulary Vocabulary = new WorkflowTypeRegistry(
        [new WorkflowTypeRecord("Pallet", typeof(Pallet)) { Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Equatable } },
         new WorkflowTypeRecord("Plain", typeof(Plain))]).Current;

    private static readonly Dictionary<string, string> Terms = new()
    {
        ["p"] = "Pallet", ["q"] = "Pallet?", ["x"] = "Plain", ["xs"] = "Collection<Pallet>", ["n"] = "Int", ["m"] = "Number?",
    };

    private static ExpressionTypingResult Type(string expression) => ExpressionTyper.Type(
        expression, new ExpressionTypingScope.Builder().Vocabulary(Vocabulary).Namespace("w", 1, new Check()).Build());

    [Theory]
    [InlineData("true ? ${w.p} : ${w.p}", "Pallet")]
    [InlineData("true ? ${w.p} : ${w.q}", "Pallet?")]
    [InlineData("true ? ${w.q} : ${w.p}", "Pallet?")]
    [InlineData("true ? ${w.p} : null", "Pallet?")]
    [InlineData("true ? ${w.n} : ${w.m}", "Number?")]
    [InlineData("true ? 1 : 2.5", "Number")]
    [InlineData("true ? ${w.xs} : ${w.xs}", "Collection<Pallet>")]
    public void A_conditional_yields_the_least_upper_bound_of_its_branches(string expression, string term)
    {
        var result = Type(expression);

        result.Refusals.Should().BeEmpty(expression);
        result.Tree!.Term.ToString().Should().Be(term);
    }

    [Theory]
    [InlineData("true ? ${w.p} : ${w.n}")]
    [InlineData("true ? ${w.p} : \"a\"")]
    public void A_conditional_with_no_least_upper_bound_is_a_type_mismatch(string expression)
    {
        var result = Type(expression);

        result.Tree.Should().BeNull();
        result.Refusal!.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
        result.Refusal.Message.Should().Contain("least upper bound");
    }

    [Theory]
    [InlineData("${w.p} == ${w.p}", true)]
    [InlineData("${w.p} != ${w.q}", true)]
    [InlineData("${w.q} == null", true)]
    [InlineData("${w.xs} == ${w.xs}", true)]
    [InlineData("${w.x} == ${w.x}", false)]
    [InlineData("${w.p} == ${w.x}", false)]
    [InlineData("${w.p} == null", false)]
    public void A_host_term_is_compared_only_when_it_is_equatable_and_the_terms_are_equal(string expression, bool typed)
    {
        var result = Type(expression);

        result.IsTyped.Should().Be(typed, expression);
        if (!typed)
            result.Refusal!.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
    }

    [Fact]
    public void The_evaluator_takes_a_typed_tree_and_a_scope_and_nothing_else()
    {
        var evaluate = typeof(TypedExpressionEvaluator).GetMethods(BindingFlags.Public | BindingFlags.Static).Should().ContainSingle(m => m.Name == "Evaluate").Which;

        evaluate.GetParameters().Select(p => p.ParameterType).Should().Equal(typeof(TypedExpression), typeof(ExpressionEvaluationScope));
        typeof(ExpressionTyper).Assembly.GetType("ReeveWorkflow.Core.Expressions.ExpressionEvaluator").Should().BeNull();
    }

    [Fact]
    public void Only_the_typer_builds_a_typed_tree()
    {
        foreach (var type in new[] { typeof(TypedLiteral), typeof(TypedRead), typeof(TypedConversion), typeof(TypedOperation) })
            type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty(type.Name);
    }

    private sealed class Check : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "w";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            Terms.TryGetValue(segments[0], out var term) ? new(NamespacePathCheck.Yes, term) : NamespacePathCheckAnswer.No;
    }
}
