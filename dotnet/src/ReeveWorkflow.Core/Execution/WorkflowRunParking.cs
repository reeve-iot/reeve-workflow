using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal static class WorkflowRunParking
{
    public sealed record Request
    {
        public IWorkflowRunStateStore? StateStore { get; init; }
        public IWorkflowSuspensionStore? Suspensions { get; init; }
        public WorkflowRunHeartbeat? Heartbeat { get; init; }
        public string? ReplicaId { get; init; }
        public Guid? StateExecutionId { get; init; }

        public Guid? ParentExecutionId { get; init; }
        public required Guid ExecutionLogId { get; init; }
        public required Guid WorkflowId { get; init; }
        public required SuspendedWorkflowException Suspended { get; init; }

        public required string VariablesJson { get; init; }

        public WorkflowSuspension? Persisted { get; init; }
        public WorkflowSuspensionParkSignal? ParkSignal { get; init; }

        public bool RowAlreadyParked { get; init; }
    }

    public sealed record Result(bool Parked, Exception? Refusal);

    private static readonly Result ParkedResult = new(true, null);

    public static async Task<Result> ParkAsync(Request request, ILogger logger)
    {
        var suspEx = request.Suspended;
        var (statePayloadJson, variablesJson, nested) = Relink(request, logger);

        var persisted = request.Persisted;
        if (persisted is null && request.Suspensions is { } suspensions)
        {
            try
            {
                persisted = await suspensions.SuspendAsync(new SuspendRequest
                {
                    ExecutionLogId = request.ExecutionLogId,
                    WorkflowId = request.WorkflowId,
                    BlockId = suspEx.BlockId,
                    Kind = suspEx.SuspendResult.Kind,
                    ConfigJson = suspEx.SuspendResult.ConfigJson,
                    StatePayloadJson = statePayloadJson,
                    SuspendedAtUtc = DateTimeOffset.UtcNow,
                    TimeoutAtUtc = suspEx.SuspendResult.TimeoutAtUtc
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SuspendPayloadTooLargeException tooLarge)
            {
                logger.LogError(tooLarge,
                    "suspend payload too large for workflow {WorkflowId} execution {ExecutionId}. Failing run.",
                    request.WorkflowId, request.ExecutionLogId);
                return new Result(false, tooLarge);
            }
        }

        if (request.Heartbeat is not null)
            await request.Heartbeat.StopAsync().ConfigureAwait(false);

        var parkToken = nested && persisted is not null ? WorkflowDurableSleep.ParkToken(persisted.Id) : null;
        if (!request.RowAlreadyParked
            && !await ParkRowAsync(request, variablesJson, parkToken, logger).ConfigureAwait(false))
        {
            var loss = await WorkflowRunOwnership.ExplainAsync(request.StateStore!, request.StateExecutionId!.Value,
                WorkflowRunStateStatus.Running, request.ReplicaId, logger, CancellationToken.None).ConfigureAwait(false);
            await WorkflowRunEndings.CloseWaitsAsync(request.Suspensions, request.ExecutionLogId, logger).ConfigureAwait(false);
            return new Result(false,
                new WorkflowRunOwnershipLostException(request.StateExecutionId.Value, suspEx.BlockId, loss));
        }

        if (persisted is not null)
        {
            try
            {
                request.Suspensions!.RegisterForResumption(persisted);
                logger.LogInformation(
                    "workflow {WorkflowId} execution {ExecutionId} suspended at block {BlockId} kind={Kind} timeout={TimeoutAtUtc}.",
                    request.WorkflowId, request.ExecutionLogId, suspEx.BlockId, suspEx.SuspendResult.Kind,
                    suspEx.SuspendResult.TimeoutAtUtc);
            }
            catch (Exception registerEx)
            {
                logger.LogWarning(registerEx,
                    "failed to wire in-process resumption for workflow {WorkflowId} execution {ExecutionId}; the durable suspension row will still be resumed by the timeout sweep / restart rehydration.",
                    request.WorkflowId, request.ExecutionLogId);
            }
        }

        if (persisted is not null)
            request.ParkSignal?.Publish();
        return ParkedResult;
    }

    private static (string StatePayloadJson, string VariablesJson, bool Nested) Relink(Request request, ILogger logger)
    {
        var statePayloadJson = request.Suspended.StatePayloadJson;
        var variablesJson = request.VariablesJson;
        var nested = false;
        if (request.StateExecutionId is null && request.ParentExecutionId is null)
            return (statePayloadJson, variablesJson, nested);

        try
        {
            var payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(statePayloadJson, WorkflowGraphHelpers.JsonOptions);
            if (payload is null)
                return (statePayloadJson, variablesJson, nested);

            if (request.StateExecutionId is not null)
            {
                nested = WorkflowRunCheckpoint.Continuation(payload).Count > 0;
                variablesJson = string.IsNullOrWhiteSpace(payload.VariablesJson) ? "{}" : payload.VariablesJson;
            }

            statePayloadJson = JsonSerializer.Serialize(
                payload.Relinked(request.StateExecutionId ?? payload.ExecutionStateId, request.ParentExecutionId ?? payload.ParentExecutionId),
                WorkflowGraphHelpers.JsonOptions);
        }
        catch (Exception relinkEx)
        {
            logger.LogWarning(relinkEx,
                "failed to relink state-row id into suspension payload for execution {StateExecutionId}.", request.StateExecutionId);
        }

        return (statePayloadJson, variablesJson, nested);
    }

    private static async Task<bool> ParkRowAsync(Request request, string variablesJson, string? parkToken, ILogger logger)
    {
        if (request.StateStore is not { } store || request.StateExecutionId is not { } id)
            return true;

        try
        {
            var parked = await store.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = id,
                FromStatus = WorkflowRunStateStatus.Running,
                ToStatus = WorkflowRunStateStatus.Suspended,
                CurrentBlockPath = request.Suspended.BlockId,
                VariablesJson = variablesJson,
                WaitForEventKey = parkToken,
                OwnedByReplicaId = request.ReplicaId,
                ExpectedOwnedByReplicaId = request.ReplicaId
            }, CancellationToken.None).ConfigureAwait(false);
            if (!parked)
                logger.LogWarning(
                    "Run state row {StateExecutionId} was not parked Suspended: it is no longer running in this replica's name.", id);
            return parked;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to park run state row {StateExecutionId} Suspended.", id);
            return true;
        }
    }
}
