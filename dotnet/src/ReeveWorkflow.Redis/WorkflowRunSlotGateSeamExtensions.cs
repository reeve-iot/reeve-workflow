using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.DependencyInjection;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis;

/// <summary>Fills the run-slot slot with this package's gate.</summary>
public static class WorkflowRunSlotGateSeamExtensions
{
    /// <summary>Answers the cluster run budget from Redis. The multiplexer is resolved optionally, because a host without one is a single-replica host and the gate is never consulted there.</summary>
    /// <param name="seam">The slot, from <c>options.Seams.RunSlotGate</c>.</param>
    /// <param name="configure">Optional: the key prefix and slot TTL. Both are cross-replica contracts — see <see cref="RedisWorkflowOptions"/>.</param>
    public static WorkflowSeam<IWorkflowRunSlotGate> UseRedis(
        this WorkflowSeam<IWorkflowRunSlotGate> seam,
        Action<RedisWorkflowOptions>? configure = null)
    {
        var options = new RedisWorkflowOptions();
        configure?.Invoke(options);
        return seam.Use(sp => new RedisWorkflowRunSlotGate(
            sp.GetService<IConnectionMultiplexer>(),
            sp.GetRequiredService<ILogger<RedisWorkflowRunSlotGate>>(),
            options));
    }
}
