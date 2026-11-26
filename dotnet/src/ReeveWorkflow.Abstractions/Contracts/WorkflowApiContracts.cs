using System.Text.Json.Serialization;

namespace ReeveWorkflow.Abstractions;

public sealed record WorkflowResponse(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyDictionary<string, string>? HostMetadata,
    string DefinitionJson,
    string? InputParametersJson,
    string? OutputParametersJson,
    bool HasImportWarnings,
    string? ImportWarningsJson,
    int? ExecutionTimeoutSeconds,
    string? RetryPolicyJson,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? CurrentVersionId,
    Guid? DraftVersionId,
    int? CurrentVersionNumber = null,
    int? DraftVersionNumber = null,
    string Mode = "Single",
    int MaxConcurrentRuns = 1,
    int MaxQueuedRuns = 10,
    string OverflowBehavior = "Drop",
    string? CheckpointPolicyJson = null)
{
    /// <summary>Whose documents <see cref="DefinitionJson"/> and the two parameter lists are, one of
    /// <see cref="WorkflowDocumentsSource"/>'s values; null when whoever built the response did not say.</summary>
    public string? DocumentsFrom { get; init; }

    /// <summary>The workflow's revision as this response read it. A caller that sends it back as
    /// <c>UpdateWorkflowRequest.ExpectedRevision</c> has its update refused when another write moved the workflow in
    /// between, instead of overwriting that write.</summary>
    public int Revision { get; init; }
}

/// <summary>The values of <see cref="WorkflowResponse.DocumentsFrom"/>.</summary>
public static class WorkflowDocumentsSource
{
    /// <summary>The current version's.</summary>
    public const string Current = "Current";

    /// <summary>The draft's, on a workflow with no current version: one whose first version did not compile.</summary>
    public const string Draft = "Draft";

}

public sealed record WorkflowSummaryResponse(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyDictionary<string, string>? HostMetadata,
    bool HasImportWarnings,
    string? ImportWarningsJson,
    bool HasInputParameters,
    bool HasOutputParameters,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? LastExecutedAtUtc,
    int ExecutionCount,
    string? LastExecutionStatus,
    bool HasUnpublishedChanges = false,
    int? CurrentVersionNumber = null);
public sealed record WorkflowExecutionResponse(
    Guid Id,
    Guid? AutomationId,
    Guid WorkflowId,
    Guid? TriggerId,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorMessage,
    double? DurationMs)
{
    /// <summary>Why the run ended where its status alone does not say: a start admission refused, which ran no step, or a run
    /// cancelled by the upgrade; null otherwise. One of the <c>WorkflowFailureReasons</c> constants.</summary>
    public string? FailureReason { get; init; }
}
/// <summary>Paginated response for workflow and automation execution history.</summary>
public sealed record PagedWorkflowExecutionsResponse(
    IReadOnlyCollection<WorkflowExecutionResponse> Items,
    int TotalCount,
    int Skip,
    int Take);
public sealed record WorkflowExecutionDetailResponse(
    Guid Id,
    Guid? AutomationId,
    Guid WorkflowId,
    Guid? TriggerId,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorMessage,
    double? DurationMs,
    IReadOnlyCollection<WorkflowExecutionLogEntryResponse> Entries,
    IReadOnlyCollection<WorkflowExecutionStepResponse> Steps,
    string? OutputsJson = null)
{
    /// <summary>As <see cref="WorkflowExecutionResponse.FailureReason"/>.</summary>
    public string? FailureReason { get; init; }
}
public sealed record WorkflowExecutionStepResponse(
    Guid Id,
    string NodeId,
    string NodeType,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long DurationMs,
    string Status,
    int SortOrder,
    string? InputsJson,
    string? OutputsJson,
    string? ErrorMessage,
    string? ErrorStackTrace,
    string? ErrorJson);
public sealed record WorkflowExecutionLogEntryResponse(
    Guid Id,
    string NodeId,
    string BlockTypeId,
    string Level,
    string Message,
    string? DataJson,
    int? IterationIndex,
    string? CallStackJson,
    DateTimeOffset Timestamp);
