using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

internal sealed class WritesFailBefore(IWorkflowRunStateStore inner, string block) : IWorkflowRunStateStore
{
    public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
        inner.BeginAsync(request, cancellationToken);

    public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
        inner.GetAsync(executionId, cancellationToken);

    public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken) =>
        request is { FromStatus: WorkflowRunStateStatus.Running, ToStatus: WorkflowRunStateStatus.Running }
            && request.CurrentBlockPath == block
            ? throw new TimeoutException("The write before the block timed out.")
            : inner.TransitionAsync(request, cancellationToken);

    public Task<bool> CompleteAsync(
        Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason, CancellationToken cancellationToken) =>
        inner.CompleteAsync(executionId, terminalStatus, failureReason, cancellationToken);

    public Task<bool> CompleteIfOwnedAsync(
        Guid executionId, string? ownedByReplicaId, WorkflowRunStateStatus terminalStatus, string? failureReason,
        CancellationToken cancellationToken) =>
        inner.CompleteIfOwnedAsync(executionId, ownedByReplicaId, terminalStatus, failureReason, cancellationToken);

    public Task<WorkflowRunStatePage> ListByWorkflowAsync(
        Guid workflowId, WorkflowRunStatePageRequest request, CancellationToken cancellationToken) =>
        inner.ListByWorkflowAsync(workflowId, request, cancellationToken);
}
