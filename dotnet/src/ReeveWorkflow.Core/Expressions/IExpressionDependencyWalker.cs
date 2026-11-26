namespace ReeveWorkflow.Core.Expressions;

/// <summary>Extracts external dependencies from an expression AST as colon-separated keys, e.g.
/// <c>${device.kitchen.temperature}</c> → <c>device:kitchen:temperature</c>.</summary>
public interface IExpressionDependencyWalker
{
    /// <summary>Extracts dependency keys from the AST subtree rooted at <paramref name="node"/>;
    /// empty for expressions with no external references.</summary>
    IReadOnlySet<string> Dependencies(ExpressionNode node);

    /// <summary>Parses <paramref name="expressionSource"/> and calls <see cref="Dependencies(ExpressionNode)"/>;
    /// strips a leading <c>expr:</c> prefix automatically. Empty set for blank or null input.</summary>
    IReadOnlySet<string> DependenciesFromSource(string? expressionSource);
}
