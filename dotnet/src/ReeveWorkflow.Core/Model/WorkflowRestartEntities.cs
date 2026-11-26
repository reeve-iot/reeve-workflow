namespace ReeveWorkflow.Core.Model;

/// <summary>A Restart workflow's generations: the last one issued and the newest one whose run started. One row per
/// workflow that has had a Restart trigger.</summary>
/// <remarks>See <c>IWorkflowRestartGenerations</c>. No foreign key to the workflow: a row outliving
/// its workflow is one small row, and a key would make every delete of a Restart workflow wait on it.</remarks>
public sealed class WorkflowRestartGeneration
{
    private WorkflowRestartGeneration()
    {
    }

    public WorkflowRestartGeneration(Guid workflowId, long issued, long started)
    {
        WorkflowId = workflowId;
        Issued = issued;
        Started = started;
    }

    public Guid WorkflowId { get; private set; }

    public long Issued { get; private set; }

    public long Started { get; private set; }
}

/// <summary>A Restart generation whose trigger is waiting to run, and until when it counts as live unless its trigger
/// touches it again.</summary>
/// <remarks>The moment is Unix milliseconds rather than a timestamp: it is only ever compared, and a
/// number compares the same in every provider, where some cannot compare a date with an offset in a query at all.</remarks>
public sealed class WorkflowRestartWaiter
{
    private WorkflowRestartWaiter()
    {
    }

    public WorkflowRestartWaiter(Guid workflowId, long generation, DateTimeOffset liveUntilUtc)
    {
        WorkflowId = workflowId;
        Generation = generation;
        LiveUntilUnixMs = liveUntilUtc.ToUnixTimeMilliseconds();
    }

    public Guid WorkflowId { get; private set; }

    public long Generation { get; private set; }

    public long LiveUntilUnixMs { get; private set; }
}
