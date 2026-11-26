using ReeveWorkflow.Core.Compilation;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What became of a run something tried to re-enter, for the caller that resolved its wait first
/// and has to decide whether the row it took goes back.</summary>
public enum WorkflowResumeOutcome
{
    /// <summary>The run was taken. Nothing else may enter it, and the wait that resolved stays resolved.</summary>
    Resumed,

    /// <summary>Nothing was done to the run and nothing is wrong with it — safe mode, or a read that did not
    /// answer. Whatever resolved its wait puts it back, and a later pass tries again.</summary>
    Deferred,

    /// <summary>The run cannot be re-entered, and nothing about that will read differently next time — its
    /// version will not parse, or it has already ended. Putting its wait back would take it again for ever.</summary>
    /// <remarks>Recorded as failed wherever there is still a run to record it on.</remarks>
    Abandoned,

    /// <summary>Somebody else has the run: its state row is no longer parked, so another resume or a recovery
    /// has moved it on. Nothing was done to it or written about it, and its wait must not be put back.</summary>
    HeldElsewhere
}

/// <summary>Re-entering a run that a wait resolved. Its own interface so that whatever decided the wait is
/// over — this library's waker, or a host's own event loop — does not have to name the implementation.</summary>
public interface IWorkflowRunResumer
{
    /// <summary>Re-enters the run the context names, from the node after the one it parked on.</summary>
    /// <remarks>A resume that did nothing to the run, a host stopping before it was taken included, answers
    /// <see cref="WorkflowResumeOutcome.Deferred"/>, and <see cref="WorkflowSuspensionWaker"/> puts that wait back. An
    /// exception, a cancellation included, says nothing about whether the run was entered, so the waker leaves that
    /// wait taken.</remarks>
    Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken);
}

