using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>The breadcrumb a run leaves before each block: the row a crash is recovered from, and the audit
/// event a host that keeps one wants beside it.</summary>
/// <remarks>Every failure here is swallowed. The row is substrate — losing it costs a recovery, not a run —
/// and a node must not fail because the breadcrumb before it could not be written. A row lost to a peer is not a
/// failure but is said out loud, and takes the audit event with it: neither describes a run this replica owns.</remarks>
public sealed class WorkflowTransitionRecorder(
    IWorkflowRunStateStore? stateStore,
    ILogger logger,
    WorkflowTransitionOptions? options = null,
    IWorkflowAuditSink? auditSink = null)
{
    private readonly WorkflowTransitionOptions options = options ?? new WorkflowTransitionOptions();

    /// <summary>Whether anything is written at all. False skips the variables snapshot the write would need,
    /// which is a dictionary copy per node — worth asking before building rather than after.</summary>
    public bool TracksRunState => stateStore is not null;

    /// <summary>Records that the run is about to enter a node, and answers whether the row is still this
    /// replica's. Only called when <see cref="TracksRunState"/> agreed, and never allowed to throw. Answers
    /// false only when the store said the row is a peer's now; a store that could not be asked answers true,
    /// because a lost write is not a lost run.</summary>
    public async Task<bool> TryWritePreBlockStateAsync(WorkflowPreBlockState state, CancellationToken cancellationToken) =>
        await WritePreBlockStateAsync(state, cancellationToken).ConfigureAwait(false) != PreBlockWrite.Lost;

    internal async Task<PreBlockWrite> WritePreBlockStateAsync(WorkflowPreBlockState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (stateStore is null) return PreBlockWrite.Written;

        bool written;
        try
        {
            written = await stateStore.TransitionAsync(
                new WorkflowRunStateTransition
                {
                    ExecutionId = state.StateExecutionId,
                    FromStatus = WorkflowRunStateStatus.Running,
                    ToStatus = WorkflowRunStateStatus.Running,
                    CurrentBlockPath = state.NodeId,
                    VariablesJson = state.CheckpointJson ?? WorkflowVariablesEncoder.Encode(state.Variables),
                    OwnedByReplicaId = options.ReplicaId,
                    ExpectedOwnedByReplicaId = options.ReplicaId
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Failed to write pre-block run state for node {NodeId}.", state.NodeId);
            return PreBlockWrite.NotWritten;
        }

        if (!written)
        {
            logger.LogWarning(
                "Pre-block run state for node {NodeId} was not written — execution {ExecutionId} is no longer this replica's.",
                state.NodeId, state.StateExecutionId);
            return PreBlockWrite.Lost;
        }

        if (options.AuditTransitions && auditSink is not null)
            await AuditAsync(state, cancellationToken).ConfigureAwait(false);

        return PreBlockWrite.Written;
    }

    /// <summary>Why a fenced write to the row was refused, read off the row as it is now. Called after a refusal
    /// and never allowed to throw: a row that cannot be read answers <see cref="WorkflowRunOwnershipLoss.Unknown"/>.</summary>
    /// <param name="stateExecutionId">The row.</param>
    /// <param name="expectedStatus">The status the refused write required the row to be in.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    public Task<WorkflowRunOwnershipLoss> ExplainLossAsync(
        Guid stateExecutionId, WorkflowRunStateStatus expectedStatus, CancellationToken cancellationToken) =>
        stateStore is null
            ? Task.FromResult(WorkflowRunOwnershipLoss.Unknown)
            : WorkflowRunOwnership.ExplainAsync(
                stateStore, stateExecutionId, expectedStatus, options.ReplicaId, logger, cancellationToken);

    private async Task AuditAsync(WorkflowPreBlockState state, CancellationToken cancellationToken)
    {
        try
        {
            await auditSink!.WriteAsync(
                new WorkflowAuditEvent(
                    Kind: WorkflowAuditEventKinds.ExecutionTransitioned,
                    ActorId: null,
                    SubjectId: state.StateExecutionId,
                    Data: new
                    {
                        ExecutionId = state.StateExecutionId,
                        WorkflowId = state.WorkflowId,
                        BlockPath = state.NodeId,
                        BlockTypeId = state.BlockTypeId
                    }),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Failed to emit the transition audit event for node {NodeId}; the run continues.", state.NodeId);
        }
    }
}

/// <summary>What the breadcrumb records, and whether an audit event goes with it.</summary>
public sealed record WorkflowTransitionOptions
{
    /// <summary>Which process owns a row it writes, so a peer can tell a live run from an abandoned one.</summary>
    public string? ReplicaId { get; init; }

    /// <summary>Whether every entered node is also an audit event. Off by default: on a graph of any size it
    /// is one audit write per node per run, which a host wants only if it is required to keep the trail.</summary>
    public bool AuditTransitions { get; init; }
}

/// <summary>The run's position, as a state row records it: which run, which node, and the variables in scope.
/// Never carries the block's inputs or outputs — those may hold plugin-supplied secrets.</summary>
public sealed record WorkflowPreBlockState(
    Guid StateExecutionId,
    Guid WorkflowId,
    string NodeId,
    string BlockTypeId,
    IReadOnlyDictionary<string, object?> Variables)
{
    /// <summary>The checkpoint this write keeps in the row's variables, in place of <see cref="Variables"/>. See
    /// <see cref="WorkflowRunCheckpoint"/>. Null writes the flattened scope.</summary>
    public string? CheckpointJson { get; init; }
}

internal enum PreBlockWrite
{
    Written,

    NotWritten,

    Lost
}
