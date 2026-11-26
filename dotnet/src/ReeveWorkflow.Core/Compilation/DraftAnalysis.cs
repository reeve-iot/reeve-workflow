using System.Text.Json.Serialization;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>What the binding pass makes of a draft, as <see cref="WorkflowCompilationService.AnalyzeAsync"/> answers it: the
/// diagnostics a save would report, and the ports of every node the pass typed.</summary>
/// <param name="RegistryGeneration">The generation of the port-type vocabulary the draft was compiled under, the one
/// <c>GET {root}/port-types</c> answers.</param>
/// <param name="Diagnostics">The compile's diagnostics, in the compiler's order; empty when the draft compiles.</param>
/// <param name="Nodes">Every node the pass typed, at every canvas depth, canvas by canvas in document order; empty when the
/// definition or a parameter list does not read.</param>
public sealed record DraftAnalysis(
    long RegistryGeneration, IReadOnlyList<CompilationDiagnostic> Diagnostics, IReadOnlyList<NodeAnalysis> Nodes);

/// <summary>One node of a <see cref="DraftAnalysis"/>.</summary>
/// <param name="CanvasPath">The canvas the node stands on, empty at the root, in the shape of
/// <see cref="CompilationDiagnostic.CanvasPath"/>.</param>
/// <param name="NodeId">The node's id on that canvas.</param>
/// <param name="TypeArguments">Every type parameter of the node's block with the document term it ends bound to, null where
/// nothing binds it or a reported refusal stands in the way.</param>
/// <param name="Inputs">The inputs the block, the node's dynamic ports, its resolver or its canvas declare, then each name only a
/// <c>passthrough</c> entry gives, typed by its feed.</param>
/// <param name="Outputs">The outputs declared the same way, the node's own declared outputs and <c>_error</c> included, then each
/// such passthrough name; empty when the node's ports could not be resolved.</param>
public sealed record NodeAnalysis(
    IReadOnlyList<CanvasPathSegment> CanvasPath, string NodeId, IReadOnlyDictionary<string, string?> TypeArguments,
    IReadOnlyList<PortAnalysis> Inputs, IReadOnlyList<PortAnalysis> Outputs);

/// <summary>The three states of <see cref="PortAnalysis.State"/>.</summary>
public static class PortAnalysisStates
{
    /// <summary>The port ends with a closed term.</summary>
    public const string Bound = "bound";

    /// <summary>The declared term names a type parameter nothing binds.</summary>
    public const string Unbound = "unbound";

    /// <summary>A reported refusal stands in the way of the port's term.</summary>
    public const string Refused = "refused";
}

/// <summary>One port of a <see cref="NodeAnalysis"/>.</summary>
/// <param name="Name">The port's name.</param>
/// <param name="Declared">The term as the declaration states it, which may name a type parameter (<c>Collection&lt;$T?&gt;</c>);
/// a collecting block's declared output is <c>Collection&lt;T?&gt;</c> over its stored type. A stored type that does not parse is
/// repeated as written. Null for a name only a <c>passthrough</c> entry gives, whose term is its feed's.</param>
/// <param name="Effective">The closed document term after binding; null unless <paramref name="State"/> is
/// <see cref="PortAnalysisStates.Bound"/>. For an <c>Expression&lt;T&gt;</c> input it is <c>T</c>, the term the expression's
/// result must fit.</param>
/// <param name="State">One of <see cref="PortAnalysisStates"/>.</param>
/// <param name="Unbound">The parameters the declared term names that nothing binds, without the <c>$</c>; empty unless the port is
/// unbound.</param>
/// <param name="Expression">The result term of the <c>expr:</c> value, config value or literal mapping, that feeds the input; absent
/// for an output and for an input no expression feeds.</param>
/// <param name="Projection">The term a projection wire into the input delivers: the type the wire states, else the input's
/// effective term; absent for an output and for an input no projection wire feeds.</param>
public sealed record PortAnalysis(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Declared,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Effective,
    string State,
    IReadOnlyList<string> Unbound,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Expression = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Projection = null);

internal static class DraftAnalyzer
{
    public static IReadOnlyList<NodeAnalysis> Nodes(CompilationContext context)
    {
        var nodes = new List<NodeAnalysis>();
        var seen = new HashSet<NodeKey>();
        foreach (var frame in CanvasWalk.Frames(context.Document.Definition))
        {
            if (context.Ambiguous.Contains(frame.Path))
                continue;

            foreach (var node in frame.Canvas.Nodes)
            {
                var key = new NodeKey(frame.Path, node.Id);
                if (!context.Types.TryGetValue(key, out var types) || !context.Bound.TryGetValue(key, out var shape) || !seen.Add(key))
                    continue;

                var isOutputNode = node.BlockTypeId == WorkflowGraphHelpers.WorkflowOutputBlockTypeId;
                IReadOnlyList<EffectivePort> settled = [];
                if (isOutputNode && context.BodyOutputs.TryGetValue(frame.Path, out var held))
                    settled = held;
                var typeArguments = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var parameter in shape.TypeParameters)
                    typeArguments[parameter.Name] = types.Bindings.GetValueOrDefault(parameter.Name)?.ToString();

                nodes.Add(new NodeAnalysis(
                    frame.Path.Segments, node.Id, typeArguments,
                    [.. types.Inputs.Select(p => Port(settled.FirstOrDefault(s => s.Name == p.Name) ?? p, types, isInput: true)).OfType<PortAnalysis>()],
                    [.. (types.Outputs ?? []).Select(p => Port(p, types, isInput: false)).OfType<PortAnalysis>()]));
            }
        }

        return nodes;
    }

    private static PortAnalysis? Port(EffectivePort port, NodeTypes types, bool isInput)
    {
        if (port.Declared is "")
            return null;

        var term = port.State == PortState.Typed ? port.Term : null;
        if (term is { Kind: PortTermKind.Constructor, IsNullable: false, Name: "Expression" } && isInput)
            term = term.Arguments[0];

        string? expression = null, projection = null;
        if (isInput)
        {
            if (types.Expressions.TryGetValue(port.Name, out var tree) && tree.Term.Kind != PortTermKind.Null)
                expression = tree.Term.ToString();
            if (types.Projections.TryGetValue(port.Name, out var stated))
                projection = (stated ?? term)?.ToString();
        }

        return new PortAnalysis(
            port.Name, port.Declared, term?.ToString(),
            port.State switch
            {
                PortState.Typed => PortAnalysisStates.Bound,
                PortState.Unbound => PortAnalysisStates.Unbound,
                _ => PortAnalysisStates.Refused,
            },
            port.State == PortState.Unbound ? port.UnboundParameters : [], expression, projection);
    }
}
