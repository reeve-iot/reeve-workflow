using System.Text.Json;

namespace ReeveWorkflow.Core.Compilation;

internal abstract record StoredKind;

internal sealed record StringKind(bool NonEmpty = false) : StoredKind;

internal sealed record NumberKind : StoredKind;

internal sealed record BooleanKind : StoredKind;

internal sealed record RefusedKind(string Rule, string Detail) : StoredKind;

internal sealed record AnyKind : StoredKind;

internal sealed record EnumKind(Type Enum) : StoredKind
{
    public IReadOnlyList<string> Names { get; } = System.Enum.GetNames(Enum);
}

internal sealed record ChoiceKind(params string[] Names) : StoredKind
{
    public string Noun { get; init; } = "a mapping type";
}

internal sealed record PortTypeKind : StoredKind;

internal sealed record ArrayKind(StoredKind Element, StoredOrder Order) : StoredKind;

internal sealed record MapKind(StoredKind Value, StoredOrder Order, StoredRole Role = StoredRole.None) : StoredKind;

internal sealed record ConfigKind(StoredOrder Order) : StoredKind;

internal abstract record StoredOrder(string Where)
{
    public sealed record Unread(string Where) : StoredOrder(Where);

    public sealed record Read(string Where) : StoredOrder(Where);

    public sealed record Walk(string Where) : StoredOrder(Where);

    public sealed record Dependent(string Where, Func<JsonElement, JsonElement, bool> Depends) : StoredOrder(Where);
}

internal sealed record ObjectKind(
    string Name, IReadOnlyList<StoredField> Fields, StoredRole Role = StoredRole.None) : StoredKind
{
    public StoredField? Exact(string key) => Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

    public StoredField? IgnoringCase(string key) =>
        Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
}

internal sealed record StoredField(string Key, StoredKind Kind, bool Nullable = false, bool Required = false, bool OmitNull = false,
    string? RefusedRule = null, string? RefusedDetail = null);

internal enum StoredRole
{
    None,
    Canvas,
    Node,
    DeclaredOutput,
    PortEntry,
    Parameter,
    TypeArguments,
    Projection,
    Connection,
    SubCanvases,
}

internal static class StoredDocumentSchema
{
    public const string InputPortsKey = "InputPorts";
    public const string OutputPortsKey = "OutputPorts";

    public static ObjectKind Definition { get; }

    public static ArrayKind ParameterList { get; } = new(new ObjectKind("parameter",
    [
        new("name", new StringKind(NonEmpty: true), Required: true),
        new("type", new PortTypeKind(), Nullable: true),
        new("isRequired", new BooleanKind()),
        new("displayName", new StringKind()),
    ], StoredRole.Parameter), SameKey("name", "the compiler takes the first parameter of a name (EffectivePorts.cs:577, :709)"));

    public static ArrayKind PortList { get; } = new(new ObjectKind("port entry",
    [
        new("name", new StringKind(NonEmpty: true), Required: true),
        new("type", new PortTypeKind(), Nullable: true),
        new("isRequired", new BooleanKind()),
        new("displayName", new StringKind()),
    ], StoredRole.PortEntry), new StoredOrder.Read("the block is handed the list's text as a configuration value (WorkflowGraphExecutor.cs:127)"));