/// <summary>Everything a C# Script block author can use, generated from the actual compilation environment so
/// it can never drift.</summary>
public sealed record WorkflowScriptingMetadataResponse(
    IReadOnlyCollection<ScriptGlobalResponse> Globals,
    IReadOnlyCollection<string> Imports,
    IReadOnlyCollection<string> ReferencedAssemblies,
    IReadOnlyCollection<ScriptHostMemberResponse> HostMembers);
/// <summary>One script global (e.g. <c>Input</c>, <c>Output</c>, <c>Host</c>).</summary>
public sealed record ScriptGlobalResponse(
    string Name,
    string TypeName,
    string Description,
    string? Example);
/// <summary>One member of the <c>Host</c> facade, with a readable C# signature.</summary>
public sealed record ScriptHostMemberResponse(
    string Name,
    string Signature,
    string Description);
public sealed record WorkflowVersionResponse(
    Guid Id,
    Guid WorkflowId,
    int VersionNumber,
    string DefinitionJson,
    string? InputParametersJson,
    string? OutputParametersJson,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    string? ChangeMessage,
    bool IsPublished,
    DateTimeOffset? PublishedAtUtc);
public sealed record WorkflowVersionSummaryResponse(
    Guid Id,
    int VersionNumber,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    string? ChangeMessage,
    bool IsPublished,
    DateTimeOffset? PublishedAtUtc);
/// <summary>What changed between two versions as a run reads them: the engine, and the compiler that admits the run.
/// Every field of a stored definition and of both parameter lists is compared as a run reads it, and every list in
/// the order a run reads it. No reader of a run reads a node's position, a declared output's display name, an error
/// policy's routeToErrorPort, an edge's id or output mappings below the root, so none of those is compared.</summary>
/// <param name="AddedNodes">Nodes on the second version only, and those whose block changed.</param>
/// <param name="RemovedNodes">Nodes on the first version only, and those whose block changed.</param>
/// <param name="ModifiedNodes">Nodes on both, under one id and one block, whose fields differ.</param>
/// <param name="AddedEdges">An edge is matched by its source, target and mappings as the engine reads them, never by
/// its id, so one rewired or remapped is a removed edge and an added one.</param>
/// <param name="RemovedEdges">Matched as <paramref name="AddedEdges"/> are.</param>
/// <param name="ParameterChanges">The input parameters.</param>
/// <param name="ModifiedCanvases">Each canvas whose own fields differ, or whose nodes or edges, on both versions,
/// stand in another order that changes the order the run takes them in.</param>
/// <param name="OutputParameterChanges">The output parameters, compared as the input parameters are.</param>
public sealed record WorkflowDiffResponse(
    IReadOnlyCollection<WorkflowDiffNode> AddedNodes,
    IReadOnlyCollection<WorkflowDiffNode> RemovedNodes,
    IReadOnlyCollection<WorkflowDiffModifiedNode> ModifiedNodes,
    IReadOnlyCollection<WorkflowDiffEdge> AddedEdges,
    IReadOnlyCollection<WorkflowDiffEdge> RemovedEdges,
    WorkflowDiffParameters ParameterChanges,
    IReadOnlyCollection<WorkflowDiffCanvas> ModifiedCanvases,
    WorkflowDiffParameters OutputParameterChanges);
