using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Filling the execution-gate slot with a delegate instead of a type.</summary>
public static class WorkflowExecutionGateSeamExtensions
{
    /// <summary>Answers "may anything run right now" with a function. Read once per run attempt, so a host
    /// flipping the flag stops the next run rather than the one already walking its graph.</summary>
    /// <param name="seam">The slot, from <c>options.Seams.ExecutionGate</c>.</param>
    /// <param name="isExecutionSuspended">True while the host wants nothing to start.</param>
    public static WorkflowSeam<IWorkflowExecutionGate> Use(
        this WorkflowSeam<IWorkflowExecutionGate> seam, Func<bool> isExecutionSuspended)
    {
        ArgumentNullException.ThrowIfNull(seam);
        ArgumentNullException.ThrowIfNull(isExecutionSuspended);
        return seam.UseInstance(new DelegateExecutionGate(isExecutionSuspended));
    }

    /// <summary>The same, for a host whose answer lives on a service the container builds.</summary>
    /// <param name="seam">The slot, from <c>options.Seams.ExecutionGate</c>.</param>
    /// <param name="isExecutionSuspended">Reads the flag off the provider it is handed.</param>
    /// <param name="lifetime">Scoped when the service the flag lives on is scoped — a singleton gate reading a
    /// scoped service holds the root's copy, and the copy a request writes to is a different object.</param>
    public static WorkflowSeam<IWorkflowExecutionGate> Use(
        this WorkflowSeam<IWorkflowExecutionGate> seam,
        Func<IServiceProvider, Func<bool>> isExecutionSuspended,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(seam);
        ArgumentNullException.ThrowIfNull(isExecutionSuspended);
        return seam.Use(
            sp => (IWorkflowExecutionGate)new DelegateExecutionGate(isExecutionSuspended(sp)), lifetime);
    }
}
