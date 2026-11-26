using System.ComponentModel.DataAnnotations;

namespace ReeveWorkflow.Abstractions;

/// <summary>Transport bounds owned by whoever owns the language. An over-length body is the deep-recursion
/// stack-overflow vector and must be refused before the parser runs.</summary>
public static class ExpressionLimits
{
    /// <summary>Max characters in an expression body. A flat chain needs roughly two characters per
    /// nesting level, so this is the depth the parser is never asked to walk.</summary>
    public const int MaxExpressionLength = 4096;

    /// <summary>Max characters in a context scope. A scope names a workflow — <c>workflow:&lt;guid&gt;</c> —
    /// so anything near this bound is already not one.</summary>
    public const int MaxContextScopeLength = 256;
}

/// <summary>Wire contract for <c>POST /api/expressions/preview</c>. <see cref="ContextScope"/> is one of
/// <c>global</c>, <c>workflow:&lt;id&gt;</c>, or <c>workflow-run-preview:&lt;id&gt;</c>.</summary>
public sealed record ExpressionPreviewRequest(
    [StringLength(ExpressionLimits.MaxExpressionLength)] string Expression,
    [StringLength(ExpressionLimits.MaxContextScopeLength)] string? ContextScope);

public sealed record ExpressionPreviewResponse(
    bool Ok,
    object? Value,
    string? Type,
    ExpressionPreviewErrorDto? Error,
    long ElapsedMicroseconds);

/// <summary>Why a preview has no value. <see cref="RuleId"/> is the compiler rule the expression breaks, <c>expression-runtime-error</c>
/// for a well-typed expression whose evaluation failed, and absent otherwise.</summary>
public sealed record ExpressionPreviewErrorDto(
    string Message,
    int Position,
    string? Suggestion,
    string? RuleId = null);
