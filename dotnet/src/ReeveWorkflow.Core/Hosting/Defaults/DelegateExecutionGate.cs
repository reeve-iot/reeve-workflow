namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>The execution gate as the one thing it actually is: a question the host answers on each run
/// attempt. A host whose answer is a field on a service it already has writes no class for this.</summary>
public sealed class DelegateExecutionGate(Func<bool> isExecutionSuspended) : IWorkflowExecutionGate
{
    private readonly Func<bool> isExecutionSuspended =
        isExecutionSuspended ?? throw new ArgumentNullException(nameof(isExecutionSuspended));

    /// <inheritdoc />
    public bool IsExecutionSuspended => isExecutionSuspended();
}
