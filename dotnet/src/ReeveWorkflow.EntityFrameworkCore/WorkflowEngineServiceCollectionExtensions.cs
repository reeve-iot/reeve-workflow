using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The composition root: one call that assembles an engine out of the seams, instead of a class every
/// adopter writes again from the same parts.</summary>
public static class WorkflowEngineServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IWorkflowEngine"/> and everything it is made of — the graph walk, a runner
    /// per call, one coordinator per workflow, the resumer, the durable sleep and the abandoned-run sweep.</summary>
    /// <remarks>Every part is read from the container when it is first asked for, so this may be written on
    /// either side of the seam declarations it composes. Which replica this is and whether it has peers are
    /// read back from <c>Defaults</c> rather than restated here: a second copy of that fact is how a deployment
    /// ends up validated as one shape and executed as another.</remarks>
    public static IWorkflowBuilder AddWorkflowEngine(
        this IWorkflowBuilder builder, Action<WorkflowEngineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new WorkflowEngineOptions();
        configure?.Invoke(options);
        Validate(options);

        var services = builder.Services;
        services.TryAddSingleton<WorkflowSuspensionParkSignal>();
        services.TryAddSingleton(options);

        services.TryAddSingleton<IWorkflowEngine>(sp => new WorkflowEngine(
            SeamsFrom(sp), SettingsFrom(sp, options), sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<WorkflowActivitySource>(),
            WalkOptionsFrom(sp, options)));

        services.TryAddSingleton(sp => new WorkflowRunResumer(
            sp.GetRequiredService<IWorkflowRunScopeFactory>(),
            GraphExecutorFrom(sp, options),
            sp.GetRequiredService<IWorkflowExecutionGate>(),
            sp.GetRequiredService<IWorkflowMetrics>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(options.LoggerCategory),
            new WorkflowResumeOptions
            {
                TraceMode = options.TraceMode,
                MaxPayloadValueBytes = options.MaxPayloadValueBytes,
                ReplicaId = Defaults(sp).ReplicaId,
                HandOverOnStop = SettingsFrom(sp, options).HandOverRunsOnStop,
                RecoveryReentersRuns = SettingsFrom(sp, options).RecoveryReentersRuns,
                HostStopping = HostStopping(sp),
                HeartbeatInterval = SettingsFrom(sp, options).RunHeartbeatInterval,
                EngineWriteMode = options.RunStateWriteMode,
                RecoveryAbandonedAfter = options.Recovery.AbandonedAfter,
                RunRecoveryStore = sp.GetService<IWorkflowRunRecoveryStore>(),
                TimeProvider = sp.GetService<TimeProvider>(),
                ParkSleepWhenAtLeast = options.DurableSleep.ParkWhenAtLeast,
                ParkSignal = sp.GetRequiredService<WorkflowSuspensionParkSignal>()
            },
            sp.GetRequiredService<WorkflowCompilationService>(),
            sp.GetService<IWorkflowRunStateStore>(),
            sp.GetService<IWorkflowSuspensionStore>(),
            CoordinatorsFor(sp, options)));

        services.TryAddSingleton(sp => new WorkflowSleepRunner(
            sp.GetRequiredService<IWorkflowRunStateStore>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(options.LoggerCategory),
            new WorkflowSleepOptions
            {
                CancellationPollInterval = options.SleepPollInterval,
                ReplicaId = Defaults(sp).ReplicaId,
                TimeProvider = sp.GetService<TimeProvider>(),
                HostStopping = HostStopping(sp)
            }));

        AddRecovery(services, options);
        AddWaker(services, options);

        return builder;
    }

    private static Func<WorkflowSuspensionCandidate, CancellationToken, Task<TimeZoneInfo?>>? ZoneLookup(
        IServiceProvider sp) =>
        Defaults(sp).ResolveTimeZoneAsync is { } resolve
            ? async (_, cancellationToken) =>
            {
                using var scope = sp.CreateScope();
                return await resolve(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            }
            : null;

    private static IWorkflowRunCoordinatorPool? CoordinatorsFor(IServiceProvider sp, WorkflowEngineOptions options)
    {
        if (options.DurableSleep.ParkWhenAtLeast is null)
            return new CoordinatorsOnFirstUse(sp);

        var pool = (sp.GetRequiredService<IWorkflowEngine>() as WorkflowEngine)?.Coordinators
            ?? sp.GetService<IWorkflowRunCoordinatorPool>();

        if (pool is null && options.DurableSleep.ParkWhenAtLeast is not null)
        {
            throw new InvalidOperationException(
                "DurableSleep.ParkWhenAtLeast is set, but the registered IWorkflowEngine is not a WorkflowEngine, so "
                + "the resumer cannot take a woken sleep's lease from its coordinators and would never resume one. "
                + "Register the inner engine's WorkflowEngine.Coordinators as IWorkflowRunCoordinatorPool.");
        }

        return pool;
    }

    private sealed class CoordinatorsOnFirstUse(IServiceProvider sp) : IWorkflowRunCoordinatorPool
    {
        private IWorkflowRunCoordinatorPool? pool;

        public WorkflowRunCoordinator For(Workflow workflow) =>
            (pool ??= (sp.GetRequiredService<IWorkflowEngine>() as WorkflowEngine)?.Coordinators
                ?? sp.GetService<IWorkflowRunCoordinatorPool>()
                ?? throw new InvalidOperationException(
                    "A parked sleep cannot resume: the registered IWorkflowEngine is not a WorkflowEngine, so its lease "
                    + "cannot be taken. Register the inner engine's WorkflowEngine.Coordinators as IWorkflowRunCoordinatorPool."))
            .For(workflow);
    }


    private static void AddWaker(IServiceCollection services, WorkflowEngineOptions options)
    {
        services.TryAddSingleton(sp => new WorkflowSuspensionWaker(
            sp.GetRequiredService<IWorkflowSuspensionResumeStore>(),
            sp.GetRequiredService<WorkflowRunResumer>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(options.LoggerCategory),
            sp.GetRequiredService<WorkflowCompilationService>(),
            options.Waker with
            {
                TimeZone = Defaults(sp).TimeZone,
                ResolveTimeZone = options.Waker.ResolveTimeZone ?? ZoneLookup(sp),
                ExpressionNamespaces = sp.GetRequiredService<WorkflowExpressionNamespaceSnapshot>().Registrations,
                NamespaceScopeFactory = sp.GetRequiredService<IServiceScopeFactory>(),
                TypeVocabulary = options.Waker.TypeVocabulary ?? (sp.GetService<CompilationOptions>() is { } compilation
                    ? () => compilation.Vocabulary
                    : null),
                RetakeAbandonedTakesAfter = options.Waker.RetakeAbandonedTakesAfter ?? options.Recovery.AbandonedAfter
            },
            sp.GetService<IHelperResolver>(),
            sp.GetRequiredService<IWorkflowRunScopeFactory>(),
            sp.GetRequiredService<IWorkflowMetrics>(),
            AuditSinkFrom(sp)));

        if (!options.Waker.Enabled)
        {
            services.AddHostedService(sp => new WorkflowUnwokenSleepCheck(
                sp.GetRequiredService<IWorkflowSuspensionResumeStore>(),
                sp.GetService<IWorkflowSuspensionStore>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger(options.LoggerCategory)));
            return;
        }

        services.AddHostedService(sp => new WorkflowSuspensionWakeService(
            sp.GetRequiredService<WorkflowSuspensionWaker>(),
            options.Waker,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(options.LoggerCategory),
            sp.GetRequiredService<WorkflowSuspensionParkSignal>(),
            sp.GetService<IWorkflowSuspensionWakeSource>()));
    }

    private static void AddRecovery(IServiceCollection services, WorkflowEngineOptions options)
    {
        services.TryAddSingleton(sp => new WorkflowRunRecoverySweep(
            sp.GetRequiredService<IWorkflowEngine>(),
            RunStateStoreForRecovery(sp),
            sp.GetRequiredService<IWorkflowRunRecoveryStore>(),
            options.Recovery,
            Defaults(sp).ReplicaId,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(options.LoggerCategory),
            sp.GetRequiredService<WorkflowRunResumer>(),
            sp.GetRequiredService<IWorkflowMetrics>(),
            AuditSinkFrom(sp),
            sp.GetRequiredService<IWorkflowExecutionGate>(),
            HostStopping(sp)));

        if (!options.Recovery.Enabled)
            return;

        services.AddHostedService(sp => new WorkflowRunRecoveryService(
            RefuseARecoveringReplicaWithNoName(sp),
            options.Recovery,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(options.LoggerCategory)));
    }

    private static WorkflowRunRecoverySweep RefuseARecoveringReplicaWithNoName(IServiceProvider sp)
    {
        if (Defaults(sp).ReplicaId is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                "Recovery.Enabled is true but Defaults.ReplicaId is not set. A claim would write no owner onto "
                + "the row, and the renewal that keeps it — which fences on that owner — would match every "
                + "peer's rows as readily as this one's, so two replicas can run one recovered run. Name this "
                + "replica, or set Recovery.Enabled to false.");
        }

        return sp.GetRequiredService<WorkflowRunRecoverySweep>();
    }

    private static IWorkflowRunStateStore RunStateStoreForRecovery(IServiceProvider sp)
    {
        var runState = sp.GetRequiredService<IWorkflowRunStateStore>();
        if (sp.GetRequiredService<IWorkflowRunRecoveryStore>() is EfWorkflowRunRecoveryStore
            && runState is not EfWorkflowRunStateStore)
        {
            throw new InvalidOperationException(
                $"Seams.RunStateStore is {runState.GetType().Name} but Seams.RunRecoveryStore is this package's "
                + "default, which queries the workflow context's own table — the one that store does not write. "
                + "The sweep would find nothing for ever and say nothing. Fill Seams.RunRecoveryStore over the "
                + "same place, or set Recovery.Enabled to false.");
        }

        return runState;
    }

    private static void Validate(WorkflowEngineOptions options)
    {
        if (options.Recovery.AbandonedAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Recovery.AbandonedAfter,
                "Recovery.AbandonedAfter must be positive: at zero every running row is abandoned the instant "
                + "it is written, and every sweep reclaims runs that are executing.");
        }

        if (options.RunHeartbeatInterval is { } beat)
        {
            if (beat <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(options), beat,
                    "RunHeartbeatInterval must be positive. A heartbeat that never beats is turned off with null.");
            }

            if (options.Recovery.AbandonedAfter < beat * 3)
            {
                throw new ArgumentOutOfRangeException(nameof(options), options.Recovery.AbandonedAfter,
                    $"Recovery.AbandonedAfter ({options.Recovery.AbandonedAfter}) must be at least three times "
                    + $"RunHeartbeatInterval ({beat}): under that, a replica whose beat was late twice has a run it "
                    + "is still executing reclaimed by a peer and run a second time.");
            }
        }

        if (options.RunStateWriteMode is null)
            throw new ArgumentNullException(nameof(options), "RunStateWriteMode is required; the default is EveryBlock.");

        if (WorkflowCheckpointPolicy.NeedsTheHeartbeat(options.RunStateWriteMode, options.RunHeartbeatInterval) is { } needsBeat)
            throw new InvalidOperationException(needsBeat);

        if (WorkflowCheckpointPolicy.OutlivesTheCutoff(options.RunStateWriteMode, options.Recovery.AbandonedAfter) is { } tooLong)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.RunStateWriteMode.Interval, tooLong);
        }

        if (options.MaxRecordedSteps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxRecordedSteps,
                "MaxRecordedSteps must be positive: at zero a run records none of its steps, not even the one that failed.");
        }

        if (options.MaxCheckpointBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxCheckpointBytes,
                "MaxCheckpointBytes must be positive: at zero no checkpoint fits, and every recovery replays from the start.");
        }

        if (options.Recovery.Enabled && options.Recovery.SweepInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Recovery.SweepInterval,
                "Recovery.SweepInterval must be positive.");
        }

        if (options.DurableSleep.ParkWhenAtLeast is { } park && park <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), park,
                "DurableSleep.ParkWhenAtLeast must be positive: at zero every sleep parks, however short. "
                + "Use null to keep every sleep in the process.");
        }

        if (options.Waker.Kinds is { Count: 0 })
        {
            throw new ArgumentException(
                "Waker.Kinds is empty, so no pass would move anything. Use null for every kind.", nameof(options));
        }


        if (options.DurableSleep.ParkWhenAtLeast is not null
            && (!options.Waker.Enabled || options.Waker.Kinds?.Contains(WorkflowSuspensionKind.Sleep) == false))
        {
            throw new InvalidOperationException(
                "DurableSleep.ParkWhenAtLeast is set, but the waker is disabled or its Kinds leave out Sleep, so "
                + "nothing would wake a parked sleep. Enable Waker with Sleep among its Kinds, or unset the threshold.");
        }

        if (options.DefaultContainerTimeoutSeconds is { } ceiling
            && ceiling is <= 0 or > WorkflowGraphExecutorOptions.MaxResilienceSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(options), ceiling,
                "DefaultContainerTimeoutSeconds is a positive number of seconds, at most "
                + $"{WorkflowGraphExecutorOptions.MaxResilienceSeconds} — one day, the longest deadline this "
                + "engine can apply. Use null for no ceiling.");
        }
    }

    private static IWorkflowAuditSink? AuditSinkFrom(IServiceProvider sp) =>
        sp.GetService<IServiceProviderIsService>() is { } registrations
            ? registrations.IsService(typeof(IWorkflowAuditSink))
                ? new ScopeOpeningWorkflowAuditSink(sp.GetRequiredService<IServiceScopeFactory>())
                : null
            : sp.GetService<IWorkflowAuditSink>();

    private static WorkflowEngineSeams SeamsFrom(IServiceProvider sp) => new()
    {
        RunScopes = sp.GetRequiredService<IWorkflowRunScopeFactory>(),
        Blocks = sp.GetRequiredService<IWorkflowBlocks>(),
        Compiler = sp.GetRequiredService<WorkflowCompilationService>(),
        ExecutionGate = sp.GetRequiredService<IWorkflowExecutionGate>(),
        Metrics = sp.GetRequiredService<IWorkflowMetrics>(),
        NodeObserver = sp.GetRequiredService<IWorkflowNodeObserver>(),
        BlockSandbox = sp.GetService<IWorkflowBlockSandbox>(),
        AuditSink = AuditSinkFrom(sp),
        HelperResolver = sp.GetService<IHelperResolver>(),
        RunStateStore = sp.GetService<IWorkflowRunStateStore>(),
        SuspensionStore = sp.GetService<IWorkflowSuspensionStore>(),
        DebugSessions = sp.GetService<IWorkflowDebugSessions>(),
        FireDedup = sp.GetService<IWorkflowFireDedup>(),
        StartAdmission = sp.GetService<IWorkflowStartAdmission>(),
        NodeValidator = sp.GetService<IWorkflowNodeValidator>(),
        StartArgumentValidator = sp.GetService<IWorkflowStartArgumentValidator>(),
        RunSlotGate = sp.GetService<IWorkflowRunSlotGate>(),
        RunLockProvider = sp.GetService<IWorkflowRunLockProvider>(),
        ClusterSignal = sp.GetService<IWorkflowClusterSignal>(),
        RunRecoveryStore = sp.GetService<IWorkflowRunRecoveryStore>(),
        ParkSignal = sp.GetRequiredService<WorkflowSuspensionParkSignal>(),
        RestartGenerations = sp.GetService<IWorkflowRestartGenerations>(),
        HostStopping = HostStopping(sp)
    };

    private static CancellationToken HostStopping(IServiceProvider sp) =>
        sp.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;

    private static WorkflowEngineSettings SettingsFrom(IServiceProvider sp, WorkflowEngineOptions options)
    {
        var defaults = Defaults(sp);
        return new WorkflowEngineSettings
        {
            TraceMode = options.TraceMode,
            MaxPayloadValueBytes = options.MaxPayloadValueBytes,
            MaxRecordedSteps = options.MaxRecordedSteps,
            FireDedupWindow = options.FireDedupWindow,
            LoggerCategory = options.LoggerCategory,
            ReplicaId = defaults.ReplicaId,
            IsMultiReplica = defaults.IsMultiReplica,
            TimeZone = defaults.TimeZone,
            ResolveTimeZone = defaults.ResolveTimeZoneAsync,
            ExpressionNamespaces = sp.GetRequiredService<WorkflowExpressionNamespaceSnapshot>().Registrations,
            AuditTransitions = defaults.AuditTransitions,
            DurableSleepBlockTypeIds = [.. defaults.DurableSleepBlockTypeIds],
            RecoveryAbandonedAfter = options.Recovery.AbandonedAfter,
            SleepPollInterval = options.SleepPollInterval,
            RetryOnlyIdempotentBlocks = options.RetryOnlyIdempotentBlocks,
            DefaultContainerTimeoutSeconds = options.DefaultContainerTimeoutSeconds,
            ParkSleepWhenAtLeast = options.DurableSleep.ParkWhenAtLeast,
            MaxSuspendPayloadBytes = (sp.GetService<WorkflowSuspensionOptions>() ?? new WorkflowSuspensionOptions())
                .MaxStatePayloadBytes,
            HandOverRunsOnStop = options.HandOverRunsOnStop ?? options.Recovery.Enabled,
            RecoveryReentersRuns = options.Recovery.Enabled,
            RunHeartbeatInterval = options.RunHeartbeatInterval,
            RunStateWriteMode = options.RunStateWriteMode,
            MaxCheckpointBytes = options.MaxCheckpointBytes,
            RestartCancelChannel = (sp.GetService<WorkflowClusterSignalChannels>()
                ?? new WorkflowClusterSignalChannels()).RestartCancel,
            CoordinatorIdleTimeout = options.CoordinatorIdleTimeout,
            TimeProvider = sp.GetService<TimeProvider>()
        };
    }

    private static WorkflowGraphExecutorOptions WalkOptionsFrom(
        IServiceProvider sp, WorkflowEngineOptions options) =>
        WorkflowGraphExecutorOptions.For(SettingsFrom(sp, options));

    private static WorkflowGraphExecutor GraphExecutorFrom(IServiceProvider sp, WorkflowEngineOptions options)
    {
        var defaults = Defaults(sp);
        var runState = sp.GetService<IWorkflowRunStateStore>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(options.LoggerCategory);
        return new WorkflowGraphExecutor(
            sp.GetRequiredService<IWorkflowBlocks>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IWorkflowMetrics>(),
            sp.GetRequiredService<IWorkflowNodeObserver>(),
            sp.GetService<IHelperResolver>() ?? NullHelperResolver.Instance,
            WalkOptionsFrom(sp, options),
            sp.GetRequiredService<WorkflowActivitySource>(),
            sp.GetService<IWorkflowBlockSandbox>(),
            new WorkflowTransitionRecorder(
                runState,
                logger,
                new WorkflowTransitionOptions
                {
                    ReplicaId = defaults.ReplicaId,
                    AuditTransitions = defaults.AuditTransitions
                },
                AuditSinkFrom(sp)),
            runState is null
                ? null
                : new WorkflowSleepRunner(
                    runState,
                    logger,
                    new WorkflowSleepOptions
                    {
                        CancellationPollInterval = options.SleepPollInterval,
                        ReplicaId = defaults.ReplicaId,
                        TimeProvider = sp.GetService<TimeProvider>(),
                        HostStopping = HostStopping(sp)
                    }));
    }

    private static WorkflowHostDefaultsOptions Defaults(IServiceProvider sp) =>
        sp.GetService<WorkflowHostDefaultsOptions>()
        ?? throw new InvalidOperationException(
            "AddWorkflowEngine composes the engine out of what AddWorkflows registers, and this container has "
            + "neither. Call services.AddWorkflows(...) — AddWorkflowEngine hangs off the builder it returns.");
}

