namespace ReeveWorkflow.Core.Execution;

/// <summary>A run asked for a specific version — a sub-workflow child, a resume, a restart — and the store
/// does not have it. A pin that cannot be honoured must fail the run, never silently fall back to current.</summary>
public sealed class PinnedWorkflowVersionNotFoundException(Guid workflowId, Guid versionId)
    : Exception($"Pinned version '{versionId}' for workflow '{workflowId}' was not found.")
{
    public Guid WorkflowId { get; } = workflowId;
    public Guid VersionId { get; } = versionId;
}
