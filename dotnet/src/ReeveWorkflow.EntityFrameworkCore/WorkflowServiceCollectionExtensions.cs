using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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

/// <summary>The one call that registers the workflow library's persistence, and the type every optional package
/// hangs its own <c>AddX()</c> on.</summary>
public static class WorkflowServiceCollectionExtensions
{
    /// <summary>Registers the workflow context and returns the builder the add-on packages extend. Options are
    /// composed when the container builds it, so an AddX() written after this call still reaches it.</summary>
    public static IWorkflowBuilder AddWorkflows(
        this IServiceCollection services, Action<WorkflowPersistenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WorkflowPersistenceOptions();
        configure(options);
        options.Seams.CaptureExpressionNamespaces();
        services.AddSingleton(new WorkflowExpressionNamespaceSnapshot(options.Seams.ExpressionNamespaceSnapshot));

        if (options.Configurators.Count == 0)
        {
            throw new InvalidOperationException(
                "AddWorkflows was given no database provider. Call options.ConfigureDbContext(b => b.UseNpgsql(...)).");
        }

        RefuseAReplicaNameNoColumnHolds(options.Defaults);

        var sweep = options.CompilationSweep.CheckedCopy();

        foreach (var configurator in options.Configurators)
        {
            services.AddSingleton(configurator);
        }

        services.AddDbContextPool<WorkflowDbContext>((serviceProvider, optionsBuilder) =>
        {
            foreach (var configurator in serviceProvider.GetServices<IWorkflowDbContextConfigurator>())
            {
                configurator.Configure(optionsBuilder);
            }

        });

        services.AddSingleton(options.Defaults);

        services.TryAddScoped<IWorkflowSchemaMigrator, EfWorkflowSchemaMigrator>();

        services.AddSingleton(sp =>
            new WorkflowClusterIdentity(sp.GetRequiredService<WorkflowHostDefaultsOptions>().ReplicaId));
        services.AddSingleton(options.Defaults.SignalChannels);

        services.AddSingleton(options.Defaults.Tracing);
        services.AddSingleton(sp => new WorkflowActivitySource(sp.GetRequiredService<WorkflowTracingOptions>()));

        options.Seams.Register(services);
        AddCompilationSweep(services, sweep);

        services.TryAddKeyedSingleton(ExpressionPreviewService.LibraryServiceKey, (sp, _) =>
        {
            var defaults = sp.GetRequiredService<WorkflowHostDefaultsOptions>();
            return new ExpressionPreviewService(
                sp.GetService<IHelperResolver>(),
                sp.GetRequiredService<WorkflowExpressionNamespaceSnapshot>().Registrations,
                defaults.TimeZone,
                defaults.ResolveTimeZoneAsync);
        });

        services.TryAddSingleton<WorkflowStartupChecks>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WorkflowStartupCheckService>());

        return new WorkflowBuilder(services);
    }

    private static void RefuseAReplicaNameNoColumnHolds(WorkflowHostDefaultsOptions defaults)
    {
        if (defaults.ReplicaId is not { Length: > WorkflowHostDefaultsOptions.ReplicaIdColumnWidth } name)
            return;

        throw new InvalidOperationException(
            $"Defaults.ReplicaId is {name.Length} characters long, and the owned_by_replica_id column holds "
            + $"{WorkflowHostDefaultsOptions.ReplicaIdColumnWidth}: this process's name is written onto every "
            + "run-state row it owns, so shorten it. A longer one fails every one of those writes once a run is "
            + "under way.");
    }

    private static void AddCompilationSweep(IServiceCollection services, WorkflowCompilationSweepOptions sweep)
    {
        services.TryAddScoped(sp => new WorkflowCompilationSweeper(
            sp.GetRequiredService<WorkflowCompilationService>(), sp.GetService<IWorkflowCompilationStore>())
        {
            Retention = sweep.Retention,
        });

        services.TryAddSingleton(sp => new WorkflowCompilationSweepRunner(
            sp.GetRequiredService<IServiceScopeFactory>(), sweep,
            sp.GetService<IWorkflowLeaderGate>() ?? WorkflowLeaderGates.DerivedFrom(sp),
            sp.GetService<ILogger<WorkflowCompilationSweepRunner>>()));

        if (!sweep.Enabled)
            return;

        services.AddHostedService(sp => new WorkflowCompilationSweepService(
            sp.GetRequiredService<WorkflowCompilationSweepRunner>(), sweep,
            sp.GetRequiredService<IHostApplicationLifetime>(), sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILogger<WorkflowCompilationSweepService>>()));
    }

    /// <summary>Adds one more mutation of the workflow context's options. This is how an add-on package changes a
    /// context registered by a call it does not own, whatever order the two calls are written in.</summary>
    public static IWorkflowBuilder ConfigureDbContext(
        this IWorkflowBuilder builder, Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton<IWorkflowDbContextConfigurator>(
            new DelegateWorkflowDbContextConfigurator(configure));
        return builder;
    }
}
