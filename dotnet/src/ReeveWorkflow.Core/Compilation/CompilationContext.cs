using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal readonly record struct NodeKey(CanvasPath CanvasPath, string NodeId);

internal sealed class CompilationContext(
    StoredWorkflowDocument document, IBlockShapeSource shapes, CompilationOptions options, NodePortEnvironment environment,
    CancellationToken cancellation = default, Dictionary<WorkflowNodeDefinition, BlockShape?>? prebound = null)
{
    public IReadOnlyDictionary<WorkflowNodeDefinition, BlockShape?> Prebound { get; } =
        prebound ?? new Dictionary<WorkflowNodeDefinition, BlockShape?>(ReferenceEqualityComparer.Instance);

    public StoredWorkflowDocument Document { get; } = document;
    public IBlockShapeSource Shapes { get; } = BoundaryBlockShapes.Over(shapes);
    public CompilationOptions Options { get; } = options;
    public NodePortEnvironment Environment { get; } = environment;
    public List<CompilationDiagnostic> Diagnostics { get; } = [];
    public Dictionary<NodeKey, BlockShape> Bound { get; } = [];

    public Dictionary<BlockShape, List<BlockDeclarationRules.DeclaredFault>> DeclaredFaults { get; } = new(ReferenceEqualityComparer.Instance);

    public Dictionary<CanvasPath, CanvasSignature> Signatures { get; } = [];

    public Dictionary<CanvasPath, IReadOnlyList<string>> Order { get; } = [];

    public Dictionary<CanvasPath, CompilationScope> Scopes { get; } = [];

    public HashSet<NodeKey> PortsUnresolved { get; } = [];

    public Dictionary<NodeKey, NodeTypes> Types { get; } = [];

    public Dictionary<CanvasPath, FrameTypes> Frames { get; } = [];

    public Dictionary<CanvasPath, IReadOnlyList<EffectivePort>> BodyOutputs { get; } = [];

    public HashSet<CanvasPath> Visited { get; } = [];

    public HashSet<CanvasPath> Ambiguous { get; } = [];

    public CanvasPath? FaultAt { get; set; }

    public CancellationToken Cancellation { get; } = cancellation;

    public Exception? Fault { get; set; }

    private readonly Dictionary<WorkflowDefinition, IReadOnlyDictionary<string, WorkflowNodeDefinition>> _nodesByCanvas = [];

    public IReadOnlyDictionary<string, WorkflowNodeDefinition> NodesById(WorkflowDefinition canvas)
    {
        if (_nodesByCanvas.TryGetValue(canvas, out var byId))
            return byId;

        var built = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in canvas.Nodes)
            built.TryAdd(node.Id, node);

        _nodesByCanvas[canvas] = built;
        return built;
    }

    private Dictionary<CanvasPath, WorkflowDefinition>? _canvasesByPath;

    private Dictionary<WorkflowNodeDefinition, string>? _nodeLabels;

    public string NodeName(CanvasPath at, string nodeId)
    {
        if (_canvasesByPath is null)
        {
            _canvasesByPath = [];
            foreach (var frame in CanvasWalk.Frames(Document.Definition))
                _canvasesByPath.TryAdd(frame.Path, frame.Canvas);
        }

        return _canvasesByPath.TryGetValue(at, out var canvas) ? NodeName(canvas, nodeId) : $"'{nodeId}'";
    }

    public string NodeName(WorkflowDefinition canvas, string nodeId)
    {
        if (!NodesById(canvas).TryGetValue(nodeId, out var node))
            return $"'{nodeId}'";

        _nodeLabels ??= LabelNodes();
        return $"'{(_nodeLabels.TryGetValue(node, out var label) ? label : VisibleName(node))}'";
    }

    private Dictionary<WorkflowNodeDefinition, string> LabelNodes()
    {
        var named = new List<(CanvasPath Path, WorkflowNodeDefinition Node, string Name)>();
        foreach (var frame in CanvasWalk.Frames(Document.Definition))
            foreach (var node in frame.Canvas.Nodes)
                named.Add((frame.Path, node, VisibleName(node)));

        var byName = named.Where(n => !BoundaryBlockShapes.IsBoundary(n.Node.BlockTypeId))
            .GroupBy(n => n.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byNameAndId = named.GroupBy(n => (n.Name, n.Node.Id)).ToDictionary(g => g.Key, g => g.Count());

        var labels = new Dictionary<WorkflowNodeDefinition, string>(ReferenceEqualityComparer.Instance);
        foreach (var (path, node, name) in named)
        {
            labels.TryAdd(node, BoundaryBlockShapes.IsBoundary(node.BlockTypeId) || byName.GetValueOrDefault(name) < 2
                ? name
                : byNameAndId[(name, node.Id)] < 2 || path == CanvasPath.Root
                    ? $"{name} ({node.Id})"
                    : $"{name} ({node.Id} in {path})");
        }

        return labels;
    }

    private string VisibleName(WorkflowNodeDefinition node)
    {
        var shape = Prebound.TryGetValue(node, out var prebound) ? prebound : Shapes.Find(node.BlockTypeId);
        return shape?.DisplayName is { Length: > 0 } displayName ? displayName : node.BlockTypeId;
    }
}
