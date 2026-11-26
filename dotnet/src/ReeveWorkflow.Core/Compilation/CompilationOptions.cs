using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>What the compiler is told beyond the definition, the catalogue and the resolved
/// <see cref="NodePortEnvironment"/>.</summary>
public sealed class CompilationOptions
{
    /// <summary>The host namespace registrations shared with execution and preview.</summary>
    public IReadOnlyList<WorkflowExpressionNamespace> NamespaceRegistrations
    {
        get => namespaceRegistrations;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            namespaceRegistrations = [.. value];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var registration in namespaceRegistrations)
                if (!seen.Add(registration.Namespace))
                    throw new ArgumentException($"Duplicate namespace registration '{registration.Namespace}'.", nameof(value));
            KeyShapes = ExpressionKeyShapes.For(namespaceRegistrations);
            RenderedNamespaces = [.. namespaceRegistrations.Select(r => $"{r.Namespace.Length}:{r.Namespace}:{r.KeySegments}:{r.AnswerVersion}")];
            PinnedNamespaceChecks = namespaceRegistrations
                .Select(registration => (registration.Namespace, (IExpressionNamespaceStaticCheck)registration.StaticCheck))
                .ToArray();
        }
    }

    private IReadOnlyList<WorkflowExpressionNamespace> namespaceRegistrations = [];

    /// <summary>The keyed resolver used to check helper paths during compilation and to read their values during evaluation.
    /// The default answers No to checks and returns absent reads.</summary>
    public IHelperResolver HelperResolver
    {
        get => helperResolver;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            helperResolver = value;
        }
    }

    private IHelperResolver helperResolver = NullHelperResolver.Instance;

    internal IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer> HelperChecks { get; private init; } = new Dictionary<HelperPath, NamespacePathCheckAnswer>();
    internal bool HelperChecksFilled { get; private init; }

    internal IReadOnlyList<string> RenderedNamespaces { get; private set; } = [];

    internal ExpressionKeyShapes KeyShapes { get; private set; } = ExpressionKeyShapes.Library;

    internal IReadOnlyList<(string Namespace, IExpressionNamespaceStaticCheck Check)> PinnedNamespaceChecks
    {
        get;
        private set;
    } = [];

    /// <summary>Where the port type vocabulary lives. When set, every read of <see cref="Vocabulary"/> is the
    /// registry's state at that moment, so a plugin's types are seen by the next compile.</summary>
    public IWorkflowTypeRegistry? TypeRegistry
    {
        get => typeRegistry;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            typeRegistry = value;
        }
    }

    /// <summary>The vocabulary the compiler checks every port type against: the registry's current state, else the library's own.</summary>
    public WorkflowTypeVocabulary Vocabulary
    {
        get
        {
            if (pinned is not null)
                return pinned;
            return typeRegistry?.Current ?? WorkflowTypeVocabulary.Builtin;
        }
    }

    private IWorkflowTypeRegistry? typeRegistry;

    internal CompilationOptions PinnedTo(WorkflowTypeVocabulary state) => new()
    {
        NamespaceRegistrations = namespaceRegistrations,
        HelperResolver = helperResolver,
        HelperChecks = HelperChecks,
        HelperChecksFilled = HelperChecksFilled,
        pinned = state,
    };

    internal CompilationOptions WithHelperChecks(IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer> checks) => new()
    {
        NamespaceRegistrations = namespaceRegistrations, HelperResolver = helperResolver, typeRegistry = typeRegistry,
        HelperChecks = checks, HelperChecksFilled = true, pinned = pinned,
    };

    internal CompilationOptions Pinned() => pinned is not null ? this : PinnedTo(Vocabulary);

    private WorkflowTypeVocabulary? pinned;
}
