using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Pins the root workflow and every transitively-referenced sub-workflow to the version current at
/// root-start. A per-workflow dedup set guarantees termination, so cycles need no count cap.</summary>
public static class WorkflowVersionManifestBuilder
{
    /// <summary>Builds a root-start snapshot of the root and every reachable published sub-workflow version.</summary>
    /// <param name="fetchCurrent">Resolves a child's current published version, or null when it has none.</param>
    /// <param name="cancellationToken">Cancels traversal and child lookups.</param>
    /// <remarks>An unbindable definition pins nothing below it; admission's compile then refuses it.</remarks>
    public static Task<IReadOnlyDictionary<Guid, Guid>> BuildAsync(
        Guid rootWorkflowId, Guid rootVersionId, string rootDefinitionJson,
        Func<Guid, CancellationToken, Task<(Guid VersionId, string DefinitionJson)?>> fetchCurrent,
        CancellationToken cancellationToken) =>
        BuildAsync(rootWorkflowId, rootVersionId,
            WorkflowVersionDataCache.Get(rootVersionId, rootDefinitionJson), fetchCurrent, cancellationToken);

    internal static async Task<IReadOnlyDictionary<Guid, Guid>> BuildAsync(
        Guid rootWorkflowId,
        Guid rootVersionId,
        WorkflowVersionDataCache.Entry rootVersion,
        Func<Guid, CancellationToken, Task<(Guid VersionId, string DefinitionJson)?>> fetchCurrent,
        CancellationToken cancellationToken)
    {
        var manifest = new Dictionary<Guid, Guid> { [rootWorkflowId] = rootVersionId };
        var visited = new HashSet<Guid> { rootWorkflowId };
        var queue = new Queue<WorkflowVersionDataCache.Entry>();
        queue.Enqueue(rootVersion);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var def = queue.Dequeue().Definition;
            if (def == null)
                continue;

            foreach (var childId in CollectSubWorkflowIds(def))
            {
                if (!visited.Add(childId))
                    continue;

                var fetched = await fetchCurrent(childId, cancellationToken).ConfigureAwait(false);
                if (fetched == null)
                    continue;

                manifest[childId] = fetched.Value.VersionId;
                queue.Enqueue(WorkflowVersionDataCache.Get(fetched.Value.VersionId, fetched.Value.DefinitionJson));
            }
        }

        return manifest;
    }

    private static IEnumerable<Guid> CollectSubWorkflowIds(WorkflowDefinition definition)
    {
        foreach (var node in definition.Nodes)
        {
            if (string.Equals(node.BlockTypeId, WorkflowGraphHelpers.SubWorkflowBlockTypeId, StringComparison.OrdinalIgnoreCase)
                && node.Config != null
                && node.Config.TryGetValue("WorkflowId", out var el)
                && el.ValueKind == JsonValueKind.String
                && Guid.TryParse(el.GetString(), out var childId))
            {
                yield return childId;
            }

            if (node.SubCanvases != null)
            {
                foreach (var sub in node.SubCanvases.Values)
                {
                    foreach (var nested in CollectSubWorkflowIds(sub))
                        yield return nested;
                }
            }
        }
    }
}
