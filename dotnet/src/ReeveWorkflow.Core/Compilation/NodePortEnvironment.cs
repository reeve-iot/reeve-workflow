using System.Collections.Frozen;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>Ports resolved outside the compiler, keyed by the same canvas path and node id every phase in
/// this namespace already addresses a node by — a referenced workflow's parameters, a host's discovered
/// ports. Immutable once built: <see cref="Empty"/> is a genuinely empty, shared value, never a mutable
/// dictionary a caller could file an entry into and poison every compile that falls back to it.</summary>
/// <remarks>Built only through <see cref="NodePortEnvironmentBuilder"/>, so a filler spells a node's address
/// once, the one way this namespace ever joins it, rather than building its own keyed dictionary against a
/// join format it would have to guess at.</remarks>
public sealed class NodePortEnvironment
{
    public static NodePortEnvironment Empty { get; } = new(FrozenDictionary<NodeKey, NodePortResolution>.Empty);

    private readonly FrozenDictionary<NodeKey, NodePortResolution> resolved;
    private readonly IReadOnlyList<KeyValuePair<NodeKey, NodePortResolution>> entries;

    internal NodePortEnvironment(FrozenDictionary<NodeKey, NodePortResolution> resolved)
    {
        this.resolved = resolved;
        entries = [.. resolved];
    }

    /// <summary>What a resolver filed for this node, or null when none did — a node whose shape needs one
    /// and finds nothing here is refused, never waved through with its block's own (absent) ports.</summary>
    public NodePortResolution? For(CanvasPath canvasPath, string nodeId) =>
        resolved.TryGetValue(new NodeKey(canvasPath, nodeId), out var resolution) ? resolution : null;

    internal IReadOnlyList<KeyValuePair<NodeKey, NodePortResolution>> Entries => entries;
}

/// <summary>The mutable half of the "mutable builder, immutable result" pair: gathers a resolver's answers
/// one <see cref="Add"/> at a time, then freezes them into a <see cref="NodePortEnvironment"/> nothing can
/// mutate afterward. A host builds one per compile rather than sharing a filled instance across calls. An answer's
/// terms are graded by the compile that reads them, as a resolver's answer, never here.</summary>
public sealed class NodePortEnvironmentBuilder
{
    private readonly Dictionary<NodeKey, NodePortResolution> resolved = [];

    /// <summary>Files a resolver's answer for one node: its canvas path — <see cref="CanvasPath.Root"/> at
    /// the root, a parent's own path otherwise — and the node's own id. A later call for the same address
    /// replaces the earlier one. Returns this builder, so a caller can chain several without a local
    /// variable per node.</summary>
    public NodePortEnvironmentBuilder Add(CanvasPath canvasPath, string nodeId, NodePortResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(canvasPath);
        ArgumentNullException.ThrowIfNull(nodeId);

        if (resolution is { Found: false, FailureReason: null })
            throw new ArgumentException("A resolution must be built through Resolved or Failed.", nameof(resolution));

        resolved[new NodeKey(canvasPath, nodeId)] = resolution;
        return this;
    }

    public NodePortEnvironment Build() => new(resolved.ToFrozenDictionary());
}

/// <summary>What a resolver answers with: a signature and the reference it answered for, or the reason a
/// human should read for why it could not produce one. <see cref="Resolved"/> and <see cref="Failed"/> are
/// the only names that construct a coherent value; the struct's own <c>default</c> is still reachable
/// (a host's <see cref="INodePortResolver.ResolveAsync"/> returning it, say) and is what
/// <see cref="NodePortEnvironmentBuilder.Add"/> exists to refuse.</summary>
public readonly record struct NodePortResolution
{
    private NodePortResolution(string? reference, CanvasSignature? signature, string? failureReason)
    {
        Reference = reference;
        Signature = signature;
        FailureReason = failureReason;
    }

    /// <summary>True once <see cref="Signature"/> is set — computed, not stored, so the two can never say
    /// different things about the same resolution.</summary>
    public bool Found => Signature is not null;

    /// <summary>The callee this answer is about, for a block with a reference input — <c>null</c> for a
    /// <see cref="Failed"/> resolution, or for a resolver answering a block with no reference to name at
    /// all. Matched ordinally, never case-folded, against a node's effective reference —
    /// <see cref="EffectivePorts.EffectiveReference"/> — and a resolution filed for one callee must never
    /// be trusted for a node whose effective reference turns out to be a different one.</summary>
    public string? Reference { get; }

    public CanvasSignature? Signature { get; }

    /// <summary>Set only on a <see cref="Failed"/> resolution — a resolver that claimed
    /// <see cref="INodePortResolver.BlockTypeId"/> and then could not describe this particular node still has
    /// to say why, distinct from a resolver that never claimed the block type at all.</summary>
    public string? FailureReason { get; }

    public static NodePortResolution Resolved(string? reference, CanvasSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        return new NodePortResolution(reference, signature, null);
    }

    public static NodePortResolution Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new NodePortResolution(null, null, reason);
    }
}

/// <summary>What a resolver is asked about: the node and its shape, where it sits, and the reference the
/// caller already worked out. <see cref="EffectiveReference"/> is the node's own configured string for the
/// block's <see cref="BlockShape.RunsWorkflowFrom"/> input, and null when the shape names no reference input.
/// A node whose reference input any mapping feeds is refused (<see cref="CompilationRules.ReferenceInputMapped"/>)
/// and never reaches a resolver, and neither does one whose configured reference is missing or empty.</summary>
public sealed record NodePortResolutionRequest(
    CanvasPath CanvasPath, WorkflowNodeDefinition Node, BlockShape Shape, string? EffectiveReference);

/// <summary>The seam a host implements to answer for a node whose block says <c>PortsAreResolvedElsewhere</c>
/// — a referenced workflow's parameters, or ports only the host can see. <see cref="BlockTypeId"/> is the
/// selector a caller matches case-insensitively before ever invoking <see cref="ResolveAsync"/>, mirroring
/// how the catalogue itself matches a block type id (<c>CatalogueBlockShapeSource</c>) — one resolver answers
/// for one block type, never a wildcard. The task that calls this seam is meant to sum the library's own
/// resolvers with a host's, the same union pattern the type-widening table uses, filling a
/// <see cref="NodePortEnvironmentBuilder"/> before the (pure) compiler ever runs. Async because answering may
/// take I/O the compiler itself is never allowed.</summary>
public interface INodePortResolver
{
    /// <summary>The block type id this resolver answers for. Matched case-insensitively, like the catalogue.</summary>
    string BlockTypeId { get; }

    Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken);
}
