namespace ReeveWorkflow.Debug;

/// <summary>How a paused run reaches whoever is watching it. The library owns the machinery of stepping; what
/// carries the events to a client — SignalR, SSE, long-poll, nothing at all — is the host's choice.</summary>
public interface IWorkflowDebugNotifier
{
    /// <summary>A run stopped at a node. <paramref name="snapshotJson"/> is the captured state, already
    /// serialised, because the host transports it rather than reading it.</summary>
    Task NotifyPausedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default);

    /// <summary>A paused run was let go.</summary>
    Task NotifyResumedAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>A node finished and its state was recorded, without pausing. Verbose mode only.</summary>
    Task NotifySnapshotCapturedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default);

    /// <summary>The session reached a status it does not leave: the run finished, failed, or was cancelled.
    /// <paramref name="status"/> is the terminal status by name (<c>Completed</c>, <c>Failed</c> or <c>Cancelled</c>),
    /// so a reader can tell them apart.</summary>
    /// <remarks>A watcher waiting on this and getting nothing is a debugger panel that never closes.</remarks>
    Task NotifyCompletedAsync(Guid sessionId, string status, CancellationToken cancellationToken = default);
}

/// <summary>The notifier a host installs nothing for. Debugging still works through it — stepping is driven by
/// calls into the service, not by these events — so an adopter with no push transport is not locked out.</summary>
public sealed class NullWorkflowDebugNotifier : IWorkflowDebugNotifier
{
    public Task NotifyPausedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task NotifyResumedAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task NotifySnapshotCapturedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task NotifyCompletedAsync(Guid sessionId, string status, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