/// <summary>Re-entering a run that stopped. It restores the outputs live at the moment it suspended, hands
/// the wait block the answer it was waiting for, and walks on from the node after it.</summary>
public sealed class WorkflowRunResumer(
    IWorkflowRunScopeFactory scopeFactory,
    WorkflowGraphExecutor graphExecutor,
    IWorkflowExecutionGate executionGate,
    IWorkflowMetrics metrics,
    ILogger logger,
    WorkflowResumeOptions options,
    WorkflowCompilationService compiler,
    IWorkflowRunStateStore? stateStore = null,
    IWorkflowSuspensionStore? suspensions = null,
    IWorkflowRunCoordinatorPool? coordinators = null) : IWorkflowRunResumer
{
    private static readonly JsonSerializerOptions JsonOptions = WorkflowGraphHelpers.JsonOptions;

    /// <summary>Re-enters the workflow engine for a resolved suspension: deserialises the per-node output map live
    /// at suspension, injects the wait block's synthetic output (outcome/triggerData/lastEvaluation/resumedAt/suspensionId), and resumes ExecuteGraphAsync from the node after it.</summary>
    public async Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(
        ResumeContext ctx, CancellationToken cancellationToken)
    {
        if (executionGate.IsExecutionSuspended)
        {
            logger.LogInformation(
                "execution {ExecutionLogId} left parked because safe mode is active.",
                ctx.ExecutionLogId);
            return WorkflowResumeOutcome.Deferred;
        }

        return await EnterAsync(Entry.Of(ctx), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a claimed row says when the journal entry its checkpoint names is gone (a retention sweep took
    /// it): there is nothing to carry the run on under.</summary>
    public const string JournalEntryMissingReason = WorkflowAbandonmentReasons.JournalEntryMissing;

    /// <summary>Re-enters a run whose replica died, at the root block its checkpoint names, under the journal entry it
    /// was writing. The row must be the caller's already: a recovery sweep's claim is what makes it so.</summary>
    /// <remarks>The block in flight runs again, so the caller has asked whether it may; this only
    /// takes the row from the name the claim left on it at the node it was at, and walks.</remarks>
    public async Task<WorkflowResumeOutcome> ReenterFromCheckpointAsync(
        WorkflowRunStateRow row, WorkflowSuspensionStatePayload checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.NodeInFlight is null || checkpoint.JournalExecutionId is null)
            throw new ArgumentException("A checkpoint names the node in flight and the journal entry.", nameof(checkpoint));

        if (executionGate.IsExecutionSuspended)
        {
            logger.LogInformation(
                "execution {ExecutionLogId} left for a later sweep because safe mode is active.", checkpoint.JournalExecutionId);
            return WorkflowResumeOutcome.Deferred;
        }

        return await EnterAsync(new Entry
        {
            WorkflowId = row.WorkflowId,
            ExecutionLogId = checkpoint.JournalExecutionId.Value,
            BlockId = checkpoint.NodeInFlight,
            StatePayloadJson = row.VariablesJson,
            SuspendedAtUtc = row.LastTransitionUtc,
            Row = row,
            SleepWakesAt = row is { Status: WorkflowRunStateStatus.Sleeping, WaitUntilUtc: { } wakesAt }
                && row.CurrentBlockPath == checkpoint.NodeInFlight
                    ? wakesAt
                    : null
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes a run's journal entry, when it is still open, as a recovery that will not carry the run on
    /// under it ended it. True when no entry is left open — this call closed it, a peer already ended it, or the
    /// journal does not have it; false when the journal could not be asked or the write did not land.</summary>
    /// <remarks>Public for a host whose own sweep ends rows this package's sweep would have. A replica that gives its row up leaves the entry open for whoever takes the row: this package's sweep closes it or
    /// carries the run on under it, and a host's own sweep, which replays from the start under an entry of its own, has
    /// to close it or the entry stays <c>Running</c> for the life of the database. A host can write this itself against
    /// <see cref="ReeveWorkflow.Core.Hosting.IWorkflowRunJournal"/> — load, <see cref="WorkflowExecutionLog.Complete"/>,
    /// save, in a scope of its own rather than a request's, and the reason cut to the 2 000 characters the column
    /// holds; this is that, plus the guard that an entry a peer already ended is not written over.
    /// <para>The entry a row named is <c>WorkflowRunCheckpoint.Of(row)?.JournalExecutionId</c> — <c>Of</c>, not
    /// <c>Read</c>, because a checkpoint naming another row is not this row's entry to close. Close it after ending the
    /// row, and only when ending it succeeded, or a peer that legitimately took the row loses the entry it is running
    /// under. False is worth acting on: the row is already ended, so no later pass of the host's sweep will see it
    /// again. A cancelled token answers false like any other failure rather than throwing, so a host draining while
    /// its closes are in flight is told what it left open.</para>
    /// <para>Best effort, as the sweep's own use of it always was. The guard reads and then writes, unfenced: an entry
    /// a peer ends in between is written over — nothing about the row is re-checked — so a close raced against a
    /// peer's ending can leave the peer's <c>Succeeded</c> as this reason's <c>Cancelled</c>. The ordering above is
    /// what keeps a host out of that race.</para></remarks>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or blank — <see cref="ArgumentNullException"/>
    /// when null; it is what a history reads.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is
    /// <see cref="WorkflowExecutionStatus.Running"/>, which is not an ending.</exception>
    public Task<bool> CloseEntryAsync(
        Guid executionLogId, WorkflowExecutionStatus status, string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (status == WorkflowExecutionStatus.Running)
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "An entry is closed with an ending. Running would write a completed-at time onto a run still going.");

        return CloseOpenEntryAsync(executionLogId, status, reason, cancellationToken);
    }

    private async Task<bool> CloseOpenEntryAsync(
        Guid executionLogId, WorkflowExecutionStatus status, string reason, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var log = await scope.Journal.LoadRunAsync(executionLogId, cancellationToken).ConfigureAwait(false);
            if (log is null || log.Status != WorkflowExecutionStatus.Running)
                return true;
            log.Complete(status, DateTimeOffset.UtcNow, Truncate(reason, 2000));
            await scope.Journal.SaveRunAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to close journal entry {ExecutionLogId} as {Status}.", executionLogId, status);
            return false;
        }
    }

    private async Task<WorkflowResumeOutcome> EnterAsync(Entry ctx, CancellationToken cancellationToken)
    {
        var taken = new StrongBox<bool>(false);
        try
        {
            return await ResumeCoreAsync(ctx, taken, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!taken.Value)
        {
            logger.LogInformation(
                "execution {ExecutionLogId} was not re-entered because the host is stopping; it stays parked.",
                ctx.ExecutionLogId);
            return WorkflowResumeOutcome.Deferred;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !taken.Value)
        {
            logger.LogWarning(ex,
                "execution {ExecutionLogId} could not be read back to resume it; it stays parked.",
                ctx.ExecutionLogId);
            return WorkflowResumeOutcome.Deferred;
        }
    }

    private static async Task<WorkflowRunLease?> TryAcquireForResumeAsync(
        IWorkflowRunCoordinatorPool pool, Workflow workflow, CancellationToken cancellationToken)
    {
        var coordinator = pool.For(workflow);
        if (!coordinator.TryPinAdmission(out var pin))
        {
            coordinator = pool.For(workflow);
            if (!coordinator.TryPinAdmission(out pin))
                throw new InvalidOperationException("The workflow coordinator was retired during resume.");
        }

        using var pinnedResume = pin;
        return await coordinator.TryAcquireForResumeAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkflowResumeOutcome> ResumeCoreAsync(
        Entry ctx, StrongBox<bool> taken, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "re-entering workflow {WorkflowId} execution {ExecutionLogId} " +
            "at block {BlockId} suspension {SuspensionId} kind={Kind}.",
            ctx.WorkflowId, ctx.ExecutionLogId, ctx.BlockId, ctx.SuspensionId, ctx.Kind);

        using var scope = scopeFactory.CreateScope();

        var workflow = await scope.Store.LoadAsync(ctx.WorkflowId, cancellationToken).ConfigureAwait(false);

        if (workflow?.CurrentVersion == null)
        {
            logger.LogError(
                "workflow {WorkflowId} or its current version not found; cannot resume execution {ExecutionLogId}.",
                ctx.WorkflowId, ctx.ExecutionLogId);
            await MarkResumeFailedAsync(ctx, scope.Journal,
                $"Workflow {ctx.WorkflowId} or current version not found during resume.",
                cancellationToken).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        var executionLog = await scope.Journal
            .LoadRunAsync(ctx.ExecutionLogId, cancellationToken).ConfigureAwait(false);

        if (executionLog == null)
        {
            logger.LogError(
                "execution log {ExecutionLogId} not found; cannot resume.",
                ctx.ExecutionLogId);
            if (ctx.Row is { } orphan && stateStore is not null)
                await WorkflowRunEndings.QuietlyAsync(() => stateStore.CompleteIfOwnedAsync(orphan.ExecutionId,
                        options.ReplicaId, WorkflowRunStateStatus.Failed, JournalEntryMissingReason, CancellationToken.None),
                    logger, "end the state row of", ctx.ExecutionLogId).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        if (executionLog.Status != WorkflowExecutionStatus.Running)
        {
            logger.LogInformation(
                "execution {ExecutionLogId} is {Status} rather than running; its wait is not resumed.",
                ctx.ExecutionLogId, executionLog.Status);

            if (await TerminalizeAsync(WorkflowDurableSleep.StateExecutionIdOf(ctx.StatePayloadJson),
                    WorkflowRunStateStatus.Cancelled, null).ConfigureAwait(false)
                && ctx.Kind == WorkflowSuspensionKind.Sleep)
            {
                graphExecutor.NotifyParkedNodeEnded(ctx.WorkflowId, ctx.ExecutionLogId, ctx.BlockId, ctx.SuspendedAtUtc,
                    $"Cancelled: the run became {executionLog.Status} while it was parked on a sleep.");
                metrics.RecordSuspensionResolved(ctx.Wait!.Kind.ToString(), WorkflowRunStatusLabels.Cancelled,
                    (DateTimeOffset.UtcNow - ctx.SuspendedAtUtc).TotalSeconds);
            }

            return WorkflowResumeOutcome.Abandoned;
        }

        var pinnedVersion = await scope.Store
            .LoadVersionAsync(executionLog.WorkflowVersionId, cancellationToken).ConfigureAwait(false);
        if (pinnedVersion == null)
        {
            logger.LogError(
                "Version pinning: pinned version {VersionId} for workflow {WorkflowId} not found; cannot resume {ExecutionLogId}.",
                executionLog.WorkflowVersionId, ctx.WorkflowId, ctx.ExecutionLogId);
            await MarkResumeFailedAsync(ctx, scope.Journal,
                $"{WorkflowAbandonmentReasons.PinnedVersionUnavailable}: {executionLog.WorkflowVersionId}", cancellationToken).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs;
        string? manifestJson = null;
        IReadOnlyDictionary<string, object?>? inputParameters = null;
        string? inputNodeId = null;
        Guid? triggerIdForRun = null;
        TriggerEnvelope? triggerEnvelopeForRun = null;
        Guid? stateExecutionId = null;
        var resumeVariablesJson = "{}";
        var callDepth = 0;
        IReadOnlyList<WorkflowContinuationFrame> continuation = [];
        string? continuationRoot = null;

        WorkflowSuspensionStatePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(ctx.StatePayloadJson, JsonOptions)
                ?? throw new InvalidOperationException("State payload deserialized to null.");
            manifestJson = payload.PinnedVersionsJson;
            stateExecutionId = payload.ExecutionStateId;
            inputNodeId = payload.InputNodeId;
            callDepth = payload.CallDepth;
            continuation = WorkflowRunCheckpoint.Continuation(payload);
            continuationRoot = payload.NodeInFlight ?? ctx.BlockId;
            resumeVariablesJson = string.IsNullOrWhiteSpace(payload.VariablesJson) ? "{}" : payload.VariablesJson;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "failed to deserialize state payload for execution {ExecutionLogId}.",
                ctx.ExecutionLogId);
            await MarkResumeFailedAsync(ctx, scope.Journal,
                $"State payload deserialization failed: {ex.Message}",
                cancellationToken).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        var startedARun = ctx.Kind == WorkflowSuspensionKind.SubWorkflow;
        var subRunAnswer = startedARun ? WorkflowSubRunOutcomes.Read(ctx.BlockId, ctx.OutcomeJson) : null;

        if (startedARun && subRunAnswer is null)
        {
            const string why = "The run was parked on a run it started and the answer of that run will not read, "
                + "so there is nothing to put in front of the block.";
            logger.LogError("{Why} Execution log {ExecutionLogId}, block {BlockId}.", why, ctx.ExecutionLogId, ctx.BlockId);
            await MarkResumeFailedAsync(ctx, scope.Journal, why, cancellationToken).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        WorkflowDefinition? definition = null;
        JsonException? unreadable = null;
        try
        {
            var cached = WorkflowVersionDataCache.Get(pinnedVersion);
            if (cached.ParseFailure is { } parseFailure)
                throw parseFailure.CreateException();
            definition = cached.Definition;
        }
        catch (JsonException ex)
        {
            unreadable = ex;
        }

        if (definition is null)
        {
            logger.LogError(unreadable,
                "pinned version {VersionId} could not be read; cannot resume execution {ExecutionLogId}.",
                pinnedVersion.Id, ctx.ExecutionLogId);
            await MarkResumeFailedAsync(ctx, scope.Journal,
                $"{WorkflowAbandonmentReasons.PinnedVersionUnreadable}: {pinnedVersion.Id}", cancellationToken).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        if (definition.Nodes.Count == 0)
        {
            logger.LogWarning(
                "workflow {WorkflowId} version has no nodes; completing execution {ExecutionLogId}.",
                ctx.WorkflowId, ctx.ExecutionLogId);
            executionLog.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow);
            await scope.Journal.SaveRunAsync(CancellationToken.None).ConfigureAwait(false);
            await TerminalizeAsync(stateExecutionId, WorkflowRunStateStatus.Completed, null).ConfigureAwait(false);
            return WorkflowResumeOutcome.Resumed;
        }

        var traceMode = options.TraceMode;

        var metricsResult = WorkflowRunStatusLabels.Succeeded;
        var reparkedKind = (WorkflowSuspensionKind?)null;
        var resumedFor = System.Diagnostics.Stopwatch.StartNew();
        var lastException = (Exception?)null;

        var resumeManifest = WorkflowResumeDecisions.ResolveResumeManifest(
            manifestJson, ctx.WorkflowId, pinnedVersion.Id, JsonOptions);
        using var resumePinScope = WorkflowVersionPinScope.Push(resumeManifest);
        var (verdict, program) = await compiler.EnsureWithProgramAsync(pinnedVersion, resumeManifest, cancellationToken)
            .ConfigureAwait(false);
        if (program is null)
        {
            logger.LogError(
                "pinned version {VersionId} no longer compiles; cannot resume execution {ExecutionLogId}.",
                pinnedVersion.Id, ctx.ExecutionLogId);
            await MarkResumeFailedAsync(ctx, scope.Journal,
                $"{WorkflowAbandonmentReasons.PinnedVersionUncompilable}: {pinnedVersion.Id}: {string.Join(", ", verdict.Diagnostics.Select(d => d.RuleId).Distinct())}",
                cancellationToken).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        var rootWalk = CompiledWalk.Root(program);
        try
        {
            executedOutputs = PersistedValueCodec.ReadNodeOutputs(payload.PreviousOutputsJson, rootWalk.Canvas, "outputs");
            (triggerIdForRun, triggerEnvelopeForRun) = PersistedValueCodec.ReadTriggerEnvelope(
                payload.TriggerEnvelopeJson, WorkflowStartArguments.PayloadTerms(compiler.Options));
            PersistedValueCodec.ReadPosition(continuation, payload.ContinuationJson, rootWalk, continuationRoot);
            if (subRunAnswer is { Succeeded: true } answer)
            {
                var node = ResumedNode(rootWalk, continuation, continuationRoot, ctx.BlockId)
                    ?? throw new PersistedPayloadException("child", ctx.BlockId, null, "the pinned program has no such node");
                PersistedValueCodec.ReadChildAnswer(
                    answer.OutputsJson ?? throw new PersistedPayloadException("child", ctx.BlockId, null, "the child's result is missing"),
                    node.OutputTypes, ctx.BlockId);
            }
        }
        catch (PersistedPayloadException ex)
        {
            logger.LogError(ex, "A persisted value of execution {ExecutionLogId} does not read back; the run is abandoned.",
                ctx.ExecutionLogId);
            await MarkResumeFailedAsync(ctx, scope.Journal,
                $"{WorkflowAbandonmentReasons.ResumeValueInvalid}: {ex.Message}", cancellationToken).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        IReadOnlyDictionary<string, object?>? blockOutputs;
        IReadOnlyDictionary<string, object?>? auditOutputs = null;
        Exception? resumedFailure = null;
        try
        {
            var answer = startedARun || ctx.Wait is null ? null : WaitAnswer.Read(ctx.OutcomeJson);
            if (answer is { Unevaluable: { } because })
            {
                logger.LogError("The waker could not evaluate execution {ExecutionLogId}'s wait ({Reason}); the run is abandoned.",
                    ctx.ExecutionLogId, because);
                await MarkResumeFailedAsync(ctx, scope.Journal, $"{because}: the waker's answer for node '{ctx.BlockId}'",
                    cancellationToken).ConfigureAwait(false);
                return WorkflowResumeOutcome.Abandoned;
            }

            auditOutputs = answer?.Audited;
            resumedFailure = answer?.Fault;
            var byBlock = answer is not null && resumedFailure is null
                && answer.Audited.GetValueOrDefault(WorkflowResumeDecisions.OutcomeKey) is WorkflowWaitOutcomes.Satisfied or WorkflowWaitOutcomes.TimedOut
                    ? await BlockResumedOutputsAsync(ctx, rootWalk, executedOutputs, continuation, continuationRoot, inputNodeId,
                        pinnedVersion, workflow.CreatedByUserId, scope.Services, triggerIdForRun, triggerEnvelopeForRun, cancellationToken)
                        .ConfigureAwait(false)
                    : null;
            blockOutputs = startedARun || ctx.Wait is not { } wait || resumedFailure is not null
                ? null
                : byBlock
                    ?? (ctx.Kind == WorkflowSuspensionKind.Sleep
                        ? WorkflowDurableSleep.ResumedOutputs(ctx.OutcomeJson)
                        : WorkflowExecutionHelpers.BuildResumeBlockOutput(wait));
        }
        catch (WorkflowResumedFaultException ex)
        {
            logger.LogWarning(ex, "The block of execution {ExecutionLogId}'s resumed node failed to build its outputs; the node fails.",
                ctx.ExecutionLogId);
            blockOutputs = null;
            resumedFailure = ex;
        }
        catch (Exception ex) when (ex is JsonException or PersistedValueReadException or PersistedPayloadException)
        {
            logger.LogError(ex, "The outcome of execution {ExecutionLogId}'s wait does not read; the run is abandoned.", ctx.ExecutionLogId);
            await MarkResumeFailedAsync(ctx, scope.Journal,
                $"{WorkflowAbandonmentReasons.ResumeValueInvalid}: outcome, node '{ctx.BlockId}': {ex.Message}", cancellationToken)
                .ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        if (blockOutputs is not null)
        {
            var resumedNode = ResumedNode(rootWalk, continuation, continuationRoot, ctx.BlockId);
            if (resumedNode is not null)
            {
                try
                {
                    blockOutputs = ValueGate.Outputs(resumedNode, blockOutputs);
                }
                catch (WorkflowOutputTypeMismatchException ex)
                {
                    logger.LogWarning(ex, "The resumed outputs of execution {ExecutionLogId} are off their terms; the node fails.",
                        ctx.ExecutionLogId);
                    resumedFailure = ex;
                }
            }

            if (resumedFailure is not null)
            {
                blockOutputs = null;
            }
            else if (continuation.Count > 0)
            {
                if (resumedNode is null)
                {
                    logger.LogError("The level execution {ExecutionLogId} was parked on has no node '{BlockId}'; the run is abandoned.",
                        ctx.ExecutionLogId, ctx.BlockId);
                    await MarkResumeFailedAsync(ctx, scope.Journal,
                        $"{WorkflowAbandonmentReasons.ResumeValueInvalid}: frame, node '{ctx.BlockId}': the pinned program has no such node",
                        cancellationToken).ConfigureAwait(false);
                    return WorkflowResumeOutcome.Abandoned;
                }

                continuation = WorkflowContinuation.WithResumedNode(continuation, resumedNode, blockOutputs);
            }
            else
                executedOutputs[ctx.BlockId] = blockOutputs;
        }

        inputParameters = WorkflowExecutionHelpers.RunParametersOf(
            executedOutputs, inputNodeId ?? WorkflowGraphExecutor.InputNodeIdOf(definition));
        var resumeSeed = inputParameters ?? new Dictionary<string, object?>();
        foreach (var node in definition.Nodes.Where(n => n.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId))
        {
            if (!executedOutputs.ContainsKey(node.Id))
                executedOutputs[node.Id] = resumeSeed;
        }

        var compiled = rootWalk;
        using var callDepthScope = WorkflowCallDepthScope.Push(callDepth);

        if (stateStore != null && stateExecutionId.HasValue && ctx.Wait is not null
            && await NotParkedHereAsync(stateExecutionId.Value, ctx, scope.Journal, executionLog, cancellationToken)
                .ConfigureAwait(false) is { } elsewhere)
        {
            return elsewhere;
        }

        if (ctx.Kind == WorkflowSuspensionKind.Sleep && workflow.Mode == WorkflowMode.Restart
            && suspensions is IWorkflowParkedSleepStore restartSleeps
            && await restartSleeps.HasRunStartedSinceAsync(
                ctx.WorkflowId, ctx.ExecutionLogId, ctx.SuspendedAtUtc, cancellationToken).ConfigureAwait(false))
        {
            await AbandonSupersededRunAsync(ctx, scope, executionLog).ConfigureAwait(false);
            return WorkflowResumeOutcome.Abandoned;
        }

        if (ctx.Kind == WorkflowSuspensionKind.Sleep && coordinators is null && WorkflowDurableSleep.ParksIn(workflow.Mode))
        {
            logger.LogError(
                "execution {ExecutionLogId} stays parked: it sleeps in a {Mode} workflow, and this resumer was built "
                + "without the engine's coordinators to take its place from. Pass WorkflowEngine.Coordinators.",
                ctx.ExecutionLogId, workflow.Mode);
            return WorkflowResumeOutcome.Deferred;
        }

        var lease = coordinators is null
            ? null
            : await TryAcquireForResumeAsync(coordinators, workflow, cancellationToken).ConfigureAwait(false);
        WorkflowRunHeartbeat? heartbeat = null;
        try
        {
            var leaseToken = lease?.Token ?? CancellationToken.None;

            if (coordinators is not null && lease is null)
            {
                logger.LogDebug(
                    "execution {ExecutionLogId} left parked: workflow {WorkflowId} has no free place in its {Mode} budget.",
                    ctx.ExecutionLogId, ctx.WorkflowId, workflow.Mode);
                return WorkflowResumeOutcome.Deferred;
            }

            if (stateStore != null && stateExecutionId.HasValue
                && await (ctx.Row is { } claimed
                        ? FenceCheckpointAsync(stateExecutionId.Value, ctx, claimed)
                        : FenceAsync(stateExecutionId.Value, ctx, resumeVariablesJson, nested: continuation.Count > 0))
                    .ConfigureAwait(false)
                    is { } refused)
            {
                return refused;
            }

            if (ctx.Row is not null && stateStore != null && stateExecutionId.HasValue)
                await WorkflowRunEndings.CloseWaitsAsync(suspensions, ctx.ExecutionLogId, logger).ConfigureAwait(false);

            if (ctx.SleepWakesAt is { } wakesAt && stateExecutionId is { } asleep)
                WorkflowSleepResumption.Begin(asleep, ctx.BlockId, wakesAt);

            taken.Value = true;

            var resumeAuditKind = ctx.Kind is { } kind
                ? WorkflowResumeDecisions.ResolveResumeAuditKind(kind, auditOutputs ?? WorkflowSubRunOutcomes.Audited(subRunAnswer))
                : WorkflowAuditEventKinds.ReenteredByRecovery;

            try
            {
                await scope.Audit.WriteAsync(new WorkflowAuditEvent(
                    Kind: resumeAuditKind,
                    ActorId: null,
                    SubjectId: ctx.ExecutionLogId,
                    Data: new
                    {
                        suspensionId = ctx.SuspensionId,
                        executionId = ctx.ExecutionLogId,
                        workflowId = ctx.WorkflowId,
                        blockId = ctx.BlockId,
                        resumeReason = resumeAuditKind
                    }),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception auditEx)
            {
                logger.LogWarning(auditEx, "failed to write resume audit event.");
            }

            heartbeat = WorkflowRunHeartbeat.Start(
                options.HeartbeatInterval, options.RunRecoveryStore, stateStore, stateExecutionId, options.ReplicaId,
                lease, logger, options.TimeProvider);
            using var segment = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, leaseToken, heartbeat?.Token ?? CancellationToken.None);

            var retryPolicy = WorkflowRunAttempt.ParseRetryPolicy(workflow.RetryPolicyJson, JsonOptions, logger);
            var maxAttempts = WorkflowRunAttempt.AttemptsAllowed(retryPolicy);
            var timeoutSeconds = workflow.ExecutionTimeoutSeconds;

            using var checkpointPolicyScope = WorkflowCheckpointPolicyScope.Push(WorkflowCheckpointPolicy.For(
                workflow.CheckpointPolicyJson, options.EngineWriteMode, options.HeartbeatInterval,
                options.RecoveryAbandonedAfter, JsonOptions, logger));

            var mayParkSleep = options.ParkSleepWhenAtLeast is not null && suspensions is IWorkflowParkedSleepStore
                && stateStore is not null && stateExecutionId.HasValue
                && WorkflowDurableSleep.ParksIn(workflow.Mode)
                && !WorkflowDurableSleep.HoldsRunStateInProcess(timeoutSeconds, maxAttempts);

            for (var attemptNumber = 1; ; attemptNumber++)
            {
                WorkflowSuspension? persistedSleep = null;
                var logEntries = new List<WorkflowExecutionLogEntry>();
                var executionSteps = new List<WorkflowExecutionStep>();
                var attemptOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>(executedOutputs);
                var walkDone = false;
                WorkflowRunEnding? ending = null;
                CancellationTokenSource? timeoutCts = null;
                CancellationTokenSource? attempt = null;
                try
                {
                    var delayMs = WorkflowRunAttempt.BackoffDelayMs(retryPolicy, attemptNumber);
                    if (delayMs > 0)
                        await Task.Delay(delayMs, segment.Token).ConfigureAwait(false);
                    if (timeoutSeconds is > 0)
                        timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value));
                    attempt = CancellationTokenSource.CreateLinkedTokenSource(
                        segment.Token, timeoutCts?.Token ?? CancellationToken.None);

                    if (ctx.Kind == WorkflowSuspensionKind.Sleep && blockOutputs is not null
                        && definition.Nodes.FirstOrDefault(node => node.Id == ctx.BlockId) is { } sleepNode)
                    {
                        graphExecutor.RecordResumedNode(sleepNode, ctx.WorkflowId, ctx.ExecutionLogId, blockOutputs!,
                            ctx.SuspendedAtUtc, logEntries, executionSteps, traceMode);
                    }

                    if (stateExecutionId is { } writesFor)
                        WorkflowRunWrites.Begin(writesFor, recoverable: options.RecoveryReentersRuns,
                            rowNamesEntry: ctx.Row is not null || WorkflowRunWrites.Find(writesFor)?.RowNamesEntry == true,
                            caller: cancellationToken);

                    WorkflowSubRunResumeScope.Swap(
                        startedARun ? WorkflowSubRunOutcomes.Read(ctx.BlockId, ctx.OutcomeJson) : null);
                    WorkflowResumedFailure.Swap(resumedFailure is null
                        ? null
                        : new WorkflowResumedFailure.Armed(ctx.ExecutionLogId, ctx.BlockId, resumedFailure));

                    WorkflowRunContinuationScope.Swap(continuation.Count == 0 || continuationRoot is null
                        ? null
                        : new WorkflowRunContinuationScope.Armed(ctx.ExecutionLogId, continuationRoot, continuation));

                    Dictionary<string, IReadOnlyDictionary<string, object?>> allOutputs;
                    while (true)
                    {
                    try
                    {
                    allOutputs = await graphExecutor.ExecuteGraphAsync(
                        attemptOutputs, ctx.WorkflowId, ctx.ExecutionLogId,
                        workflow.CreatedByUserId,
                        scope.Services, logEntries, executionSteps, traceMode,
                        debugMode: false,
                        iterationIndex: null, callStackJson: null, depth: 0,
                        visitedNodeIds: [],
                        isInsideTryCatch: false,
                        inputParametersForRun: inputParameters,
                        triggerIdForRun: triggerIdForRun,
                        triggerEnvelopeForRun: triggerEnvelopeForRun,
                        cancellationToken: attempt.Token,
                        stateExecutionId: stateExecutionId,
                        debugHook: null,
                        debugCounter: null,
                        mayParkSleep: mayParkSleep,
                        parameterSignature: WorkflowExecutionHelpers.RootSignature(pinnedVersion.InputParametersJson),
                        compiled: compiled)
                        .ConfigureAwait(false);
                    break;
                    }
                    catch (SuspendedWorkflowException sleepEx) when (
                        sleepEx.SuspendResult.Kind == WorkflowSuspensionKind.Sleep && mayParkSleep && suspensions is not null)
                    {
                        var pre = await WorkflowDurableSleep.PreParkAsync(new WorkflowDurableSleep.PreParkRequest
                        {
                            Suspensions = suspensions,
                            ParkedSleeps = suspensions as IWorkflowParkedSleepStore,
                            StateStore = stateStore!,
                            Heartbeat = heartbeat,
                            Sleep = sleepEx,
                            WorkflowId = ctx.WorkflowId,
                            ExecutionLogId = ctx.ExecutionLogId,
                            StateExecutionId = stateExecutionId,
                            ReplicaId = options.ReplicaId,
                            ExecutionSteps = executionSteps,
                            StateRowTimeout = options.StateRowTimeout
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
                    var resumeOutputs = WorkflowExecutionHelpers.BuildResultOutputs(allOutputs, compiled);
                    walkDone = true;

                    executionLog.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow,
                        outputsJson: PayloadSanitizer.SerializeAndSanitize(resumeOutputs, options.MaxPayloadValueBytes));
                    if (await ShouldStoreRawResultForParentAsync(stateExecutionId, payload.ParentExecutionId).ConfigureAwait(false))
                        executionLog.RecordResultForParent(SerializeRawResult(resumeOutputs, compiled));
                    await scope.Journal.SaveRunDetailsAsync(
                        ctx.ExecutionLogId, recordLogEntries: false, logEntries, executionSteps, CancellationToken.None).ConfigureAwait(false);
                    ending = WorkflowRunEnding.Decide(await ExitAsync(null).ConfigureAwait(false));
                }
                catch (SuspendedWorkflowException suspEx)
                {
                    WorkflowRunParking.Result park;
                    try
                    {
                        await scope.Journal.SaveRunDetailsAsync(
                            ctx.ExecutionLogId, recordLogEntries: false, logEntries, executionSteps, CancellationToken.None).ConfigureAwait(false);

                        park = await WorkflowRunParking.ParkAsync(new WorkflowRunParking.Request
                        {
                            Persisted = persistedSleep,
                            RowAlreadyParked = persistedSleep is not null,
                            StateStore = stateStore,
                            Suspensions = suspensions,
                            ParkSignal = options.ParkSignal,
                            Heartbeat = heartbeat,
                            ReplicaId = options.ReplicaId,
                            StateExecutionId = stateExecutionId,
                            ParentExecutionId = payload.ParentExecutionId,
                            ExecutionLogId = ctx.ExecutionLogId,
                            WorkflowId = ctx.WorkflowId,
                            Suspended = suspEx,
                            VariablesJson = resumeVariablesJson
                        }, logger).ConfigureAwait(false);
                    }
                    catch (Exception parkEx)
                    {
                        park = new WorkflowRunParking.Result(false, parkEx);
                    }

                    if (park.Parked)
                    {
                        logger.LogInformation(
                            "workflow {WorkflowId} re-suspended at block {BlockId} after resume.", ctx.WorkflowId, suspEx.BlockId);
                        reparkedKind = suspEx.SuspendResult.Kind;
                        metricsResult = WorkflowRunStatusLabels.Succeeded;
                        CountTheSegment();
                        return WorkflowResumeOutcome.Resumed;
                    }

                    ending = WorkflowRunEnding.Decide(await ExitAsync(park.Refusal).ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    ending = WorkflowRunEnding.Decide(await ExitAsync(ex).ConfigureAwait(false));
                }
                finally
                {
                    attempt?.Dispose();
                    timeoutCts?.Dispose();
                }

                async Task<WorkflowRunExit> ExitAsync(Exception? thrown)
                {
                    var callerCancelled = cancellationToken.IsCancellationRequested;
                    var hostStopping = options.HostStopping.IsCancellationRequested;
                    var budgetLost = heartbeat?.BudgetLost == true;
                    var mayHandOver = options.HandOverOnStop && stateStore != null && stateExecutionId.HasValue
                        && !walkDone && ((callerCancelled && hostStopping) || budgetLost)
                        && !await DispatchedByAnotherRunAsync(stateExecutionId.Value).ConfigureAwait(false);
                    return new WorkflowRunExit
                    {
                        WorkflowId = ctx.WorkflowId,
                        RunRow = stateStore is null ? null : stateExecutionId,
                        Thrown = thrown,
                        WalkDone = walkDone,
                        RowLostByBeat = heartbeat?.RowLoss,
                        BudgetLostByBeat = budgetLost,
                        CallerCancelled = callerCancelled,
                        HostStopping = hostStopping,
                        LeaseCancelled = leaseToken.IsCancellationRequested,
                        TimeoutFired = timeoutCts?.IsCancellationRequested == true,
                        TimeoutSeconds = timeoutSeconds,
                        Mode = lease?.Mode ?? workflow.Mode,
                        MayHandOver = mayHandOver,
                        AttemptNumber = attemptNumber,
                        MaxAttempts = maxAttempts
                    };
                }

                metricsResult = ending.MetricsLabel;
                if (ending.TryAgain)
                {
                    lastException = ending.Error;
                    await WorkflowRunEndings.QuietlyAsync(() => scope.Journal.SaveRunDetailsAsync(
                            ctx.ExecutionLogId, recordLogEntries: false, logEntries, executionSteps, CancellationToken.None),
                        logger, "save the failed attempt of", ctx.ExecutionLogId).ConfigureAwait(false);
                    logger.LogWarning(ending.Error,
                        "resumed segment of execution {ExecutionLogId} failed on attempt {Attempt} of {MaxAttempts}; trying it again.",
                        ctx.ExecutionLogId, attemptNumber, maxAttempts);
                    continue;
                }

                await (heartbeat?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
                ending = await WorkflowRunEndings.EndRowAsync(ending, stateStore, stateExecutionId, options.ReplicaId, logger)
                    .ConfigureAwait(false);
                metricsResult = ending.MetricsLabel;
                if (ending.ClosesWaits)
                    await WorkflowRunEndings.CloseWaitsAsync(suspensions, ctx.ExecutionLogId, logger).ConfigureAwait(false);

                if (ending.Phase == WorkflowRunPhase.Finished)
                {
                    lastException = null;
                    if (ending.Error is not null)
                    {
                        logger.LogError(ending.Error,
                            "execution {ExecutionLogId} finished its resumed segment, but what it did could not all be recorded.",
                            ctx.ExecutionLogId);
                        if (executionLog.Status == WorkflowExecutionStatus.Running)
                            executionLog.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow);
                    }
                    break;
                }

                lastException = ending.Phase is WorkflowRunPhase.Stopped or WorkflowRunPhase.Cancelled
                    or WorkflowRunPhase.HandedOver or WorkflowRunPhase.Superseded
                    ? null
                    : ending.Error is WorkflowNodeExecutionException { InnerException: { } cause } ? cause : ending.Error;
                if (await WorkflowRunEndings.EntryGoesOnAsync(ending, stateStore, stateExecutionId, ctx.ExecutionLogId, logger)
                        .ConfigureAwait(false))
                {
                    if (ending.Phase == WorkflowRunPhase.HandedOver && ending.JournalMessage is { } handedOver)
                        executionLog.NoteHandedOver(handedOver);
                }
                else
                {
                    executionLog.Complete(ending.JournalStatus, DateTimeOffset.UtcNow, ending.JournalMessage);
                }

                await WorkflowRunEndings.QuietlyAsync(() => scope.Journal.SaveRunDetailsAsync(
                        ctx.ExecutionLogId, recordLogEntries: false, logEntries, executionSteps, CancellationToken.None),
                    logger, "save the ending of", ctx.ExecutionLogId).ConfigureAwait(false);
                break;
            }

            try
            {
                await scope.Journal.SaveRunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception saveEx)
            {
                logger.LogError(saveEx,
                    "failed to save execution log completion for {ExecutionLogId}.",
                    ctx.ExecutionLogId);
            }

            CountTheSegment();

            if (lastException != null)
            {
                logger.LogError(lastException,
                    "workflow {WorkflowId} execution {ExecutionLogId} failed after resume.",
                    ctx.WorkflowId, ctx.ExecutionLogId);
            }

            return WorkflowResumeOutcome.Resumed;

            void CountTheSegment()
            {
                resumedFor.Stop();
                try
                {
                    if (ctx.Kind is { } resolved)
                        metrics.RecordSuspensionResolved(
                            resolved.ToString(), metricsResult, (DateTimeOffset.UtcNow - ctx.SuspendedAtUtc).TotalSeconds);

                    if (reparkedKind is { } kind) metrics.RecordSuspensionStarted(kind.ToString());
                    else metrics.RecordExecutionCompleted(metricsResult, resumedFor.Elapsed.TotalSeconds);
                }
                catch (Exception metricEx)
                {
                    logger.LogWarning(metricEx, "execution {ExecutionLogId}: the ending of a resumed segment could not be counted.",
                        ctx.ExecutionLogId);
                }
            }
        }
        finally
        {
            try
            {
                if (heartbeat is not null)
                    await heartbeat.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (lease is not null)
                    await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> DispatchedByAnotherRunAsync(Guid stateExecutionId)
    {
        try
        {
            var row = await stateStore!.GetAsync(stateExecutionId, CancellationToken.None).ConfigureAwait(false);
            return row?.ParentExecutionId is not null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read whether run state row {StateExecutionId} was dispatched by another run; it is handed over.",
                stateExecutionId);
            return false;
        }
    }

    private async Task<bool> ShouldStoreRawResultForParentAsync(Guid? stateExecutionId, Guid? payloadParentExecutionId)
    {
        if (stateStore is null || stateExecutionId is null)
            return payloadParentExecutionId is not null;

        try
        {
            var row = await stateStore.GetAsync(stateExecutionId.Value, CancellationToken.None).ConfigureAwait(false);
            return row is null ? payloadParentExecutionId is not null : row.ParentExecutionId is not null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read whether run state row {StateExecutionId} was dispatched; preserving its result for the parent.",
                stateExecutionId);
            return true;
        }
    }

    private static string? SerializeRawResult(IReadOnlyDictionary<string, object?> resultOutputs, CompiledWalk compiled)
    {
        try { return PersistedValueCodec.WriteAnswer(resultOutputs, compiled.Canvas.OutputTypes); }
        catch (Exception ex) when (ExpressionJsonValue.Unwritable(ex)) { return null; }
    }

    private async Task<WorkflowResumeOutcome?> NotParkedHereAsync(
        Guid stateExecutionId, Entry ctx, IWorkflowRunJournal journal, WorkflowExecutionLog executionLog,
        CancellationToken cancellationToken)
    {
        var mayDefer = MayDefer(ctx.Wait!.Kind);

        WorkflowRunStateRow? row;
        try
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            read.CancelAfter(options.StateRowTimeout);
            row = await stateStore!.GetAsync(stateExecutionId, read.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read run state row {StateExecutionId} before resuming; {Answer}.",
                stateExecutionId, mayDefer ? "this pass tries again later" : "the swap decides");
            return mayDefer ? WorkflowResumeOutcome.Deferred : null;
        }

        if (row is null || (row.Status == WorkflowRunStateStatus.Suspended && row.CurrentBlockPath == ctx.BlockId))
            return null;

        if (row.Status == WorkflowRunStateStatus.Running && row.CurrentBlockPath == ctx.BlockId)
        {
            logger.LogInformation(
                "execution {ExecutionLogId} is not resumed yet: its state row is still Running at wait {Wait} in the name "
                + "of {Owner}, a park landing or a resume starting.", ctx.ExecutionLogId, ctx.BlockId,
                row.OwnedByReplicaId ?? "no replica");
            return mayDefer ? WorkflowResumeOutcome.Deferred : WorkflowResumeOutcome.HeldElsewhere;
        }

        if (row.Status is WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed or WorkflowRunStateStatus.Cancelled)
        {
            logger.LogInformation(
                "execution {ExecutionLogId} is not resumed: its state row ended {Status}, so its entry and its waits are closed.",
                ctx.ExecutionLogId, row.Status);
            await WorkflowRunEndings.CloseWaitsAsync(suspensions, ctx.ExecutionLogId, logger).ConfigureAwait(false);
            if (row.Status == WorkflowRunStateStatus.Completed && row.FailureReason is null)
            {
                executionLog.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow);
            }
            else
            {
                executionLog.Complete(
                    row.Status == WorkflowRunStateStatus.Failed ? WorkflowExecutionStatus.Failed : WorkflowExecutionStatus.Cancelled,
                    DateTimeOffset.UtcNow,
                    $"{RunStateEndedReason}: the run's state row ended {row.Status} ({row.FailureReason ?? "no reason"}) "
                    + "while this entry was still running; the run was replayed under another entry or ended by a recovery.");
            }
            await WorkflowRunEndings.QuietlyAsync(() => journal.SaveRunAsync(CancellationToken.None),
                logger, "close the entry of", ctx.ExecutionLogId).ConfigureAwait(false);
            return WorkflowResumeOutcome.HeldElsewhere;
        }

        logger.LogInformation(
            "execution {ExecutionLogId} is not resumed: its state row is {Status} at {Block} in the name of {Owner}, not "
            + "parked at {Wait}.", ctx.ExecutionLogId, row.Status, row.CurrentBlockPath, row.OwnedByReplicaId ?? "no replica",
            ctx.BlockId);
        return WorkflowResumeOutcome.HeldElsewhere;
    }

    private async Task AbandonSupersededRunAsync(Entry ctx, IWorkflowRunScope scope, WorkflowExecutionLog executionLog)
    {
        const string Superseded = "Superseded by a newer run in Restart mode while parked on a sleep.";
        logger.LogInformation(
            "execution {ExecutionLogId} was superseded by a newer Restart run while its sleep was being taken; it is not resumed.",
            ctx.ExecutionLogId);

        var stateExecutionId = WorkflowDurableSleep.StateExecutionIdOf(ctx.StatePayloadJson);
        if (await TerminalizeAsync(stateExecutionId, WorkflowRunStateStatus.Cancelled, null).ConfigureAwait(false)
            || stateExecutionId is null)
        {
            graphExecutor.NotifyParkedNodeEnded(ctx.WorkflowId, ctx.ExecutionLogId, ctx.BlockId, ctx.SuspendedAtUtc,
                "Cancelled: " + Superseded);
            if (ctx.Kind is { } kind)
                metrics.RecordSuspensionResolved(kind.ToString(), WorkflowRunStatusLabels.Cancelled,
                    (DateTimeOffset.UtcNow - ctx.SuspendedAtUtc).TotalSeconds);
        }

        try
        {
            executionLog.Complete(WorkflowExecutionStatus.Cancelled, DateTimeOffset.UtcNow, Superseded);
            await scope.Journal.SaveRunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to record superseded execution {ExecutionLogId} as cancelled.", ctx.ExecutionLogId);
            return;
        }

        if (suspensions is IWorkflowParkedSleepStore parkedSleeps)
        {
            try
            {
                await parkedSleeps.CancelParkedSleepAsync(ctx.SuspensionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "failed to cancel the sleep row of superseded execution {ExecutionLogId}.", ctx.ExecutionLogId);
            }
        }
    }

    private static bool MayDefer(WorkflowSuspensionKind kind) =>
        kind is not (WorkflowSuspensionKind.WaitForTrigger or WorkflowSuspensionKind.Predicate);

    /// <summary>What the journal entry of a run says when a resume found the run's state row already ended.</summary>
    public const string RunStateEndedReason = WorkflowAbandonmentReasons.RunStateEnded;

    private async Task<WorkflowResumeOutcome?> FenceAsync(
        Guid stateExecutionId, Entry ctx, string variablesJson, bool nested)
    {
        var sleep = ctx.Kind == WorkflowSuspensionKind.Sleep;
        var parkToken = sleep || nested
            ? WorkflowDurableSleep.ParkToken(ctx.SuspensionId)
            : null;

        using var fence = new CancellationTokenSource(options.StateRowTimeout);
        var fenceToken = fence.Token;

        bool moved;
        var swapUnknown = false;
        try
        {
            moved = await stateStore!.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = stateExecutionId,
                FromStatus = WorkflowRunStateStatus.Suspended,
                ToStatus = WorkflowRunStateStatus.Running,
                CurrentBlockPath = ctx.BlockId,
                VariablesJson = variablesJson,
                WaitForEventKey = parkToken is null ? null : WorkflowDurableSleep.TakenToken(ctx.SuspensionId),
                OwnedByReplicaId = options.ReplicaId,
                ExpectedWaitForEventKey = parkToken,
                ExpectedCurrentBlockPath = ctx.BlockId
            }, fenceToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to move run state row {StateExecutionId} from Suspended to Running before resuming.",
                stateExecutionId);
            if (!sleep)
                return null;
            moved = false;
            swapUnknown = true;
        }

        if (moved)
            return null;

        WorkflowRunStateRow? row;
        try
        {
            using var readBack = new CancellationTokenSource(options.StateRowTimeout);
            row = await stateStore.GetAsync(stateExecutionId, readBack.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Run state row {StateExecutionId} refused the resume and could not be read back; execution "
                + "{ExecutionLogId} {Answer}.", stateExecutionId, ctx.ExecutionLogId,
                MayDefer(ctx.Wait!.Kind) ? "stays parked for a later pass" : "walks on");
            return MayDefer(ctx.Wait!.Kind) ? WorkflowResumeOutcome.Deferred : null;
        }

        if (row is null)
            return null;

        if (sleep && row is { Status: WorkflowRunStateStatus.Running }
            && row.WaitForEventKey == WorkflowDurableSleep.TakenToken(ctx.SuspensionId)
            && row.OwnedByReplicaId == options.ReplicaId)
        {
            return null;
        }

        if (sleep && swapUnknown && row is { Status: WorkflowRunStateStatus.Running, WaitForEventKey: null }
            && row.CurrentBlockPath == ctx.BlockId && row.OwnedByReplicaId == options.ReplicaId)
        {
            return null;
        }

        if (sleep && row is { Status: WorkflowRunStateStatus.Suspended }
            && row.CurrentBlockPath == ctx.BlockId
            && (row.WaitForEventKey == parkToken || swapUnknown && row.WaitForEventKey is null))
        {
            logger.LogInformation(
                "execution {ExecutionLogId} stays parked: its state row is still parked on this take{Why}.",
                ctx.ExecutionLogId, swapUnknown ? " and the swap's answer was lost" : string.Empty);
            return WorkflowResumeOutcome.Deferred;
        }

        if (parkToken is not null && row is { Status: WorkflowRunStateStatus.Suspended, WaitForEventKey: null }
            && row.CurrentBlockPath == ctx.BlockId)
        {
            if (Interlocked.Exchange(ref warnedOfNoFence, 1) == 0)
            {
                logger.LogWarning(
                    "Run state row {StateExecutionId} is parked without its park token — an IWorkflowRunStateStore that does "
                    + "not keep WaitForEventKey, or a park written before the release that writes one — so resumes of "
                    + "parked sleeps and of waits below the root are fenced by the node alone.", stateExecutionId);
            }

            return await FenceUnfencedAsync(stateExecutionId, ctx, variablesJson).ConfigureAwait(false);
        }

        logger.LogInformation(
            "execution {ExecutionLogId} is not resumed: its state row is {Status} in the name of {Owner}, so "
            + "somebody else has the run.", ctx.ExecutionLogId, row.Status, row.OwnedByReplicaId ?? "no replica");
        return WorkflowResumeOutcome.HeldElsewhere;
    }

    private async Task<WorkflowResumeOutcome?> FenceUnfencedAsync(
        Guid stateExecutionId, Entry ctx, string variablesJson)
    {
        try
        {
            using var fence = new CancellationTokenSource(options.StateRowTimeout);
            var moved = await stateStore!.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = stateExecutionId,
                FromStatus = WorkflowRunStateStatus.Suspended,
                ToStatus = WorkflowRunStateStatus.Running,
                CurrentBlockPath = ctx.BlockId,
                VariablesJson = variablesJson,
                WaitForEventKey = WorkflowDurableSleep.TakenToken(ctx.SuspensionId),
                OwnedByReplicaId = options.ReplicaId,
                ExpectedCurrentBlockPath = ctx.BlockId
            }, fence.Token).ConfigureAwait(false);

            return moved ? null : WorkflowResumeOutcome.HeldElsewhere;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to move run state row {StateExecutionId} from Suspended to Running before resuming.", stateExecutionId);
            return null;
        }
    }

    private int warnedOfNoFence;

    private async Task<bool> TerminalizeAsync(
        Guid? stateExecutionId, WorkflowRunStateStatus terminalStatus, string? failureReason)
    {
        if (stateStore == null || !stateExecutionId.HasValue)
            return false;

        try
        {
            return await stateStore.CompleteAsync(stateExecutionId.Value, terminalStatus, failureReason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to end run state row {StateExecutionId} as {Status}.",
                stateExecutionId.Value, terminalStatus);
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<string, object?>?> BlockResumedOutputsAsync(
        Entry ctx, CompiledWalk rootWalk, Dictionary<string, IReadOnlyDictionary<string, object?>> rootOutputs,
        IReadOnlyList<WorkflowContinuationFrame> frames, string? rootNode, string? inputNodeId, WorkflowVersion pinnedVersion,
        Guid userId, IServiceProvider services, Guid? triggerId, TriggerEnvelope? envelope, CancellationToken cancellationToken)
    {
        if (ResumedNode(rootWalk, frames, rootNode, ctx.BlockId) is not { } node)
            return null;
        return await graphExecutor.ResumedOutputsAsync(node, ctx.OutcomeJson, ctx.ExecutionLogId, userId, services, () =>
            {
                var (_, outputs, parameters) = ParkedScope.Of(
                    rootWalk, rootOutputs, frames, rootNode ?? ctx.BlockId, ctx.BlockId, inputNodeId);
                return (outputs, parameters);
            }, triggerId, envelope, cancellationToken).ConfigureAwait(false);
    }

    private static CompiledNode? ResumedNode(
        CompiledWalk root, IReadOnlyList<WorkflowContinuationFrame> frames, string? rootNode, string blockId) =>
        CompiledWalk.LevelOf(root, frames, rootNode)?.Node(blockId);

    private async Task MarkResumeFailedAsync(
        Entry ctx, IWorkflowRunJournal journal, string reason, CancellationToken cancellationToken)
    {
        var stateExecutionId = WorkflowDurableSleep.StateExecutionIdOf(ctx.StatePayloadJson);
        if ((await TerminalizeAsync(stateExecutionId, WorkflowRunStateStatus.Failed, Truncate(reason, 400)).ConfigureAwait(false)
                || stateExecutionId is null)
            && ctx.Kind == WorkflowSuspensionKind.Sleep)
        {
            graphExecutor.NotifyParkedNodeEnded(ctx.WorkflowId, ctx.ExecutionLogId, ctx.BlockId, ctx.SuspendedAtUtc,
                "Failed: the run could not be resumed after its sleep. " + reason);
            metrics.RecordSuspensionResolved(ctx.Wait!.Kind.ToString(), WorkflowRunStatusLabels.Failed,
                (DateTimeOffset.UtcNow - ctx.SuspendedAtUtc).TotalSeconds);
        }

        try
        {
            var log = await journal.LoadRunAsync(ctx.ExecutionLogId, cancellationToken).ConfigureAwait(false);
            if (log == null)
                return;

            log.Complete(WorkflowExecutionStatus.Failed, DateTimeOffset.UtcNow, Truncate(reason, 2000));
            await journal.SaveRunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;

    private async Task<WorkflowResumeOutcome?> FenceCheckpointAsync(Guid stateExecutionId, Entry ctx, WorkflowRunStateRow claimed)
    {
        try
        {
            using var fence = new CancellationTokenSource(options.StateRowTimeout);
            var moved = await stateStore!.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = stateExecutionId,
                FromStatus = claimed.Status,
                ToStatus = WorkflowRunStateStatus.Running,
                CurrentBlockPath = ctx.BlockId,
                VariablesJson = claimed.VariablesJson,
                OwnedByReplicaId = options.ReplicaId,
                ExpectedOwnedByReplicaId = options.ReplicaId,
                ExpectedCurrentBlockPath = claimed.CurrentBlockPath
            }, fence.Token).ConfigureAwait(false);
            if (moved)
                return null;

            logger.LogInformation(
                "execution {ExecutionLogId} is not re-entered: its state row is no longer this replica's at {Block}.",
                ctx.ExecutionLogId, claimed.CurrentBlockPath);
            return WorkflowResumeOutcome.HeldElsewhere;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not take run state row {StateExecutionId} back to re-enter it; a later sweep tries again.",
                stateExecutionId);
            return WorkflowResumeOutcome.Deferred;
        }
    }

    private sealed record Entry
    {
        public required Guid WorkflowId { get; init; }
        public required Guid ExecutionLogId { get; init; }
        public required string BlockId { get; init; }
        public required string StatePayloadJson { get; init; }
        public required DateTimeOffset SuspendedAtUtc { get; init; }

        public ResumeContext? Wait { get; init; }

        public WorkflowRunStateRow? Row { get; init; }

        public DateTimeOffset? SleepWakesAt { get; init; }

        public Guid SuspensionId => Wait?.SuspensionId ?? Guid.Empty;
        public WorkflowSuspensionKind? Kind => Wait?.Kind;
        public string OutcomeJson => Wait?.OutcomeJson ?? "{}";

        public static Entry Of(ResumeContext wait) => new()
        {
            WorkflowId = wait.WorkflowId,
            ExecutionLogId = wait.ExecutionLogId,
            BlockId = wait.BlockId,
            StatePayloadJson = wait.StatePayloadJson,
            SuspendedAtUtc = wait.SuspendedAtUtc,
            Wait = wait
        };
    }
}

/// <summary>What a resuming run needs to know about the host it is resuming on.</summary>
public sealed record WorkflowResumeOptions
{
    /// <summary>Signals the local waker after this resumer parks a run.</summary>
    public WorkflowSuspensionParkSignal? ParkSignal { get; init; }
    /// <summary>How much of a run's steps to record.</summary>
    public WorkflowTraceMode TraceMode { get; init; }

    /// <summary>The cap on one serialised value in a persisted payload, past which it is replaced by a note
    /// saying so. A workflow's outputs are not bounded by anything else.</summary>
    public int MaxPayloadValueBytes { get; init; }

    /// <summary>Which replica this is, so a parked row records an owner.</summary>
    public string? ReplicaId { get; init; }

    /// <summary>Whether a resumed segment its host stops is left for a peer rather than ended. See
    /// <see cref="WorkflowEngineSettings.HandOverRunsOnStop"/>.</summary>
    public bool HandOverOnStop { get; init; }

    /// <summary>Whether a recovery sweep re-enters an abandoned run from its checkpoint. See
    /// <see cref="WorkflowEngineSettings.RecoveryReentersRuns"/>.</summary>
    public bool RecoveryReentersRuns { get; init; }

    /// <summary>Fires when the host begins to stop. See <see cref="WorkflowEngineSeams.HostStopping"/>.</summary>
    public CancellationToken HostStopping { get; init; }

    /// <summary>How often a resumed segment renews its claim. Null, or no <see cref="RunRecoveryStore"/>, renews nothing.</summary>
    public TimeSpan? HeartbeatInterval { get; init; }

    /// <summary>How often a resumed segment writes its row where its workflow says nothing. See
    /// <see cref="WorkflowEngineSettings.RunStateWriteMode"/>.</summary>
    public RunStateWriteMode EngineWriteMode { get; init; } = RunStateWriteMode.EveryBlock;

    /// <summary>See <see cref="WorkflowEngineSettings.RecoveryAbandonedAfter"/>.</summary>
    public TimeSpan RecoveryAbandonedAfter { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Where the heartbeat renews the row, on the clock the recovery sweep reads.</summary>
    public IWorkflowRunRecoveryStore? RunRecoveryStore { get; init; }

    /// <summary>The clock the heartbeat waits on between beats. Null is the system clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>The threshold at which a resumed segment's own sleep parks rather than waiting in this process.
    /// Null parks nothing. See <see cref="WorkflowEngineSettings.ParkSleepWhenAtLeast"/>.</summary>
    public TimeSpan? ParkSleepWhenAtLeast { get; init; }

    /// <summary>How long one of a sleep park's reads or writes may take before it counts as failed.</summary>
    public TimeSpan StateRowTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
