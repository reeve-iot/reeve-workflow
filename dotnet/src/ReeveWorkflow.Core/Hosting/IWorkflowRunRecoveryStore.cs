namespace ReeveWorkflow.Core.Hosting;

/// <summary>Finding the runs whose replica died. It is the one question about run state that has no id to ask
/// it with: <see cref="IWorkflowRunStateStore"/> reads and writes a row at a time, and a sweep starts with none.</summary>
/// <remarks>A separate interface rather than two more members on the store, because every host that keeps run
/// state implements that one and would have to grow a query it may have no sweep for.</remarks>
public interface IWorkflowRunRecoveryStore
{
    /// <summary>Takes ownership of the runs no live replica is touching, and returns them as they now are.
    /// The rows come back already claimed: the caller has them, and nothing else will hand them out again.</summary>
    /// <remarks>Must be safe on every replica at once. The abandonment predicate has to be re-asserted as the
    /// new owner is written, or two sweeps that read one row both believe they took it.
    /// <para>A row counts as abandoned when it has gone untouched for <see cref="WorkflowRunRecoveryClaim.AbandonedAfter"/>,
    /// and, for a claim made in a replica's name, also when it names no replica at all: that is the row a graceful stop
    /// handed over, which nobody is running, and a store that waits for it to age makes every deploy as slow as a crash.
    /// A sleeping row is taken only once its wake-up time has passed, either way.</para></remarks>
    Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
        WorkflowRunRecoveryClaim request, CancellationToken cancellationToken);

    /// <summary>Says this replica still has the row, so a claim it is working through does not age back into
    /// another sweep. Answers false once the row is somebody else's, which is when the work should stop.</summary>
    /// <remarks>Here rather than on the run-state store because it must stamp the same clock the claim reads:
    /// a renewal written from a process clock is a renewal the predicate may already consider expired.</remarks>
    Task<bool> RenewClaimAsync(Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken);
}

/// <summary>One sweep's question: what counts as abandoned, how many to take at once, and whose name goes on
/// what it takes.</summary>
public sealed record WorkflowRunRecoveryClaim
{
    /// <summary>How long a row may go untouched before its owner is presumed dead.</summary>
    /// <remarks>A run's row is written before each block and not again until the next one, so a cutoff shorter
    /// than the longest block a workflow may contain reclaims runs that are merely slow — see the sweep.</remarks>
    public required TimeSpan AbandonedAfter { get; init; }

    /// <summary>The most rows one sweep may take, so a replica coming up after an outage does not take the
    /// whole backlog of a fleet in a single tick.</summary>
    public required int BatchSize { get; init; }

    /// <summary>The name to write into the rows this sweep takes. Null on a host that named no replica, which
    /// leaves the rows unowned and readable only by their age; set, it also lets the claim take rows that name no
    /// replica without waiting for them to age.</summary>
    public string? ClaimedByReplicaId { get; init; }
}
