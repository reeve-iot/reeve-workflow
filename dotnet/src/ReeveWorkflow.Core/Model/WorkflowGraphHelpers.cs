using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReeveWorkflow.Core.Model;

/// <summary>Everything that can be decided from a workflow definition alone: acyclicity, the single
/// start point, reachability, edge mappings, and the JSON coercion those answers are read through.</summary>
public static class WorkflowGraphHelpers
{
    public const string WorkflowInputBlockTypeId = "WorkflowInput";
    public const string WorkflowOutputBlockTypeId = "WorkflowOutput";
    public const string SubWorkflowBlockTypeId = "SubWorkflow";

    /// <summary>The width of the node log's own two text columns — <c>execution_node_log.NodeId</c> and
    /// <c>.NodeType</c> — which is what a node's id and its block type's id must fit. The same two columns of
    /// <c>workflow_execution_log_entries</c> are unbounded; this table's are not widened, because it is the one
    /// the Timescale add-on converts, where some TimescaleDB versions refuse the <c>ALTER COLUMN … TYPE</c> a widening
    /// is. Whether a given version refuses it is the server's answer and no version's answer is a contract, so the
    /// width is left alone; since it does not move,
    /// <see cref="ReeveWorkflow.Core.Compilation.WorkflowCompiler"/> refuses a graph that does not fit it, rather
    /// than storing one whose step write fails on the day it is outgrown.</summary>
    public const int NodeLogTextLimit = 100;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IEnumerable<WorkflowMappingDefinition> ReadMappings(JsonElement mappings)
    {
        if (mappings.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var property in mappings.EnumerateObject())
        {
            var targetInput = property.Name;
            var mapping = property.Value;
            if (mapping.ValueKind != JsonValueKind.Object)
                continue;

            var mappingType = mapping.TryGetProperty("type", out var typeEl)
                ? typeEl.GetString()
                : null;

            if (string.Equals(mappingType, "literal", StringComparison.OrdinalIgnoreCase))
            {
                var literalValue = mapping.TryGetProperty("literalValue", out var literalEl)
                    ? JsonToValue(literalEl)
                    : null;
                yield return new WorkflowMappingDefinition(string.Empty, targetInput, true, literalValue, ReadProjection(mapping));
                continue;
            }

            var sourceOutput = mapping.TryGetProperty("sourceOutputName", out var sourceOutputNameEl)
                ? sourceOutputNameEl.GetString() ?? string.Empty
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(sourceOutput))
                yield return new WorkflowMappingDefinition(sourceOutput, targetInput, Projection: ReadProjection(mapping));
        }
    }

    private static WorkflowProjection? ReadProjection(JsonElement mapping)
    {
        if (!mapping.TryGetProperty("projection", out var projection) || projection.ValueKind != JsonValueKind.Object)
            return null;
        static string? Text(JsonElement owner, string key) =>
            owner.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new WorkflowProjection(Text(projection, "path") ?? "", Text(projection, "mode"), Text(projection, "type"));
    }

    public static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal static void RequireNoOutputMappings(WorkflowDefinition definition)
    {
        if (definition.OutputMappings is not null)
            throw new WorkflowValidationException(
                "Workflow definition holds outputMappings, which no run reads (rule output-mappings-unread); "
                + "an output is a wire into the WorkflowOutput node.");
    }

    internal static void RequireConnectionEndpoints(IEnumerable<WorkflowConnectionDefinition>? connections)
    {
        foreach (var connection in connections ?? [])
        {
            if (connection is null)
                throw new WorkflowValidationException("Workflow definition holds a null connection.");
            if (connection.SourceNodeId is null)
                throw new WorkflowValidationException($"Connection '{connection.Id}' has a null sourceNodeId; a connection names the node it leaves.");
            if (connection.TargetNodeId is null)
                throw new WorkflowValidationException($"Connection '{connection.Id}' has a null targetNodeId; a connection names the node it enters.");
        }
    }

    public static IReadOnlyList<string> TopologicalSort(WorkflowDefinition definition)
    {
        EnsureUniqueNodeIds(definition);
        RequireConnectionEndpoints(definition.GetConnections());
        var graph = new Dictionary<string, List<string>>();
        var inDegree = new Dictionary<string, int>();

        foreach (var node in definition.Nodes)
        {
            graph[node.Id] = [];
            inDegree[node.Id] = 0;
        }

        var connections = definition.GetConnections();
        if (connections != null)
        {
            foreach (var conn in connections)
            {
                if (graph.ContainsKey(conn.SourceNodeId) && inDegree.ContainsKey(conn.TargetNodeId))
                {
                    graph[conn.SourceNodeId].Add(conn.TargetNodeId);
                    inDegree[conn.TargetNodeId]++;
                }
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var result = new List<string>();

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            result.Add(nodeId);

            foreach (var neighbor in graph[nodeId])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (result.Count != definition.Nodes.Count)
            throw new WorkflowValidationException("Workflow contains a cycle.");

        return result;
    }

    /// <summary>Root-canvas-only (depth 0): exactly one in-degree-0 node, which must be the WorkflowInput
    /// node, and every node reachable from it. Acyclicity is enforced separately by <see cref="TopologicalSort"/>.</summary>
    public static void ValidateStartAndReachability(WorkflowDefinition definition)
    {
        var nodes = definition.Nodes;
        EnsureUniqueNodeIds(definition);
        RequireConnectionEndpoints(definition.GetConnections());
        if (nodes.Count == 0)
            return;

        var inDegree = nodes.ToDictionary(n => n.Id, _ => 0);
        var adjacency = nodes.ToDictionary(n => n.Id, _ => new List<string>());

        var connections = definition.GetConnections();
        if (connections != null)
        {
            foreach (var conn in connections)
            {
                if (adjacency.ContainsKey(conn.SourceNodeId) && inDegree.ContainsKey(conn.TargetNodeId))
                {
                    inDegree[conn.TargetNodeId]++;
                    adjacency[conn.SourceNodeId].Add(conn.TargetNodeId);
                }
            }
        }

        var inputNode = nodes.FirstOrDefault(n => n.BlockTypeId == WorkflowInputBlockTypeId);
        if (inputNode is null)
            throw new WorkflowValidationException(
                "Workflow has no start node. Add a Workflow Input node as the single entry point.");

        if (inDegree[inputNode.Id] != 0)
            throw new WorkflowValidationException(
                "The Workflow Input node must be the single start point and cannot have incoming connections.");

        var extraRoots = nodes.Where(n => n.Id != inputNode.Id && inDegree[n.Id] == 0).Select(n => n.Id).ToList();
        if (extraRoots.Count > 0)
            throw new WorkflowValidationException(
                "Workflow must have a single start point (the Workflow Input node). " +
                $"These nodes have no incoming connection: {string.Join(", ", extraRoots)}.");

        var reachable = new HashSet<string> { inputNode.Id };
        var queue = new Queue<string>();
        queue.Enqueue(inputNode.Id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in adjacency[current])
            {
                if (reachable.Add(next))
                    queue.Enqueue(next);
            }
        }

        var unreachable = nodes.Where(n => !reachable.Contains(n.Id)).Select(n => n.Id).ToList();
        if (unreachable.Count > 0)
            throw new WorkflowValidationException(
                $"These nodes are not reachable from the Workflow Input start node: {string.Join(", ", unreachable)}.");
    }

    private static void EnsureUniqueNodeIds(WorkflowDefinition definition)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes)
        {
            if (!seen.Add(node.Id))
                throw new WorkflowValidationException(
                    $"Workflow definition contains a duplicate node id '{node.Id}' (rule duplicate-node-id).");
        }
    }

    /// <summary>At a join, a single target input must not be fed by more than one incoming edge — the host
    /// applies mappings last-edge-wins, so an undetected double-mapping is silently ambiguous.</summary>
    public static void ValidateNoDuplicateInputMappings(WorkflowDefinition definition)
    {
        var connections = definition.GetConnections();
        RequireConnectionEndpoints(connections);
        if (connections == null)
            return;

        var nodeIds = definition.Nodes.Select(n => n.Id).ToHashSet();

        var byTarget = new Dictionary<string, Dictionary<string, int>>();
        foreach (var conn in connections)
        {
            if (!nodeIds.Contains(conn.TargetNodeId))
                continue;

            if (conn.Mappings is null)
                continue;

            var seenThisEdge = new HashSet<string>();
            foreach (var mapping in ReadMappings(conn.Mappings.Value))
            {
                if (!seenThisEdge.Add(mapping.TargetInput))
                    continue;

                if (!byTarget.TryGetValue(conn.TargetNodeId, out var inputs))
                    byTarget[conn.TargetNodeId] = inputs = new Dictionary<string, int>();

                inputs[mapping.TargetInput] = inputs.GetValueOrDefault(mapping.TargetInput) + 1;
            }
        }

        var collisions = byTarget
            .SelectMany(t => t.Value.Where(i => i.Value > 1).Select(i => $"input '{i.Key}' on node '{t.Key}'"))
            .ToList();

        if (collisions.Count > 0)
            throw new WorkflowValidationException(
                "A node input must be fed by a single incoming connection. " +
                $"These inputs receive values from multiple parents: {string.Join("; ", collisions)}.");
    }

    /// <summary>Root-canvas structural gate: single start + reachability, and no join input-collision.
    /// Call only at depth 0; acyclicity is enforced separately by <see cref="TopologicalSort"/>.</summary>
    public static void ValidateRootCanvas(WorkflowDefinition definition)
    {
        RequireNoOutputMappings(definition);
        ValidateStartAndReachability(definition);
        ValidateNoDuplicateInputMappings(definition);
    }

    /// <summary>Enforces that a SubWorkflow node selects its child via a fixed config value, never a wired
    /// edge or expression — this makes the run's version manifest complete by construction. Recurses into sub-canvases.</summary>
    public static void ValidateSubWorkflowReferencesStatic(WorkflowDefinition definition)
    {
        var connections = definition.GetConnections();

        foreach (var node in definition.Nodes)
        {
            if (string.Equals(node.BlockTypeId, SubWorkflowBlockTypeId, StringComparison.OrdinalIgnoreCase))
            {
                if (connections != null)
                {
                    foreach (var conn in connections.Where(c => c.TargetNodeId == node.Id))
                    {
                        if (conn.Mappings is null)
                            continue;
                        foreach (var mapping in ReadMappings(conn.Mappings.Value))
                        {
                            if (string.Equals(mapping.TargetInput, "WorkflowId", StringComparison.Ordinal))
                                throw new WorkflowValidationException(
                                    $"Sub-Workflow node '{node.Id}' must select its workflow in the side panel; " +
                                    "wiring an edge into the WorkflowId input is not allowed.");
                        }
                    }
                }

                if (node.Config == null
                    || !node.Config.TryGetValue("WorkflowId", out var el)
                    || el.ValueKind != JsonValueKind.String)
                    throw new WorkflowValidationException(
                        $"Sub-Workflow node '{node.Id}' must have a workflow selected in the side panel.");

                var raw = el.GetString();
                if (string.IsNullOrWhiteSpace(raw)
                    || raw.Contains("${", StringComparison.Ordinal)
                    || !Guid.TryParse(raw, out _))
                    throw new WorkflowValidationException(
                        $"Sub-Workflow node '{node.Id}' WorkflowId must be a fixed workflow id, not an expression.");
            }

            if (node.SubCanvases != null)
            {
                foreach (var sub in node.SubCanvases.Values)
                    ValidateSubWorkflowReferencesStatic(sub);
            }
        }
    }

    /// <summary>Recursively applies the start, reachability and duplicate-input checks of <see cref="ValidateRootCanvas"/> to every
    /// canvas; unlike it, this says nothing about <c>OutputMappings</c>. The runtime validates only the root canvas — this is a build-time-only guard.</summary>
    public static void ValidateAllCanvases(WorkflowDefinition definition)
    {
        ValidateStartAndReachability(definition);
        ValidateNoDuplicateInputMappings(definition);

        foreach (var node in definition.Nodes)
        {
            if (node.SubCanvases is null)
                continue;

            foreach (var subCanvas in node.SubCanvases.Values)
                ValidateAllCanvases(subCanvas);
        }
    }

    internal static object? JsonToValue(JsonElement element) => ReadJsonElementValue(element);

    public static HashSet<string> GetDownstreamNodes(string nodeId, IReadOnlyCollection<WorkflowConnectionDefinition>? connections)
    {
        var downstreamNodes = new HashSet<string>();
        if (connections == null || connections.Count == 0)
            return downstreamNodes;

        var queue = new Queue<string>();
        queue.Enqueue(nodeId);
        var visited = new HashSet<string> { nodeId };

        while (queue.Count > 0)
        {
            var currentNode = queue.Dequeue();
            foreach (var conn in connections.Where(c => c.SourceNodeId == currentNode))
            {
                if (!visited.Contains(conn.TargetNodeId))
                {
                    downstreamNodes.Add(conn.TargetNodeId);
                    visited.Add(conn.TargetNodeId);
                    queue.Enqueue(conn.TargetNodeId);
                }
            }
        }

        return downstreamNodes;
    }

    private static object? ReadJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => ReadJsonElementValue(p.Value)),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ReadJsonElementValue)
                .ToList(),
            _ => element.GetRawText()
        };
    }
}
