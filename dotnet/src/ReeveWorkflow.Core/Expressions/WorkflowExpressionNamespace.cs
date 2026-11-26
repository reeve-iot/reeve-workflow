using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>The information available when a host namespace is resolved for an expression.</summary>
public sealed record ExpressionNamespaceScope(
    IServiceProvider Services,
    Guid? ExecutionId,
    Guid? TriggerId,
    TriggerEnvelope? TriggerEnvelope);

/// <summary>One host namespace shared by compilation, runs, parked waits and expression preview.</summary>
/// <remarks>The namespace named <c>trigger</c> with one key segment is the one admission reads a fire's payload through: each
/// accepted key is converted into the term its check answers, and any other key is dropped from the envelope.</remarks>
public sealed class WorkflowExpressionNamespace
{
    /// <summary>Registers one host namespace, the number of path segments its key takes, its static check and its resolver factory.</summary>
    public WorkflowExpressionNamespace(
        string @namespace,
        int keySegments,
        IExpressionNamespaceStaticCheck staticCheck,
        Func<ExpressionNamespaceScope, CancellationToken, Task<IExpressionNamespaceResolver>> resolverFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        WorkflowExpressionNamespaces.ValidateHostNamespace(@namespace);
        ArgumentOutOfRangeException.ThrowIfLessThan(keySegments, 1);
        ArgumentNullException.ThrowIfNull(staticCheck);
        ArgumentNullException.ThrowIfNull(resolverFactory);
        if (!string.Equals(@namespace, staticCheck.Namespace, StringComparison.Ordinal))
            throw new ArgumentException("The static check must answer for the registered namespace.", nameof(staticCheck));
        Namespace = @namespace;
        KeySegments = keySegments;
        StaticCheck = staticCheck;
        ResolverFactory = resolverFactory;
    }

    /// <summary>The namespace's ordinal expression name.</summary>
    public string Namespace { get; }

    /// <summary>How many path segments after the namespace make up its key: 1 for <c>${trigger.field}</c>, 2 for
    /// <c>${device.name.key}</c>. The resolver and the static check are given those segments alone; whatever follows
    /// them in a path is a member or an index read out of the value the resolver answered.</summary>
    public int KeySegments { get; }

    /// <summary>The compile-time check paired with this resolver: it answers the term of every path the namespace accepts,
    /// and the values the resolver answers are held to it.</summary>
    public IExpressionNamespaceStaticCheck StaticCheck { get; }

    /// <summary>The version of what the static check answers. Raise it in the release that changes an answer, so a
    /// stored compile verdict made with the old answers is never served: the dependency hash covers it beside the name.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A negative version.</exception>
    public int AnswerVersion
    {
        get => answerVersion;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            answerVersion = value;
        }
    }

    private readonly int answerVersion;

    /// <summary>Creates an immutable resolver snapshot for one expression scope. Runs call the factory once per
    /// graph walk, a waker calls it once per parked template per pass, and preview calls it only when the expression
    /// references this namespace. A waker's service scope ends when the factory returns, so the resolver must not
    /// retain scoped services.</summary>
    public Func<ExpressionNamespaceScope, CancellationToken, Task<IExpressionNamespaceResolver>> ResolverFactory { get; }
}

/// <summary>Resolves the host's registered expression namespaces for a scope.</summary>
public static class WorkflowExpressionNamespaces
{
    internal static readonly IReadOnlyList<string> ScopeVariableNames = ["item", "value"];

    internal static bool IsScopeVariableName(string? name) => name is not null && ScopeVariableNames.Contains(name, StringComparer.Ordinal);

    internal static void ValidateHostNamespace(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "previous" or "parameter" or "now" or "helper" || IsScopeVariableName(name))
            throw new ArgumentException($"The namespace '{name}' is reserved by the library.", nameof(name));
    }

    /// <summary>Creates one resolver for each registered host namespace, bound to the key length its registration
    /// declares, and refuses duplicate names.</summary>
    public static async Task<IReadOnlyCollection<ExpressionNamespaceBinding>> MergeResolversAsync(
        IEnumerable<WorkflowExpressionNamespace>? registrations,
        ExpressionNamespaceScope scope,
        CancellationToken cancellationToken)
    {
        var result = new List<ExpressionNamespaceBinding>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in registrations ?? [])
        {
            ValidateHostNamespace(registration.Namespace);
            if (!names.Add(registration.Namespace))
                throw new ArgumentException($"Duplicate namespace registration '{registration.Namespace}'.", nameof(registrations));
            var resolver = await registration.ResolverFactory(scope, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(resolver.Namespace, registration.Namespace, StringComparison.Ordinal))
                throw new InvalidOperationException($"Resolver factory for '{registration.Namespace}' returned '{resolver.Namespace}'.");
            result.Add(new ExpressionNamespaceBinding(resolver, registration.KeySegments, registration.StaticCheck));
        }
        return result.ToArray();
    }

}
