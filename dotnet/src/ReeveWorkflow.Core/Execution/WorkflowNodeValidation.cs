using System.Collections.ObjectModel;
using System.Text.Json;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal static class WorkflowNodeValidation
{
    internal static WorkflowNodeValidationRequest Request(
        WorkflowVersion version, WorkflowProgram program, WorkflowValidationPhase phase)
    {
        var root = WorkflowVersionDataCache.Get(version).Definition;
        var nodes = new List<WorkflowNodeToValidate>();
        foreach (var canvas in program.Canvases)
        {
            var definition = DefinitionAt(root, canvas.CanvasPath);
            if (definition is null)
                continue;
            var stored = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);
            foreach (var node in definition.Nodes)
                stored.TryAdd(node.Id, node);

            foreach (var compiled in canvas.Nodes)
            {
                if (stored.TryGetValue(compiled.NodeId, out var node))
                    nodes.Add(NodeOf(canvas.CanvasPath, node, compiled));
            }
        }

        return new WorkflowNodeValidationRequest(version.WorkflowId, version.Id, phase, nodes);
    }

    private static WorkflowDefinition? DefinitionAt(WorkflowDefinition? root, CanvasPath path)
    {
        var definition = root;
        foreach (var segment in path.Segments)
        {
            definition = definition?.Nodes.FirstOrDefault(n => n.Id == segment.NodeId)?.SubCanvases is { } branches
                && branches.TryGetValue(segment.BranchName, out var branch)
                ? branch
                : null;
        }

        return definition;
    }

    private static WorkflowNodeToValidate NodeOf(CanvasPath path, WorkflowNodeDefinition node, CompiledNode compiled)
    {
        var literals = new Dictionary<string, WorkflowNodeLiteral>(StringComparer.Ordinal);
        foreach (var edge in compiled.Incoming)
        {
            foreach (var mapping in edge.Mappings)
            {
                if (!mapping.IsLiteral
                    || ExpressionSource.IsExpression(mapping.LiteralValue as string)
                    || !compiled.InputTypes.TryGetValue(mapping.TargetInput, out var type))
                {
                    continue;
                }

                literals[mapping.TargetInput] = new WorkflowNodeLiteral(
                    JsonSerializer.SerializeToElement(mapping.LiteralValue), type.Term.ToString());
            }
        }

        return new WorkflowNodeToValidate(
            path, node.Id, node.BlockTypeId,
            new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>(node.Config ?? [], StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, WorkflowNodeLiteral>(literals),
            new ReadOnlyDictionary<string, string>(
                compiled.TypeArguments.ToDictionary(a => a.Key, a => a.Value.Term.ToString(), StringComparer.Ordinal)));
    }
}
