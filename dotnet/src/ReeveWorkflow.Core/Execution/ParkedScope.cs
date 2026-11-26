using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal static class ParkedScope
{
    public static (CompiledNode Node, Dictionary<string, IReadOnlyDictionary<string, object?>> Outputs, IReadOnlyDictionary<string, object?>? Parameters) Of(
        CompiledWalk root, Dictionary<string, IReadOnlyDictionary<string, object?>> rootOutputs, IReadOnlyList<WorkflowContinuationFrame> frames,
        string rootNode, string blockId, string? inputNodeId)
    {
        if (frames.Count == 0)
        {
            var node = root.Node(blockId)
                ?? throw new PersistedPayloadException("outputs", blockId, null, "the pinned program has no such node");
            var parameters = WorkflowExecutionHelpers.ParameterScope(
                WorkflowExecutionHelpers.RunParametersOf(rootOutputs, inputNodeId ?? InputNodeOf(root.Canvas)),
                root.Canvas.Signature);
            return (node, rootOutputs, parameters);
        }

        var level = CompiledWalk.LevelOf(root, frames, rootNode)
            ?? throw new PersistedPayloadException("frame", rootNode, null, "the pinned program has no such level");
        var levelNode = level.Node(blockId)
            ?? throw new PersistedPayloadException("frame", blockId, null, "the pinned program has no such node");
        var levelOutputs = PersistedValueCodec.ReadNodeOutputs(frames[^1].PreviousOutputsJson, level.Canvas, "frame");
        var levelParameters = WorkflowExecutionHelpers.ParameterScope(
            WorkflowExecutionHelpers.RunParametersOf(levelOutputs, InputNodeOf(level.Canvas)), level.Canvas.Signature);
        return (levelNode, levelOutputs, levelParameters);
    }

    private static string? InputNodeOf(CompiledCanvas canvas) =>
        canvas.Nodes.FirstOrDefault(n => n.Shape.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId)?.NodeId;
}
