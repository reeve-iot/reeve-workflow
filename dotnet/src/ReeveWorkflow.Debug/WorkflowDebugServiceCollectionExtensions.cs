using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug;

public static class WorkflowDebugServiceCollectionExtensions
{
    /// <summary>Wires the whole package: the session service, the seams a host may leave unset, and the sweep.
    /// The clock and the sink are asked for, because a host's own are rarely plain singletons.</summary>
    public static IServiceCollection AddWorkflowDebug(
        this IServiceCollection services,
        Action<WorkflowDebugOptions>? configure = null,
        Func<IServiceProvider, TimeProvider>? clock = null,
        Func<IServiceProvider, IWorkflowAuditSink>? auditSink = null,
        Action<WorkflowDebugSeams>? seams = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        ConfigureOptions(services, configure);

        var declared = new WorkflowDebugSeams();
        seams?.Invoke(declared);
        declared.Register(services);

        var resolveClock = clock ?? (sp => sp.GetService<TimeProvider>() ?? TimeProvider.System);

        Func<IServiceProvider, IWorkflowAuditSink> resolveAudit = auditSink
            ?? (sp => new ScopeOpeningWorkflowAuditSink(sp.GetRequiredService<IServiceScopeFactory>()));

        services.TryAddSingleton(sp => new WorkflowDebugSessionService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IWorkflowDebugNotifier>(),
            sp.GetRequiredService<IWorkflowMetrics>(),
            resolveAudit(sp),
            sp.GetRequiredService<IWorkflowDebugBlobStore>(),
            sp.GetRequiredService<IOptions<WorkflowDebugOptions>>(),
            sp.GetRequiredService<ILogger<WorkflowDebugSessionService>>(),
            resolveClock(sp),
            sp.GetService<IWorkflowClusterSignal>(),
            sp.GetService<WorkflowClusterSignalChannels>()));
        services.TryAddSingleton<IWorkflowDebugSessionService>(
            sp => sp.GetRequiredService<WorkflowDebugSessionService>());

        services.TryAddSingleton<IWorkflowDebugSessions>(
            sp => new WorkflowDebugSessions(sp.GetRequiredService<IWorkflowDebugSessionService>()));

        AddRetentionCore(services, clock, leaderGateAlreadyRegistered: true);

        return services;
    }

    /// <summary>Registers the debug session sweep alone, for a host that keeps the sessions elsewhere.
    /// <see cref="AddWorkflowDebug"/> already calls this; calling both is harmless.</summary>
    public static IServiceCollection AddWorkflowDebugRetention(
        this IServiceCollection services,
        Action<WorkflowDebugOptions>? configure = null,
        Func<IServiceProvider, TimeProvider>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        ConfigureOptions(services, configure);
        AddRetentionCore(services, clock, leaderGateAlreadyRegistered: false);

        return services;
    }

    private static void ConfigureOptions(IServiceCollection services, Action<WorkflowDebugOptions>? configure)
    {
        var builder = services.AddOptions<WorkflowDebugOptions>();
        if (configure is not null) builder.Configure(configure);
    }

    private static void AddRetentionCore(
        IServiceCollection services, Func<IServiceProvider, TimeProvider>? clock, bool leaderGateAlreadyRegistered)
    {
        if (!leaderGateAlreadyRegistered) services.TryAddSingleton(WorkflowLeaderGates.DerivedFrom);

        var resolveClock = clock ?? (sp => sp.GetService<TimeProvider>() ?? TimeProvider.System);

        services.AddHostedService(sp => new WorkflowDebugSessionRetentionService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<WorkflowDebugOptions>>(),
            resolveClock(sp),
            sp.GetRequiredService<IWorkflowLeaderGate>(),
            sp.GetRequiredService<IWorkflowDebugBlobStore>(),
            sp.GetRequiredService<ILogger<WorkflowDebugSessionRetentionService>>()));
    }
}
