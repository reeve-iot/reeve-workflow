using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>The leader gate a deployment already has: whoever takes the lock for the key runs the sweep, and
/// whoever does not, skips it.</summary>
/// <remarks>This is why <see cref="IWorkflowLeaderGate"/> is not a seam a clustered host has to fill. Leading
/// for one sweep and leading for an hour are the same question, and a host that answered
/// <see cref="IWorkflowRunLockProvider"/> has answered it.
///
/// The lock is held for the length of one run of the work and released after it, so a replica that dies
/// mid-sweep loses the lock with its session and the next tick has a leader again. Nothing renews and nothing
/// is fenced, which is the trade: a replica alive enough to keep its session but wedged inside a sweep holds
/// leadership until it dies, and every other replica skips the tick rather than racing it.</remarks>
public sealed class LockedLeaderGate(
    IWorkflowRunLockProvider locks, ILogger<LockedLeaderGate>? logger = null) : IWorkflowLeaderGate
{
    private readonly IWorkflowRunLockProvider locks = locks ?? throw new ArgumentNullException(nameof(locks));
    private readonly ILogger logger = logger ?? NullLogger<LockedLeaderGate>.Instance;

    /// <inheritdoc />
    public async Task RunUnderLeaderAsync(string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(work);

        await using var lease = await locks.TryAcquireAsync(key, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            logger.LogDebug("Not leading {LeaderKey} this tick; another replica holds it.", key);
            return;
        }

        await work(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Where the packages that run a sweep get their gate from when the host names none.</summary>
public static class WorkflowLeaderGates
{
    /// <summary>The gate this deployment's own lock provider implies, or the single-replica one when nothing
    /// answers the run-lock seam — which is the case for a host that installed a sweep and no engine.</summary>
    /// <remarks>Read at resolve rather than at registration: a host may call these packages on either side of
    /// <c>AddWorkflows</c>, and which gate is right is only knowable once both have run.</remarks>
    public static IWorkflowLeaderGate DerivedFrom(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            return services.GetService(typeof(IWorkflowRunLockProvider)) is IWorkflowRunLockProvider locks
                ? new LockedLeaderGate(locks, services.GetService(typeof(ILogger<LockedLeaderGate>)) as ILogger<LockedLeaderGate>)
                : new SingleReplicaLeaderGate();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "The leader gate is derived from Seams.RunLockProvider, and this host filled that slot with a "
                + "scoped lifetime. A sweep runs on a timer with no scope of its own: register the run-lock "
                + "provider as a singleton, or name a leader gate of your own on the package's seams.", ex);
        }
    }
}
