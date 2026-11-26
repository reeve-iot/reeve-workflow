using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal sealed class CompiledWalk
{
    private readonly Dictionary<string, CompiledNode> nodes;
    private readonly Dictionary<CanvasPath, CompiledWalk> walks;

    private CompiledWalk(CompiledCanvas canvas, Dictionary<CanvasPath, CompiledWalk> walks)
    {
        Canvas = canvas;
        this.walks = walks;
        nodes = canvas.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
    }

    public CompiledCanvas Canvas { get; }

    internal string? InputNodeId => Canvas.Nodes.FirstOrDefault(n => n.Shape.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId)?.NodeId;

    internal IReadOnlySet<string> Downstream(string nodeId)
    {
        var downstream = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(nodeId);
        while (pending.TryPop(out var source))
        {
            foreach (var target in Canvas.Nodes.Where(n => n.Incoming.Any(edge => edge.SourceNodeId == source)))
                if (downstream.Add(target.NodeId)) pending.Push(target.NodeId);
        }
        downstream.Remove(nodeId);
        return downstream;
    }

    public static CompiledWalk Root(WorkflowProgram program)
    {
        var walks = new Dictionary<CanvasPath, CompiledWalk>();
        foreach (var canvas in program.Canvases)
            walks[canvas.CanvasPath] = new CompiledWalk(canvas, walks);
        return walks.GetValueOrDefault(CanvasPath.Root)
            ?? throw new InvalidOperationException("The compiled program holds no root canvas.");
    }

    public CompiledNode? Node(string nodeId) => nodes.GetValueOrDefault(nodeId);

    public static CompiledWalk? LevelOf(CompiledWalk root, IReadOnlyList<WorkflowContinuationFrame> frames, string? rootNode)
    {
        var walk = root;
        var container = rootNode;
        foreach (var frame in frames)
        {
            if (container is null || walk.Child(container, frame.SubCanvas) is not { } level)
                return null;
            walk = level;
            container = frame.NodeInFlight;
        }

        return walk;
    }

    internal CompiledNode Required(string nodeId) =>
        Node(nodeId) ?? throw new InvalidOperationException($"Node '{nodeId}' is absent from the compiled program of its canvas.");

    public CompiledWalk? Child(string nodeId, string branch) =>
        walks.GetValueOrDefault(Canvas.CanvasPath.Child(nodeId, branch));
}
