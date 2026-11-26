namespace ReeveWorkflow.Core.Execution;

internal static class WorkflowSleepResumption
{
    private static readonly AsyncLocal<Pending?> current = new();

    public static void Begin(Guid stateExecutionId, string nodeId, DateTimeOffset wakeUpAt) =>
        current.Value = new Pending(stateExecutionId, nodeId, wakeUpAt);

    public static DateTimeOffset? Take(Guid? stateExecutionId, string nodeId) =>
        current.Value is { } pending && pending.StateExecutionId == stateExecutionId && pending.NodeId == nodeId
        && Interlocked.Exchange(ref pending.Taken, 1) == 0
            ? pending.WakeUpAt
            : null;

    private sealed class Pending(Guid stateExecutionId, string nodeId, DateTimeOffset wakeUpAt)
    {
        public Guid StateExecutionId { get; } = stateExecutionId;
        public string NodeId { get; } = nodeId;
        public DateTimeOffset WakeUpAt { get; } = wakeUpAt;
        public int Taken;
    }
}