/// <summary>A node added or removed, on the canvas <paramref name="CanvasPath"/> names. A node whose block changed
/// under the same id is both; a block type id respelled in case alone names the same block, a modified node, but for
/// <c>WorkflowInput</c> and <c>WorkflowOutput</c>, which the engine matches exactly.</summary>
/// <param name="Id">The node's id, unique on its canvas.</param>
/// <param name="BlockTypeId">The node's block.</param>
/// <param name="Name">Always empty: a stored node has no name. Kept so a client that reads it still finds it.</param>
/// <param name="CanvasPath">The steps from the root canvas down to the node's canvas; empty at the root.</param>
public sealed record WorkflowDiffNode(string Id, string BlockTypeId, string Name, IReadOnlyList<WorkflowDiffCanvasStep> CanvasPath);
/// <summary>A node on both versions, under one id and one block, whose fields differ as a run reads them.</summary>
/// <param name="Id">The node's id, unique on its canvas.</param>
/// <param name="ConfigChanges">Each configuration key whose value differs as the engine hands it to the block, the spacing
/// of its JSON aside and a whole number apart from a fraction, with the stored texts; a null or absent configuration is no keys.
/// A key beginning <c>__</c>, which only the editor reads, is not compared, and a resilience key is listed only where
/// the timeout or retries the engine builds from it differ, for this block, under some host setting.</param>
/// <param name="ErrorPolicyChanged">Whether the node's <c>errorPolicy</c> differs as the engine reads it. A configuration
/// key of that name is no policy the run applies, and differs, where it does, in <see cref="ConfigChanges"/> alone.</param>
/// <param name="RetryPolicyChanged">Whether the retries the engine builds from <c>retryEnabled</c>, <c>retryCount</c>
/// and <c>retryBackoffSeconds</c> differ: their number, the delay between them, or a count set aside.</param>
/// <param name="CanvasPath">The steps from the root canvas down to the node's canvas; empty at the root.</param>
/// <param name="ChangedFields">The node's fields that differ as a run reads them, by their stored keys:
/// <c>blockTypeId</c> (respelled in case, which the run's log and error data carry), <c>configuration</c>,
/// <c>subCanvases</c> (its set of names), <c>passthrough</c> (as a set), <c>outputPorts</c> (in order where one port
/// reads what another writes or both claim one key), <c>errorPolicy</c> (its <c>defaultOutputJson</c> under
/// <c>ContinueWithDefault</c> only, which alone reads it, as the outputs it parses to).</param>
public sealed record WorkflowDiffModifiedNode(string Id, IReadOnlyDictionary<string, WorkflowDiffValueChange> ConfigChanges, bool ErrorPolicyChanged, bool RetryPolicyChanged, IReadOnlyList<WorkflowDiffCanvasStep> CanvasPath, IReadOnlyCollection<string> ChangedFields);
public sealed record WorkflowDiffValueChange(object? From, object? To);
/// <summary>An edge added or removed on the canvas <paramref name="CanvasPath"/> names.</summary>
/// <param name="Id">The edge's id as its version stores it; it identifies nothing to a run.</param>
/// <param name="SourceNodeId">The node it leaves.</param>
/// <param name="TargetNodeId">The node it enters.</param>
/// <param name="MappingsJson">Its mappings as stored, <c>[]</c> when it has none.</param>
/// <param name="CanvasPath">The steps from the root canvas down to the edge's canvas; empty at the root.</param>
public sealed record WorkflowDiffEdge(string Id, string SourceNodeId, string TargetNodeId, string MappingsJson, IReadOnlyList<WorkflowDiffCanvasStep> CanvasPath);
/// <summary>One step down from a canvas: the container node's id and the name of its sub-canvas.</summary>
public sealed record WorkflowDiffCanvasStep(string NodeId, string SubCanvas);
/// <summary>A canvas whose own fields differ: the root's <c>outputMappings</c>, in order per workflow output, and the
/// order of <c>nodes</c> or <c>edges</c> where it changes the order the run takes the nodes on both versions in or its
/// boundary nodes in.</summary>
/// <param name="CanvasPath">The steps from the root canvas down to the canvas; empty at the root.</param>
/// <param name="ChangedFields">The canvas's fields that differ, by their stored keys: <c>nodes</c>, <c>edges</c>,
/// <c>outputMappings</c>.</param>
public sealed record WorkflowDiffCanvas(IReadOnlyList<WorkflowDiffCanvasStep> CanvasPath, IReadOnlyCollection<string> ChangedFields);
/// <summary>The parameters of one list, by name; where a list holds two of one name, the entries of that name in
/// order, since the compiler takes the first.</summary>
/// <param name="Added">On the second version only.</param>
/// <param name="Removed">On the first version only.</param>
/// <param name="Modified">Each parameter on both sides whose type or <c>isRequired</c> differs; a type compared as
/// <c>PortValueType</c> compares, ignoring case, and neither the order of an entry's keys nor a key the reader
/// ignores counts.</param>
public sealed record WorkflowDiffParameters(
    IReadOnlyCollection<string> Added,
    IReadOnlyCollection<string> Removed,
    IReadOnlyCollection<string> Modified);
