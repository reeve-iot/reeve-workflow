namespace ReeveWorkflow.Core.Execution;

/// <summary>Nothing is stored under this id. Thrown by admission before any run state exists, so a host maps
/// it to 404 and there is no execution row to explain.</summary>
public sealed class WorkflowNotFoundException(Guid workflowId) : Exception($"Workflow '{workflowId}' was not found.")
{
    public Guid WorkflowId { get; } = workflowId;
}
