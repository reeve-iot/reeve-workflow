namespace ReeveWorkflow.Core.Hosting;

/// <summary>A cluster-wide lock lease. Null everywhere in these three seams means in-process only, which is
/// correct for one replica and under-enforces on several — see docs/operations/clustering.md.</summary>
public interface IWorkflowRunLock : IAsyncDisposable
{
    /// <summary>Whether this replica still holds the lock. A lease can expire under a stalled process, and the
    /// budget it guards is wrong the moment it has.</summary>
    Task<bool> IsValidAsync(CancellationToken cancellationToken = default);
}

/// <summary>The one-permit modes' cluster gate: whoever holds this key is the single running replica.</summary>
public interface IWorkflowRunLockProvider
{
    /// <summary>Takes the key, or returns null when a peer holds it. Must not block waiting for it — the caller
    /// decides whether to retry, and for how long.</summary>
    Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default);
}

/// <summary>The N-permit modes' cluster gate: a shared count of slots held across replicas.</summary>
public interface IWorkflowRunSlotGate
{
    /// <summary>Atomically takes one cluster-wide slot for <paramref name="workflowId"/>. True when granted — the
    /// caller must release it on completion or suspend; false when <paramref name="maxConcurrent"/> are already held.</summary>
    Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct);

    /// <summary>Gives a slot back. Idempotent and best-effort: releasing a token nobody holds is a no-op.</summary>
    Task ReleaseAsync(Guid workflowId, string slotToken);

    /// <summary>Says a held slot is still in use, so a gate that expires slots does not expire a live run's. False
    /// when the slot is no longer held — expired or never taken — which the caller answers by asking for it again.</summary>
    /// <remarks>Answers true by default, which is what a gate with no expiry means. A gate that expires slots and keeps
    /// this default expires them under long runs.</remarks>
    Task<bool> RenewAsync(Guid workflowId, string slotToken, CancellationToken ct) => Task.FromResult(true);
}


/// <summary>The run-budget gate a host installs nothing for: it hands back a lease that nothing else can
/// contend, because on one replica nothing else exists to contend it.</summary>
/// <remarks>Correct for a single replica and wrong the moment a second one starts, which is why it is a
/// named type and not an absence. A host that has declared peers and left this seam here does not start —
/// see <c>Seams.RunLockProvider</c>.</remarks>
public sealed class SingleReplicaRunLockProvider : IWorkflowRunLockProvider
{
    /// <inheritdoc />
    public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<IWorkflowRunLock?>(new SingleReplicaRunLock());

    private sealed class SingleReplicaRunLock : IWorkflowRunLock
    {
        public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The slot gate a host installs nothing for: every slot is granted, because the in-process permit
/// the caller already holds is the whole budget when there is one process.</summary>
/// <remarks>A host that has declared peers and left
/// this seam here does not start — see <c>Seams.RunSlotGate</c>.</remarks>
public sealed class SingleReplicaRunSlotGate : IWorkflowRunSlotGate
{
    /// <inheritdoc />
    public Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public Task ReleaseAsync(Guid workflowId, string slotToken) => Task.CompletedTask;
}

