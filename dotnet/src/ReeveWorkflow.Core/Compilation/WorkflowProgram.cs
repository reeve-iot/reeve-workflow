using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using System.Runtime.CompilerServices;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>The lowered graph the workflow walk executes.</summary>
public sealed record WorkflowProgram(IReadOnlyList<CompiledCanvas> Canvases);

/// <summary>One canvas frame in walk order, with its nodes already resolved. <see cref="CanvasPath"/> is the
/// frame's own path — <see cref="Compilation.CanvasPath"/> is a value-equal type, so unlike the joined-string
/// keying this replaced, two entries with the same path compare equal with a plain <c>==</c> or <c>Be(...)</c>.
/// <see cref="Signature"/> is the subprogram's parameter list and result, the same one the compiler checked
/// every wire on this canvas against.</summary>
/// <remarks>Like <see cref="CanvasSignature"/>, equality on <see cref="Nodes"/> and <see cref="NodeOrder"/>
/// is still reference equality — only <see cref="CanvasPath"/> itself compares structurally.</remarks>
public sealed record CompiledCanvas(
    CanvasPath CanvasPath,
    CanvasSignature Signature,
    IReadOnlyList<string> NodeOrder,
    IReadOnlyList<CompiledNode> Nodes)
{
    /// <summary>The closed type of each input the canvas declares, by name, which the output gate checks the values a container hands
    /// it against; null for an input with no closed type, whose value passes as written. A key outside it is dropped.</summary>
    public IReadOnlyDictionary<string, ClosedPortType?> InputTypes { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, ClosedPortType?>.Empty;

    internal IReadOnlyList<string> OutputNodeOrder { get; init; } = [];

    internal IReadOnlyDictionary<string, ClosedPortType?> OutputTypes { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, ClosedPortType?>.Empty;
}

/// <summary>One node, with its block bound, its configuration already merged into its inputs, and every
/// <c>expr:</c> value typed. <see cref="Expressions"/> and
/// <see cref="Inputs"/> are keyed by input name; a configuration value in <see cref="Inputs"/> is coerced to the CLR type of
/// its input's effective term, or of its field's term, and an Enum field's empty text is absent.
/// <see cref="SubCanvases"/> maps a branch name to the <see cref="CompiledCanvas.CanvasPath"/> of the
/// program entry that lowers it, so a walk that enters a branch never has to fall back to the raw
/// definition's own <c>SubCanvases</c> to find it.</summary>
/// <remarks>These members preserve the node's error policy, passthrough names and declared output projection
/// for execution after the definition has been lowered.</remarks>
public sealed record CompiledNode(
    string NodeId,
    BlockShape Shape,
    IReadOnlyDictionary<string, object?> Inputs,
    IReadOnlyDictionary<string, CompiledExpression> Expressions,
    IReadOnlyList<CompiledEdge> Incoming,
    IReadOnlyDictionary<string, CanvasPath> SubCanvases,
    ConfiguredResilience Resilience,
    NodeErrorConfig ErrorPolicy,
    IReadOnlyList<string> Passthrough,
    IReadOnlyList<CompiledOutputPort> OutputPorts)
{
    private static readonly ConditionalWeakTable<CompiledNode, HelperPathCache> HelperPathCaches = new();
    internal IReadOnlyCollection<ReeveWorkflow.Core.Expressions.HelperPath> HelperPaths =>
        HelperPathCaches.GetValue(this, static node => new HelperPathCache(node.Expressions,
            node.Expressions.Values.SelectMany(expression => expression.Tree.HelperPaths).Distinct().ToArray())).Get(Expressions);

    private sealed class HelperPathCache(IReadOnlyDictionary<string, CompiledExpression> source,
        IReadOnlyCollection<ReeveWorkflow.Core.Expressions.HelperPath> paths)
    {
        private readonly object gate = new();
        private IReadOnlyDictionary<string, CompiledExpression> source = source;
        private IReadOnlyCollection<ReeveWorkflow.Core.Expressions.HelperPath> paths = paths;
        public IReadOnlyCollection<ReeveWorkflow.Core.Expressions.HelperPath> Get(IReadOnlyDictionary<string, CompiledExpression> current)
        {
            lock (gate)
            {
                if (ReferenceEquals(source, current)) return paths;
                paths = current.Values.SelectMany(expression => expression.Tree.HelperPaths).Distinct().ToArray();
                source = current;
                return paths;
            }
        }
    }

    /// <summary>The effective closed type of each typed input, by name. An input whose term names a parameter
    /// nothing bound has none.</summary>
    public IReadOnlyDictionary<string, ClosedPortType> InputTypes { get; init; } = Empty;

    /// <summary>The effective closed type of each output the node writes, by name; a dropped output has none.</summary>
    public IReadOnlyDictionary<string, ClosedPortType> OutputTypes { get; init; } = Empty;

    /// <summary>The closed type of each scope variable of each <c>Expression&lt;T&gt;</c> input, by input name and then variable name,
    /// with the node's bindings substituted. A variable whose term names a parameter nothing bound has none.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, ClosedPortType>> ScopeVariables { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyDictionary<string, ClosedPortType>>.Empty;

    /// <summary>The closed type each bound type parameter of the block was bound to, by parameter name.</summary>
    public IReadOnlyDictionary<string, ClosedPortType> TypeArguments { get; init; } = Empty;

    /// <summary>What a block of this node reads as <see cref="WorkflowBlockContext.Types"/>: <see cref="InputTypes"/> and
    /// <see cref="OutputTypes"/> by port name (an output's where both name one), each sub-canvas port as
    /// <c>&lt;sub-canvas&gt;.&lt;port&gt;</c>, and each of <see cref="TypeArguments"/> as <c>$&lt;name&gt;</c>.</summary>
    public IReadOnlyDictionary<string, ClosedPortType> Types { get; init; } = Empty;

    /// <summary>The required inputs among the node's resolved inputs: its block's, its dynamic ones and a resolver's answer,
    /// a <c>SubWorkflow</c> callee's parameters included. The compile refuses one nothing feeds; a run stops the node when
    /// one holds no value.</summary>
    public IReadOnlyList<string> RequiredInputs { get; init; } = [];

    /// <summary>The name of every output the node writes: typed or not, and not one dropped. The output gate leaves out
    /// a key the block wrote that is not among them.</summary>
    public IReadOnlySet<string> OutputNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The outputs whose term names a parameter nothing bound and that nothing reads.</summary>
    public IReadOnlyList<string> DroppedOutputs { get; init; } = [];

    /// <summary>The outputs a <c>ContinueWithDefault</c> node writes when its block fails: each value of
    /// <c>defaultOutputJson</c> the compiler graded against its output's effective term, coerced to that term's CLR type;
    /// a key naming an output with no closed term keeps the run's JSON reading. Null for any other policy, and for text the run cannot read.</summary>
    public IReadOnlyDictionary<string, object?>? DefaultOutputs { get; init; }

    internal IReadOnlyList<string> InputNames { get; init; } = [];

    internal object? BindWire(string sourceId, string input, object? value)
    {
        for (var index = 0; index < Incoming.Count; index++)
        {
            var edge = Incoming[index];
            if (edge.SourceNodeId != sourceId || !edge.Conversions.TryGetValue(input, out var conversion))
                continue;
            try
            {
                return conversion.Apply(value);
            }
            catch (PortConversionException ex)
            {
                var producer = "";
                if (ex.NamedCode is not null)
                    for (var mappingIndex = 0; mappingIndex < edge.Mappings.Count; mappingIndex++)
                    {
                        var wire = edge.Mappings[mappingIndex];
                        if (wire.TargetInput == input && !wire.IsLiteral)
                        {
                            producer = $" (output '{wire.SourceOutput}')";
                            break;
                        }
                    }
                throw new PortConversionException(
                    $"Input '{input}' of '{NodeId}' from '{sourceId}'{producer}: {ex.Message}", ex) { NamedCode = ex.NamedCode };
            }
        }

        return value;
    }

    internal CompiledProjection? ProjectionOf(string sourceId, string input)
    {
        for (var index = 0; index < Incoming.Count; index++)
        {
            var edge = Incoming[index];
            if (edge.SourceNodeId == sourceId && edge.Projections.TryGetValue(input, out var projection))
                return projection;
        }

        return null;
    }

    internal Dictionary<string, object?>? BindDefaultOutputs()
    {
        if (DefaultOutputs is null)
            return null;
        var outputs = new Dictionary<string, object?>(DefaultOutputs.Count);
        foreach (var (name, value) in DefaultOutputs)
            outputs[name] = OutputTypes.TryGetValue(name, out var type) ? type.Copy(value) : WorkflowExecutionHelpers.FreshLiteral(value);
        return outputs;
    }

    internal bool TryBindLiteral(string sourceId, string input, out object? value)
    {
        for (var index = 0; index < Incoming.Count; index++)
        {
            var edge = Incoming[index];
            if (edge.SourceNodeId == sourceId && edge.Literals.TryGetValue(input, out var literal))
            {
                value = Fresh(input, literal);
                return true;
            }
        }

        value = null;
        return false;
    }

    internal bool TryBindConfig(string key, out object? value)
    {
        var found = Inputs.TryGetValue(key, out var configured);
        value = found ? Fresh(key, configured) : null;
        return found;
    }

    private object? Fresh(string input, object? value) =>
        InputTypes.TryGetValue(input, out var type) ? type.Copy(value) : WorkflowExecutionHelpers.FreshLiteral(value);

    private static readonly IReadOnlyDictionary<string, ClosedPortType> Empty =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, ClosedPortType>.Empty;
}

/// <summary>An <c>expr:</c> value as the compiler typed it: a configuration value or a literal mapping, an
/// <c>Expression&lt;T&gt;</c> input's text included. <see cref="Conversion"/> takes its result to the term of the port or
/// field it feeds; for an <c>Expression&lt;T&gt;</c> input, whose text the block receives, it takes the result to <c>T</c> when the
/// block evaluates the input.</summary>
/// <param name="Text">The stored value, <c>expr:</c> prefix included; a run evaluates <paramref name="Tree"/> where the input holds this text.</param>
/// <param name="Tree">The typed tree; the compile refuses an expression it cannot type, so lowering never builds one without it.</param>
public sealed record CompiledExpression(string Text, TypedExpression Tree)
{
    /// <summary>The conversion the input binder applies to the evaluated result.</summary>
    public PortConversion? Conversion { get; init; }
}

/// <summary>One wired edge, its mappings already read. <see cref="Conversions"/> holds, by target input, the conversion the
/// binder applies to a wire's value; <see cref="Literals"/> a literal mapping's value coerced to its input's CLR type.</summary>
public sealed record CompiledEdge(string SourceNodeId, IReadOnlyList<WorkflowMappingDefinition> Mappings)
{
    /// <summary>The conversion of each wire mapping whose source and target terms are known, by target input.</summary>
    public IReadOnlyDictionary<string, PortConversion> Conversions { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, PortConversion>.Empty;

    /// <summary>Each literal mapping's value in its input's CLR type, by target input; an input with no closed type has none.</summary>
    public IReadOnlyDictionary<string, object?> Literals { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty;

    internal IReadOnlyDictionary<string, CompiledProjection> Projections { get; init; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, CompiledProjection>.Empty;
}

/// <summary>One declared sub-canvas output port, exactly as much of it as
/// <c>InlineSubCanvasExecutor</c>'s convergence projection reads at run time: which branch's raw output key
/// (<see cref="Sources"/>, by sub-canvas name) becomes this parent port (<see cref="Name"/>).</summary>
public sealed record CompiledOutputPort(string Name, IReadOnlyDictionary<string, string> Sources);

/// <summary>A node's own resilience configuration, read exactly as the author left it — never resolved
/// against a host's executor options or a block's idempotence, which only the second step can answer with
/// its own settings in hand. Each member is null exactly when the node never configured that key; a
/// configured value equal to what <c>WorkflowGraphExecutor</c> would otherwise default to is still a
/// configured value, not an absent one, and the two read differently here on purpose.</summary>
public sealed record ConfiguredResilience(
    int? TimeoutSeconds, bool? RetryEnabled, int? RetryCount, double? RetryBackoffSeconds);
