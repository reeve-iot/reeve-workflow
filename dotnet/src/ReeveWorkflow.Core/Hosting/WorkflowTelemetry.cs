namespace ReeveWorkflow.Core.Hosting;

/// <summary>The two names a host passes to its OpenTelemetry builder. Published as constants because that is
/// the whole integration: <c>.AddMeter(WorkflowTelemetry.MeterName)</c> and <c>.AddSource(…)</c>, no adapter.</summary>
public static class WorkflowTelemetry
{
    /// <summary>The meter every instrument this library records is created on.</summary>
    /// <remarks>A host that renames it through <see cref="WorkflowMetricsOptions.MeterName"/> has to pass its
    /// own string here instead; this constant is what an unconfigured host gets.</remarks>
    public const string MeterName = "ReeveWorkflow";

    /// <summary>The activity source the run and the block walk start their spans on.</summary>
    /// <remarks>A host whose collector is already configured for another name renames it through
    /// <see cref="WorkflowTracingOptions.ActivitySourceName"/>; this constant is what an unconfigured host gets.</remarks>
    public const string ActivitySourceName = "ReeveWorkflow";
}
