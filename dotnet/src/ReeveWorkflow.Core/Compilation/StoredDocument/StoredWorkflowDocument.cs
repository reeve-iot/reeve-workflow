using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal sealed record StoredPortTypeSite(
    PortTypeNameSite Site, string Location, IReadOnlyList<CanvasPathSegment> CanvasPath, string? NodeId,
    string PortName, int Index, string? RawType, bool IsRequired)
{
    public PortTypeSyntaxError? Error { get; } = PortValueType.TryParse(RawType, out _, out var error) ? null : error;
}

internal sealed class StoredWorkflowDocument
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = StoredDocumentLexer.MaxNesting };

    private static readonly JsonSerializerOptions WriterOptions = new()
    {
        Converters = { new WellFormedString() },
        MaxDepth = StoredDocumentLexer.MaxNesting + 1,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { WriteAsTheKindTableReads } },
    };

    private readonly Dictionary<WorkflowNodeDefinition, IReadOnlyList<StoredPortTypeSite>> sitesByNode;

    private StoredWorkflowDocument(
        WorkflowDefinition definition, IReadOnlyList<StoredPortTypeSite> sites,
        Dictionary<WorkflowNodeDefinition, IReadOnlyList<StoredPortTypeSite>> sitesByNode)
    {
        Definition = definition;
        Sites = sites;
        this.sitesByNode = sitesByNode;
    }

    public static StoredWorkflowDocument Empty { get; } = new(new WorkflowDefinition(), [], []);

    public WorkflowDefinition Definition { get; }

    public IReadOnlyList<StoredPortTypeSite> Sites { get; }

    public IReadOnlyList<StoredPortTypeSite> SitesOf(WorkflowNodeDefinition node) =>
        sitesByNode.TryGetValue(node, out var sites) ? sites : [];

    public StoredWorkflowDocument WithDeclaredBranches(
        Func<WorkflowNodeDefinition, DeclaredBranches.Declared?> declared, Action<WorkflowNodeDefinition, WorkflowNodeDefinition> copied)
    {
        var copies = new List<(WorkflowNodeDefinition Of, WorkflowNodeDefinition Copy)>();
        var filled = DeclaredBranches.Filled(Definition, declared, (of, copy) =>
        {
            copies.Add((of, copy));
            copied(of, copy);
        });
        if (ReferenceEquals(filled, Definition))
            return this;

        var byNode = new Dictionary<WorkflowNodeDefinition, IReadOnlyList<StoredPortTypeSite>>(sitesByNode, ReferenceEqualityComparer.Instance);
        foreach (var (of, copy) in copies)
        {
            if (sitesByNode.TryGetValue(of, out var sites))
                byNode[copy] = sites;
        }

        return new StoredWorkflowDocument(filled, Sites, byNode);
    }

    public static StoredWorkflowDocument? Read(string? text, out StoredDocumentFault? fault)
    {
        if (StoredDocumentLexer.IsBlank(text))
        {
            fault = new StoredDocumentFault(StoredDocumentRules.L1, "", "no document");
            return null;
        }

        var bytes = StoredDocumentLexer.Lex(text, out fault);
        if (bytes is null)
            return null;

        using var document = JsonDocument.Parse(bytes, ParseOptions);
        var walk = new StoredDocumentWalk(PortTypeNameSite.DeclaredOutput);
        if (!walk.Value(document.RootElement, StoredDocumentSchema.Definition, nullable: false, ""))
        {
            fault = walk.Fault;
            return null;
        }

        WorkflowDefinition definition;
        try
        {
            definition = WorkflowDefinitionParse.FromElement(document.RootElement)!;
        }
        catch (JsonException ex)
        {
            fault = new StoredDocumentFault(StoredDocumentRules.L7, "", ex.Message);
            return null;
        }

        return new StoredWorkflowDocument(definition, walk.Sites, Zip(definition, walk.Frames));
    }

    internal const string BlankList = "a parameter list is blank; no parameters is an absent list, or []";
    internal const string NullList = "a parameter list is the JSON null; no parameters is an absent list, or []";

    public static IReadOnlyList<StoredPortTypeSite>? ReadParameters(string? text, PortTypeNameSite site, out StoredDocumentFault? fault)
    {
        fault = null;
        if (text is null)
            return [];

        if (StoredDocumentLexer.IsBlank(text))
        {
            fault = new StoredDocumentFault(StoredDocumentRules.L7, "", BlankList);
            return null;
        }

        var bytes = StoredDocumentLexer.Lex(text, out fault);
        if (bytes is null)
            return null;

        using var document = JsonDocument.Parse(bytes, ParseOptions);
        if (document.RootElement.ValueKind == JsonValueKind.Null)
        {
            fault = new StoredDocumentFault(StoredDocumentRules.L7, "", NullList);
            return null;
        }

        var walk = new StoredDocumentWalk(site);
        if (!walk.Value(document.RootElement, StoredDocumentSchema.ParameterList, nullable: false, ""))
        {
            fault = walk.Fault;
            return null;
        }

        return walk.Sites;
    }

    public static IReadOnlyList<StoredPortTypeSite> PortList(WorkflowNodeDefinition node, string configKey)
    {
        if (EffectivePorts.ReadConfigString(node, configKey) is not { Length: > 0 } text)
            return [];

        var walk = new StoredDocumentWalk(PortTypeNameSite.DeclaredOutput);
        var site = configKey == StoredDocumentSchema.InputPortsKey ? PortTypeNameSite.DynamicInputPort : PortTypeNameSite.DynamicOutputPort;
        return walk.Embedded(text, site, "") ? walk.Sites : [];
    }

    public static string? Write(WorkflowDefinition definition, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Write(definition, out _, out failure);
    }

    public static StoredWorkflowDocument? ReadModel(WorkflowDefinition definition, out StoredDocumentFault? fault)
    {
        if (Write(definition, out var unpaired, out var failure) is { } text)
            return Read(text, out fault);

        fault = unpaired
            ? new StoredDocumentFault(StoredDocumentRules.L4, "", "a string with an unpaired surrogate in the model")
            : new StoredDocumentFault(StoredDocumentRules.L1, "", $"the model cannot be written as JSON: {failure}");
        return null;
    }

    private static string? Write(WorkflowDefinition definition, out bool unpaired, out string? failure)
    {
        unpaired = false;
        try
        {
            failure = null;
            return JsonSerializer.Serialize(definition, WriterOptions);
        }
        catch (UnpairedSurrogateException ex)
        {
            (unpaired, failure) = (true, ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            failure = ex.Message;
            return null;
        }
    }

    public static CompilationDiagnostic Unreadable(StoredDocumentFault fault) =>
        new(FindingRule(fault, CompilationRules.DefinitionUnreadable),
            $"The workflow definition cannot be read: {fault.Describe()}.",
            fault.NodeId, FindingField(fault), fault.CanvasPath);

    internal static string? FindingField(StoredDocumentFault fault) =>
        fault.Rule switch
        {
            StoredDocumentRules.ErrorPolicyRouteUnread => "errorPolicy",
            StoredDocumentRules.OutputMappingsUnread => "outputMappings",
            _ => null,
        };

    internal static string FindingRule(StoredDocumentFault fault, string unreadable) => fault.Rule switch
    {
        StoredDocumentRules.DuplicateNodeId => CompilationRules.DuplicateNodeId,
        StoredDocumentRules.DuplicateEdgeId => CompilationRules.DuplicateEdgeId,
        StoredDocumentRules.OutputMappingsUnread => CompilationRules.OutputMappingsUnread,
        StoredDocumentRules.UnknownKey => CompilationRules.StoredKeyUnknown,
        StoredDocumentRules.DottedName => CompilationRules.DottedName,
        StoredDocumentRules.ErrorPolicyRouteUnread => CompilationRules.ErrorPolicyRouteUnread,
        _ => unreadable,
    };

    private static void WriteAsTheKindTableReads(JsonTypeInfo type)
    {
        if (!StoredDocumentSchema.Models.TryGetValue(type.Type, out var kind))
            return;
        foreach (var property in type.Properties)
        {
            if (kind.Exact(property.Name) is not { } field || !(field.Nullable || field.OmitNull || field.Kind is RefusedKind))
                continue;
            property.ShouldSerialize = field.Kind is RefusedKind
                ? static (_, value) => value is not false
                : static (_, value) => value is not null;
        }
    }

    private sealed class UnpairedSurrogateException(string message) : JsonException(message);

    private sealed class WellFormedString : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Checked(value));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WritePropertyName(Checked(value));

        private static string Checked(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    i++;
                else if (char.IsSurrogate(value[i]))
                    throw new UnpairedSurrogateException($"a string with an unpaired surrogate at character {i}");
            }

            return value;
        }
    }

    private static Dictionary<WorkflowNodeDefinition, IReadOnlyList<StoredPortTypeSite>> Zip(
        WorkflowDefinition definition, IReadOnlyList<StoredDocumentWalk.ReadFrame> frames)
    {
        var byNode = new Dictionary<WorkflowNodeDefinition, IReadOnlyList<StoredPortTypeSite>>(ReferenceEqualityComparer.Instance);
        var index = 0;
        foreach (var frame in CanvasWalk.Frames(definition))
        {
            var read = frames[index++];
            var i = 0;
            foreach (var node in frame.Canvas.Nodes)
                byNode[node] = [.. read.Nodes[i++], .. read.Projections.Where(p => p.NodeId == node.Id)];
        }

        return byNode;
    }
}
