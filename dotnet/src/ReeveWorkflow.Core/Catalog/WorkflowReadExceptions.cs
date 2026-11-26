namespace ReeveWorkflow.Core.Catalog;

/// <summary>No run under that id. Thrown by the reads rather than returning null, so a caller cannot
/// mistake "there is no such run" for "the run has nothing to show".</summary>
public sealed class WorkflowExecutionNotFoundException(Guid executionId)
    : Exception($"Execution '{executionId}' was not found.")
{
    /// <summary>The run that was asked for.</summary>
    public Guid ExecutionId { get; } = executionId;
}

/// <summary>No version with that number on that workflow. The number is the caller's, so this is an
/// ordinary answer to a bad request rather than a broken invariant.</summary>
public sealed class WorkflowVersionNotFoundException(Guid workflowId, int versionNumber)
    : Exception($"Workflow '{workflowId}' version '{versionNumber}' was not found.")
{
    /// <summary>The workflow whose versions were searched.</summary>
    public Guid WorkflowId { get; } = workflowId;

    /// <summary>The version number that is not there.</summary>
    public int VersionNumber { get; } = versionNumber;
}
