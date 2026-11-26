namespace ReeveWorkflow.Core.Execution;

/// <summary>A run was asked for further down a chain of workflows calling workflows than the engine allows. Thrown
/// by admission before the run exists, so the caller — a sub-workflow block, most often — fails in its place.</summary>
public sealed class WorkflowCallDepthExceededException(Guid workflowId, int callDepth, int limit)
    : InvalidOperationException(
        $"Workflow '{workflowId}' was called at depth {callDepth}, past the limit of {limit} runs calling one another. "
        + "A workflow that reaches itself through its sub-workflows recurses until it gets here.")
{
    /// <summary>The workflow that was refused.</summary>
    public Guid WorkflowId { get; } = workflowId;

    /// <summary>The depth it was asked for at.</summary>
    public int CallDepth { get; } = callDepth;

    /// <summary>The deepest a run may be called at.</summary>
    public int Limit { get; } = limit;
}
