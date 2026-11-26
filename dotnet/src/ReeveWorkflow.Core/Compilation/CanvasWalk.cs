using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal readonly record struct CanvasFrame(CanvasPath Path, WorkflowDefinition Canvas)
{
    public CanvasFrame Child(string nodeId, string subCanvasName, WorkflowDefinition canvas) =>
        new(Path.Child(nodeId, subCanvasName), canvas);
}

internal static class CanvasWalk
{
    public static IEnumerable<CanvasFrame> Frames(WorkflowDefinition root) => Frames(new CanvasFrame(CanvasPath.Root, root));

    private static IEnumerable<CanvasFrame> Frames(CanvasFrame frame)
    {
        yield return frame;

        foreach (var node in frame.Canvas.Nodes)
            foreach (var (name, sub) in node.SubCanvases ?? [])
                foreach (var childFrame in Frames(frame.Child(node.Id, name, sub)))
                    yield return childFrame;
    }
}
