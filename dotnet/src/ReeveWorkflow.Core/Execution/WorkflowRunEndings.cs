using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Execution;

internal static class WorkflowRunEndings
{
    public static async Task<WorkflowRunEnding> EndRowAsync(
        WorkflowRunEnding ending, IWorkflowRunStateStore? store, Guid? stateExecutionId, string? replicaId, ILogger logger)
    {
        if (store is null || stateExecutionId is not { } id || ending.Row == WorkflowRunRowEnding.Leave)
            return ending;

        bool written;
        WorkflowRunStateRow? released = null;
        if (ending.Row == WorkflowRunRowEnding.Release)
        {
            released = await WorkflowRunHandover.ReleasedAsync(store, id, replicaId, logger).ConfigureAwait(false);
            written = released is not null;
        }
        else
        {
            var threw = false;
            for (var write = 1; ; write++)
            {
                try
                {
                    written = await store.CompleteIfOwnedAsync(
                        id, replicaId, ending.RowStatus, ending.RowReason, CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (write < EndingWrites)
                {
                    threw = true;
                    logger.LogDebug(ex, "Ending run state row {StateExecutionId} as {Status} failed; writing it again.",
                        id, ending.RowStatus);
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * write)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to end run state row {StateExecutionId} as {Status}.", id, ending.RowStatus);
                    switch (await ReadBackAsync(store, id, ending, replicaId, logger).ConfigureAwait(false))
                    {
                        case ReadBack.Landed:
                            return ending;
                        case ReadBack.NotEndedHere:
                        case ReadBack.Unreadable:
                            return ending with { TryAgain = false, RowWriteFailed = true };
                    }

                    written = false;
                    threw = false;
                    break;
                }
            }

            if (!written && threw && await ReadBackAsync(store, id, ending, replicaId, logger).ConfigureAwait(false) == ReadBack.Landed)
                return ending;
        }

        if (written)
            return released is not null ? ending with { ReleasedRow = released } : ending;

        var loss = await WorkflowRunOwnership.ExplainAsync(store, id, null, replicaId, logger, CancellationToken.None)
            .ConfigureAwait(false);
        if (loss is not (WorkflowRunOwnershipLoss.ReclaimedByPeer or WorkflowRunOwnershipLoss.Released))
            return ending.Row == WorkflowRunRowEnding.Release && ending.RowReason is { } reason
                ? ending with
                {
                    TryAgain = false,
                    JournalMessage = WorkflowRunHandover.JournalMessageNotReleased(
                        reason, rowEnded: loss is WorkflowRunOwnershipLoss.StatusMoved or WorkflowRunOwnershipLoss.RowMissing)
                }
                : ending with { TryAgain = false };

        logger.LogInformation(
            "Run state row {StateExecutionId} was not ended {Phase}: it is no longer this replica's, so the run is the peer's.",
            id, ending.Phase);
        return ending.RowRefused();
    }

    public static async Task<bool> EntryGoesOnAsync(
        WorkflowRunEnding ending, IWorkflowRunStateStore? store, Guid? stateExecutionId, Guid executionLogId, ILogger logger)
    {
        if (ending.Phase is not (WorkflowRunPhase.HandedOver or WorkflowRunPhase.LostToPeer)
            || store is null || stateExecutionId is not { } id)
            return false;

        try
        {
            if (ending.Phase == WorkflowRunPhase.LostToPeer)
                return WorkflowRunWrites.Find(id)?.RowNamesEntry == true;

            if (ending.ReleasedRow is { } releasedRow)
                return WorkflowRunCheckpoint.Of(releasedRow)?.JournalExecutionId == executionLogId;

            var row = await store.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
            return WorkflowRunCheckpoint.Of(row)?.JournalExecutionId == executionLogId
                && row!.Status is WorkflowRunStateStatus.Running or WorkflowRunStateStatus.Sleeping;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read run state row {StateExecutionId} to learn whether a peer carries on journal entry "
                + "{ExecutionLogId}; closing the entry here.", id, executionLogId);
            return false;
        }
    }

    private const int EndingWrites = 3;

    private enum ReadBack
    {
        Landed,

        NotEndedHere,

        Other,

        Unreadable
    }

    private static async Task<ReadBack> ReadBackAsync(
        IWorkflowRunStateStore store, Guid id, WorkflowRunEnding ending, string? replicaId, ILogger logger)
    {
        WorkflowRunStateRow? row;
        try
        {
            row = await store.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Run state row {StateExecutionId} could not be read back after its ending.", id);
            return ReadBack.Unreadable;
        }

        if (row is { } ended && ended.Status == ending.RowStatus && ended.FailureReason == ending.RowReason)
            return ReadBack.Landed;
        if (row is { } unended && unended.OwnedByReplicaId == replicaId
            && unended.Status is not (WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed or WorkflowRunStateStatus.Cancelled))
            return ReadBack.NotEndedHere;
        return ReadBack.Other;
    }

    public static async Task CloseWaitsAsync(IWorkflowSuspensionStore? suspensions, Guid executionLogId, ILogger logger)
    {
        if (suspensions is null)
            return;

        await QuietlyAsync(() => suspensions.CancelByExecutionAsync(executionLogId, CancellationToken.None),
            logger, "close the waits of", executionLogId).ConfigureAwait(false);
        try
        {
            suspensions.TearDownByExecution(executionLogId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to tear down the waits of execution {ExecutionId} in this process.", executionLogId);
        }
    }

    public static async Task QuietlyAsync(Func<Task> write, ILogger logger, string what, Guid executionId)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to {What} execution {ExecutionId}.", what, executionId);
        }
    }
}
