namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>Watches nothing. The default for a host with no live view of a run in progress; what outlives the
/// run is written by the journal, so this observer's silence loses no record.</summary>
public sealed class NullWorkflowNodeObserver : IWorkflowNodeObserver
{
    /// <summary>The one instance. It holds no state, so a host never needs a second.</summary>
    public static readonly NullWorkflowNodeObserver Instance = new();

    /// <inheritdoc />
    public Task NotifyNodeStartedAsync(
        Guid workflowId,
        Guid executionId,
        string nodeId,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyNodeCompletedAsync(
        Guid workflowId,
        Guid executionId,
        string nodeId,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, object?> outputs,
        long durationMs,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task NotifyNodeFailedAsync(
        Guid workflowId,
        Guid executionId,
        string nodeId,
        IReadOnlyDictionary<string, object?> inputs,
        string error,
        long durationMs,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}
