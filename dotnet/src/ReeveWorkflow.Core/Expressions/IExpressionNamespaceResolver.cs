namespace ReeveWorkflow.Core.Expressions;

/// <summary>One top-level namespace in a <c>${path}</c> lookup. The first segment picks the resolver, which is
/// handed the segments of its key and nothing else: not the namespace, and not a field path read out of the value.</summary>
public interface IExpressionNamespaceResolver
{
    /// <summary>The first path segment this answers to. Matched with ordinal equality, never case-folded.</summary>
    string Namespace { get; }

    /// <summary>Answers the value the key names, in the CLR type of the term the namespace's static check answers for
    /// it (<c>long</c> for <c>Int</c>, <c>DateTimeOffset</c> for <c>DateTime</c>, a <see cref="System.Text.Json.JsonElement"/>
    /// or null for <c>Json?</c>). <paramref name="segments"/> holds exactly the
    /// <see cref="WorkflowExpressionNamespace.KeySegments"/> its registration declares: <c>["kitchen", "temp"]</c> for
    /// <c>${device.kitchen.temp.unit}</c> registered with two. A value off its term fails the read.</summary>
    ResolveResult Resolve(string[] segments);
}

/// <summary>A host resolver, the number of path segments after its name that make up its key, and the static check
/// that types the values it answers.</summary>
public sealed class ExpressionNamespaceBinding
{
    /// <param name="resolver">The resolver.</param>
    /// <param name="keySegments">One or more; fixed per namespace.</param>
    /// <param name="staticCheck">The check that answers the term of each path. Required: every value the resolver
    /// answers is held to that term at the read, and a path with no term is not read.</param>
    public ExpressionNamespaceBinding(
        IExpressionNamespaceResolver resolver, int keySegments, IExpressionNamespaceStaticCheck staticCheck)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(staticCheck);
        ArgumentOutOfRangeException.ThrowIfLessThan(keySegments, 1);
        Resolver = resolver;
        KeySegments = keySegments;
        StaticCheck = staticCheck;
    }

    /// <summary>The resolver.</summary>
    public IExpressionNamespaceResolver Resolver { get; }

    /// <summary>How many path segments after the namespace make up the key.</summary>
    public int KeySegments { get; }

    /// <summary>The check whose term each resolved value is held to.</summary>
    public IExpressionNamespaceStaticCheck StaticCheck { get; }
}

/// <summary>Whether a path under a host namespace can resolve.</summary>
public enum NamespacePathCheck
{
    /// <summary>No such path: a read of it is <c>expression-unknown-namespace</c>. The zero value, so an answer nobody
    /// filled in refuses rather than admits.</summary>
    No = 0,

    /// <summary>The path can resolve, and the answer names the term of its value.</summary>
    Yes = 1,
}

/// <summary>A static check's answer for one path: whether it can resolve and, when it can, the term of its value.</summary>
/// <param name="Verdict">Whether the path can resolve.</param>
/// <param name="KeyPortType">Required with <see cref="NamespacePathCheck.Yes"/>: the closed term of the value the key names
/// (<c>Int</c>, <c>Boolean?</c>, <c>Json?</c>). One missing, malformed, an alias or unregistered is refused.</param>
public readonly record struct NamespacePathCheckAnswer(NamespacePathCheck Verdict, string? KeyPortType)
{
    /// <summary>The answer for a path that cannot resolve.</summary>
    public static NamespacePathCheckAnswer No => new(NamespacePathCheck.No, null);
}

/// <summary>Answers, without running anything, whether a path under this namespace could resolve and what type its
/// value has. Every registered namespace has one: an expression that reads a path is typed by the answer.</summary>
public interface IExpressionNamespaceStaticCheck
{
    /// <summary>The first path segment this answers to, matched with ordinal equality.</summary>
    string Namespace { get; }

    /// <summary>Answers for the key alone (<c>["kitchen", "temp"]</c> of <c>${device.kitchen.temp.unit}</c>); a <c>Yes</c>
    /// carries the term the resolver answers in. Runs at every compile and run-time read, so keep it cheap; a throw fails the compile.</summary>
    NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments);
}