/// <summary>What an import would need before it runs, and the documents it was judged on: the block types this host does not
/// have and every id the graph names, grouped by the port type that declared them.</summary>
/// <remarks><c>ReferenceIdsByPortType</c> is keyed by the port type's own name — a host that declared a
/// <c>Device</c> port reads its devices under "Device". A collection port files under its element type.
/// The three texts are the ones sent, unchanged: a document that spells a term wrongly is refused, not rewritten.</remarks>
/// <param name="MissingBlockTypeIds">Block types in the graph that this host does not have.</param>
/// <param name="ReferenceIdsByPortType">The ids the graph names, by the port type that declared them.</param>
/// <param name="MissingWorkflowIds">Callee workflows of a <c>SubWorkflow</c> that this host does not have.</param>
/// <param name="DefinitionJson">The definition text that was compiled.</param>
/// <param name="InputParametersJson">The input parameter list; null when none was sent.</param>
/// <param name="OutputParametersJson">The output parameter list; null when none was sent.</param>
/// <param name="DroppedSecrets">The secret entries an import of this definition as a new workflow drops for their form: a <c>***</c>
/// and a protected value. It asks the host's codec to classify and nothing else, so it does not predict a drop the codec decides: a
/// <c>protect:</c> or a name-rule value the host refuses to protect, a reference the host refuses, a <c>protect:</c> form where no codec is
/// registered.</param>
public sealed record WorkflowImportValidationResult(
    IReadOnlyCollection<string> MissingBlockTypeIds,
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> ReferenceIdsByPortType,
    IReadOnlyCollection<string> MissingWorkflowIds,
    string DefinitionJson,
    string? InputParametersJson,
    string? OutputParametersJson,
    IReadOnlyCollection<WorkflowDroppedSecret> DroppedSecrets);

/// <summary>A secret entry an import drops instead of storing, and why.</summary>
/// <remarks>The site is the entry's place in the definition, with no value. <paramref name="CanvasPath"/> is opaque: compare it, never parse it.</remarks>
/// <param name="CanvasPath">The canvas of the node; empty at the root.</param>
/// <param name="NodeId">The node.</param>
/// <param name="Input">The secret-capable input.</param>
/// <param name="Key">The entry's key, which is a field name and so holds no secret.</param>
/// <param name="EdgeId">The edge whose literal mapping holds the entry; null for the node's own configuration.</param>
/// <param name="Reason">One of <see cref="WorkflowDroppedSecretReasons"/>; the last two are followed by a colon, a space and the host's reason.</param>
public sealed record WorkflowDroppedSecret(string CanvasPath, string NodeId, string Input, string Key, string? EdgeId, string Reason);

/// <summary>The reasons a <see cref="WorkflowDroppedSecret"/> carries.</summary>
public static class WorkflowDroppedSecretReasons
{
    /// <summary>The entry was <c>***</c> and the version it would keep holds no entry at that place.</summary>
    public const string Masked = "masked";

    /// <summary>The entry was a protected value the target workflow did not store at that place.</summary>
    public const string ForeignProtected = "foreign-protected";

    /// <summary>The entry was a <c>protect:</c> form and the host registered no secret codec.</summary>
    public const string CodecMissing = "codec-missing";

    /// <summary>The host's codec refused to protect the entry; the reason continues as <c>protect-refused: &lt;host reason&gt;</c>.</summary>
    public const string ProtectRefused = "protect-refused";

    /// <summary>The host's codec refused the reference; the reason continues as <c>reference-refused: &lt;host reason&gt;</c>.</summary>
    public const string ReferenceRefused = "reference-refused";
}

/// <summary>A workflow export file. A member it does not have is refused (400)
/// on <c>import/validate</c>, not dropped.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowExportDto(
    string SchemaVersion,
    DateTimeOffset ExportedAtUtc,
    string Name,
    string? Description,
    string? DefinitionJson,
    string? InputParametersJson,
    string? OutputParametersJson,
    int? ExecutionTimeoutSeconds = null,
    string? RetryPolicyJson = null,
    string? Mode = null,
    int? MaxConcurrentRuns = null,
    int? MaxQueuedRuns = null,
    string? OverflowBehavior = null)
{
    /// <summary>
    /// On export, copy the workflow's policy here; on import, pass it to
    /// <c>CreateWorkflowRequest.CheckpointPolicyJson</c>. Null preserves the engine's default.
    /// </summary>
    public string? CheckpointPolicyJson { get; init; }
}
public sealed record ExecuteWorkflowRequest(
    IReadOnlyDictionary<string, object?>? InputParameters = null,
    bool DebugMode = true);

