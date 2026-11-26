using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>Evaluates a typed tree. The only input is a tree <see cref="ExpressionTyper"/> built, so no value is ever read
/// as another term: an <c>Int</c> is a <see cref="long"/>, a <c>Number</c> a finite <see cref="double"/>, a nullable term a null or its
/// value. A failure throws <see cref="ExpressionEvaluationException"/> whose <c>Category</c> is <c>overflow</c>, <c>division-by-zero</c>,
/// <c>narrowing</c>, <c>value-absent</c> or <c>too-large</c> for the failures that have one, and none for a throw of a host's resolver or helper; the code of every
/// failure is <c>expression-runtime-error</c>.</summary>
public static class TypedExpressionEvaluator
{
    /// <summary>The recursion bound, comfortably above what the parser admits and below the native stack.</summary>
    public const int MaxEvaluationDepth = ExpressionParser.MaxNodeCount;

    /// <summary>The code every evaluation failure carries.</summary>
    public const string FailureCode = Abstractions.WorkflowErrorCodes.ExpressionRuntimeError;

    /// <summary>The value of <paramref name="tree"/>, in the CLR type its term has in the scope's vocabulary.</summary>
    /// <exception cref="ExpressionEvaluationException">A run-time failure. Its <see cref="ExpressionEvaluationException.Category"/> is set for the failures that have one and is null for a host resolver or helper throw, the depth limit and an unknown <c>now</c> field.</exception>
    public static object? Evaluate(TypedExpression tree, ExpressionEvaluationScope scope)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(scope);
        return TypedExpressionCompiler.For(tree).Evaluate(scope);
    }
}
