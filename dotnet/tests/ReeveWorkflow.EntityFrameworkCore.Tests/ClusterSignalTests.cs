using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class ClusterSignalTests
{
    [Fact]
    public void A_clustered_host_that_left_the_transport_alone_still_starts()
    {
        var starting = () => Build(options =>
        {
            options.Defaults.IsMultiReplica = true;
            options.Seams.RunSlotGate.UseInstance(new ClusterSlotGate());
            options.Seams.FireDedup.UseInstance(new ClusterFireDedup());
            options.Seams.RunLockProvider.UseInstance(new ClusterRunLockProvider());
        });

        starting.Should().NotThrow();
    }

    [Fact]
    public void The_replica_the_host_named_is_the_one_a_backend_reads_back()
    {
        using var provider = Build(options => options.Defaults.ReplicaId = "desk-2");

        provider.GetRequiredService<WorkflowClusterIdentity>().ReplicaId.Should().Be("desk-2");
    }

    [Fact]
    public void Two_hosts_that_named_no_replica_are_still_two_origins()
    {
        using var first = Build(_ => { });
        using var second = Build(_ => { });

        first.GetRequiredService<WorkflowClusterIdentity>().ReplicaId
            .Should().NotBeNullOrWhiteSpace()
            .And.NotBe(second.GetRequiredService<WorkflowClusterIdentity>().ReplicaId);
    }

    [Fact]
    public void The_channel_names_the_container_answers_with_are_the_ones_the_host_configured()
    {
        using var provider = Build(options => options.Defaults.SignalChannels.DebugCommands = "desk_debug_commands");

        provider.GetRequiredService<WorkflowClusterSignalChannels>().DebugCommands.Should().Be("desk_debug_commands");
    }

    private static readonly Guid Workflow = Guid.Parse("1b2c3d4e-5f60-7182-93a4-b5c6d7e8f901");

    private static ServiceProvider Build(
        Action<WorkflowPersistenceOptions> configure,
        Action<IServiceCollection>? before = null,
        Action<IServiceCollection>? after = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        before?.Invoke(services);
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(new SqliteConnection("Filename=:memory:")));
            configure(options);
        });
        after?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class RecordingSignal : IWorkflowClusterSignal
    {
        private readonly List<(string Channel, Func<Guid, string?, CancellationToken, Task> Handler)> handlers = [];

        public List<(string Channel, Guid Id, string? Payload)> Published { get; } = [];

        public Task PublishAsync(
            string channel, Guid id, string? payload = null, CancellationToken cancellationToken = default)
        {
            Published.Add((channel, id, payload));
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
        {
            handlers.Add((channel, handler));
            return new Unsubscribe(() => handlers.RemoveAll(h => h.Handler == handler));
        }

        public async Task DeliverAsync(string channel, Guid id, string? payload)
        {
            foreach (var (subscribed, handler) in handlers.Where(h => h.Channel == channel).ToArray())
                await handler(id, payload, CancellationToken.None);
        }

        private sealed class Unsubscribe(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }

    private sealed class ClusterSlotGate : IWorkflowRunSlotGate
    {
        public Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ReleaseAsync(Guid workflowId, string slotToken) => throw new NotSupportedException();
    }

    private sealed class ClusterFireDedup : Core.Execution.IWorkflowFireDedup
    {
        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string Fingerprint(string fireKey) => throw new NotSupportedException();
    }

    private sealed class ClusterRunLockProvider : IWorkflowRunLockProvider
    {
        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
