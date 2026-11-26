using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Execution;

/// <summary>One run, start to finish: admission, the record it opens, the retry loop around the graph walk,
/// and every way it can end. What the host still owns is where those rows go, not when they are written.</summary>
/// <remarks>It takes the <see cref="WorkflowCompilationService"/> admission compiles a start through.</remarks>
public sealed class WorkflowRunner(
    IWorkflowRunScopeFactory scopeFactory,
    WorkflowGraphExecutor graphExecutor,
    IWorkflowBlocks blocks,
    WorkflowCompilationService compiler,
    IWorkflowExecutionGate executionGate,
    IWorkflowMetrics metrics,
    ILogger logger,
    WorkflowRunnerOptions options,
    IWorkflowRunStateStore? stateStore = null,
    IWorkflowSuspensionStore? suspensions = null,
    IWorkflowDebugSessions? debugSessions = null,
    IWorkflowFireDedup? fireDedup = null,
    IWorkflowRunCoordinatorPool? coordinators = null,
    WorkflowActivitySource? activitySource = null,
    IWorkflowStartAdmission? startAdmission = null,
    IWorkflowNodeValidator? nodeValidator = null,
    IWorkflowStartArgumentValidator? argumentValidator = null)
{
    private readonly WorkflowActivitySource activitySource = activitySource ?? WorkflowActivitySource.Default;

    private static readonly JsonSerializerOptions JsonOptions = WorkflowGraphHelpers.JsonOptions;

    /// <summary>Runs a workflow and answers what it produced. A run that was never admitted answers with
    /// nothing, which reads the same as a run that produced nothing — see <see cref="ExecuteWithOutcomeAsync"/>.</summary>
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(Guid workflowId,
        WorkflowFire? fire,
        IReadOnlyDictionary<string, object?>? inputParameters,
        Guid? parentExecutionId = null,
        int? iterationIndex = null,
        string? callStackJson = null,
        CancellationToken cancellationToken = default) =>
        (await ExecuteWithOutcomeAsync(
            workflowId, fire, inputParameters, parentExecutionId, iterationIndex, callStackJson, cancellationToken)
            .ConfigureAwait(false)).Outputs;

    /// <summary>The same run, with whether it started at all. Admission refuses for reasons that are not
    /// failures — safe mode, a peer holding the one permit, a fire already claimed — and a caller that must
    /// know the difference (recovery, which would otherwise record a refusal as a finished run) asks here.</summary>
    public async Task<WorkflowRunOutcome> ExecuteWithOutcomeAsync(Guid workflowId,
        WorkflowFire? fire,
        IReadOnlyDictionary<string, object?>? inputParameters,
        Guid? parentExecutionId = null,
        int? iterationIndex = null,
        string? callStackJson = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            workflowId, fire, inputParameters, parentExecutionId, iterationIndex, callStackJson, cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is { } error)
            ExceptionDispatchInfo.Capture(error).Throw();
        return result.Outcome;
    }

    internal async Task<WorkflowRunResult> RunAsync(Guid workflowId,
        WorkflowFire? fire,
        IReadOnlyDictionary<string, object?>? inputParameters,
        Guid? parentExecutionId = null,
        int? iterationIndex = null,
        string? callStackJson = null,
        CancellationToken cancellationToken = default)
    {
        var pin = WorkflowRunPinScope.Consume();
        var observation = WorkflowRunObservationScope.Consume();
        var admissionSignal = new AdmissionSignal(observation?.OnAdmissionDecided, workflowId, logger);

        try
        {
            var result = await RunCoreAsync(workflowId, fire, inputParameters, parentExecutionId, iterationIndex,
                callStackJson, pin, observation, admissionSignal, cancellationToken).ConfigureAwait(false);
            admissionSignal.Threw(result.Error);
            return result;
        }
        catch (Exception ex)
        {
            admissionSignal.Threw(ex);
            throw;
        }
        finally
        {
            admissionSignal.Threw(null);
        }
    }

    private async Task<WorkflowRunResult> RunCoreAsync(Guid workflowId,
        WorkflowFire? fire,
        IReadOnlyDictionary<string, object?>? inputParameters,
        Guid? parentExecutionId,
        int? iterationIndex,
        string? callStackJson,
        WorkflowRunPin? pin,
        WorkflowRunObservation? observation,
        AdmissionSignal admissionSignal,
        CancellationToken cancellationToken)
    {
        var reportTo = WorkflowSubRunOutcomeScope.Take();

        var automationId = fire?.AutomationId;
        var triggerId = fire?.TriggerId;
        var fireDedupKey = fire?.DedupKey;
        var triggerPayload = fire?.Payload;

        var pinnedVersionId = pin?.VersionId;
        var pinnedVersions = pin?.Manifest;
        var callDepth = pin?.CallDepth ?? 0;
        var handsOver = options.HandOverOnStop && parentExecutionId is null;
        var debugMode = observation?.RecordLogEntries ?? false;
        var debugSessionId = observation?.DebugSessionId;

        if (executionGate.IsExecutionSuspended)
        {
            logger.LogInformation(
                "Workflow {WorkflowId} execution skipped because safe mode is active.", workflowId);
            admissionSignal.Refused(WorkflowAdmissionOutcomes.Skipped, "execution_suspended");
            reportTo?.ReportRefused(RefusalText(WorkflowAdmissionOutcomes.Skipped, "execution_suspended"));
            return WorkflowRunResult.Refused;
        }

        using var workflowActivity = this.activitySource.StartActivity("workflow.execute", ActivityKind.Internal);
        workflowActivity?.SetTag("workflow.id", workflowId.ToString());
        if (automationId.HasValue)
            workflowActivity?.SetTag("automation.id", automationId.Value.ToString());
        if (triggerId.HasValue)
            workflowActivity?.SetTag("trigger.id", triggerId.Value.ToString());
        if (parentExecutionId.HasValue)
            workflowActivity?.SetTag("workflow.parent_execution.id", parentExecutionId.Value.ToString());

        var firedAt = fire?.FiredAtUtc ?? DateTimeOffset.UtcNow;
        TriggerEnvelope? EnvelopeOf(
            IReadOnlyDictionary<string, object?>? payload, IReadOnlyDictionary<string, Abstractions.ClosedPortType>? terms = null) =>
            triggerId.HasValue || payload is { Count: > 0 }
                ? new TriggerEnvelope(firedAt, payload is { Count: > 0 }
                    ? new Dictionary<string, object?>(payload, StringComparer.Ordinal)
                    : TriggerEnvelope.EmptyPayload) { Terms = terms }
                : null;
        var triggerEnvelope = EnvelopeOf(triggerPayload);

        using var scope = scopeFactory.CreateScope();

        var parkedSleeps = options.ParkSleepWhenAtLeast is not null && suspensions is IWorkflowParkedSleepStore table
            ? new LiveParkedSleeps(table, stateStore, logger)
            : null;

        var resultOutputs = new Dictionary<string, object?>();

        var admission = await BuildAdmission(scope).AdmitAsync(
            new WorkflowAdmissionRequest(workflowId)
            {
                TriggerId = triggerId,
                AutomationId = automationId,
                FireDedupKey = fireDedupKey,
                PinnedVersionId = pinnedVersionId,
                PinnedVersions = pinnedVersions,
                CallDepth = callDepth,
                IsMultiReplica = options.IsMultiReplica,
                StartKind = options.ReplaysAdmittedRun ? null : KindOfStart(fire, parentExecutionId),
                ParentExecutionId = parentExecutionId,
                Arguments = inputParameters,
                TriggerPayload = triggerEnvelope?.Payload
            },
            CoordinatorFor,
            parkedSleeps,
            admissionSignal.Queued,
            cancellationToken).ConfigureAwait(false);

        if (!admission.IsAdmitted)
        {
            admissionSignal.Refused(admission.Outcome, admission.Reason);
            reportTo?.ReportRefused(RefusalText(admission.Outcome, admission.Reason));
            return WorkflowRunResult.Refused;
        }

        admissionSignal.Admitted(admission.Outcome);

        var seed = (IReadOnlyDictionary<string, object?>?)admission.Started?.Values ?? inputParameters;
        if (admission.Started is not null)
            triggerEnvelope = EnvelopeOf(admission.Started.TriggerPayload, admission.Started.TriggerTerms);

        var workflow = admission.Workflow!;
        var effectiveVersion = admission.EffectiveVersion!;
        var decision = admission.Decision!;
        var pinManifestJson = admission.PinManifestJson;
        using var pinScope = WorkflowVersionPinScope.Push(admission.PinManifest!);
        using var callDepthScope = WorkflowCallDepthScope.Push(callDepth);

        var traceMode = options.TraceMode;
        var sw = Stopwatch.StartNew();
        var metricsResult = WorkflowRunStatusLabels.Failed;
        var parkedKind = (WorkflowSuspensionKind?)null;

        async Task ReleaseDebugHookAsync(Guid runExecutionId, string runStatus) =>
            await (debugSessions?.ReleaseHookAsync(runExecutionId, runStatus, CancellationToken.None)
                ?? Task.CompletedTask).ConfigureAwait(false);

        var retryPolicy = WorkflowRunAttempt.ParseRetryPolicy(
            workflow.RetryPolicyJson, JsonOptions, logger);

        using var checkpointPolicyScope = WorkflowCheckpointPolicyScope.Push(WorkflowCheckpointPolicy.For(
            workflow.CheckpointPolicyJson, options.EngineWriteMode, options.HeartbeatInterval,
            options.RecoveryAbandonedAfter, JsonOptions, logger));
        var timeoutSeconds = workflow.ExecutionTimeoutSeconds;
        var maxAttempts = WorkflowRunAttempt.AttemptsAllowed(retryPolicy);
        var leaseToken = CancellationToken.None;

        try
        {
            leaseToken = decision.Lease!.Token;

            if (parkedSleeps is not null && decision.Mode == WorkflowMode.Restart)
            {
                try
                {
                    await CancelParkedSleepersAsync(parkedSleeps, workflowId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    metricsResult = WorkflowRunStatusLabels.Cancelled;
                    throw;
                }
            }
            for (var attemptNumber = 1; ; attemptNumber++)
            {
                var executionId = Guid.NewGuid();
                var debugBound = false;
                Guid? stateExecutionId = null;
                WorkflowSuspension? persistedSleep = null;
                var executionLog = new WorkflowExecutionLog(
                    executionId, automationId, workflowId, effectiveVersion.Id, triggerId,
                    WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow, attemptNumber);

                await scope.Journal.OpenRunAsync(executionLog, cancellationToken).ConfigureAwait(false);

                try
                {
                    if (stateStore != null)
                    {
                        var triggerSource = triggerId.HasValue ? $"trigger:{triggerId}" : (automationId.HasValue ? $"automation:{automationId}" : "manual");
                        stateExecutionId = await stateStore.BeginAsync(new WorkflowRunStateBegin
                        {
                            WorkflowId = workflowId,
                            WorkflowVersion = effectiveVersion.VersionNumber,
                            VariablesJson = WorkflowVariablesEncoder.Encode(seed),
                            TriggerSource = triggerSource,
                            OwnedByReplicaId = options.ReplicaId,
                            PinnedVersionsJson = pinManifestJson,
                            ParentExecutionId = parentExecutionId
                        }, cancellationToken).ConfigureAwait(false);
                    }

                    if (attemptNumber == 1 && debugSessionId.HasValue && debugSessions is not null)
                    {
                        debugBound = true;
                        await debugSessions.BindToRunAsync(debugSessionId.Value, executionId, cancellationToken)
                            .ConfigureAwait(false);

                        await debugSessions.MarkRunningAsync(debugSessionId.Value, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception beginEx)
                {
                    await AbandonAttemptAsync(scope, executionLog, stateExecutionId, beginEx).ConfigureAwait(false);
                    if (debugBound)
                    {
                        try
                        {
                            await ReleaseDebugHookAsync(executionId, beginEx is OperationCanceledException
                                ? WorkflowRunStatusLabels.Cancelled : WorkflowRunStatusLabels.Failed).ConfigureAwait(false);
                        }
                        catch (Exception releaseEx)
                        {
                            logger.LogWarning(releaseEx, "Failed to release the debug hook of execution {ExecutionId}.", executionId);
                        }
                    }
                    throw;
                }

                var debugHook = debugSessions?.HookFor(executionId);
                var debugCounter = debugHook is not null ? new DebugPausedCounter() : null;
                var killToken = debugHook?.KillToken ?? CancellationToken.None;
                if (debugHook is not null)
                {
                    workflowActivity?.SetTag("workflow.debug.session_id", "bound");
                }

                var logEntries = new List<WorkflowExecutionLogEntry>();
                var executionSteps = new List<WorkflowExecutionStep>();
                var walkDone = false;
                WorkflowRunEnding? ending = null;

                var effectiveDebugMode = debugMode;

                CancellationTokenSource? timeoutCts = null;
                CancellationTokenSource? cts = null;
                var heartbeat = WorkflowRunHeartbeat.Start(
                    options.HeartbeatInterval, options.RunRecoveryStore, stateStore, stateExecutionId,
                    options.ReplicaId, decision.Lease, logger, options.TimeProvider);
                try
                {
                    WorkflowDefinition? definition;
                    try
                    {
                        var cached = WorkflowVersionDataCache.Get(effectiveVersion);
                        if (cached.ParseFailure is { } parseFailure)
                            throw parseFailure.CreateException();
                        definition = cached.Definition;
                    }
                    catch (JsonException jsonEx)
                    {
                        throw new InvalidWorkflowDefinitionException("the stored definition is not a valid workflow graph", jsonEx);
                    }
                    if (definition is null)
                        throw new InvalidWorkflowDefinitionException("the stored definition is the JSON literal null, not a workflow graph");
                    if (definition.Nodes.Count == 0)
                    {
                        walkDone = true;
                        executionLog.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        if (timeoutSeconds.HasValue && timeoutSeconds.Value > 0)
                        {
                            timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value));
                            cts = CancellationTokenSource.CreateLinkedTokenSource(
                                leaseToken, timeoutCts.Token, heartbeat?.Token ?? CancellationToken.None, killToken);
                        }
                        else
                        {
                            cts = CancellationTokenSource.CreateLinkedTokenSource(
                                leaseToken, heartbeat?.Token ?? CancellationToken.None, killToken);
                        }

                        var executedOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
                        var seedInputs = seed ?? new Dictionary<string, object?>();
                        foreach (var node in definition.Nodes.Where(n => n.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId))
                        {
                            executedOutputs[node.Id] = seedInputs;
                        }

                        var mayParkSleep = parkedSleeps is not null && stateStore is not null && parentExecutionId is null
                            && WorkflowDurableSleep.ParksIn(workflow.Mode) && !debugMode
                            && !WorkflowDurableSleep.HoldsRunStateInProcess(timeoutSeconds, maxAttempts);

                        if (stateExecutionId is { } writesFor)
                            WorkflowRunWrites.Begin(writesFor,
                                recoverable: options.RecoveryReentersRuns && parentExecutionId is null,
                                caller: cancellationToken);

                        var compiled = CompiledWalk.Root(admission.Program);

                        Dictionary<string, IReadOnlyDictionary<string, object?>> allOutputs;
                        while (true)
                        {
                            try
                            {
                                allOutputs = await graphExecutor.ExecuteGraphAsync(
                                    executedOutputs, workflowId, executionId, workflow.CreatedByUserId,
                                    scope.Services, logEntries, executionSteps, traceMode, effectiveDebugMode,
                                    iterationIndex, callStackJson, 0, [], false,
                                    seed, triggerId, triggerEnvelope, cts.Token, compiled,
                                    debugHook: debugHook, debugCounter: debugCounter,
                                    stateExecutionId: stateExecutionId,
                                    mayParkSleep: mayParkSleep,
                                    parameterSignature: WorkflowExecutionHelpers.RootSignature(effectiveVersion.InputParametersJson))
                                    .ConfigureAwait(false);
                                break;
                            }
                            catch (SuspendedWorkflowException sleepEx) when (
                                sleepEx.SuspendResult.Kind == WorkflowSuspensionKind.Sleep && mayParkSleep && suspensions is not null)
                            {
                                var pre = await WorkflowDurableSleep.PreParkAsync(new WorkflowDurableSleep.PreParkRequest
                                {
                                    Suspensions = suspensions,
                                    ParkedSleeps = parkedSleeps,
                                    StateStore = stateStore!,
                                    Heartbeat = heartbeat,
                                    Sleep = sleepEx,
                                    WorkflowId = workflowId,
                                    ExecutionLogId = executionId,
                                    StateExecutionId = stateExecutionId,
                                    ReplicaId = options.ReplicaId,
                                    ExecutionSteps = executionSteps
                                }, logger).ConfigureAwait(false);

                                if (pre.SleepHere)
                                {
                                    mayParkSleep = false;
                                    continue;
                                }

                                persistedSleep = pre.Persisted;
                                throw;
                            }
                        }

                        resultOutputs = WorkflowExecutionHelpers.BuildResultOutputs(allOutputs, compiled);
                        walkDone = true;

                        executionLog.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow,
                            outputsJson: PayloadSanitizer.SerializeAndSanitize(resultOutputs, options.MaxPayloadValueBytes));
                        if (parentExecutionId is not null)
                            executionLog.RecordResultForParent(SerializeRawResult(resultOutputs, compiled));
                    }

                    await SaveRunDetailsAsync(scope, effectiveDebugMode, logEntries, executionSteps, executionId, CancellationToken.None).ConfigureAwait(false);
                    ending = WorkflowRunEnding.Decide(Exit(null));
                }
                catch (SuspendedWorkflowException suspEx)
                {
                    try
                    {
                        await SaveRunDetailsAsync(scope, debugMode, logEntries, executionSteps, executionId, CancellationToken.None).ConfigureAwait(false);

                        var park = await WorkflowRunParking.ParkAsync(new WorkflowRunParking.Request
                        {
                            Persisted = persistedSleep,
                            RowAlreadyParked = persistedSleep is not null,
                            StateStore = stateStore,
                            Suspensions = suspensions,
                            ParkSignal = options.ParkSignal,
                            Heartbeat = heartbeat,
                            ReplicaId = options.ReplicaId,
                            StateExecutionId = stateExecutionId,
                            ParentExecutionId = parentExecutionId,
                            ExecutionLogId = executionId,
                            WorkflowId = workflowId,
                            Suspended = suspEx,
                            VariablesJson = "{}"
                        }, logger).ConfigureAwait(false);

                        if (park.Parked)
                        {
                            parkedKind = suspEx.SuspendResult.Kind;
                            metricsResult = WorkflowRunStatusLabels.Succeeded;
                        }
                        else
                            ending = WorkflowRunEnding.Decide(Exit(park.Refusal));
                    }
                    catch (Exception parkEx)
                    {
                        ending = WorkflowRunEnding.Decide(Exit(parkEx));
                    }
                }
                catch (Exception ex)
                {
                    ending = WorkflowRunEnding.Decide(Exit(ex));
                }
                finally
                {
                    try
                    {
                        if (heartbeat is not null)
                            await heartbeat.StopAsync().ConfigureAwait(false);
                        cts?.Dispose();
                        timeoutCts?.Dispose();
                        if (ending is not null)
                        {
                            ending = await CarryOutAsync(ending, scope, executionLog, stateExecutionId, executionId,
                                effectiveDebugMode, logEntries, executionSteps).ConfigureAwait(false);
                            metricsResult = ending.MetricsLabel;
                        }
                    }
                    finally
                    {
                        if (heartbeat is not null)
                            await heartbeat.DisposeAsync().ConfigureAwait(false);
                        if (debugHook is not null)
                        {
                            workflowActivity?.SetTag("workflow.debug.paused_count", debugCounter?.Value ?? 0);
                            await ReleaseDebugHookAsync(executionId, metricsResult).ConfigureAwait(false);
                        }
                    }
                }

                WorkflowRunExit Exit(Exception? thrown) => new()
                {
                    WorkflowId = workflowId,
                    RunRow = stateExecutionId,
                    Thrown = thrown,
                    WalkDone = walkDone,
                    RowLostByBeat = heartbeat?.RowLoss,
                    BudgetLostByBeat = heartbeat?.BudgetLost == true,
                    DebugKilled = killToken.IsCancellationRequested,
                    CallerCancelled = cancellationToken.IsCancellationRequested,
                    HostStopping = options.HostStopping.IsCancellationRequested,
                    LeaseCancelled = leaseToken.IsCancellationRequested,
                    TimeoutFired = timeoutCts?.IsCancellationRequested == true,
                    TimeoutSeconds = timeoutSeconds,
                    Mode = decision.Lease!.Mode,
                    MayHandOver = handsOver && stateExecutionId.HasValue,
                    AttemptNumber = attemptNumber,
                    MaxAttempts = maxAttempts
                };

                if (ending is null)
                {
                    reportTo?.Report(parked: true, executionId);
                    return new WorkflowRunResult(WorkflowRunOutcome.Ran(resultOutputs), WorkflowRunPhase.Parked, null);
                }

                if (!ending.TryAgain)
                {
                    reportTo?.Report(parked: false, executionId);
                    return new WorkflowRunResult(
                        WorkflowRunOutcome.Ran(resultOutputs), ending.Phase, ending.Error, ending.RowWriteFailed);
                }

                var delayMs = WorkflowRunAttempt.BackoffDelayMs(retryPolicy, attemptNumber + 1);
                if (delayMs > 0)
                {
                    try
                    {
                        using var backoff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseToken);
                        await Task.Delay(delayMs, backoff.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return new WorkflowRunResult(WorkflowRunOutcome.Ran(resultOutputs), ending.Phase, ending.Error);
                    }
                    catch (OperationCanceledException) when (leaseToken.IsCancellationRequested)
                    {
                    }
                }

                if (leaseToken.IsCancellationRequested)
                {
                    var leaseEnding = WorkflowRunEnding.Decide(new WorkflowRunExit
                    {
                        WorkflowId = workflowId,
                        LeaseCancelled = true,
                        Mode = decision.Lease!.Mode,
                        AttemptNumber = attemptNumber,
                        MaxAttempts = maxAttempts
                    });
                    metricsResult = leaseEnding.MetricsLabel;
                    return new WorkflowRunResult(WorkflowRunOutcome.Ran(resultOutputs), leaseEnding.Phase, leaseEnding.Error);
                }
            }
        }
        finally
        {
            sw.Stop();

            try
            {
                if (parkedKind is { } kind) metrics.RecordSuspensionStarted(kind.ToString());
                else metrics.RecordExecutionCompleted(metricsResult, sw.Elapsed.TotalSeconds);
            }
            catch (Exception metricEx)
            {
                logger.LogWarning(metricEx, "Workflow {WorkflowId}: the ending of a run could not be counted.", workflowId);
            }
            workflowActivity?.SetTag("workflow.result", metricsResult);
            if (metricsResult != WorkflowRunStatusLabels.Succeeded)
                workflowActivity?.SetStatus(ActivityStatusCode.Error, metricsResult);

            await decision.Lease!.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<WorkflowRunEnding> CarryOutAsync(
        WorkflowRunEnding ending, IWorkflowRunScope scope, WorkflowExecutionLog executionLog, Guid? stateExecutionId,
        Guid executionId, bool recordLogEntries, List<WorkflowExecutionLogEntry> logEntries,
        List<WorkflowExecutionStep> executionSteps)
    {
        ending = await WorkflowRunEndings.EndRowAsync(ending, stateStore, stateExecutionId, options.ReplicaId, logger)
            .ConfigureAwait(false);

        if (ending.ClosesWaits)
            await WorkflowRunEndings.CloseWaitsAsync(suspensions, executionId, logger).ConfigureAwait(false);

        if (ending.Phase == WorkflowRunPhase.Finished)
        {
            if (ending.Error is null)
                return ending;

            logger.LogError(ending.Error,
                "workflow {WorkflowId} execution {ExecutionId} finished, but what it did could not all be recorded.",
                executionLog.WorkflowId, executionId);
            if (executionLog.Status == WorkflowExecutionStatus.Running)
                executionLog.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow);
            await WorkflowRunEndings.QuietlyAsync(
                () => scope.Journal.SaveRunAsync(CancellationToken.None), logger, "save the journal entry of", executionId)
                .ConfigureAwait(false);
            return ending;
        }

        if (await WorkflowRunEndings.EntryGoesOnAsync(ending, stateStore, stateExecutionId, executionLog.Id, logger)
                .ConfigureAwait(false))
        {
            if (ending.Phase == WorkflowRunPhase.HandedOver && ending.JournalMessage is { } handedOver)
                executionLog.NoteHandedOver(handedOver);
        }
        else
        {
            executionLog.Complete(ending.JournalStatus, DateTimeOffset.UtcNow, ending.JournalMessage);
        }

        await WorkflowRunEndings.QuietlyAsync(
            () => SaveRunDetailsAsync(scope, recordLogEntries, logEntries, executionSteps, executionId, CancellationToken.None),
            logger, "save the ending of", executionId).ConfigureAwait(false);
        return ending;
    }

    private async Task AbandonAttemptAsync(
        IWorkflowRunScope scope, WorkflowExecutionLog executionLog, Guid? stateExecutionId, Exception thrown)
    {
        var cancelled = thrown is OperationCanceledException;
        var message = Cut(thrown.Message);
        if (stateStore is not null && stateExecutionId is { } rowId)
        {
            await WorkflowRunEndings.QuietlyAsync(() => stateStore.CompleteIfOwnedAsync(rowId, options.ReplicaId,
                    cancelled ? WorkflowRunStateStatus.Cancelled : WorkflowRunStateStatus.Failed, message, CancellationToken.None),
                logger, "end the state row of", executionLog.Id).ConfigureAwait(false);
        }

        executionLog.Complete(
            cancelled ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Failed, DateTimeOffset.UtcNow, message);
        await WorkflowRunEndings.QuietlyAsync(() => scope.Journal.SaveRunAsync(CancellationToken.None),
            logger, "close the entry of", executionLog.Id).ConfigureAwait(false);

        static string Cut(string value) => value.Length > 400 ? value[..400] : value;
    }

    private static string RefusalText(string outcome, string? reason) =>
        reason is null ? outcome : $"{outcome} ({reason})";

    private static WorkflowStartKind KindOfStart(WorkflowFire? fire, Guid? parentExecutionId) =>
        parentExecutionId is not null ? WorkflowStartKind.SubWorkflow
        : fire is not null ? WorkflowStartKind.Trigger
        : WorkflowStartKind.Manual;

    private WorkflowRunAdmission BuildAdmission(IWorkflowRunScope scope) =>
        new(scope.Store, blocks, compiler, scope.Journal, executionGate, scope.Authorization, scope.Audit, metrics, logger,
            fireDedup, new WorkflowAdmissionOptions
            {
                FireDedupWindowSeconds = options.FireDedupWindow.TotalSeconds
            }, startAdmission, nodeValidator, argumentValidator);

    private WorkflowRunCoordinator CoordinatorFor(Workflow workflow) =>
        coordinators is null
            ? new WorkflowRunCoordinator(
                workflow.Id, workflow.Mode, workflow.MaxConcurrentRuns, workflow.MaxQueuedRuns,
                workflow.OverflowBehavior, metrics)
            : coordinators.For(workflow);

    private async Task CancelParkedSleepersAsync(
        IWorkflowParkedSleepStore parkedSleeps, Guid workflowId, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkflowParkedSleep> sleepers;
        try
        {
            sleepers = await parkedSleeps.ListParkedSleepsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Workflow {WorkflowId}: could not read the runs parked on a sleep before a Restart. They were not "
                + "cancelled, and may run the rest of their graph after this run when they wake.",
                workflowId);
            return;
        }

        if (sleepers.Count == 0)
            return;

        const string Superseded = "Superseded by a newer run in Restart mode while parked on a sleep.";
        var ended = 0;
        foreach (var sleeper in sleepers)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var stateExecutionId = WorkflowDurableSleep.StateExecutionIdOf(sleeper.StatePayloadJson);

                var stateEnded = stateExecutionId is { } rowId && stateStore is not null
                    && await stateStore.CompleteAsync(rowId, WorkflowRunStateStatus.Cancelled, null, CancellationToken.None)
                        .ConfigureAwait(false);

                using var scope = scopeFactory.CreateScope();
                var log = await scope.Journal.LoadRunAsync(sleeper.ExecutionLogId, CancellationToken.None)
                    .ConfigureAwait(false);
                var journalEnded = log?.Status == WorkflowExecutionStatus.Running;

                if (stateEnded || (stateExecutionId is null && journalEnded))
                {
                    graphExecutor.NotifyParkedNodeEnded(workflowId, sleeper.ExecutionLogId, sleeper.BlockId, sleeper.SuspendedAtUtc,
                        "Cancelled: " + Superseded);
                    metrics.RecordSuspensionResolved(nameof(WorkflowSuspensionKind.Sleep), WorkflowRunStatusLabels.Cancelled,
                        (now - sleeper.SuspendedAtUtc).TotalSeconds);
                }

                if (journalEnded)
                {
                    log!.Complete(WorkflowExecutionStatus.Cancelled, now, Superseded);
                    await scope.Journal.SaveRunAsync(CancellationToken.None).ConfigureAwait(false);
                }

                await parkedSleeps.CancelParkedSleepAsync(sleeper.SuspensionId, CancellationToken.None).ConfigureAwait(false);
                ended++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Workflow {WorkflowId}: parked run {ExecutionLogId} could not be fully cancelled before a Restart; "
                    + "whatever of it was ended stays ended, and a wake that finds its run over does not resume it.",
                    workflowId, sleeper.ExecutionLogId);
            }
        }

        logger.LogInformation(
            "Workflow {WorkflowId}: a Restart ended {Count} of {Total} run(s) parked on a sleep.",
            workflowId, ended, sleepers.Count);
    }

    private static Task SaveRunDetailsAsync(
        IWorkflowRunScope scope, bool recordLogEntries,
        List<WorkflowExecutionLogEntry> logEntries, List<WorkflowExecutionStep> executionSteps,
        Guid? executionId, CancellationToken cancellationToken) =>
        scope.Journal.SaveRunDetailsAsync(executionId, recordLogEntries, logEntries, executionSteps, cancellationToken);

    private static string? SerializeRawResult(IReadOnlyDictionary<string, object?> resultOutputs, CompiledWalk compiled)
    {
        try { return PersistedValueCodec.WriteAnswer(resultOutputs, compiled.Canvas.OutputTypes); }
        catch (Exception ex) when (ExpressionJsonValue.Unwritable(ex)) { return null; }
    }

}