/// <summary>What the composed engine cannot read off a seam: how much of a run to record, what a durable sleep
/// is here, and whether this host picks up runs whose replica died.</summary>
/// <remarks>Which replica this is and whether it has peers are deliberately absent — they are stated once, in
/// <c>Defaults</c>, and the engine reads them back from there.</remarks>
public sealed class WorkflowEngineOptions
{
    /// <summary>How long an idle run coordinator stays cached before it is rebuilt on the next run.</summary>
    public TimeSpan CoordinatorIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>How much of a run's steps to record.</summary>
    public WorkflowTraceMode TraceMode { get; set; }

    /// <summary>The cap on one serialised value in a persisted payload, past which it is replaced by a note
    /// saying so. Zero truncates every value, so this is a real number rather than "unset".</summary>
    public int MaxPayloadValueBytes { get; set; } = 8 * 1024;

    /// <summary>The most steps, and node log entries, one segment of a run's walk records (from a start or a resume to
    /// its end or next park), 10 000 by default. A walk executing more nodes keeps its first ones and records no later
    /// one, except those carrying an error, up to twice this; the run itself goes on. Bounds the memory a long loop
    /// holds until they are written. Must be positive.</summary>
    public int MaxRecordedSteps { get; set; } = 10_000;

    /// <summary>How long one physical trigger event stays claimed against the fire-de-duplication seam.</summary>
    public TimeSpan FireDedupWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The category the engine's own lines are logged under. A wire name an operator's log filters
    /// read, so a host with filters already written keeps saying whatever they say.</summary>
    public string LoggerCategory { get; set; } = "ReeveWorkflow.Engine";

