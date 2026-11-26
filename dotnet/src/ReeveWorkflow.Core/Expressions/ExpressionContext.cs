using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>What a host namespace's resolver answers for a key: the value in the CLR type of the term its check answered, or
/// a path that was not found.</summary>
/// <remarks>Use <see cref="Resolved(object?)"/> and <see cref="Failed"/> to create a result. A default value
/// behaves as a failure. Equality is not part of the result contract.</remarks>
public readonly struct ResolveResult
{
    private readonly bool _found;
    private readonly object? _value;

    private ResolveResult(bool found, object? value)
    {
        _found = found;
        _value = value;
    }

    public bool Found => _found;

    /// <summary>The value the leaf resolved to, as the CLR value of its term.</summary>
    public object? Value => _value;

    /// <summary>A leaf that resolved to a value, in the CLR type of the term its namespace's check answered for the
    /// path.</summary>
    public static ResolveResult Resolved(object? value) => new(true, value);

    /// <summary>A path that did not resolve. The read fails with <c>value-absent</c>.</summary>
    public static ResolveResult Failed() => new(false, null);
}

/// <summary>What a preview evaluates an expression against besides its operands: the clock, the helper
/// resolver, the host namespaces and the vocabulary their values are held to. Use <see cref="Builder"/> to construct one.</summary>
public sealed class ExpressionContext
{
    private readonly ExpressionNamespaceBinding[] _hosts;
    private readonly IHelperResolver _helperResolver;
    private readonly bool _localNowPinned;

    /// <summary>The names of the host namespaces registered on this context.</summary>
    public IReadOnlyList<string> Namespaces { get; }

    public DateTimeOffset Now { get; }

    /// <summary>"Now" in the home's local time zone, backing the local-time fields (<c>localHour</c> etc.).
    /// Defaults to <see cref="Now"/> (UTC) when the caller supplies no home-local value.</summary>
    public DateTimeOffset LocalNow { get; }

    /// <summary>The port-type vocabulary a host value is held to: the term its namespace's check answered is read in it.
    /// The library's own types when the caller supplies none.</summary>
    public WorkflowTypeVocabulary TypeVocabulary { get; }

    public ExpressionContext(
        DateTimeOffset? now = null,
        IHelperResolver? helperResolver = null,
        DateTimeOffset? localNow = null,
        IEnumerable<ExpressionNamespaceBinding>? namespaces = null,
        WorkflowTypeVocabulary? typeVocabulary = null)
    {
        TypeVocabulary = typeVocabulary ?? WorkflowTypeVocabulary.Builtin;
        Now = now ?? DateTimeOffset.UtcNow;
        LocalNow = localNow ?? Now;
        _localNowPinned = localNow is not null;
        _helperResolver = helperResolver ?? NullHelperResolver.Instance;

        var names = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new List<ExpressionNamespaceBinding>();
        foreach (var extra in namespaces ?? [])
        {
            ArgumentNullException.ThrowIfNull(extra);
            WorkflowExpressionNamespaces.ValidateHostNamespace(extra.Resolver.Namespace);
            if (!names.Add(extra.Resolver.Namespace))
                throw new ArgumentException($"Duplicate namespace resolver '{extra.Resolver.Namespace}'.", nameof(namespaces));
            hosts.Add(extra);
        }
        _hosts = hosts.ToArray();
        Namespaces = Array.ConvertAll(_hosts, h => h.Resolver.Namespace);
    }

    internal IReadOnlyList<ExpressionNamespaceBinding> Hosts => _hosts;

    internal IHelperResolver Helper => _helperResolver;

    internal bool LocalNowPinned => _localNowPinned;

    internal ExpressionContext WithNamespaces(
        IEnumerable<ExpressionNamespaceBinding> namespaces, WorkflowTypeVocabulary? typeVocabulary = null) => new(
        Now, _helperResolver, _localNowPinned ? LocalNow : null, _hosts.Concat(namespaces), typeVocabulary ?? TypeVocabulary);

    public sealed class Builder
    {
        private DateTimeOffset? _now;
        private DateTimeOffset? _localNow;
        private IHelperResolver? _helperResolver;
        private WorkflowTypeVocabulary? _typeVocabulary;
        private readonly List<ExpressionNamespaceBinding> _namespaces = [];

        public Builder Now(DateTimeOffset now)
        {
            _now = now;
            return this;
        }

        /// <summary>Sets "now" in the home's local time zone, backing the local-time fields. When not called,
        /// <see cref="ExpressionContext.LocalNow"/> defaults to <see cref="ExpressionContext.Now"/> (UTC).</summary>
        public Builder LocalNow(DateTimeOffset localNow)
        {
            _localNow = localNow;
            return this;
        }

        public Builder HelperResolver(IHelperResolver? resolver)
        {
            _helperResolver = resolver;
            return this;
        }

        /// <summary>Sets the vocabulary a host or helper value is held to; the library's own types when not called.</summary>
        public Builder TypeVocabulary(WorkflowTypeVocabulary? vocabulary)
        {
            _typeVocabulary = vocabulary;
            return this;
        }

        /// <summary>Registers a namespace the engine does not ship: <paramref name="keySegments"/> key segments, values held
        /// to the terms <paramref name="staticCheck"/> answers. A reserved or registered name throws.</summary>
        public Builder Namespace(IExpressionNamespaceResolver resolver, int keySegments, IExpressionNamespaceStaticCheck staticCheck) =>
            Namespace(new ExpressionNamespaceBinding(resolver, keySegments, staticCheck));

        /// <summary>Registers a resolver together with the key length it was registered with.</summary>
        public Builder Namespace(ExpressionNamespaceBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);
            WorkflowExpressionNamespaces.ValidateHostNamespace(binding.Resolver.Namespace);
            if (_namespaces.Any(existing => string.Equals(existing.Resolver.Namespace, binding.Resolver.Namespace, StringComparison.Ordinal)))
                throw new ArgumentException($"Duplicate namespace resolver '{binding.Resolver.Namespace}'.", nameof(binding));
            _namespaces.Add(binding);
            return this;
        }

        public ExpressionContext Build() => new(_now, _helperResolver, _localNow, _namespaces, _typeVocabulary);
    }
}
