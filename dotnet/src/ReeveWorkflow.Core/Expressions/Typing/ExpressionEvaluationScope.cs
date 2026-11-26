using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>What a typed tree reads while it is evaluated: the clock, the host namespaces' resolvers, the helper resolver and the
/// vocabulary every value is held to. An expression outside a workflow has no node outputs and no parameters; a run adds both.</summary>
public sealed class ExpressionEvaluationScope
{
    private ExpressionEvaluationScope(
        WorkflowTypeVocabulary vocabulary, DateTimeOffset now, DateTimeOffset localNow, IReadOnlyDictionary<HelperPath, object?> helper,
        IReadOnlyDictionary<string, ExpressionNamespaceBinding> hosts,
        Func<string, string, (bool Found, object? Value)>? previous, Func<string, (bool Found, object? Value)>? parameter,
        IReadOnlyDictionary<string, object?>? variables = null)
    {
        Vocabulary = vocabulary;
        Now = now;
        LocalNow = localNow;
        Helper = helper;
        Hosts = hosts;
        Previous = previous;
        Parameter = parameter;
        Variables = variables;
    }

    /// <summary>The vocabulary a value is held to: the CLR type of the term of the read that produced it.</summary>
    public WorkflowTypeVocabulary Vocabulary { get; }

    /// <summary>The value of <c>${now}</c>.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>"Now" in the home's time zone, behind the <c>local…</c> fields.</summary>
    public DateTimeOffset LocalNow { get; }

    internal IReadOnlyDictionary<HelperPath, object?> Helper { get; }

    internal IReadOnlyDictionary<string, ExpressionNamespaceBinding> Hosts { get; }

    internal Func<string, string, (bool Found, object? Value)>? Previous { get; }

    internal Func<string, (bool Found, object? Value)>? Parameter { get; }

    internal IReadOnlyDictionary<string, object?>? Variables { get; }

    internal ExpressionEvaluationScope WithVariables(IReadOnlyDictionary<string, object?> variables) =>
        new(Vocabulary, Now, LocalNow, Helper, Hosts, Previous, Parameter, variables);

    /// <summary>Builds a scope.</summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, ExpressionNamespaceBinding> hosts = new(StringComparer.Ordinal);
        private WorkflowTypeVocabulary vocabulary = WorkflowTypeVocabulary.Builtin;
        private DateTimeOffset? now;
        private DateTimeOffset? localNow;
        private IReadOnlyDictionary<HelperPath, object?> helper = new Dictionary<HelperPath, object?>();
        private Func<string, string, (bool Found, object? Value)>? previous;
        private Func<string, (bool Found, object? Value)>? parameter;

        /// <summary>The vocabulary to hold values to; the library's own when not called.</summary>
        public Builder Vocabulary(WorkflowTypeVocabulary value)
        {
            ArgumentNullException.ThrowIfNull(value);
            vocabulary = value;
            return this;
        }

        /// <summary>The clock; the system clock when not called.</summary>
        public Builder Now(DateTimeOffset value, DateTimeOffset? local = null)
        {
            now = value;
            localNow = local;
            return this;
        }

        /// <summary>The helper value snapshot read once for this evaluation unit.</summary>
        public Builder Helper(IReadOnlyDictionary<HelperPath, object?> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            helper = values;
            return this;
        }

        /// <summary>A host namespace's resolver, with the key length it was registered with and the check that answered the terms of its paths.</summary>
        /// <exception cref="ArgumentException">The name is reserved by the library or bound already.</exception>
        public Builder Namespace(ExpressionNamespaceBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);
            WorkflowExpressionNamespaces.ValidateHostNamespace(binding.Resolver.Namespace);
            if (!hosts.TryAdd(binding.Resolver.Namespace, binding))
                throw new ArgumentException($"Duplicate namespace '{binding.Resolver.Namespace}'.", nameof(binding));
            return this;
        }

        internal Builder Previous(Func<string, string, (bool Found, object? Value)> lookup)
        {
            previous = lookup;
            return this;
        }

        internal Builder Parameters(Func<string, (bool Found, object? Value)> lookup)
        {
            parameter = lookup;
            return this;
        }

        /// <summary>The scope.</summary>
        public ExpressionEvaluationScope Build()
        {
            var clock = now ?? DateTimeOffset.UtcNow;
            return new(vocabulary, clock, localNow ?? clock, helper, hosts, previous, parameter);
        }
    }
}