    /// <summary>How often a durable sleep wakes to check for cancellation. It is also the row's renewal
    /// interval, so it must stay well under <see cref="WorkflowRunRecoveryOptions.AbandonedAfter"/>.</summary>
    /// <remarks>Only the blocks named in <c>Defaults.DurableSleepBlockTypeIds</c> read this; every other
    /// wait happens inside the block that asked for it.</remarks>
    public TimeSpan SleepPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether a node that says nothing about retries inherits its block's answer to
    /// <see cref="IWorkflowBlockFactory.IsIdempotent"/> instead of being retried three times. On by default; a node's own <c>retryEnabled</c> wins either way.</summary>
    /// <remarks>A block that says it is not idempotent and is retried after a timeout runs its effect twice,
    /// which is why this is now on. A host whose factories have not been audited sets it false and does that
    /// first: <see cref="IWorkflowBlockFactory.IsIdempotent"/> answers false unless a factory says otherwise,
    /// so silence reads as "do not retry".</remarks>
    public bool RetryOnlyIdempotentBlocks { get; set; } = true;

    /// <summary>The ceiling a container runs under when its own node names none. Null — the run's own deadline
    /// and nothing else — because the engine cannot size a body and a guessed deadline kills a long loop and then
    /// repeats it whole. A number on the node still wins, and leaf blocks keep their own default either way.</summary>
    /// <remarks>The per-node number lives in a definition row this library owns, so a host with graphs it did
    /// not author has nowhere else to say this. A container nested in another gets the ceiling too, so an outer
    /// one has to cover everything its inner ones do. Non-positive is refused when the engine is built: "no
    /// ceiling" is null.</remarks>
    public int? DefaultContainerTimeoutSeconds { get; set; }

