using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Debug;

/// <summary>The four things a running workflow asks of a debug session, out of the twenty the session service
/// offers — the rest belong to whoever is driving the session, not to the run being watched.</summary>
/// <remarks>Both halves of this ship in this package, so the narrowing is the library's own and not something
/// each host writes again. A host wires it only by installing the package.</remarks>
public sealed class WorkflowDebugSessions(IWorkflowDebugSessionService inner) : IWorkflowDebugSessions
{
    private readonly IWorkflowDebugSessionService inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public Task BindToRunAsync(Guid debugSessionId, Guid executionId, CancellationToken cancellationToken) =>
        inner.BindToExecutionAsync(debugSessionId, executionId, cancellationToken);

    /// <inheritdoc />
    public Task MarkRunningAsync(Guid debugSessionId, CancellationToken cancellationToken) =>
        inner.MarkRunningAsync(debugSessionId, cancellationToken);

    /// <inheritdoc />
    public IWorkflowDebugHook? HookFor(Guid executionId) => inner.GetHookForExecution(executionId);

    /// <inheritdoc />
    public Task ReleaseHookAsync(
        Guid executionId, string runStatus, CancellationToken cancellationToken = default) =>
        inner.EndSessionForExecutionAsync(executionId, runStatus, cancellationToken);
}
