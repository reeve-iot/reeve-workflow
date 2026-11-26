using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What a parked sleep reads back when it wakes. Public for a host that resolves suspension rows
/// itself: it writes <see cref="OutcomeJson"/> into the row, and the resumer reports the sleep from it.</summary>
public static class WorkflowDurableSleep
{
    /// <summary>The <c>_resumeReason</c> a woken sleep carries.</summary>
    public const string ResumeReason = WorkflowResumeDecisions.SleepReason;

    /// <summary>The outcome key the sleep's length travels under, because a resume is not handed the row's config.</summary>
    public const string DurationKey = "durationMs";

    private static readonly JsonSerializerOptions JsonOptions = WorkflowGraphHelpers.JsonOptions;

    internal static bool ParksIn(WorkflowMode mode) => mode is WorkflowMode.Single or WorkflowMode.Restart;

    internal static bool HoldsRunStateInProcess(int? executionTimeoutSeconds, int maxAttempts) =>
        executionTimeoutSeconds is > 0 || maxAttempts > 1;

    /// <summary>What a run's state row holds in <c>WaitForEventKey</c> while it is parked on this suspension row. A resume
    /// swaps the row only from this token, so a row that parked again since is not re-entered from an older park.</summary>
    public static string ParkToken(Guid suspensionId) => "reeveworkflow:parked:" + suspensionId.ToString("N");

    /// <summary>What the resume of this suspension row writes as it takes the run, until the walk's first block.</summary>
    public static string TakenToken(Guid suspensionId) => "reeveworkflow:resumed:" + suspensionId.ToString("N");

