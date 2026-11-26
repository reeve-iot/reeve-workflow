namespace ReeveWorkflow.Core.Execution;

/// <summary>How often a run writes its row, and with it its checkpoint and its fence. Every write is one statement
/// against the database per block, which on a durable disk is the cost of a run.</summary>
/// <remarks>Anything but <see cref="EveryBlock"/> needs the run heartbeat, which is then what
/// stops a replica whose row a peer took between two writes; <c>AddWorkflowEngine</c> refuses it otherwise.</remarks>
public sealed record RunStateWriteMode
{
    private RunStateWriteMode(RunStateWriteModeKind kind, TimeSpan? interval)
    {
        Kind = kind;
        Interval = interval;
    }

    /// <summary>Which of the three this is.</summary>
    public RunStateWriteModeKind Kind { get; }

    /// <summary>How old the last write may be before the next block writes again. Only for
    /// <see cref="RunStateWriteModeKind.Heartbeat"/>.</summary>
    public TimeSpan? Interval { get; }

    /// <summary>A write before every block: a recovery repeats at most the block in flight. The default.</summary>
    public static RunStateWriteMode EveryBlock { get; } = new(RunStateWriteModeKind.EveryBlock, null);

    /// <summary>A write before the first block, before every block that is not idempotent, and before the block
    /// after one. Between two writes only idempotent blocks run, so a recovery still repeats nothing else.</summary>
    public static RunStateWriteMode BeforeNonIdempotentOnly { get; } =
        new(RunStateWriteModeKind.BeforeNonIdempotentOnly, null);

    /// <summary>A write before a block only when the last one is at least <paramref name="interval"/> old. The
    /// checkpoint may be blocks behind, so a recovery re-enters only if every block it would repeat is idempotent.</summary>
    public static RunStateWriteMode Heartbeat(TimeSpan interval) =>
        interval > TimeSpan.Zero
            ? new(RunStateWriteModeKind.Heartbeat, interval)
            : throw new ArgumentOutOfRangeException(nameof(interval), interval, "A heartbeat interval must be positive.");
}

/// <summary>The three ways a run can write its row. See <see cref="RunStateWriteMode"/>.</summary>
public enum RunStateWriteModeKind
{
    /// <summary>See <see cref="RunStateWriteMode.EveryBlock"/>.</summary>
    EveryBlock,

    /// <summary>See <see cref="RunStateWriteMode.BeforeNonIdempotentOnly"/>.</summary>
    BeforeNonIdempotentOnly,

    /// <summary>See <see cref="RunStateWriteMode.Heartbeat"/>.</summary>
    Heartbeat
}
