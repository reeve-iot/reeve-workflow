namespace ReeveWorkflow.Core.Execution;

/// <summary>Thrown to the caller of a run that finished its walk when what it did could not all be written down.</summary>
/// <remarks>The run is over, and nothing here tries it again: its row is completed, unless a peer took
/// it meanwhile or that write failed as well, in which case whoever holds the row, or the sweep it ages out to, decides. A parent run whose
/// block surfaces this fails without a retry, and no error policy or try/catch container handles it, because running
/// the child again would repeat everything it already did.</remarks>
public sealed class WorkflowRunNotRecordedException(Guid workflowId, Exception innerException)
    : Exception(
        $"run_not_recorded: a run of workflow {workflowId} finished, but what it did could not all be recorded: "
        + innerException.Message, innerException)
{
    public Guid WorkflowId { get; } = workflowId;
}
