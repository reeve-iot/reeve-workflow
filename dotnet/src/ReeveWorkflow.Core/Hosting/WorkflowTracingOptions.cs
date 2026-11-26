namespace ReeveWorkflow.Core.Hosting;

/// <summary>What this library's spans are published under. A span name is a series an operator's dashboard
/// reads, so the source is settable and does not move without a host saying so.</summary>
public sealed class WorkflowTracingOptions
{
    /// <summary>What the host passes to <c>AddSource</c>. Defaults to <see cref="WorkflowTelemetry.ActivitySourceName"/>.</summary>
    public string ActivitySourceName { get; set; } = WorkflowTelemetry.ActivitySourceName;
}
