using System.Globalization;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>The expression typer: gives every node of an expression a term, binds every operator and function to one overload of
/// <c>expression-functions.json</c> with its conversions explicit, or refuses the expression with the rule it breaks. A value is
/// never typed as a wildcard: what the typer cannot type is refused. The scope supplies what a read holds.</summary>
public static class ExpressionTyper
{
    /// <summary>Types the expression text, without its <c>expr:</c> prefix.</summary>
    /// <param name="expression">The text. A syntax error is an <c>expression-syntax</c> refusal.</param>
    /// <param name="scope">What the reads hold.</param>
    /// <returns>The typed tree, or every refusal; never an exception for a text the language refuses.</returns>
    /// <exception cref="Exception">A namespace or helper check that throws, whose exception is the host's.</exception>
    public static ExpressionTypingResult Type(string expression, ExpressionTypingScope scope)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(scope);
        ExpressionNode ast;
        try
        {
            ast = ExpressionSyntaxCache.ParseCached(expression);
        }
        catch (ExpressionSyntaxException ex)
        {
            return new ExpressionTypingResult(null, [new ExpressionRefusal(CompilationRules.ExpressionSyntax, ex.Message, ex.Position)]);
        }

        return Type(ast, scope);
    }

    internal static ExpressionTypingResult Type(ExpressionNode expression, ExpressionTypingScope scope)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(scope);
        return new TypingRun(scope).Run(expression);
    }
}

internal sealed partial class TypingRun
{
    private readonly ExpressionTypingScope scope;
    private readonly WorkflowTypeVocabulary vocabulary;
    private readonly List<ExpressionRefusal> refusals = [];
    private readonly Dictionary<string, PortTerm?> keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedRead?> reads = new(StringComparer.Ordinal);

    public TypingRun(ExpressionTypingScope scope)
    {
        this.scope = scope;
        vocabulary = scope.Vocabulary;
    }

    public ExpressionTypingResult Run(ExpressionNode ast)
    {
        var paths = new List<(string Path, int Position)>();
        CollectReads(ast, paths);
        foreach (var (path, position) in paths
                     .DistinctBy(p => p.Path, StringComparer.Ordinal)
                     .OrderBy(p => ExpressionPath.DependencyKeyOf(p.Path, scope.KeyShapes), StringComparer.Ordinal)
                     .ThenBy(p => p.Path, StringComparer.Ordinal))
        {
            reads[path] = ResolveRead(path, position);
        }

        var tree = Visit(ast);
        if (tree is not null && Terms.IsJson(tree.Term))
        {
            Refuse(CompilationRules.ExpressionTypeMismatch,
                "the expression is a Json value, which is read only through a projection and is not a result", tree.Position);
            tree = null;
        }

        return new ExpressionTypingResult(refusals.Count == 0 ? tree : null, refusals);
    }

    private static void CollectReads(ExpressionNode node, List<(string Path, int Position)> paths)
    {
        switch (node)
        {
            case VariableNode v when !string.IsNullOrWhiteSpace(v.Path):
                paths.Add((v.Path, v.Position));
                break;
            case UnaryNode u:
                CollectReads(u.Operand, paths);
                break;
            case BinaryNode b:
                CollectReads(b.Left, paths);
                CollectReads(b.Right, paths);
                break;
            case TernaryNode t:
                CollectReads(t.Condition, paths);
                CollectReads(t.WhenTrue, paths);
                CollectReads(t.WhenFalse, paths);
                break;
            case CallNode c:
                foreach (var arg in c.Args)
                    CollectReads(arg, paths);
                break;
        }
    }

    private TypedExpression? Refuse(string rule, string reason, int position, string? path = null)
    {
        refusals.Add(new ExpressionRefusal(rule, reason, position, path));
        return null;
    }

    private TypedExpression? Visit(ExpressionNode node) => node switch
    {
        NumberLiteralNode n => Number(n),
        StringLiteralNode s => new TypedLiteral(Terms.String, s.Value, s.Position),
        BooleanLiteralNode b => new TypedLiteral(Terms.Boolean, b.Value, b.Position),
        NullLiteralNode z => new TypedLiteral(PortTerm.Null, null, z.Position),
        VariableNode v => reads.GetValueOrDefault(v.Path) is { } read ? new TypedRead(read.Term, read.Namespace, read.Key, v.Position) : null,
        UnaryNode u => Unary(u),
        BinaryNode b => Binary(b),
        TernaryNode t => Conditional(t),
        CallNode c => Call(c),
        _ => Refuse(CompilationRules.ExpressionSyntax, $"unknown expression node {node.GetType().Name}", node.Position),
    };

    private TypedExpression? Number(NumberLiteralNode node)
    {
        if (node.RawText is { } text && !text.Contains('.'))
        {
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer)
                ? new TypedLiteral(Terms.Int, integer, node.Position)
                : Refuse(CompilationRules.ExpressionSyntax, $"the integer literal {text} does not fit an Int, a 64-bit integer", node.Position);
        }

        return double.IsFinite(node.Value)
            ? new TypedLiteral(Terms.Number, node.Value, node.Position)
            : Refuse(CompilationRules.ExpressionSyntax, "the number literal is not finite, so it is not a Number", node.Position);
    }

    private sealed record ResolvedRead(PortTerm Term, string Namespace, IReadOnlyList<string> Key);
}
