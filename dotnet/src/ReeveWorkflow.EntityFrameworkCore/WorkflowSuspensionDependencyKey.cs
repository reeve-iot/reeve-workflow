namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>A lookup key for a parked suspension, maintained with the row it indexes.</summary>
public sealed class WorkflowSuspensionDependencyKey(Guid suspensionId, string dependencyKey)
{
    private WorkflowSuspensionDependencyKey() : this(Guid.Empty, string.Empty) { }

    public Guid SuspensionId { get; private set; } = suspensionId;
    public string DependencyKey { get; private set; } = dependencyKey;
}