/// <summary>Hands out one coordinator per workflow and keeps it. Two coordinators over one workflow would
/// each believe they held its whole budget, so this is a host singleton, not a per-run object.</summary>
public interface IWorkflowRunCoordinatorPool
{
    /// <summary>The coordinator for this workflow, created on first use and re-read when its budget changes.</summary>
    WorkflowRunCoordinator For(Workflow workflow);
}

/// <summary>What one call to the runner did: whether the run started, and what it produced if it did.</summary>
/// <remarks>The distinction only a caller outside the run loop needs. Admission refuses without failing, and a
/// refusal that reads as an empty result is how recovery recorded a run it never dispatched as finished.</remarks>
public sealed record WorkflowRunOutcome(bool Started, IReadOnlyDictionary<string, object?> Outputs)
{
    /// <summary>Admission said no. Nothing ran, nothing failed, and the run is still owed.</summary>
    /// <remarks>Read-only rather than an empty dictionary: this instance is shared by every refused run in the
    /// process, and a caller that treated the outputs as its own to write into would reach all of them.</remarks>
    public static WorkflowRunOutcome Refused { get; } =
        new(false, System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty);

    /// <summary>The run started and reached an end — successful, suspended or failed.</summary>
    public static WorkflowRunOutcome Ran(IReadOnlyDictionary<string, object?> outputs) => new(true, outputs);
}

