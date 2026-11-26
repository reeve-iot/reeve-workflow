using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>A codec refuses a save: it will not protect a value or admit a reference. <see cref="Exception.Message"/> is the reason
/// the host writes for the caller, so it carries no value.</summary>
public sealed class WorkflowSecretRefusedException(string reason) : Exception(reason);

/// <summary>A codec cannot reveal a stored value. The library discards the host's message and fails the node with a message that
/// names the input and the key only.</summary>
public sealed class WorkflowSecretUnresolvedException : Exception, IWorkflowCodedException
{
    /// <summary>For a host's codec; the message is not shown.</summary>
    public WorkflowSecretUnresolvedException()
        : base("The secret could not be revealed.")
    {
    }

    internal WorkflowSecretUnresolvedException(string input, string entry)
        : base($"Input '{input}' {entry} holds a secret that could not be revealed.")
    {
    }

    /// <summary>Always <see cref="WorkflowErrorCodes.SecretUnresolved"/>.</summary>
    public string Code => WorkflowErrorCodes.SecretUnresolved;
}
