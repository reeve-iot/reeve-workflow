using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>A wait long enough to outlive a poll. It parks the row Sleeping so a peer can take the run over,
/// wakes often enough to notice a cancellation, and renews the row so no sweep calls a live sleeper dead.</summary>
public sealed class WorkflowSleepRunner(
    IWorkflowRunStateStore stateStore, ILogger logger, WorkflowSleepOptions options)
{
    private readonly TimeProvider clock = options.TimeProvider ?? TimeProvider.System;

    /// <summary>Sleeps for the duration the block's inputs ask for, and returns what the block reports.</summary>
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        string nodeId,
        Guid? stateExecutionId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(inputs, nodeId, stateExecutionId, wakeUpAt: null, cancellationToken);

    /// <summary>Sleeps until <paramref name="wakeUpAt"/> when it is given, the time a sleep a recovery re-entered was
    /// to end, and otherwise for the duration the block's inputs ask for. Returns what the block reports.</summary>
    /// <remarks>A wake-up time already past ends the sleep at once.</remarks>
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        string nodeId,
        Guid? stateExecutionId,
        DateTimeOffset? wakeUpAt,
        CancellationToken cancellationToken)
    {
        var durationMs = WorkflowExecutionHelpers.ResolveSleepDurationMs(inputs);
        if (durationMs < 0)
            throw new ArgumentException("DurationMs must be greater than or equal to 0.");

        var wakesAt = wakeUpAt ?? WorkflowExecutionHelpers.SleepEnd(DateTimeOffset.UtcNow, durationMs);

        var row = stateExecutionId.HasValue
            ? await TryGetRowAsync(stateExecutionId.Value, cancellationToken).ConfigureAwait(false)
            : null;

        var sleeping = row != null && await TryEnterSleepingAsync(row, nodeId, wakesAt, cancellationToken)
            .ConfigureAwait(false);

        var stoppedByHost = false;
        try
        {
            await PollUntilAwakeAsync(sleeping ? row : null, nodeId, wakesAt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
                                                  && options.HostStopping.IsCancellationRequested
                                                  && CallerStopped(stateExecutionId, cancellationToken))
        {
            stoppedByHost = true;
            throw;
        }
        finally
        {
            if (sleeping && !stoppedByHost)
                await LeaveSleepingAsync(row!, nodeId).ConfigureAwait(false);
        }

        return new Dictionary<string, object?>
        {
            ["Completed"] = true,
            ["DurationMs"] = durationMs
        };
    }

    private static bool CallerStopped(Guid? stateExecutionId, CancellationToken sleep) =>
        (stateExecutionId is { } id && WorkflowRunWrites.Find(id) is { } writes ? writes.Caller : sleep)
        .IsCancellationRequested;

    private async Task<WorkflowRunStateRow?> TryGetRowAsync(Guid executionId, CancellationToken cancellationToken)
    {
        try
        {
            return await stateStore.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to look up the run state row before sleeping execution {ExecutionId}.", executionId);
            return null;
        }
    }

    private async Task<bool> TryEnterSleepingAsync(
        WorkflowRunStateRow row, string nodeId, DateTimeOffset wakeUpAt, CancellationToken cancellationToken)
    {
        try
        {
            return await stateStore.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = row.ExecutionId,
                FromStatus = WorkflowRunStateStatus.Running,
                ToStatus = WorkflowRunStateStatus.Sleeping,
                CurrentBlockPath = nodeId,
                VariablesJson = row.VariablesJson,
                WaitUntilUtc = wakeUpAt,
                OwnedByReplicaId = options.ReplicaId
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to transition execution {ExecutionId} to Sleeping.", row.ExecutionId);
            return false;
        }
    }

    private async Task PollUntilAwakeAsync(
        WorkflowRunStateRow? row, string nodeId, DateTimeOffset wakeUpAt, CancellationToken cancellationToken)
    {
        var pollInterval = options.CancellationPollInterval;
        var startedAtUtc = DateTimeOffset.UtcNow;
        var started = clock.GetTimestamp();
        DateTimeOffset Now() => startedAtUtc + clock.GetElapsedTime(started);
        var lastRenewUtc = Now();

        while (Now() < wakeUpAt)
        {
            var thisWait = WorkflowSleepSchedule.NextWait(Now(), wakeUpAt, pollInterval);
            if (thisWait <= TimeSpan.Zero)
                break;

            await Task.Delay(thisWait, clock, cancellationToken).ConfigureAwait(false);

            if (row == null)
                continue;

            try
            {
                var current = await stateStore.GetAsync(row.ExecutionId, cancellationToken).ConfigureAwait(false);
                if (current?.Status == WorkflowRunStateStatus.Cancelled)
                {
                    logger.LogInformation(
                        "Execution {ExecutionId} was cancelled in the store while sleeping; abandoning the wait.",
                        row.ExecutionId);
                    throw new OperationCanceledException("Workflow cancelled via the run state store during sleep.");
                }

                if (current is null)
                {
                    throw new WorkflowRunOwnershipLostException(
                        row.ExecutionId, nodeId, WorkflowRunOwnershipLoss.RowMissing);
                }

                if (current is { Status: WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed })
                {
                    throw new WorkflowRunOwnershipLostException(
                        row.ExecutionId, nodeId, WorkflowRunOwnershipLoss.StatusMoved);
                }

                if (current.OwnedByReplicaId is { } holder && !string.IsNullOrEmpty(options.ReplicaId)
                    && holder != options.ReplicaId)
                {
                    throw new WorkflowRunOwnershipLostException(
                        row.ExecutionId, nodeId, WorkflowRunOwnershipLoss.ReclaimedByPeer);
                }

                if (current is { Status: WorkflowRunStateStatus.Sleeping }
                    && WorkflowSleepSchedule.ShouldRenew(Now(), lastRenewUtc, pollInterval))
                {
                    if (!await RenewAsync(row, current, nodeId, wakeUpAt, cancellationToken).ConfigureAwait(false))
                    {
                        throw new WorkflowRunOwnershipLostException(
                            row.ExecutionId, nodeId,
                            await WorkflowRunOwnership.ExplainAsync(
                                stateStore, row.ExecutionId, WorkflowRunStateStatus.Sleeping, options.ReplicaId, logger,
                                cancellationToken).ConfigureAwait(false));
                    }

                    lastRenewUtc = Now();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WorkflowRunOwnershipLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read the run state row while sleeping execution {ExecutionId}.", row.ExecutionId);
            }
        }
    }

    private async Task<bool> RenewAsync(
        WorkflowRunStateRow row, WorkflowRunStateRow current, string nodeId,
        DateTimeOffset wakeUpAt, CancellationToken cancellationToken)
    {
        var renewed = await stateStore.TransitionAsync(new WorkflowRunStateTransition
        {
            ExecutionId = row.ExecutionId,
            FromStatus = WorkflowRunStateStatus.Sleeping,
            ToStatus = WorkflowRunStateStatus.Sleeping,
            CurrentBlockPath = nodeId,
            VariablesJson = current.VariablesJson,
            WaitUntilUtc = wakeUpAt,
            OwnedByReplicaId = options.ReplicaId,
            ExpectedOwnedByReplicaId = options.ReplicaId
        }, cancellationToken).ConfigureAwait(false);

        if (!renewed)
        {
            logger.LogInformation(
                "The sleep heartbeat for execution {ExecutionId} lost its swap — the row is no longer Sleeping and ours; the sleep stops here.",
                row.ExecutionId);
        }

        return renewed;
    }

    private async Task LeaveSleepingAsync(WorkflowRunStateRow row, string nodeId)
    {
        try
        {
            await stateStore.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = row.ExecutionId,
                FromStatus = WorkflowRunStateStatus.Sleeping,
                ToStatus = WorkflowRunStateStatus.Running,
                CurrentBlockPath = nodeId,
                VariablesJson = row.VariablesJson,
                OwnedByReplicaId = options.ReplicaId,
                ExpectedOwnedByReplicaId = options.ReplicaId
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to transition execution {ExecutionId} from Sleeping back to Running.", row.ExecutionId);
        }
    }
}

/// <summary>What a sleeping run needs to know about the host it is sleeping on.</summary>
public sealed record WorkflowSleepOptions
{
    /// <summary>How often to wake and check. It is also the renewal interval: the row must not look stale
    /// to a reclaim sweep, and the cheapest moment to say so is one we are already awake for.</summary>
    public required TimeSpan CancellationPollInterval { get; init; }

    /// <summary>Which replica this is, so the row records an owner and the renewal can require it.</summary>
    public string? ReplicaId { get; init; }

    /// <summary>Fires when the host begins to stop. A sleep whose run's caller cancelled it after it has fired leaves
    /// its row <c>Sleeping</c> with its wake-up time, as a crash would, so the run handed over to a peer sleeps only
    /// what was left of the wait. Any other cancellation, a Restart or a deadline after the stop began among them,
    /// takes the row out of <c>Sleeping</c>, because the walk that goes on writes the row as <c>Running</c>.</summary>
    public CancellationToken HostStopping { get; init; }

    /// <summary>The clock an in-process sleep waits on: its timers and timestamp, not its wall-clock reading, because the
    /// row's wake-up time is compared with the store's clock. Null is the system clock.</summary>
    public TimeProvider? TimeProvider { get; init; }
}
