using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.DependencyInjection;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis.Tests;

public class RunSlotGateSeamTests
{
    private static ServiceProvider Build(Action<WorkflowSeam<IWorkflowRunSlotGate>> fill, bool withRedis = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (withRedis)
            services.AddSingleton(Substitute.For<IConnectionMultiplexer>());

        var seam = new WorkflowSeam<IWorkflowRunSlotGate>();
        fill(seam);
        seam.Register(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void UseRedis_fills_the_slot_with_this_packages_gate()
    {
        using var provider = Build(seam => seam.UseRedis());

        provider.GetRequiredService<IWorkflowRunSlotGate>().Should().BeOfType<RedisWorkflowRunSlotGate>();
    }

    [Fact]
    public void A_host_without_a_multiplexer_still_builds()
    {
        using var provider = Build(seam => seam.UseRedis(), withRedis: false);

        provider.GetRequiredService<IWorkflowRunSlotGate>().Should().BeOfType<RedisWorkflowRunSlotGate>();
    }

    [Fact]
    public void The_configured_options_reach_the_gate()
    {
        var captured = new RedisWorkflowOptions();
        using var provider = Build(seam => seam.UseRedis(o =>
        {
            o.RunSlotKeyPrefix = "elsewhere:";
            captured = o;
        }));

        provider.GetRequiredService<IWorkflowRunSlotGate>();
        captured.RunSlotKeyPrefix.Should().Be("elsewhere:");
    }

    [Fact]
    public void The_slot_reads_as_host_supplied()
    {
        var seam = new WorkflowSeam<IWorkflowRunSlotGate>();

        seam.UseRedis();

        seam.IsHostSupplied.Should().BeTrue();
    }
}
