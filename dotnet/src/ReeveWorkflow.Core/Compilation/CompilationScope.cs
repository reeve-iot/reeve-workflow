using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal sealed record ScopeEntry(string NodeId, IReadOnlyList<EffectivePort>? Outputs, bool IsFrameInput);

internal enum ScopeVisibility
{
    NotInScope,

    Unresolved,

    Known,
}

internal sealed class CompilationScope
{
    private readonly List<ScopeEntry> entries;
    private readonly Dictionary<string, int> positionById;

    private CompilationScope(IReadOnlyList<string> order, IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        entries = new List<ScopeEntry>(order.Count);
        positionById = new Dictionary<string, int>(order.Count, StringComparer.Ordinal);
        foreach (var nodeId in order)
        {
            positionById.Add(nodeId, entries.Count);
            var isInput = nodesById.TryGetValue(nodeId, out var node) && node.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId;
            entries.Add(new ScopeEntry(nodeId, null, isInput));
        }
    }

    public static CompilationScope Over(IReadOnlyList<string> order, IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById) =>
        new(order, nodesById);

    public void Publish(string nodeId, IReadOnlyList<EffectivePort>? outputs)
    {
        if (positionById.TryGetValue(nodeId, out var index))
            entries[index] = entries[index] with { Outputs = outputs };
    }

    public ScopeVisibility TryResolve(string nodeId, string publisherId, out IReadOnlyList<EffectivePort> outputs)
    {
        outputs = [];

        if (!positionById.TryGetValue(nodeId, out var referencingIndex))
        {
            throw new ArgumentException(
                $"'{nodeId}' is not a node this frame's own canvas walk reached.", nameof(nodeId));
        }

        if (!positionById.TryGetValue(publisherId, out var publisherIndex))
            return ScopeVisibility.NotInScope;

        var entry = entries[publisherIndex];
        var isSelf = string.Equals(publisherId, nodeId, StringComparison.Ordinal);
        if (isSelf || !(entry.IsFrameInput || publisherIndex < referencingIndex))
            return ScopeVisibility.NotInScope;

        if (entry.Outputs is null)
            return ScopeVisibility.Unresolved;

        outputs = entry.Outputs;
        return ScopeVisibility.Known;
    }
}
