using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.Npgsql;

/// <summary>Fills the run-lock slot with this package's advisory locks.</summary>
public static class WorkflowRunLockSeamExtensions
{
    /// <summary>Holds the one-permit modes to a single replica, over PostgreSQL advisory locks.</summary>
    /// <param name="seam">The slot, from <c>options.Seams.RunLockProvider</c>.</param>
    /// <param name="configure">Required in practice: this package needs a connection string of its own —
    /// see <see cref="NpgsqlWorkflowLockOptions.ConnectionString"/>.</param>
    /// <remarks>Filling this slot also answers the leader gate the history and debug packages ask for: a gate
    /// that holds a lock for the length of one sweep is the same question this seam answers.</remarks>
    public static WorkflowSeam<IWorkflowRunLockProvider> UseNpgsqlLocks(
        this WorkflowSeam<IWorkflowRunLockProvider> seam,
        Action<NpgsqlWorkflowLockOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(seam);
        var options = new NpgsqlWorkflowLockOptions();
        configure?.Invoke(options);
        return seam.Use(sp => new NpgsqlWorkflowRunLockProvider(
            options, sp.GetRequiredService<ILogger<NpgsqlWorkflowRunLockProvider>>()));
    }
}
