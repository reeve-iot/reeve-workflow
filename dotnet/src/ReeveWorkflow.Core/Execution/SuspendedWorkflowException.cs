using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Thrown when a wait block requests suspension. Caught by the outer execution loop, NOT the Polly
/// retry pipeline, which persists the suspension row and exits cleanly.</summary>
public sealed class SuspendedWorkflowException : Exception
{
    public SuspendedWorkflowException(
        SuspendResult suspendResult,
        string blockId,
        Guid executionId,
        Guid workflowId,
        string statePayloadJson)
        : base("Workflow suspended at wait block.")
    {
        SuspendResult = suspendResult;
        BlockId = blockId;
        ExecutionId = executionId;
        WorkflowId = workflowId;
        StatePayloadJson = statePayloadJson;
    }

    public SuspendResult SuspendResult { get; }
    public string BlockId { get; }
    public Guid ExecutionId { get; }
    public Guid WorkflowId { get; }
    public string StatePayloadJson { get; }

    internal WorkflowExecutionStep? RecordedStep { get; init; }
}