internal sealed record WorkflowRunResult(
    WorkflowRunOutcome Outcome, WorkflowRunPhase Phase, Exception? Error, bool RowLeftRunning = false)
{
    public static WorkflowRunResult Refused { get; } = new(WorkflowRunOutcome.Refused, WorkflowRunPhase.Stopped, null);
}

/// <summary>What a run needs to know about the host it is running on.</summary>
public sealed record WorkflowRunnerOptions
{
    /// <summary>Signals the local waker after this runner parks a run.</summary>
    public WorkflowSuspensionParkSignal? ParkSignal { get; init; }

    /// <summary>Set for the replay of a run an earlier admission let start: its start is not asked of the host
    /// again.</summary>
    public bool ReplaysAdmittedRun { get; init; }

    /// <summary>How much of a run's steps to record.</summary>
    public WorkflowTraceMode TraceMode { get; init; }

    /// <summary>The cap on one serialised value in a persisted payload, past which it is replaced by a note
    /// saying so. A workflow's outputs are not bounded by anything else.</summary>
    public int MaxPayloadValueBytes { get; init; }

    /// <summary>Which replica this is, so a row records an owner.</summary>
    public string? ReplicaId { get; init; }

    /// <summary>Whether there are peers. On one replica the fire-dedup round trip buys nothing, because
    /// there is no fan-out to collapse.</summary>
    public bool IsMultiReplica { get; init; }

