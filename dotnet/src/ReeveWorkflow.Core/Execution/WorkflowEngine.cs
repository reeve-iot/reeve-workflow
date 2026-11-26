using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>The engine, assembled: the graph walk, a runner per call, and one coordinator per workflow for the
/// life of the process.</summary>
/// <remarks>Composition and nothing else. It decides nothing a seam could answer, which is why a host that
/// wants a different engine fills a slot rather than replacing this class.</remarks>
public sealed class WorkflowEngine : IWorkflowEngine, IWorkflowRunReentry, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = WorkflowGraphHelpers.JsonOptions;

    private readonly WorkflowEngineSeams seams;
    private readonly IWorkflowRestartGenerations restartGenerations;
    private readonly WorkflowEngineSettings settings;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly WorkflowGraphExecutor graphExecutor;

    private readonly TimeSpan? walkParkSleepWhenAtLeast;
    private readonly WorkflowActivitySource activitySource;

    private readonly ConcurrentDictionary<Guid, WorkflowRunCoordinator> coordinators = new();
    private readonly Dictionary<Guid, DateTimeOffset> coordinatorLastUsed = new();
    private readonly object coordinatorLock = new();
    private readonly CoordinatorPool pool;
    private readonly IDisposable? restartCancelSubscription;
    private readonly ITimer coordinatorCleanupTimer;
    private readonly TimeProvider clock;
    private bool disposed;

    /// <param name="seams">Every seam the runner and the graph walk are handed, resolved once. Resolving them
    /// per run would hand the library a different object each time and make the slots decorative.</param>
    /// <param name="settings">What this deployment knows about itself and how much of a run it records.</param>
    /// <param name="loggerFactory">Where the engine and the graph walk write.</param>
    /// <param name="activitySource">Where its spans go. Null publishes under the library's own name; a host
    /// that renames it owns the source it passes, this engine only borrows it.</param>
    /// <param name="walkOptions">The options the graph walk runs on. Null derives them from
    /// <paramref name="settings"/>; a host that registers its own passes that object, and then the engine and
    /// the resumer are reading one thing rather than two copies of it.</param>
    public WorkflowEngine(
        WorkflowEngineSeams seams, WorkflowEngineSettings settings, ILoggerFactory loggerFactory,
        WorkflowActivitySource? activitySource = null, WorkflowGraphExecutorOptions? walkOptions = null)
    {
        this.seams = seams ?? throw new ArgumentNullException(nameof(seams));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        RefuseADeclaredClusterWithNoSeams(seams, settings);
        restartGenerations = seams.RestartGenerations ?? new InProcessRestartGenerations();
        clock = settings.TimeProvider ?? TimeProvider.System;
        if (settings.CoordinatorIdleTimeout < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(settings.CoordinatorIdleTimeout),
                "CoordinatorIdleTimeout must be at least one second.");

        logger = loggerFactory.CreateLogger(settings.LoggerCategory);
        this.activitySource = activitySource ?? WorkflowActivitySource.Default;
        pool = new CoordinatorPool(this);
        var cleanupInterval = settings.CoordinatorIdleTimeout < TimeSpan.FromMinutes(1)
            ? settings.CoordinatorIdleTimeout
            : TimeSpan.FromMinutes(1);
        var walk = walkOptions ?? WorkflowGraphExecutorOptions.For(settings);
        walkParkSleepWhenAtLeast = walk.ParkSleepWhenAtLeast;
        graphExecutor = new WorkflowGraphExecutor(
            seams.Blocks,
            loggerFactory,
            seams.Metrics,
            seams.NodeObserver,
            seams.HelperResolver ?? NullHelperResolver.Instance,
            walk,
            this.activitySource,
            seams.BlockSandbox,
            new WorkflowTransitionRecorder(
                seams.RunStateStore,
                logger,
                new WorkflowTransitionOptions
                {
                    ReplicaId = settings.ReplicaId,
                    AuditTransitions = settings.AuditTransitions
                },
                seams.AuditSink),
            seams.RunStateStore is null
                ? null
                : new WorkflowSleepRunner(
                    seams.RunStateStore,
                    logger,
                    new WorkflowSleepOptions
                    {
                        CancellationPollInterval = settings.SleepPollInterval,
                        ReplicaId = settings.ReplicaId,
                        TimeProvider = walk.TimeProvider,
                        HostStopping = seams.HostStopping
                    }));
        restartCancelSubscription = seams.ClusterSignal?.Subscribe(
            settings.RestartCancelChannel, (id, payload, _) =>
            {
                if (coordinators.TryGetValue(id, out var coordinator))
                    coordinator.ApplyRestartCancel(id, payload);
                return Task.CompletedTask;
            });
        if (ExecutionContext.IsFlowSuppressed())
            coordinatorCleanupTimer = clock.CreateTimer(static state => ((WorkflowEngine)state!).SweepIdleCoordinators(), this, cleanupInterval, cleanupInterval);
        else
            using (ExecutionContext.SuppressFlow())
                coordinatorCleanupTimer = clock.CreateTimer(static state => ((WorkflowEngine)state!).SweepIdleCoordinators(), this, cleanupInterval, cleanupInterval);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        Guid workflowId,
        WorkflowFire? fire,
        IReadOnlyDictionary<string, object?>? inputParameters,
        Guid? parentExecutionId = null,
        int? iterationIndex = null,
        string? callStackJson = null,
        CancellationToken cancellationToken = default) =>
        RunnerFor().ExecuteAsync(
            workflowId, fire, inputParameters, parentExecutionId, iterationIndex, callStackJson, cancellationToken);

    private WorkflowRunner RunnerFor(bool? handOverOnStop = null, bool replaysAdmittedRun = false) =>
        new WorkflowRunner(
                seams.RunScopes,
                graphExecutor,
                seams.Blocks,
                seams.Compiler,
                seams.ExecutionGate,
                seams.Metrics,
                logger,
                new WorkflowRunnerOptions
                {
                    TraceMode = settings.TraceMode,
                    MaxPayloadValueBytes = settings.MaxPayloadValueBytes,
                    ReplicaId = settings.ReplicaId,
                    IsMultiReplica = settings.IsMultiReplica,
                    FireDedupWindow = settings.FireDedupWindow,
                    ParkSleepWhenAtLeast = walkParkSleepWhenAtLeast,
                    HandOverOnStop = handOverOnStop ?? settings.HandOverRunsOnStop,
                    RecoveryReentersRuns = settings.RecoveryReentersRuns,
                    HostStopping = seams.HostStopping,
                    HeartbeatInterval = settings.RunHeartbeatInterval,
                    RunRecoveryStore = seams.RunRecoveryStore,
                    TimeProvider = clock,
                    EngineWriteMode = settings.RunStateWriteMode,
                    RecoveryAbandonedAfter = settings.RecoveryAbandonedAfter,
                    ParkSignal = seams.ParkSignal,
                    ReplaysAdmittedRun = replaysAdmittedRun
                },
                seams.RunStateStore,
                seams.SuspensionStore,
                seams.DebugSessions,
                seams.FireDedup,
                pool,
                activitySource,
                seams.StartAdmission,
                seams.NodeValidator,
                seams.StartArgumentValidator);

    /// <inheritdoc />
    /// <remarks>Re-executes the workflow from the start on the version the abandoned run was pinned to — every
    /// block again, not the one it stopped at; <see cref="IsRunReplayableAsync"/> is what makes that safe. The
    /// input parameters it started with are NOT restored: the row's variable snapshot is the live scope at the
    /// last block it reached, not what it was called with, so a workflow reading <c>${input.*}</c> sees none.
    /// Whether the run was admitted at all is <see cref="TryResumeAsync"/>.</remarks>
    public Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default) =>
        TryResumeAsync(executionId, workflowId, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Every ending the replay reaches once it has a row of its own is an answer, a wait it could not persist
    /// included. So a throw while the caller is stopping is a replay stopped before that row, and is answered as
    /// stopped. A replay stopped whose own row could not be ended is answered as finished: that row carries the run and
    /// is replayed in turn, and the old one must not be as well.</remarks>
    public async Task<WorkflowReplayOutcome> ReplayAbandonedRunAsync(
        Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
    {
        WorkflowRunResult result;
        try
        {
            var pin = await ReadPinAsync(executionId, workflowId, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Replaying workflow {WorkflowId} from abandoned run {ExecutionId}.", workflowId, executionId);

            using var pinScope = WorkflowRunPinScope.Push(pin);
            result = await RunnerFor(handOverOnStop: false, replaysAdmittedRun: true).RunAsync(
                workflowId, fire: null, inputParameters: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            return WorkflowReplayOutcome.Stopped;
        }

        if (!result.Outcome.Started)
            return WorkflowReplayOutcome.NotAdmitted;

        if (result.Error is not null && result.Phase != WorkflowRunPhase.Finished)
            logger.LogWarning(result.Error, "The replay of abandoned run {ExecutionId} ended {Phase}.", executionId, result.Phase);

        return result.Phase switch
        {
            WorkflowRunPhase.Finished or WorkflowRunPhase.Parked => WorkflowReplayOutcome.Finished,
            WorkflowRunPhase.Stopped or WorkflowRunPhase.HandedOver => result.RowLeftRunning
                ? WorkflowReplayOutcome.Finished
                : WorkflowReplayOutcome.Stopped,
            _ => WorkflowReplayOutcome.Failed
        };
    }

    /// <inheritdoc />
    public async Task<bool> TryResumeAsync(
        Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
    {
        var pin = await ReadPinAsync(executionId, workflowId, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Resuming workflow {WorkflowId} from abandoned run {ExecutionId}.", workflowId, executionId);

        using var pinScope = WorkflowRunPinScope.Push(pin);
        var outcome = await RunnerFor(handOverOnStop: false, replaysAdmittedRun: true).ExecuteWithOutcomeAsync(
            workflowId, fire: null, inputParameters: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return outcome.Started;
    }

    /// <inheritdoc />
    public async Task<bool> IsBlockIdempotentAsync(
        Guid workflowId, string blockPath, Guid? versionId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(blockPath))
            return false;

        var definition = await LoadDefinitionAsync(workflowId, versionId, cancellationToken).ConfigureAwait(false);
        var node = definition?.Nodes?.FirstOrDefault(candidate => candidate.Id == blockPath);
        return node is not null && Replayable(node);
    }

    /// <inheritdoc />
    /// <remarks>Every block in the definition, and not the one the run stopped at: a recovered run starts over,
    /// so a block earlier in the graph is repeated whatever the row says about where it got to. That also makes
    /// a run that died before entering any block replayable, which is the one case where nothing can repeat. A
    /// container is asked about with everything inside it, since the replay runs that again too.</remarks>
    public async Task<bool> IsRunReplayableAsync(
        Guid workflowId, string currentBlockPath, Guid? versionId = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await LoadDefinitionAsync(workflowId, versionId, cancellationToken).ConfigureAwait(false);
        if (definition?.Nodes is null)
            return false;

        return definition.Nodes.All(ReplayableWhole);
    }

    /// <inheritdoc />
    /// <remarks>A container in flight is repeated whole, so every block inside it is asked too. Under
    /// <see cref="RunStateWriteModeKind.Heartbeat"/> the checkpoint may be blocks behind, so every root node it has no
    /// outputs for is asked.</remarks>
    async Task<WorkflowRunReentryDecision> IWorkflowRunReentry.DecideReentryAsync(
        WorkflowRunStateRow row, WorkflowSuspensionStatePayload checkpoint, CancellationToken cancellationToken)
    {
        var (read, journalVersion) = await JournalVersionAsync(checkpoint.JournalExecutionId, cancellationToken).ConfigureAwait(false);
        if (!read)
            return WorkflowRunReentryDecision.Replay();
        var versionId = journalVersion ?? PinOf(row, row.WorkflowId).VersionId;
        if (await UnreadableCheckpointAsync(row.WorkflowId, versionId, checkpoint, cancellationToken).ConfigureAwait(false) is { } unreadable)
            return WorkflowRunReentryDecision.Abandon(unreadable);

        var definition = await LoadDefinitionAsync(row.WorkflowId, versionId, cancellationToken).ConfigureAwait(false);
        var inFlight = definition?.Nodes?.FirstOrDefault(node => node.Id == checkpoint.NodeInFlight);
        if (definition is null || inFlight is null)
            return WorkflowRunReentryDecision.Replay();

        IEnumerable<WorkflowNodeDefinition> repeated = [inFlight];
        if (checkpoint.WriteMode == nameof(RunStateWriteModeKind.Heartbeat))
        {
            IReadOnlySet<string> done;
            try
            {
                done = PersistedValueCodec.NodeIds(checkpoint.PreviousOutputsJson);
            }
            catch (PersistedPayloadException ex)
            {
                return WorkflowRunReentryDecision.Abandon(ex.Message);
            }

            repeated = definition.Nodes.Where(node => !done.Contains(node.Id));
        }
        else if (WorkflowRunCheckpoint.Continuation(checkpoint) is { Count: > 0 } frames)
        {
            if (Innermost(inFlight, frames) is not { } inner)
                return WorkflowRunReentryDecision.Replay();
            repeated = [inner];
        }

        return repeated.FirstOrDefault(node => !ReplayableWhole(node)) is { } refused
            ? WorkflowRunReentryDecision.Refuse(refused.Id)
            : WorkflowRunReentryDecision.Reenter();
    }

    private static WorkflowNodeDefinition? Innermost(
        WorkflowNodeDefinition container, IReadOnlyList<WorkflowContinuationFrame> frames)
    {
        var node = container;
        foreach (var frame in frames)
        {
            if (node.SubCanvases is not { } canvases
                || !canvases.TryGetValue(frame.SubCanvas, out var canvas)
                || canvas.Nodes?.FirstOrDefault(inner => inner.Id == frame.NodeInFlight) is not { } next)
            {
                return null;
            }
            node = next;
        }

        return node;
    }

    private async Task<(bool Read, Guid? VersionId)> JournalVersionAsync(Guid? executionLogId, CancellationToken cancellationToken)
    {
        if (executionLogId is not { } id)
            return (true, null);
        try
        {
            using var scope = seams.RunScopes.CreateScope();
            return (true, (await scope.Journal.LoadRunAsync(id, cancellationToken).ConfigureAwait(false))?.WorkflowVersionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read journal entry {ExecutionLogId} to learn the version a recovery re-enters.", id);
            return (false, null);
        }
    }

    private bool ReplayableWhole(WorkflowNodeDefinition node) => WorkflowIdempotence.Whole(node, seams.Blocks);

    private bool Replayable(WorkflowNodeDefinition node) => WorkflowIdempotence.Node(node, seams.Blocks);

    private async Task<string?> UnreadableCheckpointAsync(
        Guid workflowId, Guid? versionId, WorkflowSuspensionStatePayload checkpoint, CancellationToken cancellationToken)
    {
        WorkflowProgram? program;
        try
        {
            using var scope = seams.RunScopes.CreateScope();
            var version = versionId is { } id
                ? await scope.Store.LoadVersionAsync(id, cancellationToken).ConfigureAwait(false)
                : (await scope.Store.LoadAsync(workflowId, cancellationToken).ConfigureAwait(false))?.CurrentVersion;
            if (version is null)
                return null;
            var manifest = WorkflowResumeDecisions.ResolveResumeManifest(
                checkpoint.PinnedVersionsJson, workflowId, version.Id, WorkflowGraphHelpers.JsonOptions);
            using var pins = WorkflowVersionPinScope.Push(manifest);
            program = (await seams.Compiler.EnsureWithProgramAsync(version, manifest, cancellationToken).ConfigureAwait(false)).Program;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not compile workflow {WorkflowId} to read a lost run's checkpoint.", workflowId);
            return null;
        }

        if (program is null)
            return null;
        var root = CompiledWalk.Root(program);
        try
        {
            PersistedValueCodec.ReadNodeOutputs(checkpoint.PreviousOutputsJson, root.Canvas, "outputs");
            PersistedValueCodec.ReadTriggerEnvelope(checkpoint.TriggerEnvelopeJson,
                WorkflowStartArguments.PayloadTerms(seams.Compiler.Options));
            PersistedValueCodec.ReadPosition(
                WorkflowRunCheckpoint.Continuation(checkpoint), checkpoint.ContinuationJson, root, checkpoint.NodeInFlight ?? "");
            return null;
        }
        catch (PersistedPayloadException ex)
        {
            logger.LogWarning(ex, "A value in the checkpoint of a lost run of workflow {WorkflowId} does not read; the run is abandoned.", workflowId);
            return ex.Message;
        }
    }

    private async Task<WorkflowDefinition?> LoadDefinitionAsync(
        Guid workflowId, Guid? versionId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = seams.RunScopes.CreateScope();

            var version = versionId.HasValue
                ? await scope.Store.LoadVersionAsync(versionId.Value, cancellationToken).ConfigureAwait(false)
                : (await scope.Store.LoadAsync(workflowId, cancellationToken).ConfigureAwait(false))?.CurrentVersion;

            if (version is null)
                return null;
            var cached = WorkflowVersionDataCache.Get(version);
            if (cached.ParseFailure is { } failure)
                logger.LogWarning(failure.CreateException(),
                    "Could not read workflow {WorkflowId} to decide whether a lost run of it may be replayed; assuming not.",
                    workflowId);
            return cached.Definition;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not read workflow {WorkflowId} to decide whether a lost run of it may be replayed; assuming not.",
                workflowId);
            return null;
        }
    }

    private async Task<WorkflowRunPin> ReadPinAsync(
        Guid executionId, Guid workflowId, CancellationToken cancellationToken)
    {
        if (seams.RunStateStore is null)
            return new WorkflowRunPin();

        var row = await seams.RunStateStore.GetAsync(executionId, cancellationToken).ConfigureAwait(false);
        var pin = PinOf(row, workflowId);
        if (pin.VersionId is not null || row is null)
            return pin;

        var numbered = await NumberedVersionAsync(workflowId, row.WorkflowVersion, cancellationToken)
            .ConfigureAwait(false);
        if (numbered is null)
            return pin;

        return new WorkflowRunPin
        {
            VersionId = numbered.Value,
            Manifest = pin.Manifest ?? new Dictionary<Guid, Guid> { [workflowId] = numbered.Value }
        };
    }

    private async Task<Guid?> NumberedVersionAsync(
        Guid workflowId, int versionNumber, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = seams.RunScopes.CreateScope();
            var version = await scope.Store
                .LoadVersionNumberedAsync(workflowId, versionNumber, cancellationToken).ConfigureAwait(false);
            return version?.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not read which version number {VersionNumber} of workflow {WorkflowId} is; the resumed "
                + "run will be built from the current version.", versionNumber, workflowId);
            return null;
        }
    }

    /// <summary>The manifest a run recorded, as the pin a re-execution takes. Without it the run would rebuild
    /// one from whatever is current now, which is a different workflow from the one that was interrupted.</summary>
    public static WorkflowRunPin PinOf(WorkflowRunStateRow? row, Guid workflowId)
    {
        if (string.IsNullOrWhiteSpace(row?.PinnedVersionsJson))
            return new WorkflowRunPin();

        var manifest = JsonSerializer.Deserialize<Dictionary<Guid, Guid>>(row.PinnedVersionsJson, JsonOptions);
        if (manifest is null)
            return new WorkflowRunPin();

        return manifest.TryGetValue(workflowId, out var pinnedVersionId)
            ? new WorkflowRunPin { VersionId = pinnedVersionId, Manifest = manifest }
            : new WorkflowRunPin { Manifest = manifest };
    }

    private WorkflowRunCoordinator CoordinatorFor(Workflow workflow)
    {
        lock (coordinatorLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var coordinator = coordinators.GetOrAdd(workflow.Id, _ => new WorkflowRunCoordinator(
                workflow.Id,
                workflow.Mode,
                workflow.MaxConcurrentRuns,
                workflow.MaxQueuedRuns,
                workflow.OverflowBehavior,
                seams.Metrics,
                loggerFactory.CreateLogger<WorkflowRunCoordinator>(),
                seams.RunLockProvider,
                seams.RunSlotGate,
                settings.IsMultiReplica,
                seams.ClusterSignal,
                settings.RestartCancelChannel,
                restartGenerations,
                settings.RunHeartbeatInterval,
                subscribeToClusterSignal: false));

            coordinatorLastUsed[workflow.Id] = clock.GetUtcNow();
            coordinator.Reconfigure(
                workflow.Mode, workflow.MaxConcurrentRuns, workflow.MaxQueuedRuns, workflow.OverflowBehavior);
            return coordinator;
        }
    }

    internal void SweepIdleCoordinators()
    {
        lock (coordinatorLock)
        {
            var now = clock.GetUtcNow();
            foreach (var pair in coordinators)
            {
                if (!coordinatorLastUsed.TryGetValue(pair.Key, out var lastUsed)
                    || now - lastUsed < settings.CoordinatorIdleTimeout)
                {
                    continue;
                }

                if (!pair.Value.TryRetireIfIdle())
                    continue;

                if (!coordinators.TryRemove(new KeyValuePair<Guid, WorkflowRunCoordinator>(pair.Key, pair.Value)))
                    continue;

                coordinatorLastUsed.Remove(pair.Key);
                pair.Value.Dispose();
            }
        }
    }

    private static void RefuseADeclaredClusterWithNoSeams(WorkflowEngineSeams seams, WorkflowEngineSettings settings)
    {
        if (!settings.IsMultiReplica)
            return;

        var unanswered = new List<string>();
        if (seams.RunSlotGate is null) unanswered.Add($"{nameof(seams.RunSlotGate)} (the cluster run budget)");
        if (seams.FireDedup is null) unanswered.Add($"{nameof(seams.FireDedup)} (one trigger event reaching every replica)");
        if (seams.RunLockProvider is null) unanswered.Add($"{nameof(seams.RunLockProvider)} (Single and Restart across replicas)");
        if (seams.RestartGenerations is null) unanswered.Add($"{nameof(seams.RestartGenerations)} (which Restart trigger is the newest)");

        if (unanswered.Count == 0)
            return;

        throw new InvalidOperationException(
            $"{nameof(WorkflowEngineSettings)}.{nameof(settings.IsMultiReplica)} is true, so this deployment has "
            + $"peers, but {unanswered.Count} cluster seam(s) are null: {string.Join(", ", unanswered)}. Each of "
            + "them degrades to in-process behaviour rather than failing, so the deployment would run more than "
            + "it was configured to and say nothing. Fill them, or say false if this really is one process.");
    }

    /// <summary>The coordinators this engine's runs are admitted through. A resumer handed this pool re-enters a
    /// parked run inside the same budget a fresh run of that workflow is admitted under, and a woken sleep holds
    /// the same Single or Restart lease a fresh run would.</summary>
    public IWorkflowRunCoordinatorPool Coordinators => pool;

    /// <summary>How many workflows this process is currently holding a budget for. One per workflow that has
    /// run since the process started, until something forgets it.</summary>
    public int CoordinatorCount => coordinators.Count;

    /// <summary>Drops the coordinator for a workflow that no longer exists, cancelling whatever it was running,
    /// and answers which runs those were. A host with no notion of deletion never calls this.</summary>
    /// <returns>The ids of the runs this cancelled, snapshotted before the tear-down that cancels them — the
    /// only moment they can be read, and what a host needs to record why each of them stopped.</returns>
    public IReadOnlyList<Guid> ForgetWorkflow(Guid workflowId)
    {
        lock (coordinatorLock)
        {
            if (!coordinators.TryRemove(workflowId, out var coordinator))
                return [];

            coordinatorLastUsed.Remove(workflowId);
            var cancelled = coordinator.SnapshotActiveRunIds();
            coordinator.Dispose();
            return cancelled;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        coordinatorCleanupTimer.Dispose();
        restartCancelSubscription?.Dispose();

        foreach (var coordinator in coordinators.Values)
        {
            try { coordinator.Dispose(); }
            catch (Exception ex) { logger.LogWarning(ex, "A run coordinator failed to dispose."); }
        }

        coordinators.Clear();
        coordinatorLastUsed.Clear();
    }

    private sealed class CoordinatorPool(WorkflowEngine engine) : IWorkflowRunCoordinatorPool
    {
        public WorkflowRunCoordinator For(Workflow workflow) => engine.CoordinatorFor(workflow);
    }
}

/// <summary>Every seam <see cref="WorkflowEngine"/> is composed from, as one object: a seam added later is one
/// more property here rather than a fifteenth constructor parameter nobody can call positionally.</summary>
public sealed record WorkflowEngineSeams
{
    /// <summary>Opens the per-run world the engine works inside.</summary>
    public required IWorkflowRunScopeFactory RunScopes { get; init; }
    /// <summary>Signals this engine's suspension waker after a run parks.</summary>
    public WorkflowSuspensionParkSignal? ParkSignal { get; init; }

    /// <summary>The one lookup the graph walk performs.</summary>
    public required IWorkflowBlocks Blocks { get; init; }

    /// <summary>What admission compiles a start through. <c>AddWorkflowEngine</c> sets the one <c>AddWorkflows</c>
    /// registers, which the editor's doors use; one built by hand must read the same <see cref="Blocks"/> and
    /// <see cref="RunScopes"/>, or runs are judged against a catalogue they do not run with.</summary>
    public required WorkflowCompilationService Compiler { get; init; }

    /// <summary>A host-level stop on starting work at all.</summary>
    public required IWorkflowExecutionGate ExecutionGate { get; init; }

    /// <summary>Every moment of a run worth counting.</summary>
    public required IWorkflowMetrics Metrics { get; init; }

    /// <summary>Per-node progress, for whoever is watching a run happen.</summary>
    public required IWorkflowNodeObserver NodeObserver { get; init; }

    /// <summary>The confinement a block the host did not write is run in. Null hands every block the raw
    /// container, which is the answer for a host with no plugin model.</summary>
    public IWorkflowBlockSandbox? BlockSandbox { get; init; }

    /// <summary>Where a transition audit event goes, for a host that keeps one. Null, or the audit setting
    /// left off, writes the recovery row and nothing beside it.</summary>
    public IWorkflowAuditSink? AuditSink { get; init; }

    /// <summary>The host's own expression helpers. Null leaves the language as the library ships it.</summary>
    public IHelperResolver? HelperResolver { get; init; }

    /// <summary>Where a run is, durably. Null gives a correct engine that cannot survive a restart mid-run.</summary>
    public IWorkflowRunStateStore? RunStateStore { get; init; }

    /// <summary>Where a run parks while it waits. Null means nothing ever parks.</summary>
    public IWorkflowSuspensionStore? SuspensionStore { get; init; }

    /// <summary>The debugger's hold on a run. Null runs every workflow at full speed.</summary>
    public IWorkflowDebugSessions? DebugSessions { get; init; }

    /// <summary>Collapses the N-replica fan-out of one trigger event. Null admits every fire.</summary>
    public IWorkflowFireDedup? FireDedup { get; init; }

    /// <summary>The cluster-wide held-slot semaphore for the N-permit modes.</summary>
    public IWorkflowRunSlotGate? RunSlotGate { get; init; }

    /// <summary>The cluster-wide "at most one run" gate for the one-permit modes.</summary>
    public IWorkflowRunLockProvider? RunLockProvider { get; init; }

    /// <summary>The one transport every cross-replica signal travels on. Null tears down only this process's
    /// runs on a Restart, and loses a debugger step to the replica that is not this one.</summary>
    public IWorkflowClusterSignal? ClusterSignal { get; init; }

    /// <summary>Where every replica reads which Restart trigger is the newest. Null orders them in this process,
    /// which is every trigger there is on one replica; a host that declared peers is refused without one.</summary>
    public IWorkflowRestartGenerations? RestartGenerations { get; init; }

    /// <summary>The host's say over whether a new run may start. Null admits every start.</summary>
    public IWorkflowStartAdmission? StartAdmission { get; init; }

    /// <summary>The host's check of the nodes of the versions a new run would execute. Null finds nothing.</summary>
    public IWorkflowNodeValidator? NodeValidator { get; init; }

    /// <summary>The host's check of the start arguments whose type is the host's own. Null finds nothing.</summary>
    public IWorkflowStartArgumentValidator? StartArgumentValidator { get; init; }

    /// <summary>Where a live run renews its claim on its row, on the clock the recovery sweep reads. Null
    /// leaves a run's row untouched between blocks, so a block longer than the sweep's cutoff is reclaimed.</summary>
    public IWorkflowRunRecoveryStore? RunRecoveryStore { get; init; }

    /// <summary>Fires when the host begins to stop. A run that is cancelled after it has fired was stopped by
    /// the host rather than by anyone who wanted it ended — which is what <see cref="WorkflowEngineSettings.HandOverRunsOnStop"/> reads.</summary>
    public CancellationToken HostStopping { get; init; }
}

/// <summary>What the composed engine knows about the deployment it runs in, and how much of a run it records.</summary>
public sealed record WorkflowEngineSettings
{
    /// <summary>How long an unused coordinator is retained before the engine drops it.</summary>
    public TimeSpan CoordinatorIdleTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Clock used for coordinator retention, a node's deadline and retry backoff, and an in-process sleep,
    /// primarily for deterministic hosts and tests. Reaches the graph walk and the sleeper.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>How much of a run's steps to record.</summary>
    public WorkflowTraceMode TraceMode { get; init; }

    /// <summary>The cap on one serialised value in a persisted payload, past which it is replaced by a note
    /// saying so. Zero truncates everything, which is never what a host means.</summary>
    public int MaxPayloadValueBytes { get; init; } = 8 * 1024;

    /// <summary>The most steps and node log entries one walk segment records; see <see cref="WorkflowGraphExecutorOptions.MaxRecordedSteps"/>.</summary>
    public int MaxRecordedSteps { get; init; } = 10_000;

    /// <summary>Which replica this is, so a row records an owner.</summary>
    public string? ReplicaId { get; init; }

    /// <summary>Whether this deployment has peers.</summary>
    public bool IsMultiReplica { get; init; }

    /// <summary>How long one physical trigger event stays claimed.</summary>
    public TimeSpan FireDedupWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The category the engine's own lines are logged under. A wire name an operator's log filters
    /// read, so a host moving onto this class keeps whatever its filters already say.</summary>
    public string LoggerCategory { get; init; } = "ReeveWorkflow.Engine";

    /// <summary>The channel a superseding Restart publishes its cancel on. A contract between replicas rather
    /// than a setting: two that disagree stop hearing each other and degrade to per-process supersession.</summary>
    public string RestartCancelChannel { get; init; } = new WorkflowClusterSignalChannels().RestartCancel;

    /// <summary>The zone a workflow's local wall-clock expression fields are read in.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>The same zone for a host where it is a lookup rather than a setting. Set, it is what
    /// <see cref="TimeZone"/> would have answered.</summary>
    public Func<IServiceProvider, CancellationToken, Task<TimeZoneInfo>>? ResolveTimeZone { get; init; }

    /// <summary>The host namespaces shared with compilation and expression preview.</summary>
    public IReadOnlyList<WorkflowExpressionNamespace> ExpressionNamespaces { get; init; } = [];

    /// <summary>Whether every entered node is an audit event beside the recovery row it already writes.</summary>
    public bool AuditTransitions { get; init; }

    /// <summary>The block types whose delay is parked in the run's row rather than awaited in this process.</summary>
    public IReadOnlyCollection<string> DurableSleepBlockTypeIds { get; init; } = [];

    /// <summary>The shortest durable sleep that parks its run as a suspension row. Null never parks; see
    /// <see cref="WorkflowGraphExecutorOptions.ParkSleepWhenAtLeast"/>.</summary>
    public TimeSpan? ParkSleepWhenAtLeast { get; init; }

    /// <summary>The suspension store's cap on a parked payload; a sleep whose run exceeds it is not parked.</summary>
    public int MaxSuspendPayloadBytes { get; init; } = 256 * 1024;

    /// <summary>How often a run writes its row. See <see cref="Execution.RunStateWriteMode"/>.</summary>
    public RunStateWriteMode RunStateWriteMode { get; init; } = RunStateWriteMode.EveryBlock;

    /// <summary>The cap on one checkpoint; see <see cref="WorkflowGraphExecutorOptions.MaxCheckpointBytes"/>.</summary>
    public int MaxCheckpointBytes { get; init; } = 256 * 1024;

    /// <summary>The recovery sweep's cutoff, which a workflow's own checkpoint policy has to stay under: a
    /// checkpoint older than what a peer reads is one a recovery re-enters from blocks behind the run. Carried
    /// here because a run reads a policy and the sweep's own options are the sweep's.</summary>
    public TimeSpan RecoveryAbandonedAfter { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How often a parked run looks up to see whether it was cancelled while it slept.</summary>
    public TimeSpan SleepPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether a node that says nothing about retries inherits its block's answer to
    /// <see cref="IWorkflowBlockFactory.IsIdempotent"/> instead of being retried. On by default; a node's
    /// own <c>retryEnabled</c> wins either way, and a host not ready for it sets this false.</summary>
    public bool RetryOnlyIdempotentBlocks { get; init; } = true;

    /// <summary>The ceiling a container runs under when its own node names none. Null — the run's own deadline
    /// and nothing else — because the engine cannot size a body and a guessed deadline kills a long loop and then
    /// repeats it whole. A number on the node still wins, and leaf blocks keep their own default either way.</summary>
    /// <remarks>A container nested in another gets the ceiling too, so an outer one has to cover everything its
    /// inner ones do. Non-positive is refused: "no ceiling" is null.</remarks>
    public int? DefaultContainerTimeoutSeconds { get; init; }

    /// <summary>Whether a run its host stops mid-walk leaves its row with no owner, Running or, in an in-process
    /// sleep, Sleeping with its wake-up time, for a peer's recovery sweep to carry on, instead of ending it Cancelled — which recovery never selects. Off unless a sweep exists:
    /// a released row nothing picks up stays Running for ever, and a Running row keeps its workflow undeletable.</summary>
    /// <remarks>A run counts as stopped by its host only when <see cref="WorkflowEngineSeams.HostStopping"/> has
    /// fired and the run's own caller cancelled it. A lease cancelled by a Restart or a forgotten workflow is not.
    /// <para>A handover also leaves the run's journal entry open, for whoever takes the row. A host whose
    /// own sweep takes these rows owns that entry: this package's sweep closes it or carries the run on under it, and
    /// a host's sweep, which replays from the start under an entry of its own, has to close it with
    /// <see cref="WorkflowRunResumer.CloseEntryAsync"/> when it ends the row — or the entry stays <c>Running</c> and
    /// the run is never over as far as any reader of the history is concerned. The entry a row named is
    /// <c>WorkflowRunCheckpoint.Of(row)?.JournalExecutionId</c>, and the close answers <c>bool</c>: <c>false</c> means
    /// the entry may still be open, on a row that is already ended.</para></remarks>
    public bool HandOverRunsOnStop { get; init; }

    /// <summary>Whether a recovery sweep re-enters this host's abandoned runs from their checkpoints. Only then does a
    /// root block that may not run twice wait for the write before it to land, and refuse to run without it
    /// (<see cref="WorkflowCheckpointNotWrittenException"/>): a re-entry asks only about the block the row names. Off
    /// unless a sweep exists.</summary>
    /// <remarks>Separate from <see cref="HandOverRunsOnStop"/>, which says what a stop leaves behind: a host can cancel
    /// its runs on a stop and still have a sweep that re-enters the ones a crash left, and a host can hand runs over to
    /// a sweep of its own that only replays them.</remarks>
    public bool RecoveryReentersRuns { get; init; }

    /// <summary>How often a live run renews its row's claim, and re-asserts its cluster lock or slot. Null stops
    /// the heartbeat; it also needs <see cref="WorkflowEngineSeams.RunRecoveryStore"/> for the row half.</summary>
    /// <remarks>Keep it well under the recovery sweep's <c>AbandonedAfter</c>: two missed beats should not be
    /// enough for a peer to reclaim a run that is still executing.
    ///
    /// It is also how often a running Restart run on a replica with peers looks for a newer trigger
    /// waiting for it, which is what stops it when the cancel on the cluster signal was lost. Null leaves that look at
    /// ten seconds: a Restart run is not left running beside a newer trigger because the heartbeat is off.</remarks>
    public TimeSpan? RunHeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
}
