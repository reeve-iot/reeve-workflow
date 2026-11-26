namespace ReeveWorkflow.Core.Execution;

/// <summary>The host's start admission refused a run, or could not be asked. Thrown by admission before anything of
/// the run exists, so the caller — the mounted endpoint, a host, a sub-workflow block — fails in its place.</summary>
/// <remarks>A refusal leaves no run row: the host that refused knows why and records it.</remarks>
public sealed class WorkflowStartRefusedException : InvalidOperationException
{
    /// <summary>The <c>ProblemDetails</c> extension an HTTP surface writes <see cref="Code"/> under.</summary>
    public const string ProblemExtension = "code";

    /// <param name="workflowId">The workflow whose start was refused.</param>
    /// <param name="code">The host's code, or <see cref="Hosting.WorkflowStartRefusalCodes.AdmissionFaulted"/>.</param>
    /// <param name="message">What the caller may be told.</param>
    /// <param name="inner">What the host's admission threw, for a refusal that is its fault.</param>
    public WorkflowStartRefusedException(Guid workflowId, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        WorkflowId = workflowId;
        Code = code;
    }

    /// <summary>The workflow whose start was refused.</summary>
    public Guid WorkflowId { get; }

    /// <summary>Why, in the host's own terms.</summary>
    public string Code { get; }

    /// <summary>Whether the host's admission failed rather than said no.</summary>
    public bool IsFault => Code == Hosting.WorkflowStartRefusalCodes.AdmissionFaulted;
}
