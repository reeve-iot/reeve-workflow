namespace ReeveWorkflow.Core.Hosting;

/// <summary>Runs a sweep at most once per cluster per tick. Every replica hosts these services, and ungated two
/// replicas' sweeps read and delete the same rows at once.</summary>
public interface IWorkflowLeaderGate
{
    /// <summary>Runs <paramref name="work"/> if this replica holds <paramref name="key"/>, and returns
    /// without running it if another one does. A single-replica host may simply always run it.</summary>
    Task RunUnderLeaderAsync(string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

/// <summary>The gate a host installs nothing for. Correct for a single replica and for a test, and wrong the
/// moment a second replica exists — which is why it is a named type rather than a null check.</summary>
public sealed class SingleReplicaLeaderGate : IWorkflowLeaderGate
{
    public Task RunUnderLeaderAsync(string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work(cancellationToken);
    }
}
