using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.DependencyInjection;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis.Tests;

public class ClusterSeamRedisFillTests
{
    private static ServiceProvider Build(Action<IServiceCollection> fill, bool withRedis = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (withRedis)
            services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        fill(services);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildDedup(bool withRedis = true) =>
        Build(services =>
        {
            var seam = new WorkflowSeam<IWorkflowFireDedup>();
            seam.UseRedis();
            seam.Register(services);
        }, withRedis);

    private static ServiceProvider BuildSignal(bool withRedis = true, WorkflowClusterIdentity? identity = null) =>
        Build(services =>
        {
            if (identity is not null) services.AddSingleton(identity);
            var seam = new WorkflowSeam<IWorkflowClusterSignal>();
            seam.UseRedis();
            seam.Register(services);
        }, withRedis);

    [Fact]
    public void UseRedis_fills_the_de_duplication_slot_with_this_packages_claim()
    {
        using var provider = BuildDedup();

        provider.GetRequiredService<IWorkflowFireDedup>().Should().BeOfType<RedisWorkflowFireDedup>();
    }

    [Fact]
    public void UseRedis_fills_the_signal_slot_with_this_packages_transport()
    {
        using var provider = BuildSignal();

        provider.GetRequiredService<IWorkflowClusterSignal>().Should().BeOfType<RedisWorkflowClusterSignal>();
    }

    [Fact]
    public void A_de_duplication_slot_named_for_redis_without_a_connection_says_which_slot_and_what_to_do()
    {
        using var provider = BuildDedup(withRedis: false);

        var resolving = () => provider.GetRequiredService<IWorkflowFireDedup>();

        resolving.Should().Throw<InvalidOperationException>()
            .WithMessage("*Seams.FireDedup.UseRedis() was called, but no IConnectionMultiplexer is registered*");
    }

    [Fact]
    public void A_signal_slot_named_for_redis_without_a_connection_says_which_slot_and_what_to_do()
    {
        using var provider = BuildSignal(withRedis: false);

        var resolving = () => provider.GetRequiredService<IWorkflowClusterSignal>();

        resolving.Should().Throw<InvalidOperationException>()
            .WithMessage("*Seams.ClusterSignal.UseRedis() was called, but no IConnectionMultiplexer is registered*");
    }

    [Fact]
    public async Task The_configured_prefix_reaches_the_key_the_claim_writes()
    {
        var database = Substitute.For<IDatabase>();
        database.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
            Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(true));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(multiplexer);
        var seam = new WorkflowSeam<IWorkflowFireDedup>();
        seam.UseRedis(o => o.KeyPrefix = "elsewhere:");
        seam.Register(services);
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IWorkflowFireDedup>()
            .TryClaimFireAsync("event:9", TimeSpan.FromSeconds(5), CancellationToken.None);

        await database.Received(1).StringSetAsync(
            (RedisKey)"elsewhere:event:9",
            Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task The_replica_the_host_named_is_the_origin_on_the_wire()
    {
        var subscriber = Substitute.For<ISubscriber>();
        RedisValue published = default;
        subscriber.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call => { published = call.ArgAt<RedisValue>(1); return Task.FromResult(1L); });
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetSubscriber(Arg.Any<object>()).Returns(subscriber);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(multiplexer);
        services.AddSingleton(new WorkflowClusterIdentity("replica-7"));
        var seam = new WorkflowSeam<IWorkflowClusterSignal>();
        seam.UseRedis();
        seam.Register(services);
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IWorkflowClusterSignal>()
            .PublishAsync("workflow_restart_cancel", Guid.NewGuid());

        published.ToString().Should().Contain("\"Origin\":\"replica-7\"");
    }

    [Fact]
    public void Both_slots_read_as_host_supplied()
    {
        var dedup = new WorkflowSeam<IWorkflowFireDedup>();
        var signal = new WorkflowSeam<IWorkflowClusterSignal>();

        dedup.UseRedis();
        signal.UseRedis();

        dedup.IsHostSupplied.Should().BeTrue();
        signal.IsHostSupplied.Should().BeTrue();
    }
}
