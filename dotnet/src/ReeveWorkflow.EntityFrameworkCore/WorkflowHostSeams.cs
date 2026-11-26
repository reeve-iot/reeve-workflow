using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Every seam the engine reaches its host through, as a named slot filled where the library is
/// configured. A slot left alone keeps the library's own answer.</summary>
/// <remarks>The whole point is that a host stops describing itself in two places. Registering a seam elsewhere
/// in the container still works, but then which of the two lines runs first decides which implementation the
/// engine gets — and the answer for an unfilled authorization slot is to grant nothing, which is not a thing to
/// leave to the order two blocks of code happen to sit in.</remarks>
public sealed class WorkflowHostSeams
{
    private readonly WorkflowHostDefaultsOptions defaults;
    private IReadOnlyList<WorkflowExpressionNamespace> expressionNamespaceSnapshot = [];

    internal WorkflowHostSeams(WorkflowHostDefaultsOptions defaults)
    {
        this.defaults = defaults;

        Store = new WorkflowSeam<IWorkflowStore>(ServiceDescriptor.Scoped<IWorkflowStore>(
            sp => new EfWorkflowStore(sp.GetRequiredService<WorkflowDbContext>())), ServiceLifetime.Scoped);

        Catalog = new WorkflowSeam<IWorkflowCatalog>(
            ServiceDescriptor.Scoped<IWorkflowCatalog, WorkflowCatalog>());

        RunJournal = new WorkflowSeam<IWorkflowRunJournal>(ServiceDescriptor.Scoped<IWorkflowRunJournal>(
            sp => new EfWorkflowRunJournal(
                sp.GetRequiredService<WorkflowDbContext>(),
                sp.GetService<IEnumerable<IWorkflowRunLogContributor>>())), ServiceLifetime.Scoped);

        RunScopeFactory = new WorkflowSeam<IWorkflowRunScopeFactory>(
            ServiceDescriptor.Singleton<IWorkflowRunScopeFactory>(sp =>
            {
                sp.GetService<SingletonJournalRefusal>()?.ThrowIfASingletonAnswers();
                return new EfWorkflowRunScopeFactory(sp.GetRequiredService<IServiceScopeFactory>());
            }));

        RunStateStore = new WorkflowSeam<IWorkflowRunStateStore>(
            ServiceDescriptor.Singleton<IWorkflowRunStateStore>(
                sp => new EfWorkflowRunStateStore(
                    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<TimeProvider>())));

        Metrics = new WorkflowSeam<IWorkflowMetrics>(
            ServiceDescriptor.Singleton<IWorkflowMetrics>(_ => new MeterWorkflowMetrics(this.defaults.Metrics)));

        NodeObserver = new WorkflowSeam<IWorkflowNodeObserver>(
            ServiceDescriptor.Singleton<IWorkflowNodeObserver>(NullWorkflowNodeObserver.Instance));

        ExecutionGate = new WorkflowSeam<IWorkflowExecutionGate>(
            ServiceDescriptor.Singleton<IWorkflowExecutionGate>(AlwaysRunningExecutionGate.Instance));

        StartAdmission = new WorkflowSeam<IWorkflowStartAdmission>();

        NodeValidator = new WorkflowSeam<IWorkflowNodeValidator>();
        StartArgumentValidator = new WorkflowSeam<IWorkflowStartArgumentValidator>();

        AuditSink = new WorkflowSeam<IWorkflowAuditSink>(
            ServiceDescriptor.Singleton<IWorkflowAuditSink, LoggingWorkflowAuditSink>());

        Authorization = new WorkflowSeam<IWorkflowAuthorization>(
            ServiceDescriptor.Singleton<IWorkflowAuthorization>(sp => new ConfiguredWorkflowAuthorization(
                this.defaults.GrantedCapabilities,
                sp.GetRequiredService<ILogger<ConfiguredWorkflowAuthorization>>())));

        Blocks = new WorkflowSeam<IWorkflowBlocks>(
            ServiceDescriptor.Singleton<IWorkflowBlocks>(
                sp => sp.GetRequiredService<RegisteredWorkflowBlocks>()));


        RunRecoveryStore = new WorkflowSeam<IWorkflowRunRecoveryStore>(
            ServiceDescriptor.Singleton<IWorkflowRunRecoveryStore>(
                sp => new EfWorkflowRunRecoveryStore(sp.GetRequiredService<IServiceScopeFactory>())));

        CompilationStore = new WorkflowSeam<IWorkflowCompilationStore>(
            ServiceDescriptor.Singleton<IWorkflowCompilationStore>(
                sp => new EfWorkflowCompilationStore(
                    sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<ILogger<EfWorkflowCompilationStore>>(),
                    sp.GetService<TimeProvider>())));

        BlockSandbox = new WorkflowSeam<IWorkflowBlockSandbox>();

        SuspensionStore = new WorkflowSeam<IWorkflowSuspensionStore>(
            ServiceDescriptor.Singleton<IWorkflowSuspensionStore>(sp => new EfWorkflowSuspensionStore(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<WorkflowExpressionNamespaceSnapshot>().Registrations,
                sp.GetService<WorkflowSuspensionOptions>())));

        SuspensionResumeStore = new WorkflowSeam<IWorkflowSuspensionResumeStore>(
            ServiceDescriptor.Singleton<IWorkflowSuspensionResumeStore>(
                sp => new EfWorkflowSuspensionResumeStore(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<WorkflowExpressionNamespaceSnapshot>().Registrations)));

        SuspensionWakeSource = new WorkflowSeam<IWorkflowSuspensionWakeSource>();

        RestartGenerations = new WorkflowSeam<IWorkflowRestartGenerations>(
            ServiceDescriptor.Singleton<IWorkflowRestartGenerations>(
                sp => new EfWorkflowRestartGenerations(sp.GetRequiredService<IServiceScopeFactory>())));

        RunSlotGate = new WorkflowSeam<IWorkflowRunSlotGate>();
        FireDedup = new WorkflowSeam<IWorkflowFireDedup>();
        RunLockProvider = new WorkflowSeam<IWorkflowRunLockProvider>();
        ClusterSignal = new WorkflowSeam<IWorkflowClusterSignal>();
    }

