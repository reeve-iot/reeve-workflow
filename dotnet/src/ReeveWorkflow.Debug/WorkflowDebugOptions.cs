using System.ComponentModel.DataAnnotations;

namespace ReeveWorkflow.Debug;

/// <summary>How long a debug session's leftovers live, and how long a paused one may be left alone. Values outside
/// their documented ranges are clamped when the debug service is created.</summary>
public sealed class WorkflowDebugOptions
{
    /// <summary>Days after which finished sessions and their snapshots are swept.</summary>
    [Range(1, 365)]
    public int SnapshotRetentionDays { get; set; } = 7;

    /// <summary>Seconds of inactivity after which a paused session is abandoned and its run killed — a
    /// half-stepped run holds its slot and its locks, which is worse than a cancelled one.</summary>
    [Range(30, 3600)]
    public int OrphanedSessionTimeoutSeconds { get; set; } = 300;

    /// <summary>Seconds a command sent to the other replicas waits for the one holding the run to confirm it.
    /// Past it the answer is <see cref="WorkflowDebugCommandOutcome.Unconfirmed"/>. A command the holder applied
    /// is not undone by a slow confirmation.</summary>
    [Range(1, 60)]
    public int CommandAckTimeoutSeconds { get; set; } = 5;

    /// <summary>Seconds a delete waits, after a kill was applied, for the run to end before it gives up and keeps
    /// the session. A block that ignores cancellation holds the run for as long as it likes.</summary>
    [Range(1, 600)]
    public int KillWaitTimeoutSeconds { get; set; } = 30;
}
