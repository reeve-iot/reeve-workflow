using System.Text.Json;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>A host's check of the nodes of a version against facts only the host knows, such as a helper's current type or
/// a device a literal names. Optional: with none filled, it finds nothing.</summary>
/// <remarks>Asked at every admission, once for the started version and once for each version it reaches, after the
/// compile and the argument check and outside the compile cache; and by the editor after the commit of every save, publish
/// and rollback (a rollback's copy is committed first, then judged). It should check only facts that do not change under a
/// compiled version. A throw or a null answer refuses a start with <see cref="WorkflowStartRefusalCodes.AdmissionFaulted"/>
/// and, in the editor, stops a publish with <see cref="WorkflowEditorFindingCodes.HostValidatorFaulted"/>. Resolved once
/// with the engine, so it is a singleton.
///</remarks>
public interface IWorkflowNodeValidator
{
    /// <summary>Judges the nodes of one version. An empty list passes them.</summary>
    ValueTask<IReadOnlyList<WorkflowValidationFinding>> ValidateAsync(
        WorkflowNodeValidationRequest request, CancellationToken cancellationToken);
}

/// <summary>Which door asks the node validator.</summary>
public enum WorkflowValidationPhase
{
    /// <summary>A save: a finding is reported and the draft still saves.</summary>
    Save,

    /// <summary>A publish: a finding also stops the promotion.</summary>
    Publish,

    /// <summary>A start: a finding refuses it.</summary>
    Admission
}

/// <summary>The nodes of one version, for <see cref="IWorkflowNodeValidator"/>.</summary>
/// <param name="WorkflowId">The workflow the version belongs to.</param>
/// <param name="VersionId">The version.</param>
/// <param name="Phase">The door that asks.</param>
/// <param name="Nodes">Every node of every canvas of the version, in walk order.</param>
public sealed record WorkflowNodeValidationRequest(
    Guid WorkflowId, Guid VersionId, WorkflowValidationPhase Phase, IReadOnlyList<WorkflowNodeToValidate> Nodes);

/// <summary>One node as the validator sees it.</summary>
/// <param name="CanvasPath">The sub-canvas chain the node is in; empty at the root.</param>
/// <param name="NodeId">The node's id.</param>
/// <param name="BlockTypeId">The block it runs.</param>
/// <param name="Configuration">Its configuration as stored.</param>
/// <param name="Literals">Each literal mapping into one of its inputs, by input name.</param>
/// <param name="TypeArguments">Each type parameter of its block that is bound, by parameter name, as a closed term in document
/// grammar (<c>Collection&lt;Int&gt;</c>).</param>
public sealed record WorkflowNodeToValidate(
    CanvasPath CanvasPath, string NodeId, string BlockTypeId,
    IReadOnlyDictionary<string, JsonElement> Configuration,
    IReadOnlyDictionary<string, WorkflowNodeLiteral> Literals,
    IReadOnlyDictionary<string, string> TypeArguments);

/// <summary>A literal mapped into an input.</summary>
/// <param name="Value">The literal as stored.</param>
/// <param name="Term">The effective term of the input it feeds, closed, in document grammar.</param>
public sealed record WorkflowNodeLiteral(JsonElement Value, string Term);
