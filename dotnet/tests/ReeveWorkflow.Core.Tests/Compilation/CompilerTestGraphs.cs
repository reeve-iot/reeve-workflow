using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Compilation;

internal static class CompilerTestGraphs
{
    public static BlockShape Shape(
        string id,
        (string Name, PortValueType Type, bool Required)[]? inputs = null,
        (string Name, PortValueType Type)[]? outputs = null,
        (string Name, ConfigFieldType Type, bool Required)[]? configFields = null,
        bool hasDynamicPorts = false,
        bool supportsDeclaredOutputs = false,
        bool canSuspend = false,
        bool onlyDrivesItsSubCanvases = false,
        bool portsAreResolvedElsewhere = false,
        bool declaredOutputsCollect = false,
        IReadOnlyDictionary<string, (string Name, PortValueType Type, bool Required)[]>? subCanvasInputs = null,
        IReadOnlyDictionary<string, (string Name, PortValueType Type, bool Required)[]>? subCanvasOutputs = null,
        string? source = null,
        string? runsWorkflowFrom = null,
        IReadOnlyList<WorkflowTypeParameter>? typeParameters = null,
        IReadOnlyList<string>? enumValues = null,
        IReadOnlyList<string>? subCanvasNames = null,
        IReadOnlyDictionary<string, ExpressionScopeVariable[]>? scopeVariables = null,
        string? secretInput = null,
        string? displayName = null) =>
        new(id,
            [.. (inputs ?? []).Select(i => new PortShape(i.Name, i.Type, i.Required)
                { ScopeVariables = scopeVariables?.GetValueOrDefault(i.Name) ?? [], SecretValues = i.Name == secretInput })],
            [.. (outputs ?? []).Select(o => new PortShape(o.Name, o.Type, false))],
            [.. (configFields ?? []).Select(f => new ConfigFieldShape(f.Name, f.Type, f.Required,
                f.Type == ConfigFieldType.Enum ? enumValues : null))],
            new BlockFlags(
                HasDynamicPorts: hasDynamicPorts,
                SupportsDeclaredOutputs: supportsDeclaredOutputs,
                CanSuspend: canSuspend,
                OnlyDrivesItsSubCanvases: onlyDrivesItsSubCanvases,
                PortsAreResolvedElsewhere: portsAreResolvedElsewhere,
                DeclaredOutputsCollect: declaredOutputsCollect),
            ToPortShapeMap(subCanvasInputs, subCanvasNames),
            ToPortShapeMap(subCanvasOutputs, subCanvasNames),
            source,
            runsWorkflowFrom,
            subCanvasNames,
            typeParameters,
            displayName);

    private static readonly IReadOnlyDictionary<string, (string Name, PortValueType Type, bool Required)[]> NoSubCanvasPorts =
        new Dictionary<string, (string Name, PortValueType Type, bool Required)[]>();

    private static IReadOnlyDictionary<string, IReadOnlyList<PortShape>> ToPortShapeMap(
        IReadOnlyDictionary<string, (string Name, PortValueType Type, bool Required)[]>? source,
        IReadOnlyList<string>? subCanvasNames)
    {
        var map = new Dictionary<string, IReadOnlyList<PortShape>>();
        foreach (var (name, ports) in source ?? NoSubCanvasPorts)
            map[name] = [.. ports.Select(p => new PortShape(p.Name, p.Type, p.Required))];
        foreach (var name in subCanvasNames ?? [])
            map.TryAdd(name, []);

        return map;
    }

    public static CanvasSignature Signature(
        (string Name, PortValueType Type, bool Required)[]? inputs = null,
        (string Name, PortValueType Type, bool Required)[]? outputs = null) =>
        new(
            [.. (inputs ?? []).Select(i => new PortShape(i.Name, i.Type, i.Required))],
            [.. (outputs ?? []).Select(o => new PortShape(o.Name, o.Type, o.Required))]);

