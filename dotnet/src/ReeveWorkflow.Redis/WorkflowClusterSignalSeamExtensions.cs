using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.Redis;

/// <summary>Fills the cluster-signal slot with this package's pub/sub transport.</summary>
public static class WorkflowClusterSignalSeamExtensions
{
    /// <summary>Carries every cross-replica signal — restart supersession and the debugger's two — over Redis
    /// pub/sub.</summary>
    /// <param name="seam">The slot, from <c>options.Seams.ClusterSignal</c>.</param>
    /// <remarks>The channel names are not configured here: they belong to the deployment rather than to the
    /// backend, and live in <c>Defaults.SignalChannels</c>, where a host states them once for whichever
    /// transport ends up carrying them.
    ///
    /// A host that already publishes its own messages on one of those channels is changing the message BODY by
    /// adopting this transport, not only where it is sent. Its replicas cannot read each other for the length
    /// of a rolling deployment, and neither side fails.</remarks>
    public static WorkflowSeam<IWorkflowClusterSignal> UseRedis(this WorkflowSeam<IWorkflowClusterSignal> seam)
    {
        ArgumentNullException.ThrowIfNull(seam);
        return seam.Use(sp => new RedisWorkflowClusterSignal(
            RedisConnectionLookup.Required(sp, "ClusterSignal"),
            sp.GetService<WorkflowClusterIdentity>() ?? new WorkflowClusterIdentity(),
            sp.GetRequiredService<ILogger<RedisWorkflowClusterSignal>>()));
    }
}