    /// <summary>How often a live run renews its row and re-asserts its cluster lock or slot, so a block longer than
    /// <see cref="WorkflowRunRecoveryOptions.AbandonedAfter"/> is not reclaimed from a replica still running it.
    /// Null turns the heartbeat off, and with it the reason that cutoff can be short.</summary>
    /// <remarks>Refused unless <see cref="WorkflowRunRecoveryOptions.AbandonedAfter"/> is at least three of these:
    /// two missed beats must not be enough for a peer to take a run that is executing. Each beat is one
    /// owner-fenced update of the run's row, plus a lock check or slot renewal where the run holds one.</remarks>
    public TimeSpan? RunHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often a run writes its row, and with it its checkpoint and its fence. Every block by default.</summary>
    /// <remarks>Anything else needs <see cref="RunHeartbeatInterval"/>, which is then the fence between
    /// two writes, and a <see cref="RunStateWriteMode.Heartbeat"/> interval must be shorter than
    /// <see cref="WorkflowRunRecoveryOptions.AbandonedAfter"/>; the call refuses either otherwise. See
    /// <see cref="RunStateWriteMode"/> for what each mode costs a recovery.</remarks>
    public RunStateWriteMode RunStateWriteMode { get; set; } = RunStateWriteMode.EveryBlock;