    internal IReadOnlyList<WorkflowExpressionNamespace> ExpressionNamespaceSnapshot => expressionNamespaceSnapshot;
    internal void CaptureExpressionNamespaces() => expressionNamespaceSnapshot = defaults.ExpressionNamespaces.ToArray();

    /// <summary>What to run, and which version of it. Reads only. Each run scope reads it, so admission, the
    /// resumer, the engine's replay and pin reads and the compiler all read what fills it.</summary>
    /// <remarks><c>Use&lt;T&gt;()</c> and <c>Use(factory)</c> register it scoped when no lifetime is named. A
    /// singleton, named explicitly or with <c>UseInstance</c>, is allowed, and is read by every concurrent run, so it
    /// must be safe for that.</remarks>
    public WorkflowSeam<IWorkflowStore> Store { get; }

    /// <summary>Every read a request makes of a workflow, its runs and its versions.</summary>
    public WorkflowSeam<IWorkflowCatalog> Catalog { get; }

    /// <summary>Where a run's own record, steps and log entries go. Each run scope reads it.</summary>
    /// <remarks>A journal holds the run it opened or loaded, since <c>SaveRunAsync</c> names no row, so it is
    /// scoped: <c>Use&lt;T&gt;()</c> and <c>Use(factory)</c> register it scoped when no lifetime is named. A
    /// singleton is refused, whether it fills this slot or is registered in the container: at <c>AddWorkflows</c>
    /// when it is registered by then, and otherwise when the run scope factory or a run scope is first built.</remarks>
    public WorkflowSeam<IWorkflowRunJournal> RunJournal { get; }

    /// <summary>Opens the per-run world the engine works inside.</summary>
    public WorkflowSeam<IWorkflowRunScopeFactory> RunScopeFactory { get; }

