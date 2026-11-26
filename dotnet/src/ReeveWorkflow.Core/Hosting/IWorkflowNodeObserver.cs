namespace ReeveWorkflow.Core.Hosting;

/// <summary>Per-node progress, for whoever is watching a run happen. Calls are fire-and-forget: the engine
/// never waits for an observer and never fails a run because one threw.</summary>
public interface IWorkflowNodeObserver
{
    Task NotifyNodeStartedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, CancellationToken cancellationToken = default);

    Task NotifyNodeCompletedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> outputs, long durationMs, CancellationToken cancellationToken = default);

    Task NotifyNodeFailedAsync(Guid workflowId, Guid executionId, string nodeId, IReadOnlyDictionary<string, object?> inputs, string error, long durationMs, CancellationToken cancellationToken = default);
}
