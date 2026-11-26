namespace ReeveWorkflow.Core.Execution;

/// <summary>Thrown when a workflow's scripting block runs without the workflows.scripting permission.
/// The message must never contain secrets, stack traces, or internal system details.</summary>
public sealed class WorkflowScriptingPermissionException : Exception
{
    public WorkflowScriptingPermissionException()
        : base("This workflow contains scripting blocks that require the 'workflows.scripting' permission. " +
               "Contact an administrator to have this permission granted to your account.")
    {
    }
}
