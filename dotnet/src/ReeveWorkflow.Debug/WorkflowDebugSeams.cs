using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.Debug;

/// <summary>What stepping a run needs from its host, as named slots filled where the package is configured.</summary>
/// <remarks>Every one of these is about how a deployment is shaped, not about what debugging does: where a
/// snapshot's bytes live, who hears about a pause, and which replica runs the sweep. Reaching the replica that
/// holds a parked run is not among them — that rides <c>Seams.ClusterSignal</c> like every other one.</remarks>
public sealed class WorkflowDebugSeams
{
    internal WorkflowDebugSeams()
    {
        Notifier = new WorkflowSeam<IWorkflowDebugNotifier>(
            ServiceDescriptor.Singleton<IWorkflowDebugNotifier, NullWorkflowDebugNotifier>());

        LeaderGate = new WorkflowSeam<IWorkflowLeaderGate>(
            ServiceDescriptor.Singleton(WorkflowLeaderGates.DerivedFrom));

        BlobStore = new WorkflowSeam<IWorkflowDebugBlobStore>(
            ServiceDescriptor.Singleton<IWorkflowDebugBlobStore>(
                sp => new EfWorkflowDebugBlobStore(sp.GetRequiredService<IServiceScopeFactory>())));
    }

    /// <summary>How a paused run reaches whoever is watching it. Absent notifies nobody.</summary>
    public WorkflowSeam<IWorkflowDebugNotifier> Notifier { get; }

    /// <summary>Which replica runs the retention sweep. By default the one holding the key, for a host that
    /// filled the run-lock seam, and every one of them for a host that did not.</summary>
    public WorkflowSeam<IWorkflowLeaderGate> LeaderGate { get; }

    /// <summary>Where snapshot bytes live. The workflow database unless a host says otherwise — these rows
    /// are large and short-lived, so a host with object storage should say otherwise.</summary>
    public WorkflowSeam<IWorkflowDebugBlobStore> BlobStore { get; }

    internal void Register(IServiceCollection services)
    {
        Notifier.Register(services);
        LeaderGate.Register(services);
        BlobStore.Register(services);
    }
}