    /// <summary>The threshold at which a root run's sleep parks as a suspension row rather than waiting in this
    /// process. Null parks nothing. See <see cref="WorkflowEngineSettings.ParkSleepWhenAtLeast"/>.</summary>
    public TimeSpan? ParkSleepWhenAtLeast { get; init; }

    /// <summary>How long one physical trigger event stays claimed. Long enough that every replica's copy of
    /// it arrives inside the window, short enough that a genuine repeat is not swallowed.</summary>
    public TimeSpan FireDedupWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether a run its host stops is left for a peer rather than ended. See
    /// <see cref="WorkflowEngineSettings.HandOverRunsOnStop"/>.</summary>
    public bool HandOverOnStop { get; init; }

    /// <summary>Whether a recovery sweep re-enters an abandoned run from its checkpoint. See
    /// <see cref="WorkflowEngineSettings.RecoveryReentersRuns"/>.</summary>
    public bool RecoveryReentersRuns { get; init; }

    /// <summary>Fires when the host begins to stop. See <see cref="WorkflowEngineSeams.HostStopping"/>.</summary>
    public CancellationToken HostStopping { get; init; }

    /// <summary>How often a live run renews its claim. Null, or no <see cref="RunRecoveryStore"/>, renews nothing.</summary>
    public TimeSpan? HeartbeatInterval { get; init; }

