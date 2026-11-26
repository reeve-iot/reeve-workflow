namespace ReeveWorkflow.Core.Execution;

/// <summary>Raised when a sibling trigger under Restart mode cancels this in-flight run. Distinct
/// from a raw <see cref="OperationCanceledException"/>: the caller's own token was not signalled.</summary>
public sealed class WorkflowSupersededException : OperationCanceledException
{
    public Guid WorkflowId { get; }
    public string Mode { get; }

    public WorkflowSupersededException(Guid workflowId, string mode, Exception? innerException = null)
        : base(BuildMessage(workflowId, mode), innerException)
    {
        WorkflowId = workflowId;
        Mode = mode;
    }

    private static string BuildMessage(Guid workflowId, string mode) =>
        $"Workflow '{workflowId}' run was superseded under {mode} mode: a newer trigger cancelled this in-flight run.";
}
