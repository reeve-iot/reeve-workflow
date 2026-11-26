using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal static class WorkflowIdempotence
{
    public static bool Node(WorkflowNodeDefinition node, IWorkflowBlocks blocks) =>
        Node(node.BlockTypeId, blocks);

    public static bool Node(string blockTypeId, IWorkflowBlocks blocks) =>
        BoundaryBlockShapes.IsBoundary(blockTypeId) || (blocks.Find(blockTypeId)?.IsIdempotent ?? false);

    public static bool Whole(WorkflowNodeDefinition node, IWorkflowBlocks blocks) =>
        (Node(node, blocks) || OnlyDrivesItsSubCanvases(node, blocks))
        && (node.SubCanvases?.Values.All(canvas => canvas.Nodes.All(inner => Whole(inner, blocks))) ?? true);

    public static bool Whole(CompiledWalk walk, CompiledNode node, IWorkflowBlocks blocks) =>
        (Node(node.Shape.BlockTypeId, blocks) || OnlyDrivesItsSubCanvases(node, blocks))
        && node.SubCanvases.Keys.All(name => walk.Child(node.NodeId, name) is { } child
            && child.Canvas.Nodes.All(inner => Whole(child, inner, blocks)));

    public static bool OnlyDrivesItsSubCanvases(WorkflowNodeDefinition node, IWorkflowBlocks blocks) =>
        blocks.Find(node.BlockTypeId) is { OnlyDrivesItsSubCanvases: true } block
        && (node.SubCanvases is { Count: > 0 } || block.SubCanvasNames.Count > 0);

    public static bool OnlyDrivesItsSubCanvases(CompiledNode node, IWorkflowBlocks blocks) =>
        blocks.Find(node.Shape.BlockTypeId) is { OnlyDrivesItsSubCanvases: true }
        && node.SubCanvases.Count > 0;
}
