namespace ReeveWorkflow.Core.Execution;

/// <summary>Raised when a workflow trigger is rejected because the run coordinator's concurrency budget
/// is exhausted and <see cref="ReeveWorkflow.Core.Model.WorkflowOverflowBehavior.Error"/> is set.</summary>
public sealed class WorkflowConcurrencyException : Exception
{
    public Guid WorkflowId { get; }
    public string Mode { get; }
    public string Outcome { get; }

    public WorkflowConcurrencyException(Guid workflowId, string mode, string outcome, string message)
        : base(message)
    {
        WorkflowId = workflowId;
        Mode = mode;
        Outcome = outcome;
    }
}
