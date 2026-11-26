using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.Redis;

/// <summary>Fills the fire-de-duplication slot with this package's claim.</summary>
public static class WorkflowFireDedupSeamExtensions
{
    /// <summary>Collapses one physical trigger event that reached every replica into a single run, over Redis.</summary>
    /// <param name="seam">The slot, from <c>options.Seams.FireDedup</c>.</param>
    /// <param name="configure">Optional: the key prefix. A cross-replica contract — see
    /// <see cref="RedisFireDedupOptions.KeyPrefix"/>.</param>
    public static WorkflowSeam<IWorkflowFireDedup> UseRedis(
        this WorkflowSeam<IWorkflowFireDedup> seam,
        Action<RedisFireDedupOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(seam);
        var options = new RedisFireDedupOptions();
        configure?.Invoke(options);
        return seam.Use(sp => new RedisWorkflowFireDedup(
            RedisConnectionLookup.Required(sp, "FireDedup"),
            sp.GetRequiredService<ILogger<RedisWorkflowFireDedup>>(),
            options));
    }
}
