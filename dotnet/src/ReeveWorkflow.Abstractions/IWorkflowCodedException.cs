namespace ReeveWorkflow.Abstractions;

/// <summary>An exception that names the code its node's <c>_error</c> and a sub-canvas error carry. A block that throws
/// one gives its failure a code of its own; any other exception is <see cref="WorkflowErrorCodes.BlockFailed"/>.</summary>
/// <remarks>The executor reads <see cref="Code"/> once, when the node fails. A blank code, or a getter that throws,
/// reads as <see cref="WorkflowErrorCodes.BlockFailed"/>.</remarks>
public interface IWorkflowCodedException
{
    /// <summary>The code, such as <c>expression-runtime-error</c> or a host's own.</summary>
    string Code { get; }
}
