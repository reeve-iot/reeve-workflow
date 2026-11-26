namespace ReeveWorkflow.Editor;

/// <summary>A write was refused for what the request said about the workflow itself, rather than about
/// its graph — a concurrency mode spelled in a way nothing recognises, say.</summary>
/// <param name="message">What is wrong and what to write instead.</param>
/// <param name="code">A <see cref="Core.Hosting.WorkflowSaveRefusalCodes"/> value when the refusal has one; the API answers it as <c>code</c>.</param>
public sealed class InvalidWorkflowEditRequestException(string message, string? code = null) : Exception(message)
{
    /// <summary>The refusal's code, or null for a refusal with none.</summary>
    public string? Code { get; } = code;
}

/// <summary>Two writers allocated the same version number and the database refused the second. The row is
/// gone rather than wrong, so the answer is "reload and try again" and not a fault.</summary>
public sealed class WorkflowVersionConflictException(Guid workflowId, int versionNumber, Exception? innerException = null)
    : Exception($"Version '{versionNumber}' of workflow '{workflowId}' was written by somebody else first.", innerException)
{
    /// <summary>The workflow whose history was being appended to.</summary>
    public Guid WorkflowId { get; } = workflowId;

    /// <summary>The number both writers wanted.</summary>
    public int VersionNumber { get; } = versionNumber;
}

/// <summary>An update named the revision its caller read (<see cref="UpdateWorkflowRequest.ExpectedRevision"/>), and
/// another write has moved the workflow since. Nothing of the update was written. Read the workflow again, and
/// apply the change to what it holds now.</summary>
public sealed class WorkflowRevisionMismatchException(Guid workflowId, int expectedRevision)
    : Exception($"Workflow '{workflowId}' is no longer at revision {expectedRevision}: another write changed it after it was read. Nothing was saved; read it again and retry.")
{
    /// <summary>The workflow that moved.</summary>
    public Guid WorkflowId { get; } = workflowId;

    /// <summary>The revision the update expected.</summary>
    public int ExpectedRevision { get; } = expectedRevision;
}

/// <summary>Another write moved the workflow after this one read it: a save into a draft that is no longer the
/// draft, or a publish, a rollback or an auto-publish whose revision moved since it read. Nothing of this write was
/// published. A save refused before its commit wrote nothing; an auto-published save overtaken after its commit stands
/// as it was saved. Read the workflow again.</summary>
public sealed class WorkflowDraftChangedException(Guid workflowId)
    : Exception($"Workflow '{workflowId}' was changed by another write after this one read it; nothing was published. Read it again and retry.")
{
    /// <summary>The workflow that moved.</summary>
    public Guid WorkflowId { get; } = workflowId;
}

/// <summary>Publish was asked for a workflow with no draft. Publishing what is already published would
/// make a version that differs from its predecessor in nothing but its number.</summary>
public sealed class NoDraftVersionToPublishException(Guid workflowId)
    : Exception($"Workflow '{workflowId}' has no draft version to publish.")
{
    /// <summary>The workflow that was asked to publish.</summary>
    public Guid WorkflowId { get; } = workflowId;
}

/// <summary>Rollback was asked for the version that is already current. It would copy a version onto
/// itself under a new number, which is a change to the history and to nothing else.</summary>
public sealed class CannotRollbackToActiveVersionException(int versionNumber)
    : Exception($"Version '{versionNumber}' is already the current version.")
{
    /// <summary>The version number that is already current.</summary>
    public int VersionNumber { get; } = versionNumber;
}

/// <summary>Delete was refused because runs of this workflow have not finished. The rows hold a foreign
/// key the database refuses to break, so this is a 409 rather than a 500 out of the provider.</summary>
public sealed class WorkflowHasActiveExecutionsException(Guid workflowId, int activeExecutionCount)
    : Exception($"Workflow '{workflowId}' has {activeExecutionCount} execution(s) that have not finished.")
{
    /// <summary>The workflow that was asked to be deleted.</summary>
    public Guid WorkflowId { get; } = workflowId;

    /// <summary>How many runs are still in a non-terminal state.</summary>
    public int ActiveExecutionCount { get; } = activeExecutionCount;
}

/// <summary>A publish or a rollback was asked for inside a transaction the host commits
/// (<see cref="IWorkflowEditorTransaction.JoinsAnotherTransaction"/>). The compiler reads outside it, so it cannot
/// judge what that transaction wrote; nothing was done. Call it again after the host commits.</summary>
public sealed class WorkflowPublishInHostTransactionException(Guid workflowId)
    : Exception($"Workflow '{workflowId}' cannot be published or rolled back inside a transaction the host commits; nothing was done. Call it again after the commit.")
{
    /// <summary>The workflow that was asked to publish or roll back.</summary>
    public Guid WorkflowId { get; } = workflowId;
}
