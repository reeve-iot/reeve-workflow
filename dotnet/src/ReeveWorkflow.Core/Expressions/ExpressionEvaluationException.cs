using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>Thrown when a typed expression fails at run time: a read with no value or a value off its term, an overflow,
/// a division by zero.</summary>
public sealed class ExpressionEvaluationException : Exception, IWorkflowCodedException
{
    public int Position { get; }
    public string? MissingPath { get; }
    public string? Suggestion { get; }
    public string? Category { get; }

    /// <summary>The code of an evaluation failure: always <c>expression-runtime-error</c>. <see cref="Category"/> is set only for the
    /// failures that have one (<c>overflow</c>, <c>division-by-zero</c>, <c>narrowing</c>, <c>value-absent</c>, <c>too-large</c>).</summary>
    public string Code => TypedExpressionEvaluator.FailureCode;

    public ExpressionEvaluationException(
        string message,
        int position,
        string? missingPath = null,
        string? suggestion = null,
        string? category = null)
        : base(message)
    {
        Position = position;
        MissingPath = missingPath;
        Suggestion = suggestion;
        Category = category;
    }

    public ExpressionEvaluationException(string message, int position, Exception inner)
        : this(message, position, inner, null)
    {
    }

    internal ExpressionEvaluationException(string message, int position, Exception inner, string? category)
        : base(message, inner)
    {
        Position = position;
        MissingPath = null;
        Suggestion = null;
        Category = category;
    }
}