/// <summary>409 body when the per-workflow concurrency coordinator rejects a run under
/// <c>OverflowBehavior.Error</c>. Structured so a caller can tell it from a 404 or a 400.</summary>
public sealed record WorkflowConcurrencyConflictResponse(
    Guid WorkflowId,
    string Mode,
    string Outcome,
    string Message);

/// <summary>One block type as a catalogue reader sees it — the same fields <see cref="IWorkflowBlockFactory"/>
/// declares, flattened. A block type does not declare <c>_error</c>: a node offers it by its error policy alone.</summary>
public sealed record WorkflowBlockTypeResponse(
    string BlockTypeId,
    string DisplayName,
    string Description,
    string Category,
    IReadOnlyCollection<BlockPortResponse> Inputs,
    IReadOnlyCollection<BlockPortResponse> Outputs,
    IReadOnlyCollection<BlockConfigFieldResponse> ConfigurationSchema,
    IReadOnlyCollection<string> SubCanvasNames,
    IReadOnlyDictionary<string, IReadOnlyCollection<BlockPortResponse>> SubCanvasInputs,
    IReadOnlyDictionary<string, IReadOnlyCollection<BlockPortResponse>> SubCanvasOutputs,
    bool HasDynamicPorts,
    bool SupportsDeclaredOutputs = false)
{
    /// <summary>The input holding the id of the workflow this block runs, whose ports then come from that workflow's
    /// parameters; null for a block that runs none. An editor offers no wire into it: the compiler refuses one.</summary>
    public string? RunsWorkflowFrom { get; init; }

    /// <summary>The type parameters the block's port types name as <c>$Name</c>, in declaration order; empty for a block
    /// that is not generic.</summary>
    public IReadOnlyCollection<BlockTypeParameterResponse> TypeParameters { get; init; } = [];

    /// <summary>True when each declared output <c>X: T</c> of a node of this block is the node output <c>X: Collection&lt;T?&gt;</c>,
    /// one element per iteration of the block's one sub-canvas; see <see cref="IWorkflowBlockFactory.DeclaredOutputsCollect"/>.
    /// An editor shows such a declared output with that type.</summary>
    public bool DeclaredOutputsCollect { get; init; }
}

/// <summary>A type parameter of a block and the capabilities a term bound to it must implement.</summary>
/// <param name="Name">The parameter's name without its <c>$</c>.</param>
/// <param name="Constraints">Capability names; empty when any term is admitted.</param>
public sealed record BlockTypeParameterResponse(string Name, IReadOnlyCollection<string> Constraints);

public sealed record BlockPortResponse(
    string Name,
    string DisplayName,
    string Type,
    bool IsRequired,
    IReadOnlyCollection<string>? AllowedValues)
{
    /// <summary>The variables the expression of an <c>Expression&lt;T&gt;</c> input may read while its block evaluates it, in
    /// declared order; empty for any other port.</summary>
    public IReadOnlyCollection<BlockScopeVariableResponse> ScopeVariables { get; init; } = [];

    /// <summary>True for an input whose entries may be protected or referenced secrets (<see cref="BlockPortDefinition.SecretValues"/>);
    /// an editor offers its hide toggle on such a row when <c>GET port-types</c> says a codec is registered.</summary>
    public bool SecretValues { get; init; }
}

/// <summary>A scope variable of an <c>Expression&lt;T&gt;</c> input: <c>${Name}</c> in its expression is a value of <c>Type</c>.
///</summary>
/// <param name="Name"><c>item</c> or <c>value</c>.</param>
/// <param name="Type">A declaration term: it may name the block's type parameters, as <c>$T</c>.</param>
public sealed record BlockScopeVariableResponse(string Name, string Type);

public sealed record BlockConfigFieldResponse(
    string Name,
    string Label,
    string Type,
    bool Required,
    string? DefaultValue,
    string? Description)
{
    /// <summary>The names an <c>Enum</c> field accepts, in the order to list them; null for any other type.</summary>
    public IReadOnlyCollection<string>? AllowedValues { get; init; }
}
