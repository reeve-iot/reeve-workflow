namespace ReeveWorkflow.Editor;

/// <summary>Every write a request can make of a workflow. Ownership is a parameter, as it is on the read
/// side: the host decides who a caller is, and the library refuses only what that user does not own.</summary>
/// <remarks>Nothing the Executor runs goes through here. A definition row written by an import, a seed or
/// another process is legitimate input to it, which is what keeps the two products two.
///
/// Every method writes through the workflow context and saves it whole, because that is what
/// <c>SaveChanges</c> does. A host that stages rows of its own on that context and then calls the editor
/// has them written and committed with the workflow — stage them on the host's own context, or call the
/// editor first.</remarks>
public interface IWorkflowEditor
{
    /// <summary>Creates a workflow with its first version as the draft, and publishes it when it compiles.</summary>
    Task<WorkflowEditResult> CreateAsync(
        Guid userId, CreateWorkflowRequest request, CancellationToken cancellationToken = default);

    /// <summary>Applies a change. A changed definition becomes the draft, published when it compiles and
    /// <see cref="UpdateWorkflowRequest.AutoPublish"/> asks.</summary>
    Task<WorkflowEditResult> UpdateAsync(
        Guid userId, Guid workflowId, UpdateWorkflowRequest request, CancellationToken cancellationToken = default);

    /// <summary>Promotes the draft to the current version.</summary>
    Task<WorkflowEditResult> PublishAsync(
        Guid userId, Guid workflowId, CancellationToken cancellationToken = default);

    /// <summary>Copies an old version forward as a new version, written as the draft and then promoted. History is appended to,
    /// never rewritten, so what ran before a rollback is still readable after it. A copy the host's node validator refuses
    /// stays the draft and is not promoted; the result is unpublished and carries the diagnostics.</summary>
    Task<WorkflowEditResult> RollbackAsync(
        Guid userId, Guid workflowId, int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>Deletes a workflow and the rows of its own that hold it back, and answers with what was
    /// deleted so a host can write its audit row without having read it first.</summary>
    Task<WorkflowEditResult> DeleteAsync(
        Guid userId, Guid workflowId, CancellationToken cancellationToken = default);
}
