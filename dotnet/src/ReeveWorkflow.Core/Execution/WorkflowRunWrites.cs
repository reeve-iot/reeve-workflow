using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal sealed class WorkflowRunWrites
{
    private static readonly AsyncLocal<WorkflowRunWrites?> current = new();
    private readonly Lock gate = new();
    private DateTimeOffset? lastWriteUtc;
    private bool afterNonIdempotentRoot;
    private bool afterNonIdempotentNested;

    private WorkflowRunWrites(Guid stateExecutionId, bool recoverable, bool rowNamesEntry, CancellationToken caller)
    {
        StateExecutionId = stateExecutionId;
        Recoverable = recoverable;
        RowNamesEntry = rowNamesEntry;
        Caller = caller;
    }

    public Guid StateExecutionId { get; }

    public bool Recoverable { get; }

    public bool RowNamesEntry { get; private set; }

    public CancellationToken Caller { get; }

    public static WorkflowRunWrites Begin(
        Guid stateExecutionId, bool recoverable, bool rowNamesEntry = false, CancellationToken caller = default)
    {
        var writes = new WorkflowRunWrites(stateExecutionId, recoverable, rowNamesEntry, caller);
        current.Value = writes;
        return writes;
    }

    public static WorkflowRunWrites? Find(Guid stateExecutionId) =>
        current.Value is { } writes && writes.StateExecutionId == stateExecutionId ? writes : null;

    public string? LastCheckpoint { get; set; }

    public RootPosition? Root { get; set; }

    public CheckpointOutputFragments OutputFragments { get; } = new();

    internal sealed record RootPosition(
        Dictionary<string, IReadOnlyDictionary<string, object?>> ExecutedOutputs,
        CompiledWalk Walk,
        string NodeInFlight,
        string? InputNodeId,
        Guid? TriggerId,
        TriggerEnvelope? TriggerEnvelope,
        bool SkipPolicyCaught);

    public static WorkflowRunWrites For(Guid stateExecutionId, bool root)
    {
        if (current.Value is { } writes && writes.StateExecutionId == stateExecutionId)
            return writes;

        var fresh = new WorkflowRunWrites(stateExecutionId, recoverable: false, rowNamesEntry: false, caller: default);
        if (root)
            current.Value = fresh;
        return fresh;
    }

    public bool ShouldWrite(RunStateWriteMode mode, bool repeatsNothing, bool root, DateTimeOffset now)
    {
        lock (gate)
        {
            var afterOne = root ? afterNonIdempotentRoot : afterNonIdempotentNested;
            var write = lastWriteUtc is not { } last
                || mode.Kind switch
                {
                    RunStateWriteModeKind.BeforeNonIdempotentOnly => !repeatsNothing || afterOne,
                    RunStateWriteModeKind.Heartbeat => now - last >= mode.Interval,
                    _ => true
                };
            if (root)
                afterNonIdempotentRoot = !repeatsNothing;
            afterNonIdempotentNested = !repeatsNothing;
            return write;
        }
    }

    public void Wrote(DateTimeOffset now, string? rootCheckpoint, bool namesEntry)
    {
        lock (gate)
        {
            lastWriteUtc = now;
            if (rootCheckpoint is not null)
                LastCheckpoint = rootCheckpoint;
            RowNamesEntry = namesEntry;
        }
    }
}
