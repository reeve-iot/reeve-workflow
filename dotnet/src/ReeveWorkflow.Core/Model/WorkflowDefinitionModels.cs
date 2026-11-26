using System.Text.Json;
using System.Text.Json.Serialization;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Model;

/// <summary>A workflow on disk and on the wire. <see cref="ToJson"/> writes it as the library stores it.</summary>
public sealed class WorkflowDefinition
{
    private IReadOnlyCollection<WorkflowNodeDefinition> _nodes = [];

    [JsonPropertyName("nodes")]
    public IReadOnlyCollection<WorkflowNodeDefinition> Nodes
    {
        get => _nodes;
        set => _nodes = value ?? [];
    }

    /// <summary>The wires.</summary>
    [JsonPropertyName("edges")]
    public IReadOnlyCollection<WorkflowConnectionDefinition>? Edges { get; set; }

    [JsonPropertyName("outputMappings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyCollection<WorkflowOutputMappingDefinition>? OutputMappings { get; set; }

    /// <summary>The same list as <see cref="Edges"/>.</summary>
    public IReadOnlyCollection<WorkflowConnectionDefinition>? GetConnections() => Edges;

    internal WorkflowDefinition WithNodes(IReadOnlyCollection<WorkflowNodeDefinition> nodes)
    {
        var copy = (WorkflowDefinition)MemberwiseClone();
        copy.Nodes = nodes;
        return copy;
    }

    /// <summary>Writes the model with the library's own options; the output is not checked. Pass it to the reader
    /// to know whether it is accepted.</summary>
    /// <remarks>A model the reader would refuse, a null node say, is written as it is, <c>null</c> and all, so a
    /// door that reads the text refuses it.</remarks>
    /// <exception cref="InvalidWorkflowDefinitionException">A string holds an unpaired surrogate, which JSON text
    /// cannot hold without a repair, or the model cannot be written as JSON at all (a sub-canvas that contains
    /// itself).</exception>
    public string ToJson() =>
        StoredWorkflowDocument.Write(this, out var failure) ?? throw new InvalidWorkflowDefinitionException(failure!);
}

public sealed class WorkflowOutputMappingDefinition
{
    [JsonPropertyName("sourceNodeId")]
    public string SourceNodeId { get; set; } = "";

    [JsonPropertyName("sourceOutput")]
    public string SourceOutput { get; set; } = "";

    [JsonPropertyName("workflowOutput")]
    public string WorkflowOutput { get; set; } = "";
}

public sealed class WorkflowNodeDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("blockTypeId")]
    public string BlockTypeId { get; set; } = "";

    [JsonPropertyName("position")]
    public WorkflowNodePosition Position { get; set; } = new();

