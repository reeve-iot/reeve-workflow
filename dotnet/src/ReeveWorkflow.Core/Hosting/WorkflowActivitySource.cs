using System.Diagnostics;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>The source every span this library starts is published on. One per host rather than one per run:
/// an <see cref="ActivitySource"/> attaches to the process's listeners and a new one per run accumulates them.</summary>
public sealed class WorkflowActivitySource : IDisposable
{
    /// <summary>What a host that named nothing gets — the library's own name, for the life of the process.
    /// A host that renames the source owns the one it made, and disposes it.</summary>
    public static WorkflowActivitySource Default { get; } = new();

    private readonly ActivitySource source;

    /// <param name="options">The name to publish under. Null is the library's own.</param>
    public WorkflowActivitySource(WorkflowTracingOptions? options = null) =>
        source = new ActivitySource((options ?? new WorkflowTracingOptions()).ActivitySourceName);

    /// <summary>The name this publishes under, which is the string the host's collector has to be told.</summary>
    public string Name => source.Name;

    /// <summary>Starts a span, or answers null when nothing is listening for one.</summary>
    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal) =>
        source.StartActivity(name, kind);

    /// <inheritdoc />
    public void Dispose() => source.Dispose();
}
