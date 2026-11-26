using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.History;

public static class WorkflowHistoryServiceCollectionExtensions
{
    /// <summary>Registers both retention sweeps. <paramref name="clock"/> is asked for rather than resolved,
    /// so a host that owns time hands it here instead of registering TimeProvider for everything it runs.</summary>
    public static IServiceCollection AddWorkflowRetention(
        this IServiceCollection services,
        Action<WorkflowRetentionOptions>? configure = null,
        Func<IServiceProvider, TimeProvider>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<WorkflowRetentionOptions>();
        if (configure is not null) builder.Configure(configure);

        services.TryAddSingleton(WorkflowLeaderGates.DerivedFrom);

        var resolveClock = clock ?? (sp => sp.GetService<TimeProvider>() ?? TimeProvider.System);

        services.AddHostedService(sp => new WorkflowExecutionHistoryRetentionService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<WorkflowRetentionOptions>>(),
            resolveClock(sp),
            sp.GetRequiredService<IWorkflowLeaderGate>(),
            sp.GetRequiredService<ILogger<WorkflowExecutionHistoryRetentionService>>()));

        services.AddHostedService(sp => new WorkflowVersionRetentionService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<WorkflowRetentionOptions>>(),
            resolveClock(sp),
            sp.GetRequiredService<IWorkflowLeaderGate>(),
            sp.GetRequiredService<ILogger<WorkflowVersionRetentionService>>()));

        return services;
    }
}