    /// <summary>The node's configuration, the block's keys as written, stored under `configuration`.</summary>
    [JsonPropertyName("configuration")]
    public Dictionary<string, JsonElement>? Config { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("subCanvases")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, WorkflowDefinition>? SubCanvases { get; set; }

    [JsonPropertyName("passthrough")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Passthrough { get; set; }

    [JsonPropertyName("outputPorts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SubCanvasOutputPortDefinition>? OutputPorts { get; set; }

    [JsonPropertyName("typeArguments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? TypeArguments { get; set; }

    [JsonPropertyName("errorPolicy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NodeErrorConfig? ErrorPolicy { get; set; }

    internal WorkflowNodeDefinition WithSubCanvases(Dictionary<string, WorkflowDefinition> subCanvases)
    {
        var copy = (WorkflowNodeDefinition)MemberwiseClone();
        copy.SubCanvases = subCanvases;
        return copy;
    }
}

public sealed class WorkflowNodePosition
{
    [JsonPropertyName("x"), JsonNumberHandling(JsonNumberHandling.Strict), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double X { get; set; }

    [JsonPropertyName("y"), JsonNumberHandling(JsonNumberHandling.Strict), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double Y { get; set; }
}

public sealed class WorkflowConnectionDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>The node this connection leaves. A null is refused with <see cref="WorkflowValidationException"/>
    /// naming the connection; an absent id binds as an empty string in the bare deserialiser, and the stored document reader refuses it.</summary>
    [JsonPropertyName("sourceNodeId")]
    public string SourceNodeId
    {
        get => sourceNodeId;
        set => sourceNodeId = value ?? throw NullEndpoint(nameof(SourceNodeId));
    }

    /// <summary>The node this connection enters. A null is refused as <see cref="SourceNodeId"/> is.</summary>
    [JsonPropertyName("targetNodeId")]
    public string TargetNodeId
    {
        get => targetNodeId;
        set => targetNodeId = value ?? throw NullEndpoint(nameof(TargetNodeId));
    }

    [JsonPropertyName("mappings")]
    public JsonElement? Mappings { get; set; }

    private string sourceNodeId = "";
    private string targetNodeId = "";

    private WorkflowValidationException NullEndpoint(string property) =>
        new($"Connection '{Id}' sets {property} to null; a connection names the node it leaves and the node it enters.");
}

/// <summary>One mapping of a connection as a run reads it. <see cref="Projection"/> is the stored <c>projection</c> of a direct
/// mapping, which a wire from a <c>Json</c> output carries and no other wire does.</summary>
public sealed record WorkflowMappingDefinition(
    string SourceOutput,
    string TargetInput,
    bool IsLiteral = false,
    object? LiteralValue = null,
    WorkflowProjection? Projection = null);

/// <summary>A projection wire's selection: an RFC 9535 query over the source's <c>Json</c> value, the mode
/// (<c>jsonText</c> delivers each node's JSON text; null is the default mode) and the stored term of what it delivers,
/// null where the target's term decides it. The compiler checks all three; nothing here is graded.</summary>
public sealed record WorkflowProjection(string Path, string? Mode, string? Type)
{
    /// <summary>The one mode a projection may name besides the default.</summary>
    public const string JsonTextMode = "jsonText";
}

/// <summary>A declared OUTPUT port on a multi-sub-canvas block: each named sub-canvas maps one of its
/// WorkflowOutput ports onto it via <see cref="Sources"/>.</summary>
public sealed class SubCanvasOutputPortDefinition
{
    /// <summary>The parent output port name, visible to downstream edges (e.g. "setpoint").</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Human-readable label shown in the workflow editor UI.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>PortValueType name (e.g. "Number"); the declared contract that propagates downstream. No
    /// default: absent, null or blank is port-type-malformed everywhere this is read (decision 2), never a
    /// guessed <c>Any</c> or a "derive from sources" fallback.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Sub-canvas name to the branch's WorkflowOutput port that feeds <see cref="Name"/>. A missing
    /// entry means the branch does not set this port, so it is null at runtime.</summary>
    [JsonPropertyName("sources")]
    public Dictionary<string, string>? Sources { get; set; }
}

/// <summary>What a node does when the block it runs throws. Part of the definition, not of the run: it is
/// declared by the author in the editor and travels with the document, by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NodeErrorPolicy>))]
public enum NodeErrorPolicy
{
    FailWorkflow = 0,
    ContinueWithDefault = 1,
    EmitError = 2,
    Skip = 3
}

/// <remarks>The policy's converter is pinned on the property, where an options-level enum converter, which would
/// otherwise win over the enum's own attribute, cannot rename it.</remarks>
public sealed record NodeErrorConfig(
    [property: JsonPropertyName("policy"), JsonConverter(typeof(JsonStringEnumConverter<NodeErrorPolicy>))]
    NodeErrorPolicy Policy = NodeErrorPolicy.FailWorkflow,
    [property: JsonPropertyName("defaultOutputJson"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DefaultOutputJson = null,
    [property: JsonPropertyName("routeToErrorPort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RouteToErrorPort = false);
