namespace ReeveWorkflow.Core.Hosting;

/// <summary>The meter's name and the name of every instrument on it. An instrument name is a series an
/// operator's dashboard reads, so each one is settable and none of them moves without a host saying so.</summary>
public sealed class WorkflowMetricsOptions
{
    /// <summary>What the host passes to <c>AddMeter</c>. Defaults to <see cref="WorkflowTelemetry.MeterName"/>.</summary>
    public string MeterName { get; set; } = WorkflowTelemetry.MeterName;

    /// <summary>Runs admitted, counted as they start.</summary>
    public string RunsStarted { get; set; } = "workflow.runs.started";

    /// <summary>Runs that ended, tagged with the status and with the trigger type when a caller supplied one —
    /// the library's own run loop does not, so the tag reads <c>unknown</c> unless a host records its own.</summary>
    public string RunsCompleted { get; set; } = "workflow.runs.completed";

    /// <summary>How long a run took, in seconds, tagged as <see cref="RunsCompleted"/> is.</summary>
    public string RunDuration { get; set; } = "workflow.run.duration";

    /// <summary>Errors raised by a node, tagged with the block type, the error policy, whether it was caught
    /// and the exception type — four tags, of which the exception type is the one that varies most.</summary>
    public string NodeErrors { get; set; } = "workflow.node.errors";

    /// <summary>What arbitration decided about an attempt to start a run, tagged with the mode and the
    /// outcome. A supersession is one value of that outcome, not the whole instrument.</summary>
    public string RunAdmissions { get; set; } = "workflow.run.admissions";

    /// <summary>Debug sessions that ended, tagged with the outcome.</summary>
    public string DebugSessions { get; set; } = "workflow.debug.sessions";

    /// <summary>How long a run sat paused at a breakpoint, in seconds.</summary>
    public string DebugPauseDuration { get; set; } = "workflow.debug.pause.duration";

    /// <summary>Runs parked on a wait, counted as they park, tagged with the kind of wait.</summary>
    public string SuspensionsStarted { get; set; } = "workflow.suspensions.started";

    /// <summary>Parked runs that were resolved, tagged with the kind and the outcome.</summary>
    public string SuspensionsResolved { get; set; } = "workflow.suspensions.resolved";

    /// <summary>How long a run stayed parked, in seconds, tagged as <see cref="SuspensionsResolved"/> is.</summary>
    public string SuspensionDuration { get; set; } = "workflow.suspension.duration";
}
