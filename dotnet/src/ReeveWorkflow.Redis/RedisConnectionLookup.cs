using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis;

internal static class RedisConnectionLookup
{
    internal static IConnectionMultiplexer Required(IServiceProvider services, string seam) =>
        services.GetService<IConnectionMultiplexer>()
        ?? throw new InvalidOperationException(
            $"Seams.{seam}.UseRedis() was called, but no IConnectionMultiplexer is registered in the container. "
            + "Register one — services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(...)) "
            + $"— or leave Seams.{seam} alone, which keeps this deployment's answer to it inside one process.");
}