    /// <summary>Where a run is, durably, so another replica can pick it up after this one dies.</summary>
    public WorkflowSeam<IWorkflowRunStateStore> RunStateStore { get; }

    /// <summary>Every moment of a run worth counting.</summary>
    public WorkflowSeam<IWorkflowMetrics> Metrics { get; }

    /// <summary>Per-node progress, for whoever is watching a run happen.</summary>
    public WorkflowSeam<IWorkflowNodeObserver> NodeObserver { get; }

    /// <summary>A host-level stop on starting work at all.</summary>
    public WorkflowSeam<IWorkflowExecutionGate> ExecutionGate { get; }

    /// <summary>Whether a new run may start, asked once per start attempt by the admission every start passes: the
    /// mounted execute endpoint, a host's call to the engine, a fire, a sub-workflow block. An admit is not a run: the
    /// lease may still refuse it. Never for a resume, a re-entry, a replay or a retry. Left alone, admits every start.</summary>
    /// <remarks>Resolved once with the engine, so fill it with a singleton.</remarks>
    public WorkflowSeam<IWorkflowStartAdmission> StartAdmission { get; }

    /// <summary>The host's check of the nodes of a version, against facts only it knows: asked at every admission, once for the
    /// started version and once per version it pins, and by the editor on every save and publish. A finding refuses the start
    /// or stops the publish; a throw refuses the start with <c>start-admission-faulted</c> and blocks the publish with
    /// <c>host-validator-faulted</c>. Left alone, finds nothing.</summary>
    /// <remarks>Resolved once with the engine, so fill it with a singleton.</remarks>
    public WorkflowSeam<IWorkflowNodeValidator> NodeValidator { get; }

    /// <summary>The host's check of the start arguments whose type holds one of its own atoms or constructors, asked once per
    /// start after each argument has been read as its parameter's type. A finding refuses the start with
    /// <c>start-arguments-invalid</c>; a throw refuses it with <c>start-admission-faulted</c>. Left alone, finds nothing.</summary>
    /// <remarks>Resolved once with the engine, so fill it with a singleton.</remarks>
    public WorkflowSeam<IWorkflowStartArgumentValidator> StartArgumentValidator { get; }

    /// <summary>What a run records for an operator to read later.</summary>
    public WorkflowSeam<IWorkflowAuditSink> AuditSink { get; }

    /// <summary>Whether a principal may do a thing. Scoped in most hosts, because the answer is a query.</summary>
    public WorkflowSeam<IWorkflowAuthorization> Authorization { get; }

    /// <summary>The blocks this host has, for the graph walk and for any reader that offers them. One slot,
    /// because the lookup and the enumeration are two questions about one collection.</summary>
    public WorkflowSeam<IWorkflowBlocks> Blocks { get; }


    /// <summary>Finding the runs whose replica died, which is the one run-state question with no id to ask it
    /// with. Filled by this package, over the same table the run-state seam writes.</summary>
    public WorkflowSeam<IWorkflowRunRecoveryStore> RunRecoveryStore { get; }

    /// <summary>Where a compiled verdict is remembered between calls and between restarts. Filled by this
    /// package, over its own table, with a singleton that opens a scope of its own for every call.</summary>
    public WorkflowSeam<IWorkflowCompilationStore> CompilationStore { get; }

    /// <summary>The port types the host registers: one record per atom and one per constructor. Applied onto the
    /// library's built-ins when <c>AddWorkflows</c> runs, into the one <see cref="IWorkflowTypeRegistry"/> it
    /// registers; a record the vocabulary refuses stops startup there.</summary>
    public WorkflowPortTypeRegistrations PortTypes { get; } = new();

    /// <summary>Registers each host namespace's compile check and runtime resolver together. A namespace named <c>trigger</c> with one
    /// key segment also converts a fire's payload at admission; without one, the payload's keys are dropped.</summary>
    public IList<WorkflowExpressionNamespace> ExpressionNamespaces => defaults.ExpressionNamespaces;

    /// <summary>What a block the host did not write is allowed to reach. No library default.</summary>
    public WorkflowSeam<IWorkflowBlockSandbox> BlockSandbox { get; }