    static StoredDocumentSchema()
    {
        var projection = new ObjectKind("projection",
        [
            new("path", new StringKind(), Required: true),
            new("mode", new ChoiceKind("jsonText") { Noun = "a projection mode" }, OmitNull: true),
            new("type", new PortTypeKind(), OmitNull: true),
        ], StoredRole.Projection);

        var connection = new ObjectKind("connection",
        [
            new("id", new StringKind(NonEmpty: true), Required: true),
            new("sourceNodeId", new StringKind(NonEmpty: true), Required: true),
            new("targetNodeId", new StringKind(NonEmpty: true), Required: true),
            new("mappings", new MapKind(new ObjectKind("mapping",
            [
                new("type", new ChoiceKind("direct", "literal"), Required: true),
                new("sourceOutputName", new StringKind(NonEmpty: true)),
                new("literalValue", new AnyKind()),
                new("projection", projection, OmitNull: true),
            ]), new StoredOrder.Unread("one entry per target input, the object's keys being unique (rule L3)")), Required: true),
        ], StoredRole.Connection);

        var outputMapping = new ObjectKind("output mapping",
        [
            new("sourceNodeId", new StringKind(NonEmpty: true), Required: true),
            new("sourceOutput", new StringKind(NonEmpty: true), Required: true),
            new("workflowOutput", new StringKind(NonEmpty: true), Required: true),
        ]);

        var declaredOutput = new ObjectKind("declared output",
        [
            new("name", new StringKind(NonEmpty: true), Required: true),
            new("displayName", new StringKind()),
            new("type", new PortTypeKind(), Nullable: true),
            new("sources", new MapKind(new StringKind(), new StoredOrder.Unread(
                "looked up by sub-canvas name (InlineSubCanvasExecutor.cs:335, CanvasSignature.cs:110)")), OmitNull: true),
        ], StoredRole.DeclaredOutput);

        var errorPolicy = new ObjectKind("error policy",
        [
            new("policy", new EnumKind(typeof(Model.NodeErrorPolicy)), Required: true),
            new("defaultOutputJson", new StringKind(), OmitNull: true),
            new("routeToErrorPort", new RefusedKind(StoredDocumentRules.ErrorPolicyRouteUnread, "a routeToErrorPort key no run reads")),
        ]);

        var position = new ObjectKind("position", [new("x", new NumberKind(), Required: true), new("y", new NumberKind(), Required: true)]);

        var canvasFields = new List<StoredField>();
        var canvas = new ObjectKind("canvas", canvasFields, StoredRole.Canvas);

        var node = new ObjectKind("node",
        [
            new("id", new StringKind(NonEmpty: true), Required: true),
            new("blockTypeId", new StringKind()),
            new("position", position, Required: true),
            new("configuration", new ConfigKind(new StoredOrder.Unread(
                "each key looked up by name (MergeConfigIntoInputs, WorkflowGraphExecutor.cs:113-130; ReadResilience)")), Required: true),
            new("subCanvases", new MapKind(canvas, new StoredOrder.Unread(
                "each canvas looked up by its name (InlineSubCanvasExecutor.cs:241)"), StoredRole.SubCanvases), OmitNull: true),
            new("passthrough", new ArrayKind(new StringKind(NonEmpty: true), new StoredOrder.Unread(
                "each name copied once, if not an output already (WorkflowGraphExecutor.cs:584-593)")), OmitNull: true),
            new("outputPorts", new ArrayKind(declaredOutput, new StoredOrder.Dependent(
                "assigned in order per sub-canvas, each reading the result the ports before it wrote "
                + "(InlineSubCanvasExecutor.cs:330-342); the compiler keeps the first port of a source key (CanvasSignature.cs:108-111) and types a name from its first port "
                + "whatever its branch (EffectivePorts.cs:208-215)", PortsInteract)), OmitNull: true),
            new("typeArguments", new MapKind(new PortTypeKind(), new StoredOrder.Unread("bindings are looked up by parameter name"), StoredRole.TypeArguments), OmitNull: true),
            new("errorPolicy", errorPolicy, OmitNull: true),
        ], StoredRole.Node);

        canvasFields.AddRange(
        [
            new("nodes", new ArrayKind(node, new StoredOrder.Walk(
                "TopologicalSort seeds its queue in node order (WorkflowGraphHelpers.cs:99-103, :118); the first "
                + "WorkflowInput holds the run's parameters (WorkflowGraphExecutor.cs:846-847); WorkflowOutput nodes are read "
                + "in order, a later one winning (WorkflowExecutionHelpers.cs:20-25, InlineSubCanvasExecutor.cs:317-325), "
                + "and the compiler types a sub-canvas's first (EffectivePorts.cs:437)")), Required: true),
            new("edges", new ArrayKind(connection, new StoredOrder.Walk(
                "TopologicalSort takes a node's successors in edge order (WorkflowGraphHelpers.cs:108-114, :126-131); "
                + "an input two wires feed is refused on every canvas before a run (WorkflowCompiler.cs CheckStructure)")), Required: true),
            new("outputMappings", new ArrayKind(outputMapping, SameKey("workflowOutput", "refused whole, so its entries are never read")),
                Nullable: true, RefusedRule: StoredDocumentRules.OutputMappingsUnread,
                RefusedDetail: "outputMappings is not stored; an output is a wire into the WorkflowOutput node"),
        ]);

        Definition = canvas;
    }

    private static StoredOrder.Dependent SameKey(string key, string where) => new(where, (a, b) =>
        Text(a, key) == Text(b, key));

    private static bool PortsInteract(JsonElement a, JsonElement b)
    {
        var (nameA, nameB) = (Text(a, "name"), Text(b, "name"));
        return nameA == nameB || Sources(a).Any(s => Sources(b).FirstOrDefault(o => o.Key == s.Key) is { Key: not null } t
            && (s.Value == t.Value || nameA == t.Value || nameB == s.Value));
    }

    private static IEnumerable<KeyValuePair<string, string?>> Sources(JsonElement port) =>
        port.ValueKind == JsonValueKind.Object && port.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Object
            ? sources.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null))
            : [];

    private static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static IReadOnlyDictionary<Type, ObjectKind> Models => models ??= new Dictionary<Type, ObjectKind>
    {
        [typeof(Model.WorkflowDefinition)] = Object(Definition, "canvas"),
        [typeof(Model.WorkflowNodeDefinition)] = Object(Definition, "node"),
        [typeof(Model.WorkflowNodePosition)] = Object(Definition, "position"),
        [typeof(Model.WorkflowConnectionDefinition)] = Object(Definition, "connection"),
        [typeof(Model.WorkflowOutputMappingDefinition)] = Object(Definition, "output mapping"),
        [typeof(Model.SubCanvasOutputPortDefinition)] = Object(Definition, "declared output"),
        [typeof(Model.NodeErrorConfig)] = Object(Definition, "error policy"),
    };

    private static IReadOnlyDictionary<Type, ObjectKind>? models;

    public static IEnumerable<ObjectKind> Objects(StoredKind root)
    {
        var seen = new HashSet<ObjectKind>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<StoredKind>([root]);
        while (pending.TryPop(out var kind))
        {
            switch (kind)
            {
                case ObjectKind o when seen.Add(o):
                    yield return o;
                    foreach (var field in o.Fields)
                        pending.Push(field.Kind);
                    break;
                case ArrayKind a:
                    pending.Push(a.Element);
                    break;
                case MapKind m:
                    pending.Push(m.Value);
                    break;
            }
        }
    }

    private static ObjectKind Object(StoredKind root, string name) => Objects(root).Single(o => o.Name == name);
}
