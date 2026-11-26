using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

internal readonly record struct ReadLookup(ReadLookupKind Kind, PortTerm? Term = null, string? Why = null)
{
    public static ReadLookup Typed(PortTerm term) => new(ReadLookupKind.Typed, term);

    public static ReadLookup Missing(string why) => new(ReadLookupKind.Missing, Why: why);

    public static ReadLookup Unresolved => new(ReadLookupKind.Unresolved);

    public static ReadLookup Unbound(string why) => new(ReadLookupKind.Unbound, Why: why);
}

internal enum ReadLookupKind
{
    Missing,

    Unresolved,

    Unbound,

    Typed,
}

/// <summary>Everything the typer reads outside the expression text: the vocabulary its terms live in, the static checks of
/// the host namespaces and of <c>${helper.*}</c>, and the clock. An expression outside a workflow has no node outputs and no
/// parameters; the compiler's scope adds both.</summary>
public sealed class ExpressionTypingScope
{
    private ExpressionTypingScope(
        WorkflowTypeVocabulary vocabulary, ExpressionKeyShapes keyShapes, Func<IReadOnlyList<string>, NamespacePathCheckAnswer> helper,
        IReadOnlyDictionary<string, Func<IReadOnlyList<string>, NamespacePathCheckAnswer>> checks,
        Func<string, string, ReadLookup>? previous, Func<string, ReadLookup>? parameter, Func<string, ReadLookup>? variable)
    {
        Vocabulary = vocabulary;
        KeyShapes = keyShapes;
        Helper = helper;
        Checks = checks;
        Previous = previous;
        Parameter = parameter;
        Variable = variable;
    }

    /// <summary>The port-type vocabulary the terms of the checks' answers are graded in, and LUB and equality are decided by.</summary>
    public WorkflowTypeVocabulary Vocabulary { get; }

    internal ExpressionKeyShapes KeyShapes { get; }

    internal Func<IReadOnlyList<string>, NamespacePathCheckAnswer> Helper { get; }

    internal IReadOnlyDictionary<string, Func<IReadOnlyList<string>, NamespacePathCheckAnswer>> Checks { get; }

    internal Func<string, string, ReadLookup>? Previous { get; }

    internal Func<string, ReadLookup>? Parameter { get; }

    internal Func<string, ReadLookup>? Variable { get; }

    /// <summary>Builds a scope.</summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, (int KeySegments, Func<IReadOnlyList<string>, NamespacePathCheckAnswer> Check)> hosts =
            new(StringComparer.Ordinal);

        private WorkflowTypeVocabulary vocabulary = WorkflowTypeVocabulary.Builtin;
        private Func<IReadOnlyList<string>, NamespacePathCheckAnswer> helper = _ => NamespacePathCheckAnswer.No;
        private Func<string, string, ReadLookup>? previous;
        private Func<string, ReadLookup>? parameter;
        private Func<string, ReadLookup>? variable;

        /// <summary>The vocabulary to type in; the library's own when not called.</summary>
        public Builder Vocabulary(WorkflowTypeVocabulary value)
        {
            ArgumentNullException.ThrowIfNull(value);
            vocabulary = value;
            return this;
        }

        /// <summary>The helper check answers collected once for this compilation; a missing answer is No.</summary>
        public Builder Helper(IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer> answers)
        {
            ArgumentNullException.ThrowIfNull(answers);
            helper = key => answers.TryGetValue(new HelperPath(key), out var answer) ? answer : NamespacePathCheckAnswer.No;
            return this;
        }

        /// <summary>A host namespace: its key length and the check that answers the term of each path.</summary>
        /// <exception cref="ArgumentException">The name is reserved by the library or registered already.</exception>
        public Builder Namespace(string name, int keySegments, IExpressionNamespaceStaticCheck check) =>
            Namespace(name, keySegments, check.CheckWithKeyType);

        /// <summary>A host namespace registration, as the compiler and a run read it.</summary>
        public Builder Namespace(WorkflowExpressionNamespace registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            return Namespace(registration.Namespace, registration.KeySegments, registration.StaticCheck.CheckWithKeyType);
        }

        /// <summary>A host namespace binding, as a preview or a run holds it.</summary>
        public Builder Namespace(ExpressionNamespaceBinding binding)
        {
            ArgumentNullException.ThrowIfNull(binding);
            return Namespace(binding.Resolver.Namespace, binding.KeySegments, binding.StaticCheck.CheckWithKeyType);
        }

        internal Builder Namespace(string name, int keySegments, Func<IReadOnlyList<string>, NamespacePathCheckAnswer> check)
        {
            WorkflowExpressionNamespaces.ValidateHostNamespace(name);
            ArgumentOutOfRangeException.ThrowIfLessThan(keySegments, 1);
            ArgumentNullException.ThrowIfNull(check);
            if (!hosts.TryAdd(name, (keySegments, check)))
                throw new ArgumentException($"Duplicate namespace '{name}'.", nameof(name));
            return this;
        }

        internal Builder Helper(Func<IReadOnlyList<string>, NamespacePathCheckAnswer> check)
        {
            helper = check;
            return this;
        }

        internal Builder Previous(Func<string, string, ReadLookup> lookup)
        {
            previous = lookup;
            return this;
        }

        internal Builder Parameters(Func<string, ReadLookup> lookup)
        {
            parameter = lookup;
            return this;
        }

        internal Builder Variables(Func<string, ReadLookup> lookup)
        {
            variable = lookup;
            return this;
        }

        /// <summary>The scope.</summary>
        public ExpressionTypingScope Build() => new(
            vocabulary,
            ExpressionKeyShapes.Library.With(hosts.Select(h => (h.Key, h.Value.KeySegments))),
            helper,
            hosts.ToDictionary(h => h.Key, h => h.Value.Check, StringComparer.Ordinal),
            previous,
            parameter,
            variable);
    }
}
