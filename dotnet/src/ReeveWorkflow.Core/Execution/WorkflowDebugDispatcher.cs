
namespace ReeveWorkflow.Core.Execution;

/// <summary>The debug/pause unit of a run. DebugPausedCounter is a tiny heap-allocated box letting a run
/// increment a pause count across nested ExecuteGraphAsync calls without a static field; allocated only when a debug session is bound.</summary>
public sealed class DebugPausedCounter
{
    public int Value { get; private set; }
    public void Increment() => Value++;
}

public static class WorkflowDebugDispatcher
{
    /// <summary>Forwards the per-node breakpoint check to the debug hook. The caller owns the
    /// non-null gate (the sole guard for the zero-cost path) and increments DebugPausedCounter afterwards.</summary>
    public static Task DispatchNodeAsync(
        IWorkflowDebugHook hook,
        string nodeId,
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        CancellationToken cancellationToken)
    {
        return hook.OnNodeAsync(nodeId, new Dictionary<string, object?>(), executedOutputs, cancellationToken);
    }
}
