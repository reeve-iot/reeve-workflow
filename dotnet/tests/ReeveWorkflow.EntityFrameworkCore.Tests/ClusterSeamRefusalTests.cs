using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class ClusterSeamRefusalTests
{
    [Fact]
    public void A_host_that_declared_peers_and_filled_nothing_does_not_start()
    {
        var refusal = Refusal(options => options.Defaults.IsMultiReplica = true);

        refusal.Message.Should().Contain("Seams.RunSlotGate")
            .And.Contain("Seams.FireDedup")
            .And.Contain("Seams.RunLockProvider",
                "a host missing three seams should learn about three, not about the first one");
    }

    [Fact]
    public void Ordering_restart_triggers_in_one_process_does_not_answer_a_host_that_declared_peers()
    {
        var refusal = Refusal(options =>
        {
            options.Defaults.IsMultiReplica = true;
            options.Seams.RunSlotGate.Use<ClusterSlotGate>();
            options.Seams.FireDedup.Use<ClusterFireDedup>();
            options.Seams.RunLockProvider.Use<ClusterRunLockProvider>();
            options.Seams.RestartGenerations.UseInstance(new InProcessRestartGenerations());
        });

        refusal.Message.Should().Contain("Seams.RestartGenerations (which Restart trigger is the newest)")
            .And.NotContain("Seams.RunLockProvider (");
    }

    [Fact]
    public void A_seam_the_host_registered_in_the_container_answers_the_refusal_too()
    {
        using var provider = Build(
            before: services =>
            {
                services.AddSingleton<IWorkflowRunSlotGate, ClusterSlotGate>();
                services.AddSingleton<IWorkflowFireDedup, ClusterFireDedup>();
                services.AddSingleton<IWorkflowRunLockProvider, ClusterRunLockProvider>();
            },
            configure: options => options.Defaults.IsMultiReplica = true);

        provider.GetRequiredService<IWorkflowRunSlotGate>().Should().BeOfType<ClusterSlotGate>();
    }

    [Fact]
    public void The_deployment_can_read_back_the_statement_it_made_about_having_peers()
    {
        using var provider = Build(configure: options => options.Defaults.IsMultiReplica = true,
            before: services =>
            {
                services.AddSingleton<IWorkflowRunSlotGate, ClusterSlotGate>();
                services.AddSingleton<IWorkflowFireDedup, ClusterFireDedup>();
                services.AddSingleton<IWorkflowRunLockProvider, ClusterRunLockProvider>();
            });

        provider.GetRequiredService<WorkflowHostDefaultsOptions>().IsMultiReplica.Should().BeTrue();
    }

    [Fact]
    public void The_refusal_names_only_the_seams_still_on_the_single_replica_answer()
    {
        var refusal = Refusal(options =>
        {
            options.Defaults.IsMultiReplica = true;
            options.Seams.RunSlotGate.Use<ClusterSlotGate>();
            options.Seams.FireDedup.Use<ClusterFireDedup>();
        });

        refusal.Message.Should().Contain("Seams.RunLockProvider")
            .And.NotContain("Seams.RunSlotGate (")
            .And.NotContain("Seams.FireDedup (");
    }

    [Fact]
    public void A_host_that_never_mentioned_peers_starts_with_the_cluster_seams_unanswered()
    {
        using var provider = Build();

        provider.GetService<IWorkflowRunSlotGate>().Should().BeNull();
        provider.GetService<IWorkflowFireDedup>().Should().BeNull();
        provider.GetService<IWorkflowRunLockProvider>().Should().BeNull();
    }

    [Fact]
    public void A_cluster_seam_registered_with_TryAdd_after_AddWorkflows_is_still_the_one_that_answers()
    {
        using var provider = Build(after: services =>
        {
            services.TryAddSingleton<IWorkflowRunSlotGate, ClusterSlotGate>();
            services.TryAddSingleton<IWorkflowFireDedup, ClusterFireDedup>();
            services.TryAddSingleton<IWorkflowRunLockProvider, ClusterRunLockProvider>();
        });

        provider.GetRequiredService<IWorkflowRunSlotGate>().Should().BeOfType<ClusterSlotGate>();
        provider.GetRequiredService<IWorkflowFireDedup>().Should().BeOfType<ClusterFireDedup>();
        provider.GetRequiredService<IWorkflowRunLockProvider>().Should().BeOfType<ClusterRunLockProvider>();
    }

    [Fact]
    public void Naming_the_single_replica_seam_does_not_answer_a_host_that_declared_peers()
    {
        var refusal = Refusal(options =>
        {
            options.Defaults.IsMultiReplica = true;
            options.Seams.RunSlotGate.Use<SingleReplicaRunSlotGate>();
            options.Seams.FireDedup.Use<SingleReplicaFireDedup>();
            options.Seams.RunLockProvider.Use<SingleReplicaRunLockProvider>();
        });

        refusal.Message.Should().Contain("Seams.RunSlotGate")
            .And.Contain("Seams.FireDedup")
            .And.Contain("Seams.RunLockProvider");
    }

    [Fact]
    public void Naming_the_single_replica_seam_as_an_instance_does_not_answer_a_host_that_declared_peers()
    {
        var refusal = Refusal(options =>
        {
            options.Defaults.IsMultiReplica = true;
            options.Seams.RunSlotGate.UseInstance(new SingleReplicaRunSlotGate());
            options.Seams.FireDedup.UseInstance(new SingleReplicaFireDedup());
            options.Seams.RunLockProvider.UseInstance(new SingleReplicaRunLockProvider());
        });

        refusal.Message.Should().Contain("Seams.RunSlotGate")
            .And.Contain("Seams.FireDedup")
            .And.Contain("Seams.RunLockProvider");
    }

    [Fact]
    public void A_cluster_seam_registered_only_under_a_key_does_not_answer_at_all()
    {
        var refusal = Refusal(
            before: services =>
            {
                services.AddKeyedSingleton<IWorkflowRunSlotGate, ClusterSlotGate>("cluster");
                services.AddKeyedSingleton<IWorkflowFireDedup, ClusterFireDedup>("cluster");
                services.AddKeyedSingleton<IWorkflowRunLockProvider, ClusterRunLockProvider>("cluster");
            },
            configure: options => options.Defaults.IsMultiReplica = true);

        refusal.Message.Should().Contain("Seams.RunSlotGate")
            .And.Contain("Seams.FireDedup")
            .And.Contain("Seams.RunLockProvider");
    }

    [Fact]
    public void The_defaults_the_container_answers_with_are_the_ones_the_refusal_read()
    {
        using var provider = Build(
            before: services => services.AddSingleton(new WorkflowHostDefaultsOptions { IsMultiReplica = true }),
            configure: options => options.Defaults.IsMultiReplica = false);

        provider.GetRequiredService<WorkflowHostDefaultsOptions>().IsMultiReplica.Should().BeFalse(
            "the deployment that was validated is the one that must run");
    }

    [Fact]
    public void A_host_that_declared_peers_and_filled_the_three_starts()
    {
        using var provider = Build(configure: options =>
        {
            options.Defaults.IsMultiReplica = true;
            options.Seams.RunSlotGate.Use<ClusterSlotGate>();
            options.Seams.FireDedup.Use<ClusterFireDedup>();
            options.Seams.RunLockProvider.Use<ClusterRunLockProvider>();
        });

        provider.GetRequiredService<IWorkflowRunLockProvider>().Should().BeOfType<ClusterRunLockProvider>();
    }

    [Fact]
    public void The_cluster_signal_is_not_one_of_the_three_a_clustered_host_must_fill()
    {
        using var provider = Build(configure: options =>
        {
            options.Defaults.IsMultiReplica = true;
            options.Seams.RunSlotGate.Use<ClusterSlotGate>();
            options.Seams.FireDedup.Use<ClusterFireDedup>();
            options.Seams.RunLockProvider.Use<ClusterRunLockProvider>();
        });

        provider.GetService<IWorkflowClusterSignal>().Should().BeNull(
            "a clustered host starts without it, which is the exemption this test is named for");
    }

    [Fact]
    public void The_two_new_slots_beat_a_separate_registration_written_before_AddWorkflows()
    {
        using var provider = Build(
            before: services =>
            {
                services.AddSingleton<IWorkflowRunLockProvider, OtherRunLockProvider>();
            },
            configure: options =>
            {
                options.Seams.RunLockProvider.Use<ClusterRunLockProvider>();
            });

        provider.GetRequiredService<IWorkflowRunLockProvider>().Should().BeOfType<ClusterRunLockProvider>();
    }

    [Fact]
    public void A_registration_written_after_AddWorkflows_still_beats_the_slot()
    {
        using var provider = Build(
            after: services => services.AddSingleton<IWorkflowRunLockProvider, OtherRunLockProvider>(),
            configure: options => options.Seams.RunLockProvider.Use<ClusterRunLockProvider>());

        provider.GetRequiredService<IWorkflowRunLockProvider>().Should().BeOfType<OtherRunLockProvider>();
    }

    [Fact]
    public void A_new_slot_left_alone_still_yields_to_a_separate_registration()
    {
        using var provider = Build(
            before: services => services.AddSingleton<IWorkflowRunLockProvider, OtherRunLockProvider>());

        provider.GetRequiredService<IWorkflowRunLockProvider>().Should().BeOfType<OtherRunLockProvider>();
    }

    [Fact]
    public void The_expression_preview_endpoint_finds_its_service_without_the_host_registering_it()
    {
        using var provider = Build();

        provider.GetKeyedService<ExpressionPreviewService>(ExpressionPreviewService.LibraryServiceKey)
            .Should().NotBeNull();
    }

    [Fact]
    public void The_unkeyed_preview_service_type_stays_the_host_s_to_claim()
    {
        using var provider = Build();

        provider.GetService<ExpressionPreviewService>().Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_host_that_built_its_own_preview_service_still_gets_that_one(bool beforeAddWorkflows)
    {
        var hostOwn = new ExpressionPreviewService();
        var register = (IServiceCollection services) => services.TryAddSingleton(hostOwn);

        using var provider = beforeAddWorkflows
            ? Build(before: register)
            : Build(after: register);

        Resolve(provider).Should().BeSameAs(hostOwn);
    }

    private static ExpressionPreviewService? Resolve(IServiceProvider provider) =>
        provider.GetService<ExpressionPreviewService>()
        ?? provider.GetKeyedService<ExpressionPreviewService>(ExpressionPreviewService.LibraryServiceKey);

    private static InvalidOperationException Refusal(
        Action<WorkflowPersistenceOptions> configure, Action<IServiceCollection>? before = null)
    {
        var act = () => Build(before: before, configure: configure);
        return act.Should().Throw<InvalidOperationException>().Which;
    }

    private static ServiceProvider Build(
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null,
        Action<WorkflowPersistenceOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        before?.Invoke(services);
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(new SqliteConnection("Filename=:memory:")));
            configure?.Invoke(options);
        });
        after?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private class ClusterRunLockProvider : IWorkflowRunLockProvider
    {
        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class OtherRunLockProvider : ClusterRunLockProvider;


    private sealed class ClusterSlotGate : IWorkflowRunSlotGate
    {
        public Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(Guid workflowId, string slotToken) => throw new NotSupportedException();
    }

    private sealed class ClusterFireDedup : IWorkflowFireDedup
    {
        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string Fingerprint(string fireKey) => throw new NotSupportedException();
    }
}