    /// <summary>Where a run parks while it waits. Filled by this package, over the workflow context's own
    /// suspension table.</summary>
    public WorkflowSeam<IWorkflowSuspensionStore> SuspensionStore { get; }

    /// <summary>Which parked runs are ready to move, and taking one — the two suspension questions with no id
    /// to ask them with. Filled by this package, over the same table.</summary>
    public WorkflowSeam<IWorkflowSuspensionResumeStore> SuspensionResumeStore { get; }

    /// <summary>Where the host says a parked run is ready now, so a wait over host state is event-driven rather
    /// than polled. No library default: only the host knows what it watches.</summary>
    /// <remarks>Left alone, the waker's passes are what move every parked run, exactly as
    /// before. A wake is a hint about when to look and not an answer about what is true — the waker still reads
    /// the row and evaluates its condition — with one exception, a trigger, whose wait has no condition.</remarks>
    public WorkflowSeam<IWorkflowSuspensionWakeSource> SuspensionWakeSource { get; }

    /// <summary>Which Restart trigger is the newest, read by every replica from one place. Filled by this package,
    /// over two tables of the workflow context.</summary>
    /// <remarks>A host that has declared peers is refused when this answers with
    /// <see cref="InProcessRestartGenerations"/>.</remarks>
    public WorkflowSeam<IWorkflowRestartGenerations> RestartGenerations { get; }

    /// <summary>The cluster-wide run budget. Absent means in-process gating only.</summary>
    /// <remarks>This seam and the three below are left unclaimed when nobody fills them, rather than
    /// defaulting to <c>SingleReplicaRunSlotGate</c> and its siblings. A default would be registered with
    /// <c>TryAdd</c> at the point <c>AddWorkflows</c> runs, and a host that registers its own cluster seam
    /// the same way afterwards — an ordinary shape, and the one this library's own debug package uses —
    /// would then silently receive the single-replica answer in place of its cluster one. Naming an absence
    /// is worth something; naming it by occupying the service type is not.</remarks>
    public WorkflowSeam<IWorkflowRunSlotGate> RunSlotGate { get; }

    /// <summary>Collapses the N-replica fan-out of one physical trigger event. Left alone, admits every fire.</summary>
    public WorkflowSeam<IWorkflowFireDedup> FireDedup { get; }

    /// <summary>The cluster-wide "at most one run" gate for the one-permit modes. Left alone, nothing outside
    /// this process can contend the budget.</summary>
    public WorkflowSeam<IWorkflowRunLockProvider> RunLockProvider { get; }

    /// <summary>The one transport every cross-replica signal travels on — restart supersession and the
    /// debugger's two. Left alone, each of them stops at this process's edge.</summary>
    /// <remarks>Registering the transport in the container answers it too, on either side of
    /// <c>AddWorkflows</c>: both the engine and the debug package read it when the container is asked.</remarks>
    /// <remarks>Unlike the three seams above, a clustered host is not refused for leaving this one alone: its
    /// absence loses a takeover or a step rather than admitting a run that should not have started, and refusing
    /// over it would refuse deployments that are merely degraded. <c>ReeveWorkflow.Redis</c> fills it through
    /// <c>Seams.ClusterSignal.UseRedis()</c>.</remarks>
    public WorkflowSeam<IWorkflowClusterSignal> ClusterSignal { get; }