    public static NodePortEnvironment Environment(
        params (CanvasPath CanvasPath, string NodeId, string? Reference,
            (string Name, PortValueType Type, bool Required)[] Inputs,
            (string Name, PortValueType Type)[] Outputs)[] entries)
    {
        var builder = new NodePortEnvironmentBuilder();
        foreach (var entry in entries)
        {
            builder.Add(entry.CanvasPath, entry.NodeId, NodePortResolution.Resolved(entry.Reference, new CanvasSignature(
                [.. entry.Inputs.Select(i => new PortShape(i.Name, i.Type, i.Required))],
                [.. entry.Outputs.Select(o => new PortShape(o.Name, o.Type, false))])));
        }

        return builder.Build();
    }

    public static NodePortEnvironment FailedEnvironment(CanvasPath canvasPath, string nodeId, string reason) =>
        new NodePortEnvironmentBuilder().Add(canvasPath, nodeId, NodePortResolution.Failed(reason)).Build();

    public static CompilationOptions HostTypes(params string[] names) => new()
    {
        TypeRegistry = new WorkflowTypeRegistry(names.Distinct().Select(n => new WorkflowTypeRecord(n, typeof(string)))),
    };

    public static IBlockShapeSource Catalogue(params BlockShape[] shapes)
    {
        var byId = shapes.ToDictionary(s => s.BlockTypeId, StringComparer.OrdinalIgnoreCase);
        return new DictionaryShapeSource(byId);
    }

    public static WorkflowNodeDefinition Node(
        string id, string blockTypeId, object? config = null, IReadOnlyList<string>? passthrough = null) =>
        new()
        {
            Id = id,
            BlockTypeId = blockTypeId,
            Config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(config ?? new Dictionary<string, object?>())),
            Passthrough = passthrough is null ? null : [.. passthrough],
        };

    public static WorkflowNodeDefinition StartNode(string id = "start") =>
        Node(id, WorkflowGraphHelpers.WorkflowInputBlockTypeId);

    public static WorkflowConnectionDefinition Edge(
        string id, string source, string target, params (string SourceOutput, string TargetInput)[] mappings) =>
        new()
        {
            Id = id,
            SourceNodeId = source,
            TargetNodeId = target,
            Mappings = JsonSerializer.SerializeToElement(mappings.ToDictionary(
                m => m.TargetInput, m => new { type = "direct", sourceOutputName = m.SourceOutput })),
        };

    public static WorkflowConnectionDefinition ProjectionEdge(
        string id, string source, string target, string sourceOutput, string targetInput, string path, string? type = null) =>
        new()
        {
            Id = id,
            SourceNodeId = source,
            TargetNodeId = target,
            Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                [targetInput] = type is null
                    ? new { type = "direct", sourceOutputName = sourceOutput, projection = (object)new { path } }
                    : new { type = "direct", sourceOutputName = sourceOutput, projection = (object)new { path, type } },
            }),
        };

    public static WorkflowConnectionDefinition LiteralEdge(
        string id, string source, string target, string targetInput, object? literalValue) =>
        new()
        {
            Id = id,
            SourceNodeId = source,
            TargetNodeId = target,
            Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                [targetInput] = new { type = "literal", literalValue },
            }),
        };

    public static WorkflowConnectionDefinition LiteralEdgeWithoutValue(string id, string source, string target, string targetInput) =>
        new()
        {
            Id = id,
            SourceNodeId = source,
            TargetNodeId = target,
            Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { [targetInput] = new { type = "literal" } }),
        };

    public static WorkflowDefinition Graph(
        IEnumerable<WorkflowNodeDefinition> nodes, IEnumerable<WorkflowConnectionDefinition>? edges = null) =>
        new() { Nodes = [.. nodes], Edges = [.. edges ?? []] };

    public static string Stored(WorkflowDefinition definition) => StoredWorkflowDocument.Write(definition, out _)!;

    public static NodeTypes TypesOf(
        WorkflowDefinition graph, IBlockShapeSource shapes, CanvasSignature signature, string nodeId,
        CompilationOptions? options = null, NodePortEnvironment? environment = null, CanvasPath? path = null) =>
        WorkflowCompiler.Run(graph, shapes, signature, options, environment).Types[new NodeKey(path ?? CanvasPath.Root, nodeId)];

    public static IReadOnlyDictionary<string, string> Terms(IEnumerable<EffectivePort>? ports) =>
        (ports ?? []).ToDictionary(p => p.Name, p => p.State switch
        {
            PortState.Typed => p.Term!.ToString(),
            PortState.Unbound => "unbound",
            _ => "refused",
        }, StringComparer.Ordinal);

    public static CanvasFrame RootFrame(WorkflowDefinition graph) => new(CanvasPath.Root, graph);

    public static CompilationContext ContextFor(
        CanvasFrame frame, IBlockShapeSource shapes, CanvasSignature? signature, NodePortEnvironment? environment = null, CompilationOptions? options = null)
    {
        var context = new CompilationContext(StoredWorkflowDocument.Empty, shapes, options ?? new CompilationOptions(), environment ?? NodePortEnvironment.Empty);
        context.Order[frame.Path] = WorkflowGraphHelpers.TopologicalSort(frame.Canvas);
        if (signature is not null)
            context.Signatures[frame.Path] = signature;

        foreach (var node in frame.Canvas.Nodes)
        {
            if (shapes.Find(node.BlockTypeId) is { } shape)
                context.Bound[new NodeKey(frame.Path, node.Id)] = shape;
        }

        return context;
    }

    public static IReadOnlyDictionary<string, WorkflowNodeDefinition> NodesById(WorkflowDefinition canvas)
    {
        var byId = new Dictionary<string, WorkflowNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in canvas.Nodes)
            byId.TryAdd(node.Id, node);

        return byId;
    }

    private sealed class DictionaryShapeSource(Dictionary<string, BlockShape> byId) : IBlockShapeSource
    {
        public BlockShape? Find(string blockTypeId) =>
            byId.TryGetValue(blockTypeId, out var shape) ? shape : null;
    }
}

