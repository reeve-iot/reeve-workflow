namespace ReeveWorkflow.Core.Expressions;

/// <summary>Concrete <see cref="IExpressionDependencyWalker"/>: walks the expression AST collecting every
/// <c>${...}</c> variable reference, normalising each to its namespace and key, colon-separated
/// (<c>device.kitchen.temp.unit</c> → <c>device:kitchen:temp</c> when <c>device</c> takes a two-segment key).
/// A field path read out of the value is not part of the key.</summary>
public sealed class ExpressionDependencyWalker : IExpressionDependencyWalker
{
    /// <summary>A walker over the library's namespaces only, so any other namespace's key is every dotted segment
    /// of its path. The walker is stateless; sharing it is safe.</summary>
    public static readonly ExpressionDependencyWalker Instance = new();

    private readonly ExpressionKeyShapes shapes;

    /// <summary>A walker over the library's namespaces only.</summary>
    public ExpressionDependencyWalker() : this(ExpressionKeyShapes.Library)
    {
    }

    /// <summary>A walker that ends a path's key where <paramref name="shapes"/> says it ends.</summary>
    public ExpressionDependencyWalker(ExpressionKeyShapes shapes) =>
        this.shapes = shapes ?? throw new ArgumentNullException(nameof(shapes));

    /// <summary>A walker over the library's namespaces and each registration's, at the key length it declares.</summary>
    public static ExpressionDependencyWalker For(IEnumerable<WorkflowExpressionNamespace>? registrations) =>
        new(ExpressionKeyShapes.For(registrations));

    /// <inheritdoc/>
    public IReadOnlySet<string> Dependencies(ExpressionNode node)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        Collect(node, result);
        return result;
    }

    /// <inheritdoc/>
    public IReadOnlySet<string> DependenciesFromSource(string? expressionSource)
    {
        if (string.IsNullOrWhiteSpace(expressionSource))
            return new HashSet<string>();

        var source = expressionSource.Trim();
        if (ExpressionSource.IsExpression(source))
            source = ExpressionSource.Strip(source);

        if (string.IsNullOrWhiteSpace(source))
            return new HashSet<string>();

        try
        {
            var ast = ExpressionParser.Parse(source);
            return Dependencies(ast);
        }
        catch (ExpressionSyntaxException)
        {
            return new HashSet<string>();
        }
    }

    private void Collect(ExpressionNode node, HashSet<string> result)
    {
        switch (node)
        {
            case NumberLiteralNode:
            case StringLiteralNode:
            case BooleanLiteralNode:
            case NullLiteralNode:
                break;

            case VariableNode v:
                if (!string.IsNullOrWhiteSpace(v.Path))
                    result.Add(KeyOf(v.Path));
                break;

            case UnaryNode u:
                Collect(u.Operand, result);
                break;

            case BinaryNode b:
                Collect(b.Left, result);
                Collect(b.Right, result);
                break;

            case TernaryNode t:
                Collect(t.Condition, result);
                Collect(t.WhenTrue, result);
                Collect(t.WhenFalse, result);
                break;

            case CallNode c:
                foreach (var arg in c.Args)
                    Collect(arg, result);
                break;

            default:
                break;
        }
    }

    private string KeyOf(string path) => ExpressionPath.DependencyKeyOf(path, shapes);
}