    /// <summary>The largest checkpoint a run writes, in UTF-8 bytes. Past it the row says so instead, and a recovery
    /// replays the run from its start rather than re-entering it without some of its outputs. 256 KB, the cap on a
    /// parked run's payload, by default.</summary>
    public int MaxCheckpointBytes { get; set; } = 256 * 1024;

    /// <summary>Whether a run this host stops mid-walk leaves its row <c>Running</c> with no owner, for a sweep to
    /// replay, instead of ending it <c>Cancelled</c>. Null — the default — reads
    /// <see cref="WorkflowRunRecoveryOptions.Enabled"/>, the only sweep this package knows of: a released row
    /// nothing picks up stays Running for ever.</summary>
    /// <remarks>1, for the host that sweeps these rows itself. Such a host turns this on with its own
    /// sweep named in the deployment and <see cref="Recovery"/> left off, and a host with neither says <c>false</c>
    /// explicitly rather than by omission. Nothing here decides what sweeps: it decides only what a stop leaves.
    /// <para>A handover also leaves the run's journal entry <c>Running</c>, for whoever takes the row.
    /// A host that sweeps these rows itself owns that entry: this package's sweep closes it or carries the run on
    /// under it, and a host's sweep, which replays from the start under an entry of its own, has to close it —
    /// <see cref="ReeveWorkflow.Core.Execution.WorkflowRunResumer.CloseEntryAsync"/>, on the resumer this call
    /// registers, after ending the row and only when ending it succeeded. The entry a row named is
    /// <c>WorkflowRunCheckpoint.Of(row)?.JournalExecutionId</c>, and the close answers <c>bool</c>: <c>false</c> means
    /// the entry may still be open, and the row is ended by then, so no later pass of that sweep will offer it again.
    /// Left open it stays <c>Running</c> for the life of the database, and every question about whether the run is
    /// still going keeps answering yes.</para></remarks>
    public bool? HandOverRunsOnStop { get; set; }

    /// <summary>Picking up runs whose replica died.</summary>
    public WorkflowRunRecoveryOptions Recovery { get; } = new();

    /// <summary>Whether a long sleep on the root canvas leaves the process and parks as a suspension row.</summary>
    public WorkflowDurableSleepOptions DurableSleep { get; } = new();

    /// <summary>Moving runs that parked on a wait. Left alone it runs every five seconds on every replica,
    /// which is what makes a parked run survive the process it parked in.</summary>
    public WorkflowSuspensionWakeOptions Waker { get; set; } = new();
}

/// <summary>When a durable sleep parks its run instead of waiting in the process. Off unless a threshold is set.</summary>
/// <remarks>A parked sleep is woken by the waker, so a host that turns this on needs it running.</remarks>
public sealed class WorkflowDurableSleepOptions
{
    /// <summary>The shortest sleep that parks. Null keeps every sleep in the process; zero or less is refused.</summary>
    public TimeSpan? ParkWhenAtLeast { get; set; }
}