internal static class FakeFactory
{
    public static IWorkflowBlockFactory For(BlockShape shape) => new ShapeBackedFactory(shape);

    private sealed class ShapeBackedFactory(BlockShape shape) : IWorkflowBlockFactory
    {
        public string BlockTypeId => shape.BlockTypeId;
        public string DisplayName => shape.BlockTypeId;
        public string Description => string.Empty;
        public string Category => string.Empty;
        public IReadOnlyCollection<BlockPortDefinition> Inputs { get; } = [.. shape.Inputs.Select(ToPortDefinition)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs { get; } = [.. shape.Outputs.Select(ToPortDefinition)];
        public bool HasDynamicPorts => shape.Flags.HasDynamicPorts;
        public bool SupportsDeclaredOutputs => shape.Flags.SupportsDeclaredOutputs;

        public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema { get; } =
            [.. shape.ConfigurationFields.Select(f => new BlockConfigFieldDefinition(f.Name, f.Name, f.Type, f.Required))];

        public IReadOnlyCollection<string> SubCanvasNames => shape.SubCanvasNames;

        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) =>
            [.. (shape.SubCanvasInputs.TryGetValue(subCanvasName, out var ports) ? ports : []).Select(ToPortDefinition)];

        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) =>
            [.. (shape.SubCanvasOutputs.TryGetValue(subCanvasName, out var ports) ? ports : []).Select(ToPortDefinition)];

        public bool CanSuspend => shape.Flags.CanSuspend;
        public bool OnlyDrivesItsSubCanvases => shape.Flags.OnlyDrivesItsSubCanvases;
        public bool PortsAreResolvedElsewhere => shape.Flags.PortsAreResolvedElsewhere;
        public string? RunsWorkflowFrom => shape.RunsWorkflowFrom;
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => shape.TypeParameters;
        public string? Source => shape.Source;

        public IWorkflowBlockPlugin Create() =>
            throw new NotSupportedException("A shape-backed fake factory answers shape lookups only.");

        private static BlockPortDefinition ToPortDefinition(PortShape port) =>
            new(port.Name, port.Name, port.Type, port.IsRequired) { ScopeVariables = port.ScopeVariables };
    }
}
