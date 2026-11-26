namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>Counts nothing. The default for a host that exports no meter — a named type rather than a null
/// reference, so "this deployment has no workflow metrics" is a line an operator can read in the container.</summary>
public sealed class NullWorkflowMetrics : IWorkflowMetrics
{
    /// <summary>The one instance. It holds no state, so a host never needs a second.</summary>
    public static readonly NullWorkflowMetrics Instance = new();

    /// <inheritdoc />
    public void RecordExecutionStarted()
    {
    }

    /// <inheritdoc />
    public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null)
    {
    }

    /// <inheritdoc />
    public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType)
    {
    }

    /// <inheritdoc />
    public void RecordSupersession(Guid workflowId, string mode, string outcome)
    {
    }

    /// <inheritdoc />
    public void RecordDebugSession(string outcome)
    {
    }

    /// <inheritdoc />
    public void RecordDebugPauseDuration(double durationSeconds)
    {
    }

    /// <inheritdoc />
    public void RecordSuspensionStarted(string kind)
    {
    }

    /// <inheritdoc />
    public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds)
    {
    }
}