    internal void Register(IServiceCollection services)
    {

        services.TryAddSingleton(sp => new RegisteredWorkflowBlocks(sp.GetServices<IWorkflowBlockFactory>()));
        services.TryAddScoped<IWorkflowDefinitionSecrets>(sp =>
            new WorkflowDefinitionSecrets(sp.GetRequiredService<IWorkflowBlocks>(), sp.GetService<IWorkflowSecretCodec>()));

        Store.Register(services);
        Catalog.Register(services);
        RunJournal.Register(services);
        RunScopeFactory.Register(services);
        RunStateStore.Register(services);
        RunRecoveryStore.Register(services);
        CompilationStore.Register(services);
        RestartGenerations.Register(services);
        Metrics.Register(services);
        NodeObserver.Register(services);
        ExecutionGate.Register(services);
        StartAdmission.Register(services);
        NodeValidator.Register(services);
        StartArgumentValidator.Register(services);
        AuditSink.Register(services);
        Authorization.Register(services);
        Blocks.Register(services);
        BlockSandbox.Register(services);
        SuspensionStore.Register(services);
        SuspensionResumeStore.Register(services);
        SuspensionWakeSource.Register(services);
        RunSlotGate.Register(services);
        FireDedup.Register(services);
        RunLockProvider.Register(services);
        ClusterSignal.Register(services);
        RegisterTheCompiler(services);
        SingletonJournalRefusal.ThrowIfASingletonAnswers(services);
        services.TryAddSingleton(new SingletonJournalRefusal(services));
        RefuseSingleReplicaSeamsOnAClusteredHost(services);
    }

    private void RegisterTheCompiler(IServiceCollection services)
    {
        var types = new WorkflowTypeRegistry(PortTypes.Records);
        services.AddSingleton<IWorkflowTypeRegistry>(types);
        var namespaces = ExpressionNamespaceSnapshot;
        _ = new CompilationOptions { TypeRegistry = types, NamespaceRegistrations = namespaces };
        services.AddSingleton(sp => new CompilationOptions
        {
            TypeRegistry = types,
            NamespaceRegistrations = namespaces,
            HelperResolver = sp.GetService<IHelperResolver>() ?? NullHelperResolver.Instance,
        });

        services.TryAddSingleton(sp => new WorkflowCompilationService(
            sp.GetRequiredService<IWorkflowBlocks>(),
            sp.GetRequiredService<IWorkflowRunScopeFactory>(),
            sp.GetRequiredService<CompilationOptions>(),
            sp.GetService<ILogger<WorkflowCompilationService>>(),
            sp.GetService<TimeProvider>()));
    }

    private static bool Registered(IServiceCollection services, Type serviceType) => services.Any(
        descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == serviceType);

    private void RefuseSingleReplicaSeamsOnAClusteredHost(IServiceCollection services)
    {
        if (!defaults.IsMultiReplica)
            return;

        var unanswered = new List<string>();
        Check(typeof(IWorkflowRunSlotGate), typeof(SingleReplicaRunSlotGate),
            nameof(RunSlotGate), "the cluster run budget");
        Check(typeof(IWorkflowFireDedup), typeof(SingleReplicaFireDedup),
            nameof(FireDedup), "one trigger event reaching every replica");
        Check(typeof(IWorkflowRunLockProvider), typeof(SingleReplicaRunLockProvider),
            nameof(RunLockProvider), "Single and Restart across replicas");
        Check(typeof(IWorkflowRestartGenerations), typeof(InProcessRestartGenerations),
            nameof(RestartGenerations), "which Restart trigger is the newest");

        if (unanswered.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Defaults.IsMultiReplica is true, so this deployment has peers, but {unanswered.Count} cluster "
            + $"seam(s) have no cross-replica answer: Seams.{string.Join(", Seams.", unanswered)}. "
            + "Each of them degrades to in-process behaviour rather than failing, so the deployment would "
            + "run more than it was configured to and say nothing. Fill them — ReeveWorkflow.Redis supplies "
            + "the run slot gate through Seams.RunSlotGate.UseRedis, which needs an IConnectionMultiplexer "
            + "in the container to gate anything — or set Defaults.IsMultiReplica to false if this really is "
            + "one process. A seam registered in the container after the AddWorkflows call is not visible "
            + "from here, and neither is one registered under a key; name it in its slot instead.");

        void Check(Type serviceType, Type singleReplicaType, string name, string what)
        {
            var answering = services.LastOrDefault(
                descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == serviceType);

            var implementation = answering?.ImplementationType ?? answering?.ImplementationInstance?.GetType();
            if (answering is not null && implementation != singleReplicaType)
                return;

            unanswered.Add($"{name} ({what})");
        }
    }
}