    internal static (string PayloadJson, string VariablesJson) RelinkStateRow(
        string statePayloadJson, Guid? stateExecutionId, string variablesJson, ILogger logger)
    {
        if (!stateExecutionId.HasValue)
            return (statePayloadJson, variablesJson);

        try
        {
            var payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(statePayloadJson, JsonOptions);
            if (payload == null)
                return (statePayloadJson, variablesJson);

            return (JsonSerializer.Serialize(payload.WithExecutionStateId(stateExecutionId.Value), JsonOptions),
                string.IsNullOrWhiteSpace(payload.VariablesJson) ? "{}" : payload.VariablesJson);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "failed to relink state-row id into suspension payload for execution {StateExecutionId}.", stateExecutionId.Value);
            return (statePayloadJson, variablesJson);
        }
    }

    internal sealed record PreParkRequest
    {
        public required IWorkflowSuspensionStore Suspensions { get; init; }
        public IWorkflowParkedSleepStore? ParkedSleeps { get; init; }
        public required IWorkflowRunStateStore StateStore { get; init; }
        public WorkflowRunHeartbeat? Heartbeat { get; init; }
        public required SuspendedWorkflowException Sleep { get; init; }
        public required Guid WorkflowId { get; init; }
        public required Guid ExecutionLogId { get; init; }
        public Guid? StateExecutionId { get; init; }
        public string? ReplicaId { get; init; }
        public required List<WorkflowExecutionStep> ExecutionSteps { get; init; }
        public TimeSpan? StateRowTimeout { get; init; }
    }

    internal sealed record PreParkResult(bool SleepHere, WorkflowSuspension? Persisted);

    internal static async Task<PreParkResult> PreParkAsync(PreParkRequest request, ILogger logger)
    {
        var sleepEx = request.Sleep;
        WorkflowSuspension? persisted;
        try
        {
            persisted = await request.Suspensions.SuspendAsync(new SuspendRequest
            {
                ExecutionLogId = request.ExecutionLogId,
                WorkflowId = request.WorkflowId,
                BlockId = sleepEx.BlockId,
                Kind = sleepEx.SuspendResult.Kind,
                ConfigJson = sleepEx.SuspendResult.ConfigJson,
                StatePayloadJson = RelinkStateRow(sleepEx.StatePayloadJson, request.StateExecutionId, "{}", logger).PayloadJson,
                SuspendedAtUtc = DateTimeOffset.UtcNow,
                TimeoutAtUtc = sleepEx.SuspendResult.TimeoutAtUtc
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SuspendPayloadTooLargeException)
        {
            return new PreParkResult(false, null);
        }
        catch (Exception persistEx)
        {
            await WithdrawFailedParkAsync(request.Suspensions, logger, persistEx, sleepEx, request.ExecutionLogId,
                request.ExecutionSteps).ConfigureAwait(false);
            return new PreParkResult(true, null);
        }

        if (request.StateExecutionId is not { } stateExecutionId)
            return new PreParkResult(false, persisted);

        if (await ParkStateRowAsync(request.StateStore, request.ParkedSleeps, logger, request.WorkflowId, stateExecutionId,
                sleepEx.BlockId, RelinkStateRow(sleepEx.StatePayloadJson, stateExecutionId, "{}", logger).VariablesJson,
                persisted.Id, request.ReplicaId, request.StateRowTimeout).ConfigureAwait(false) is { } parkEx)
        {
            await WithdrawFailedParkAsync(request.Suspensions, logger, parkEx, sleepEx, request.ExecutionLogId,
                request.ExecutionSteps).ConfigureAwait(false);
            return new PreParkResult(true, null);
        }

        if (request.Heartbeat is not null)
            await request.Heartbeat.StopAsync().ConfigureAwait(false);

        return new PreParkResult(false, persisted);
    }

    internal static readonly TimeSpan DefaultStateRowTimeout = TimeSpan.FromSeconds(30);

    private const int ParkAttempts = 3;

    internal static async Task<Exception?> ParkStateRowAsync(
        IWorkflowRunStateStore stateStore, IWorkflowParkedSleepStore? parkedSleeps, ILogger logger, Guid workflowId,
        Guid stateExecutionId, string blockId, string variablesJson, Guid suspensionId, string? replicaId,
        TimeSpan? stateRowTimeout = null)
    {
        var timeout = stateRowTimeout ?? DefaultStateRowTimeout;
        var park = new WorkflowRunStateTransition
        {
            ExecutionId = stateExecutionId,
            FromStatus = WorkflowRunStateStatus.Running,
            ToStatus = WorkflowRunStateStatus.Suspended,
            CurrentBlockPath = blockId,
            VariablesJson = variablesJson,
            WaitForEventKey = ParkToken(suspensionId),
            OwnedByReplicaId = replicaId,
            ExpectedOwnedByReplicaId = replicaId
        };

        Exception? failure = null;
        int? expectedVersion = null;
        for (var attempt = 0; attempt < ParkAttempts; attempt++)
        {
            try
            {
                if (await Bounded(timeout, token => stateStore.TransitionAsync(park with { ExpectedVersion = expectedVersion }, token))
                        .ConfigureAwait(false))
                    return null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to park run state row {StateExecutionId} Suspended.", stateExecutionId);
                failure = ex;
            }

            var (read, row) = await TryReadAsync(stateStore, stateExecutionId, timeout, logger).ConfigureAwait(false);
            if (!read)
                break;
            if (row is not { Status: WorkflowRunStateStatus.Running })
                return null;
            if (row.WaitForEventKey == TakenToken(suspensionId))
                return null;

            var ours = replicaId is null || row.OwnedByReplicaId is null || row.OwnedByReplicaId == replicaId;
            if (await StillParkedAsync(parkedSleeps, workflowId, suspensionId, timeout, logger).ConfigureAwait(false) is false)
                return null;
            if (!ours)
            {
                logger.LogWarning("Run state row {StateExecutionId} is owned by {Owner} while parking a sleep; this replica stops.",
                    stateExecutionId, row.OwnedByReplicaId);
                return new WorkflowRunOwnershipLostException(stateExecutionId, blockId);
            }

            expectedVersion = row.Version == 0 ? null : row.Version;
            failure ??= new TimeoutException($"parking run state row {stateExecutionId} did not land");
        }

        return failure;
    }

    private static async Task<bool?> StillParkedAsync(
        IWorkflowParkedSleepStore? parkedSleeps, Guid workflowId, Guid suspensionId, TimeSpan timeout, ILogger logger)
    {
        if (parkedSleeps is null)
            return null;
        try
        {
            var parked = await Bounded(timeout, token => parkedSleeps.ListParkedSleepsAsync(workflowId, token)).ConfigureAwait(false);
            return parked.Any(sleep => sleep.SuspensionId == suspensionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the parked sleeps of workflow {WorkflowId} while parking one.", workflowId);
            return null;
        }
    }

    private static async Task<(bool Read, WorkflowRunStateRow? Row)> TryReadAsync(
        IWorkflowRunStateStore stateStore, Guid stateExecutionId, TimeSpan timeout, ILogger logger)
    {
        try
        {
            return (true, await Bounded(timeout, token => stateStore.GetAsync(stateExecutionId, token)).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read run state row {StateExecutionId} while parking a sleep.", stateExecutionId);
            return (false, null);
        }
    }

    private static async Task<T> Bounded<T>(TimeSpan timeout, Func<CancellationToken, Task<T>> call)
    {
        using var deadline = new CancellationTokenSource(timeout);
        return await call(deadline.Token).ConfigureAwait(false);
    }

    internal static async Task WithdrawFailedParkAsync(
        IWorkflowSuspensionStore suspensions, ILogger logger, Exception persistEx,
        SuspendedWorkflowException sleep, Guid executionId, List<WorkflowExecutionStep> executionSteps)
    {
        var blockId = sleep.BlockId;
        logger.LogWarning(persistEx,
            "execution {ExecutionId} could not park its sleep at block {BlockId}; it sleeps in this process instead.",
            executionId, blockId);

        try
        {
            await suspensions.CancelByExecutionAsync(executionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cancelEx)
        {
            logger.LogWarning(cancelEx,
                "failed to withdraw the sleep row execution {ExecutionId} may have written; it is abandoned once the run ends.",
                executionId);
        }

        if (sleep.RecordedStep is { } parkStep)
            executionSteps.Remove(parkStep);
    }

    internal static Guid? StateExecutionIdOf(string statePayloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(statePayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(WorkflowSuspensionStatePayload.ExecutionStateId), StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetGuid(out var id))
                    return id;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The outcome to resolve a due <see cref="WorkflowSuspensionKind.Sleep"/> row with, as
    /// <see cref="WorkflowSuspensionStatus.Resumed"/>. A config that cannot be read reports the length it was parked for.</summary>
    public static string OutcomeJson(string configJson, DateTimeOffset suspendedAtUtc, DateTimeOffset timeoutAtUtc)
    {
        var durationMs = ReadDurationMs(configJson)
            ?? (long)Math.Max(0, (timeoutAtUtc - suspendedAtUtc).TotalMilliseconds);

        return JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [WorkflowResumeDecisions.OutcomeKey] = WorkflowWaitOutcomes.Satisfied,
            [WorkflowResumeDecisions.ResumeReasonKey] = ResumeReason,
            [DurationKey] = durationMs
        }, JsonOptions);
    }

    /// <summary>The outputs the woken sleep node reports: the same keys and value types the block reports when it
    /// sleeps in this process. Other traces of the park differ; docs/operations/parking.md lists them.</summary>
    /// <exception cref="JsonException">The outcome does not parse or is not an object, or its duration is not an Int: the resumer abandons the run
    /// with <c>resume_value_invalid</c>.</exception>
    public static IReadOnlyDictionary<string, object?> ResumedOutputs(string outcomeJson)
    {
        var durationMs = 0L;
        using (var outcome = JsonDocument.Parse(outcomeJson))
        {
            if (outcome.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException($"The sleep's outcome is {outcome.RootElement.ValueKind}, not an object.");
            if (outcome.RootElement.TryGetProperty(DurationKey, out var value))
            {
                durationMs = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)
                    ? parsed
                    : throw new JsonException($"The sleep's '{DurationKey}' is {value.ValueKind}, not an Int.");
            }
        }

        return new Dictionary<string, object?>
        {
            ["Completed"] = true,
            ["DurationMs"] = Math.Max(0L, durationMs)
        };
    }

    private static long? ReadDurationMs(string configJson)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowSleepConfig>(configJson, JsonOptions)?.DurationMs;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
