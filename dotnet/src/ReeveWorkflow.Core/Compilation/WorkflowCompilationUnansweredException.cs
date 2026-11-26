namespace ReeveWorkflow.Core.Compilation;

/// <summary>Whether a version compiles could not be answered, so what asked was not done: the compiler faulted
/// (<see cref="CompilationFaultException"/>), a resolver or the store it read through threw, or a version a run's
/// manifest pins could no longer be read. Run admission throws it and starts nothing; <c>WorkflowEditor</c> throws it
/// from publish and rollback, which make nothing current, and carries it on a save's <c>Fault</c>. No verdict was
/// recorded, and the same request may succeed later. The fault is <see cref="Exception.InnerException"/>.</summary>
/// <remarks>For a run outside Single mode, the refusing replica holds the fire's dedup claim for its window, and nothing
/// releases it: a retry of that fire with the same key inside the window is dropped, not started.</remarks>
public sealed class WorkflowCompilationUnansweredException : Exception
{
    /// <summary>A run's refusal, with the message admission writes on the refused row.</summary>
    /// <param name="workflowId">The workflow whose version could not be judged.</param>
    /// <param name="workflowVersionId">That version.</param>
    /// <param name="fault">What was thrown.</param>
    public WorkflowCompilationUnansweredException(Guid workflowId, Guid workflowVersionId, Exception fault)
        : this(workflowId, workflowVersionId,
            $"Whether version '{workflowVersionId}' of workflow '{workflowId}' compiles could not be answered, so "
            + "the run was not started. Nothing ran; the same start may succeed later.",
            fault)
    {
    }

    /// <summary>Another caller's refusal, which says in <paramref name="message"/> what was not done.</summary>
    /// <param name="workflowId">The workflow whose version could not be judged.</param>
    /// <param name="workflowVersionId">That version.</param>
    /// <param name="message">What was not done, for a caller to show; never the fault's own message.</param>
    /// <param name="fault">What was thrown.</param>
    public WorkflowCompilationUnansweredException(Guid workflowId, Guid workflowVersionId, string message, Exception fault)
        : base(message, fault ?? throw new ArgumentNullException(nameof(fault)))
    {
        WorkflowId = workflowId;
        WorkflowVersionId = workflowVersionId;
    }

    /// <summary>The workflow whose version could not be judged: a run's own or one its manifest pins, or the workflow
    /// an editor door wrote.</summary>
    public Guid WorkflowId { get; }

    /// <summary>The version that could not be judged. From a rollback, the version whose text was being copied.</summary>
    public Guid WorkflowVersionId { get; }
}
