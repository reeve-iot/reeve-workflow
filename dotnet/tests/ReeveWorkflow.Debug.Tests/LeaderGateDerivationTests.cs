using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug.Tests;

public class LeaderGateDerivationTests
{
    [Fact]
    public void A_host_that_named_a_run_lock_provider_leads_by_holding_it()
    {
        using var provider = Build(options => options.Seams.RunLockProvider.UseInstance(new ClusterRunLockProvider()));

        provider.GetRequiredService<IWorkflowLeaderGate>().Should().BeOfType<LockedLeaderGate>();
    }

    [Fact]
    public void A_host_with_no_lock_provider_at_all_still_sweeps()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        WorkflowDebugServiceCollectionExtensions.AddWorkflowDebugRetention(services);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IWorkflowLeaderGate>().Should().BeOfType<SingleReplicaLeaderGate>();
    }

    [Fact]
    public void A_host_that_named_a_gate_of_its_own_keeps_it()
    {
        using var provider = Build(
            options => options.Seams.RunLockProvider.UseInstance(new ClusterRunLockProvider()),
            debug => debug.LeaderGate.UseInstance(new HostLeaderGate()));

        provider.GetRequiredService<IWorkflowLeaderGate>().Should().BeOfType<HostLeaderGate>();
    }

    [Fact]
    public void A_host_that_left_the_run_lock_slot_empty_sweeps_on_every_replica()
    {
        using var provider = Build(_ => { });

        provider.GetRequiredService<IWorkflowLeaderGate>().Should().BeOfType<SingleReplicaLeaderGate>();
    }

    [Fact]
    public async Task The_derived_gate_over_the_single_replica_provider_still_runs_the_sweep()
    {
        using var provider = Build(options => options.Seams.RunLockProvider.Use<SingleReplicaRunLockProvider>());
        var ran = 0;

        provider.GetRequiredService<IWorkflowLeaderGate>().Should().BeOfType<LockedLeaderGate>();
        await provider.GetRequiredService<IWorkflowLeaderGate>().RunUnderLeaderAsync(
            "workflow-debug-retention", _ => { ran++; return Task.CompletedTask; }, CancellationToken.None);

        ran.Should().Be(1);
    }

    [Fact]
    public void A_run_lock_provider_the_host_made_scoped_is_refused_with_the_slot_it_came_from()
    {
        using var provider = Build(
            options => options.Seams.RunLockProvider.Use<ClusterRunLockProvider>(ServiceLifetime.Scoped),
            validateScopes: true);

        var resolving = () => provider.GetRequiredService<IWorkflowLeaderGate>();

        resolving.Should().Throw<InvalidOperationException>().WithMessage("*Seams.RunLockProvider*");
    }

    private static ServiceProvider Build(
        Action<WorkflowPersistenceOptions> configure,
        Action<WorkflowDebugSeams>? debug = null,
        bool validateScopes = false)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(new SqliteConnection("Filename=:memory:")));
            configure(options);
        });
        WorkflowDebugServiceCollectionExtensions.AddWorkflowDebug(
            services,
            seams: seams =>
            {
                seams.BlobStore.UseInstance(new UnusedBlobStore());
                debug?.Invoke(seams);
            });
        return services.BuildServiceProvider(validateScopes);
    }

    private sealed class ClusterRunLockProvider : IWorkflowRunLockProvider
    {
        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class HostLeaderGate : IWorkflowLeaderGate
    {
        public Task RunUnderLeaderAsync(string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedBlobStore : IWorkflowDebugBlobStore
    {
        public Task PutBytesAsync(string key, byte[] bytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
