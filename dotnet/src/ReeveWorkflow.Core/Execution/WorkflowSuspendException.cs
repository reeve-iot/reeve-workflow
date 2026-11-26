namespace ReeveWorkflow.Core.Execution;

/// <summary>Thrown by wait blocks to suspend the current run. The engine catches it at the dispatch boundary,
/// not in the outer retry pipeline.</summary>
public sealed class WorkflowSuspendException : Exception
{
    public WorkflowSuspendException(SuspendResult suspendResult)
        : base("Workflow suspend requested.")
    {
        SuspendResult = suspendResult ?? throw new ArgumentNullException(nameof(suspendResult));
    }

    public SuspendResult SuspendResult { get; }
}
