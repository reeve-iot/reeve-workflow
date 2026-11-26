namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>Never suspends execution. The default for a host with no degraded mode: nothing in such a host can
/// turn the gate on, so any other answer would stop runs for a condition it has no way to leave.</summary>
public sealed class AlwaysRunningExecutionGate : IWorkflowExecutionGate
{
    /// <summary>The one instance. It holds no state, so a host never needs a second.</summary>
    public static readonly AlwaysRunningExecutionGate Instance = new();

    /// <inheritdoc />
    public bool IsExecutionSuspended => false;
}
