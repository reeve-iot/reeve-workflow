using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>Where a run waits when it cannot proceed and must survive a restart. Optional: a host without one
/// gets an engine whose wait blocks park nothing, so a suspended run is lost with the process.</summary>
public interface IWorkflowSuspensionStore
{
    /// <summary>Persists a suspension durably. Throws <see cref="SuspendPayloadTooLargeException"/> when the
    /// state to park is bigger than the host will store, which fails the run rather than losing its middle.</summary>
    Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels every live suspension for one run: when it ends, a park of it is withdrawn, or recovery re-enters
    /// it. A row left behind would resume a run nobody is waiting for, or stand beside the run's next park.</summary>
    Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken);

    /// <summary>Wires this suspension into whatever wakes it — timers, subscriptions — right now, rather than
    /// leaving it to the next sweep. Best-effort: the durable row above is what actually guarantees a resume.</summary>
    void RegisterForResumption(WorkflowSuspension suspension);

    /// <summary>Tears those down for one run. Without it a cancelled run's subscriptions outlive it until
    /// something else happens to notice.</summary>
    void TearDownByExecution(Guid executionLogId);
}
