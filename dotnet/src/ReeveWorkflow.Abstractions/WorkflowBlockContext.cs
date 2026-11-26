using Microsoft.Extensions.Logging;

namespace ReeveWorkflow.Abstractions;

public sealed class WorkflowBlockContext
{
    public required Guid ExecutionId { get; init; }
    public required Guid UserId { get; init; }
    public required ILogger Logger { get; init; }

    /// <summary>The graph node id of the block being executed. Attributes run-log entries (e.g. HTTP-call
    /// summaries) to the correct node. Null for a context built outside a graph run.</summary>
    public string? NodeId { get; init; }

    /// <summary>The execution-scoped provider for BUILT-IN blocks. For PLUGIN-EXPORTED blocks it resolves
    /// NOTHING, so a plugin block cannot pull unmanifested services out of the container.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>The grant-scoped facade for PLUGIN-EXPORTED blocks, null for built-in ones. A gated capability
    /// resolves only when its code is in the effective granted ∩ requested set.</summary>
    public IWorkflowHostServices? HostServices { get; init; }

    public ISubCanvasExecutor? SubCanvasExecutor { get; init; }

    /// <summary>The closed type of each port of the node, as its compile fixed it: an input or output by its name, a port of a
    /// sub-canvas as <c>&lt;sub-canvas&gt;.&lt;port&gt;</c> and a bound type parameter as <c>$&lt;name&gt;</c>. A block builds
    /// each output in its term's CLR type through it. Empty for a context built outside a compiled run.</summary>
    public IReadOnlyDictionary<string, ClosedPortType> Types { get; init; } = EmptyTypes;

    /// <summary>The outputs the node's author declared (<c>outputPorts</c>), in declared order, as the executor fills it; a block
    /// that collects them builds each through <see cref="Types"/>. Empty for a node with none and for a context built by hand.</summary>
    public IReadOnlyList<string> DeclaredOutputs { get; init; } = [];

    internal IReadOnlyDictionary<string, ClosedPortType>? OutputTypes { get; init; }

    /// <summary>Evaluates the node's <c>Expression&lt;T&gt;</c> inputs, which a block receives as unevaluated text, with the scope variables
    /// each declares bound (<see cref="BlockPortDefinition.ScopeVariables"/>). Built by the library once per node; a context built
    /// by hand has one whose <see cref="IWorkflowBlockExpressions.Evaluate"/> throws <see cref="InvalidOperationException"/>.</summary>
    public IWorkflowBlockExpressions Expressions { get; init; } = NoExpressions.Instance;

    /// <summary>Reveals the protected and referenced entries of a <see cref="BlockPortDefinition.SecretValues"/> input. Built by the
    /// library once per node; a context built by hand has one that answers every map unchanged.</summary>
    public IWorkflowBlockSecrets Secrets { get; init; } = Unrevealed;

    internal static IWorkflowBlockSecrets Unrevealed => UnrevealedSecrets.Instance;

    private sealed class UnrevealedSecrets : IWorkflowBlockSecrets
    {
        public static readonly UnrevealedSecrets Instance = new();

        public ValueTask<IReadOnlyDictionary<string, string>> RevealAsync(
            string input, IReadOnlyDictionary<string, string> value, CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }

    private sealed class NoExpressions : IWorkflowBlockExpressions
    {
        public static readonly NoExpressions Instance = new();

        public object? Evaluate(string input, IReadOnlyDictionary<string, object?> variables) =>
            throw new InvalidOperationException("This context was built outside a compiled run, so it has no expressions to evaluate.");
    }

    private static readonly IReadOnlyDictionary<string, ClosedPortType> EmptyTypes =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, ClosedPortType>(new Dictionary<string, ClosedPortType>());
}
