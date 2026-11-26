namespace ReeveWorkflow.Core.Expressions;

/// <summary>Why an expression has no term: the compiler rule it breaks, and where.</summary>
/// <param name="RuleId">A <c>CompilationRules</c> id: <c>expression-syntax</c>, <c>expression-type-mismatch</c>,
/// <c>expression-out-of-scope</c>, <c>expression-unknown-namespace</c>, <c>expression-field-access-invalid</c>, or the
/// type-site code of a check's answer that is not a registered term.</param>
/// <param name="Reason">What is wrong, without the name of the path.</param>
/// <param name="Position">Where the offending node starts in the expression text.</param>
/// <param name="Path">The <c>${…}</c> path the refusal is about, or null when it is about an operator or a function.</param>
public sealed record ExpressionRefusal(string RuleId, string Reason, int Position, string? Path = null)
{
    /// <summary>The refusal as one sentence.</summary>
    public string Message => Path is null ? Reason : $"'{Path}': {Reason}";
}

/// <summary>What <see cref="ExpressionTyper"/> answers: a typed tree, or the refusals that left it without one. A
/// refusal is an answer, not an exception.</summary>
public sealed class ExpressionTypingResult
{
    internal ExpressionTypingResult(TypedExpression? tree, IReadOnlyList<ExpressionRefusal> refusals)
    {
        Tree = tree;
        Refusals = refusals;
    }

    /// <summary>The typed tree; null when there are refusals.</summary>
    public TypedExpression? Tree { get; }

    /// <summary>Every refusal, in the order the typer found them: reads first, ordered by key and path, then operators and functions in
    /// the order the text holds them. Empty with a tree.</summary>
    public IReadOnlyList<ExpressionRefusal> Refusals { get; }

    /// <summary>The first refusal, or null with a tree.</summary>
    public ExpressionRefusal? Refusal => Refusals.Count > 0 ? Refusals[0] : null;

    /// <summary>Whether the expression has a tree. False with no refusal is a read of a declaration a reported rule refuses
    /// (<see cref="ReadLookupKind.Unresolved"/>); only the compiler's scope answers that, and a host's never does.</summary>
    public bool IsTyped => Tree is not null;
}
