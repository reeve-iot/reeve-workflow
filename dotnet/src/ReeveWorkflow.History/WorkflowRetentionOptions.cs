using System.ComponentModel.DataAnnotations;

namespace ReeveWorkflow.History;

/// <summary>What the two sweeps keep, and how often they run. Nothing rejects a value outside the bounds below — each sweep clamps to them.</summary>
public sealed class WorkflowRetentionOptions
{
    public WorkflowVersionRetentionOptions Versions { get; set; } = new();

    public WorkflowExecutionHistoryRetentionOptions ExecutionHistory { get; set; } = new();
}

public sealed class WorkflowVersionRetentionOptions
{
    /// <summary>A superseded version goes once this many newer superseded versions exist, or once it was created more
    /// than <see cref="RetentionDays"/> ago, whichever comes first. Current and draft are neither counted nor deleted,
    /// and no version a run references, or a run that has not ended pins in its manifest in a state row when the sweep
    /// reads the pins, is deleted.</summary>
    [Range(1, 10_000)]
    public int MaxVersionsPerWorkflow { get; init; } = 50;

    /// <summary>Days after its creation a superseded version goes, however few newer ones exist; see
    /// <see cref="MaxVersionsPerWorkflow"/>.</summary>
    [Range(1, 36_500)]
    public int RetentionDays { get; init; } = 365;

    /// <summary>Hours between sweeps, clamped by the sweep itself.</summary>
    [Range(1, 8_760)]
    public int SweepIntervalHours { get; init; } = 24;
}

/// <summary>Retention for run history: execution logs, their entries and node steps, wait-block
/// suspensions, and the terminal distributed-execution state rows.</summary>
public sealed class WorkflowExecutionHistoryRetentionOptions
{
    /// <summary>Days a finished run stays queryable before the sweep deletes it.</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 90;

    /// <summary>Rows deleted per statement. Bounds the lock footprint of one delete.</summary>
    [Range(50, 50_000)]
    public int DeleteBatchSize { get; init; } = 1_000;

    /// <summary>Ceiling per table per sweep. What bounds a sweep by default is
    /// <see cref="MaxSweepSeconds"/>, and this is the range's top.</summary>
    [Range(50, 5_000_000)]
    public int MaxDeletesPerSweep { get; init; } = 5_000_000;

    [Range(1, 168)]
    public int SweepIntervalHours { get; init; } = 6;

    /// <summary>Seconds one sweep deletes from each table before it lets the database go. A sweep that stops
    /// here, or at <see cref="MaxDeletesPerSweep"/>, with rows still expired comes back after a pause as long
    /// rather than after <see cref="SweepIntervalHours"/>.</summary>
    [Range(1, 3_600)]
    public int MaxSweepSeconds { get; init; } = 60;
}