    /// <summary>Where the heartbeat renews the row, on the clock the recovery sweep reads.</summary>
    public IWorkflowRunRecoveryStore? RunRecoveryStore { get; init; }

    /// <summary>The clock the heartbeat waits on between beats. Null is the system clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>How often a run writes its row where its workflow says nothing. See
    /// <see cref="WorkflowEngineSettings.RunStateWriteMode"/>.</summary>
    public RunStateWriteMode EngineWriteMode { get; init; } = RunStateWriteMode.EveryBlock;

    /// <summary>See <see cref="WorkflowEngineSettings.RecoveryAbandonedAfter"/>.</summary>
    public TimeSpan RecoveryAbandonedAfter { get; init; } = TimeSpan.FromMinutes(1);
}

internal sealed class AdmissionSignal(Action<WorkflowAdmissionDecision>? callback, Guid workflowId, ILogger logger)
{
    private int heard;

    public void Queued() =>
        Hear(new WorkflowAdmissionDecision(workflowId, WorkflowAdmissionDecisionKind.Queued)
        {
            Outcome = WorkflowAdmissionOutcomes.Queued
        });

    public void Admitted(string outcome) =>
        Hear(new WorkflowAdmissionDecision(workflowId, WorkflowAdmissionDecisionKind.Admitted) { Outcome = outcome });

    public void Refused(string outcome, string? reason) =>
        Hear(new WorkflowAdmissionDecision(workflowId, WorkflowAdmissionDecisionKind.Refused)
        {
            Outcome = outcome, Reason = reason
        });

    public void Threw(Exception? exception)
    {
        if (callback is null || heard != 0)
            return;

        Hear(exception is WorkflowConcurrencyException
            ? new WorkflowAdmissionDecision(workflowId, WorkflowAdmissionDecisionKind.Refused)
            {
                Outcome = WorkflowAdmissionOutcomes.Errored, Exception = exception
            }
            : new WorkflowAdmissionDecision(workflowId, WorkflowAdmissionDecisionKind.Failed) { Exception = exception });
    }

    private void Hear(WorkflowAdmissionDecision decision)
    {
        if (callback is null || Interlocked.Exchange(ref heard, 1) != 0)
            return;

        try
        {
            callback(decision);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workflow {WorkflowId}: the admission-decided callback threw; the run is unaffected.",
                workflowId);
        }
    }
}
