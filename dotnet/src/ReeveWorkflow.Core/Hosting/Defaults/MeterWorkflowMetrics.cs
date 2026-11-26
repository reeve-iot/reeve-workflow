using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>The metrics a host gets for writing nothing: one <see cref="Meter"/>, and one line of
/// <c>AddMeter</c> wherever it configures OpenTelemetry. <see cref="IWorkflowMetrics"/> remains the override.</summary>
/// <remarks>Nothing here is tagged with a workflow id, a run id or a session id. A tag whose value set grows
/// with traffic is a series per run in the exporter's memory, which is the classic way a metrics default
/// takes a host down; the ids belong in the span, which already carries them.</remarks>
public sealed class MeterWorkflowMetrics : IWorkflowMetrics, IDisposable
{
    private readonly Meter meter;
    private readonly Counter<long> runsStarted;
    private readonly Counter<long> runsCompleted;
    private readonly Histogram<double> runDuration;
    private readonly Counter<long> nodeErrors;
    private readonly Counter<long> admissions;
    private readonly Counter<long> debugSessions;
    private readonly Histogram<double> debugPauseDuration;
    private readonly Counter<long> suspensionsStarted;
    private readonly Counter<long> suspensionsResolved;
    private readonly Histogram<double> suspensionDuration;

    private static readonly InstrumentAdvice<double> RunBuckets = new()
    {
        HistogramBucketBoundaries = [0.005, 0.025, 0.1, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 900],
    };

    private static readonly InstrumentAdvice<double> PauseBuckets = new()
    {
        HistogramBucketBoundaries = [1, 5, 15, 30, 60, 300, 900, 1800, 3600],
    };

    private static readonly InstrumentAdvice<double> ParkBuckets = new()
    {
        HistogramBucketBoundaries = [1, 60, 300, 900, 3600, 21600, 86400, 604800],
    };

    /// <summary>Creates the meter and every instrument on it, under the names the host chose or the defaults.</summary>
    public MeterWorkflowMetrics(WorkflowMetricsOptions? options = null)
    {
        var names = options ?? new WorkflowMetricsOptions();
        meter = new Meter(names.MeterName);
        runsStarted = meter.CreateCounter<long>(names.RunsStarted, unit: "{run}");
        runsCompleted = meter.CreateCounter<long>(names.RunsCompleted, unit: "{run}");
        runDuration = meter.CreateHistogram<double>(names.RunDuration, unit: "s", advice: RunBuckets);
        nodeErrors = meter.CreateCounter<long>(names.NodeErrors, unit: "{error}");
        admissions = meter.CreateCounter<long>(names.RunAdmissions, unit: "{admission}");
        debugSessions = meter.CreateCounter<long>(names.DebugSessions, unit: "{session}");
        debugPauseDuration = meter.CreateHistogram<double>(
            names.DebugPauseDuration, unit: "s", advice: PauseBuckets);
        suspensionsStarted = meter.CreateCounter<long>(names.SuspensionsStarted, unit: "{suspension}");
        suspensionsResolved = meter.CreateCounter<long>(names.SuspensionsResolved, unit: "{suspension}");
        suspensionDuration = meter.CreateHistogram<double>(
            names.SuspensionDuration, unit: "s", advice: ParkBuckets);
    }

    /// <inheritdoc />
    public void RecordExecutionStarted() => runsStarted.Add(1);

    /// <inheritdoc />
    public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null)
    {
        var tags = new TagList { { "status", status }, { "trigger_type", triggerType ?? "unknown" } };
        runsCompleted.Add(1, tags);
        runDuration.Record(durationSeconds, tags);
    }

    /// <inheritdoc />
    public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) =>
        nodeErrors.Add(1, new TagList
        {
            { "block_type", blockType },
            { "policy", Lower(policy) },
            { "caught", caught },
            { "exception_type", exceptionType },
        });

    /// <inheritdoc />
    public void RecordSupersession(Guid workflowId, string mode, string outcome) =>
        admissions.Add(1, new TagList { { "mode", Lower(mode) }, { "outcome", outcome } });

    /// <inheritdoc />
    public void RecordDebugSession(string outcome) =>
        debugSessions.Add(1, new TagList { { "outcome", outcome } });

    /// <inheritdoc />
    public void RecordDebugPauseDuration(double durationSeconds) => debugPauseDuration.Record(durationSeconds);

    /// <inheritdoc />
    public void RecordSuspensionStarted(string kind) =>
        suspensionsStarted.Add(1, new TagList { { "kind", Lower(kind) } });

    /// <inheritdoc />
    public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds)
    {
        var tags = new TagList { { "kind", Lower(kind) }, { "outcome", outcome } };
        suspensionsResolved.Add(1, tags);
        suspensionDuration.Record(durationSeconds, tags);
    }

    /// <summary>Closes the meter, which is what stops a listener seeing this host's instruments.</summary>
    public void Dispose() => meter.Dispose();

    private static string Lower(string value) => value.ToLowerInvariant();
}
