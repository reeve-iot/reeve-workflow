namespace ReeveWorkflow.Core.Hosting;

/// <summary>Where a run is, durably, so another replica can pick it up after this one dies. Optional: a host
/// that does not keep run state gets a correct engine that simply cannot survive a restart mid-run.</summary>
public interface IWorkflowRunStateStore
{
    /// <summary>Opens a row for a starting run and returns its id. That id is NOT the execution log's id —
    /// the two are separate records and only the caller holding both can keep them in step.</summary>
    Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken);

    /// <summary>The row as it is now, or null when there is none under that id.</summary>
    Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken);

    /// <summary>Moves a row between statuses, atomically. FALSE means the compare-and-swap lost — wrong status,
    /// or a peer owns it now. That is an answer every caller here acts on, and none of them may retry.</summary>
    Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken);

    /// <summary>Moves a row to a terminal status. Returns FALSE when it was already terminal, which is the
    /// ordinary outcome of two paths racing to finish the same run and never an error.</summary>
    Task<bool> CompleteAsync(
        Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason, CancellationToken cancellationToken);

    /// <summary>Moves a row to a terminal status only while it still names <paramref name="ownedByReplicaId"/>. FALSE
    /// means it was already terminal or names somebody else, who is running the run now and ends it themselves.</summary>
    /// <remarks>0: how a run ends its own row, so a row a peer reclaimed a moment before is not ended under
    /// it. The default body reads the row and then ends it, which leaves the moment between the two unfenced; a store
    /// that can make it one conditional write should.
    ///
    /// The terminal test is stated here rather than left to the owner. An ended row once named nobody, so the owner comparison answered "already terminal" by itself; since an ending keeps the name of whoever made it, a
    /// second ending in that same name would otherwise pass the fence and write over a finished run.</remarks>
    async Task<bool> CompleteIfOwnedAsync(
        Guid executionId, string? ownedByReplicaId, WorkflowRunStateStatus terminalStatus, string? failureReason,
        CancellationToken cancellationToken)
    {
        var row = await GetAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (row is null || row.OwnedByReplicaId != ownedByReplicaId
            || row.Status is WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed
                or WorkflowRunStateStatus.Cancelled)
            return false;

        return await CompleteAsync(executionId, terminalStatus, failureReason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The runs of one workflow, newest first, a page at a time. For a host rendering a run list; no
    /// part of the engine calls it.</summary>
    /// <remarks>A store that does not override it answers an empty page rather than throwing,
    /// so no host implementation breaks and a list built on it renders empty instead of failing. The library's
    /// own store overrides it; a host's should too before it builds a screen on this.
    ///
    /// <b>A decorator around another store must forward it</b>, which nothing can make it do: a wrapper written
    /// against the five members that have no default body answers this one from the default, and an empty run
    /// list is indistinguishable from a workflow nobody has ever run.</remarks>
    Task<WorkflowRunStatePage> ListByWorkflowAsync(
        Guid workflowId, WorkflowRunStatePageRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkflowRunStatePage([], null));
}

/// <summary>A run as a list renders it. Deliberately not <see cref="WorkflowRunStateRow"/>: that record carries
/// <c>VariablesJson</c>, which is the run's whole checkpoint, and fifty of those is a state dump and not a
/// page.</summary>
public sealed record WorkflowRunStateSummary
{
    /// <summary>The run's row id — what <see cref="IWorkflowRunStateStore.GetAsync"/> takes, not the execution
    /// log's id.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>The workflow it is a run of, which is what was asked for; carried so a caller holding pages of
    /// several need not remember which page came from where.</summary>
    public required Guid WorkflowId { get; init; }

    /// <summary>Where the run is.</summary>
    public required WorkflowRunStateStatus Status { get; init; }

    /// <summary>The version it started on.</summary>
    public int WorkflowVersion { get; init; }

    /// <summary>The block it is at, or the one it ended at.</summary>
    public string? CurrentBlockPath { get; init; }

    /// <summary>Which replica has it — and which replica finished it.</summary>
    public string? OwnedByReplicaId { get; init; }

    /// <summary>When the row was opened. The order a page comes back in, newest first.</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>When it ended, or null while it has not.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>What the ending said, for a run that failed or was ended by something that explained itself.</summary>
    public string? FailureReason { get; init; }
}

/// <summary>One page of run summaries and where the next begins. A null cursor is the end of the list, and a
/// cursor that is not null is a promise that the page after it holds at least one row.</summary>
public sealed record WorkflowRunStatePage(IReadOnlyList<WorkflowRunStateSummary> Rows, string? NextCursor)
{
    /// <summary>The rows, which are never null: a store that answers nothing answers an empty page.</summary>
    public IReadOnlyList<WorkflowRunStateSummary> Rows { get; init; } =
        Rows ?? throw new ArgumentNullException(nameof(Rows));
}

/// <summary>How many rows, from where, and which statuses.</summary>
/// <remarks><see cref="After"/> is opaque and is the store's own: it is read back by the store
/// that issued it and by nothing else, so a caller passes back what it was given and does not parse it. A
/// cursor from another store, or from an older version of this one, is a cursor that store may refuse.</remarks>
public sealed record WorkflowRunStatePageRequest
{
    /// <summary>The largest page a store here will answer. A list is a screen, and a caller asking for a
    /// hundred thousand runs at once wants an export, which is a different question with a different cost.</summary>
    public const int MaxLimit = 1000;

    /// <summary>How many rows at most, between one and <see cref="MaxLimit"/>. A store may return fewer and
    /// still hand back a cursor.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Where to continue from: the <see cref="WorkflowRunStatePage.NextCursor"/> of the page before,
    /// or null for the first page.</summary>
    public string? After { get; init; }

    /// <summary>Only these statuses, or every status when null or empty.</summary>
    public IReadOnlyCollection<WorkflowRunStateStatus>? Statuses { get; init; }
}

/// <summary>What a run's durable row can say about it. Terminal: Completed, Failed, Cancelled. Reclaimable by
/// another replica: Running, Sleeping, Waiting. Suspended is deliberately none of those — see below.</summary>
public enum WorkflowRunStateStatus
{
    /// <summary>The row exists; no block has run yet.</summary>
    Pending,

    /// <summary>A replica is executing a block right now.</summary>
    Running,

    /// <summary>Parked on a timed wait, with a wake-up time.</summary>
    Sleeping,

    /// <summary>Parked on an external event, with the key that will match it.</summary>
    Waiting,

    /// <summary>Every block finished.</summary>
    Completed,

    /// <summary>A block or the engine failed, and the row says why.</summary>
    Failed,

    /// <summary>Stopped by an operator.</summary>
    Cancelled,

    /// <summary>Parked awaiting a continuation that a durable suspension row already promises. NOT reclaimable:
    /// recovery must never select it, or the continuation runs twice.</summary>
    Suspended
}

/// <summary>Opening a row: which workflow, which version of it, what it starts with and who woke it.</summary>
public sealed record WorkflowRunStateBegin
{
    public required Guid WorkflowId { get; init; }

    public required int WorkflowVersion { get; init; }

    /// <summary>The starting variable scope, canonically encoded. Never logged: a plugin may have put anything in it.</summary>
    public required string VariablesJson { get; init; }

    public string? TriggerSource { get; init; }

    public string? OwnedByReplicaId { get; init; }

    /// <summary>The {workflow → version} manifest this run is pinned to, or null when it pins nothing.</summary>
    public string? PinnedVersionsJson { get; init; }

    /// <summary>The run that dispatched this one, when a block of another run did; null for a run something
    /// outside a workflow started. Recovery reads whether it is set, not what it points at.</summary>
    /// <remarks>The id a block sees as its own — the run's journal id — and not a key into this table, which
    /// no block can reach. It says which run to look at in a history, and here only that there was one.</remarks>
    public Guid? ParentExecutionId { get; init; }
}

/// <summary>One compare-and-swap: what the row must be, what it becomes, and what it should say afterwards.</summary>
public sealed record WorkflowRunStateTransition
{
    public required Guid ExecutionId { get; init; }

    public required WorkflowRunStateStatus FromStatus { get; init; }

    public required WorkflowRunStateStatus ToStatus { get; init; }

    /// <summary>The node the run is at. It must be the node's own id: recovery looks the block up by that id,
    /// so anything else — an execution id, say — misses every time and fails a run that was fine.</summary>
    public required string CurrentBlockPath { get; init; }

    public required string VariablesJson { get; init; }

    public DateTimeOffset? WaitUntilUtc { get; init; }

    public string? WaitForEventKey { get; init; }

    public string? OwnedByReplicaId { get; init; }

    /// <summary>When set, the swap also requires the row to still be owned by this replica. Without it a peer
    /// that reclaimed the row after presuming us dead gets it flipped back, and the run exists twice.</summary>
    public string? ExpectedOwnedByReplicaId { get; init; }

    /// <summary>When set, the swap also requires the row's <see cref="WaitForEventKey"/> to equal this. A resume uses it to
    /// take the run only from the park it was woken for, never from a later one. A store that ignores it loses that fence.</summary>
    public string? ExpectedWaitForEventKey { get; init; }

    /// <summary>When set, the swap also requires the row's <see cref="WorkflowRunStateRow.Version"/> to equal this, so a write
    /// decided on a read lands only if nothing moved the row since. A store that ignores it loses that fence.</summary>
    public int? ExpectedVersion { get; init; }

    /// <summary>When set, the swap also requires the row to be at this node. What a resume leaving Suspended
    /// names: the status alone cannot tell the wait it took from the next one the run parked at, after somebody
    /// else took that same wait over and walked on. The root canvas has no cycles, so a walk resumed past a root node
    /// never comes back to it; below the root a loop can, and there <see cref="ExpectedWaitForEventKey"/> names the wait.</summary>
    /// <remarks>A store that ignores it lets a late resume of a wait the run has walked past
    /// enter the run again.</remarks>
    public string? ExpectedCurrentBlockPath { get; init; }
}

/// <summary>A run's durable row, as the engine reads it. Narrower than what a host may store: this is what a
/// running engine actually consults, not everything an operator's console shows.</summary>
public sealed record WorkflowRunStateRow
{
    public required Guid ExecutionId { get; init; }

    public required Guid WorkflowId { get; init; }

    public required WorkflowRunStateStatus Status { get; init; }

    /// <summary>The version number the run started on. Zero from a store that does not keep it, which is what
    /// a reader that only ever asked for the status left behind.</summary>
    public int WorkflowVersion { get; init; }

    public string CurrentBlockPath { get; init; } = string.Empty;

    public string VariablesJson { get; init; } = "{}";

    public DateTimeOffset? WaitUntilUtc { get; init; }

    /// <summary>The key the last transition wrote. A parked run's is its park's token. Null from a store that
    /// does not return it, which leaves a resume to the older, unfenced swap.</summary>
    public string? WaitForEventKey { get; init; }

    public string? OwnedByReplicaId { get; init; }

    public DateTimeOffset LastTransitionUtc { get; init; }

    /// <summary>A number every transition of the row raises. Zero from a store that does not return it.</summary>
    public int Version { get; init; }

    /// <summary>The {workflow -> version} manifest this run was pinned to, or null when it pinned nothing.</summary>
    /// <remarks>Here so that recovery can resume the version the run was on. Without it the only way to that
    /// answer is a read of the library's own table, which a host would otherwise have to write.</remarks>
    public string? PinnedVersionsJson { get; init; }

    /// <summary>The run that dispatched this one, or null when nothing did. The dispatching run's journal id,
    /// not a key into this table — see the same member on <see cref="WorkflowRunStateBegin"/>.</summary>
    public Guid? ParentExecutionId { get; init; }

    /// <summary>What ended the row said about its ending: why it failed, or which path ended it — a sweep that replayed
    /// the run writes <c>replayed_by_recovery</c>. Null for a run that ended itself <c>Completed</c>, for a row not
    /// ended yet, and from a store that does not keep it.</summary>
    /// <remarks>0, for a resume that finds its run's row ended: a row the run completed itself is a run that
    /// finished under the entry the resume holds, where one a sweep completed was replayed under another.</remarks>
    public string? FailureReason { get; init; }

    /// <summary>When the row was opened. Default
    /// from a store that does not return it.</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>When the run ended, or null while it has not. Beside <see cref="StartedAtUtc"/>
    /// and for the same reason.</summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }
}
