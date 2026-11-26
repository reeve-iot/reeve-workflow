using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>The three suspension questions that have no id to ask them with: which parked runs are ready to
/// move, taking one of them, and putting one back. <see cref="IWorkflowSuspensionStore"/> works a row at a time.</summary>
/// <remarks>Its own interface rather than three more members on that one, which every host that parks a run
/// implements and would otherwise have had to grow a waker it may not want.</remarks>
public interface IWorkflowSuspensionResumeStore
{
    /// <summary>Reads the database instant and whether a pass has any work, so an idle waker can avoid its pages.</summary>
    Task<WorkflowSuspensionWakeProbe?> ProbeWakeAsync(TimeSpan? abandonedAfter, WorkflowAbandonedTakeCursor abandonedAfterCursor, CancellationToken cancellationToken) =>
        Task.FromResult<WorkflowSuspensionWakeProbe?>(null);

    /// <summary>One pass's view of the table: the instant it judges against, and the rows ready at it.</summary>
    /// <param name="afterPosition">How far into the rows that are not ready, but are re-read every tick, the
    /// previous pass got. Zero starts again from the oldest.</param>
    /// <param name="limit">The most rows the pass reads.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken);

    /// <summary>The same pass, read on from a cursor the previous pass handed back as
    /// <see cref="WorkflowSuspensionScan.NextCursor"/>. What the waker calls.</summary>
    /// <remarks>By default it reads by the cursor's position; a store that pages by key reads the key it wrote there, and does not drift as the rows before it resolve.</remarks>
    Task<WorkflowSuspensionScan> ScanAsync(
        WorkflowSuspensionScanCursor after, int limit, CancellationToken cancellationToken) =>
        ScanAsync((after ?? throw new ArgumentNullException(nameof(after))).Position, limit, cancellationToken);

    /// <summary>The same pass, of these kinds of row only, so the limit counts rows the caller will take. Null is every
    /// kind. A store that does not override it reads every kind, and the waker filters after.</summary>
    /// <remarks>A store behind a waker limited to <c>Sleep</c> must override this. The default fills the limit with the
    /// oldest overdue rows of any kind, and while <c>BatchSize</c> of those wait for another waker no sleep is ever read.</remarks>
    Task<WorkflowSuspensionScan> ScanAsync(
        WorkflowSuspensionScanCursor after, int limit, IReadOnlyCollection<WorkflowSuspensionKind>? kinds,
        CancellationToken cancellationToken) =>
        ScanAsync(after, limit, cancellationToken);

    /// <summary>The one parked row a wake is about, and the store's own instant to judge it against. Null means this store does not answer the read at all, which is every store that does not implement it; a read whose <see cref="WorkflowParkedRowRead.Row"/> is null means the row is not there or is no longer parked.</summary>
    /// <remarks>The instant comes back with the row for the reason <see cref="WorkflowSuspensionScan.AsOfUtc"/> exists: a settle window or a deadline measured against the calling process's clock means something different on every replica. A host that does not override this has no wake path.</remarks>
    Task<WorkflowParkedRowRead?> GetParkedAsync(
        Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken) =>
        Task.FromResult<WorkflowParkedRowRead?>(null);

    /// <summary>Takes one parked row, exactly once. Null means a peer took it first, which is what makes the
    /// waker safe on every replica at the same time without a lock or an election.</summary>
    Task<ResumeContext?> TryResolveAsync(
        Guid suspensionId,
        WorkflowSuspensionStatus status,
        string outcomeJson,
        DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>Puts a row this caller took back where it was, for a pass that could not use it. False when
    /// something else has moved it on since — the row is no longer this caller's to give back.</summary>
    Task<bool> ReleaseAsync(
        Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken);

    /// <summary>Waits somebody took longer ago than <paramref name="abandonedAfter"/>, by the store's own clock,
    /// whose run is still running and has not parked again since: a taker that died between taking the wait
    /// and re-entering the run. Each comes back with the status and answer it was taken with.</summary>
    /// <param name="after">Where the previous look got, handed back from its
    /// <see cref="WorkflowAbandonedTakeScan.NextCursor"/>; <see cref="WorkflowAbandonedTakeCursor.Start"/> reads
    /// every take there is. A look reads the takes since, not every wait ever taken.</param>
    /// <param name="abandonedAfter">How long ago a wait must have been taken to count as abandoned.</param>
    /// <param name="limit">The most takes the look reads.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <remarks>Answers with nothing by default. A store that keeps the default never recovers such a wait: it stays taken,
    /// and its run stays parked, for ever.</remarks>
    Task<WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(
        WorkflowAbandonedTakeCursor after, TimeSpan abandonedAfter, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkflowAbandonedTakeScan([], after));

    /// <summary>Takes an abandoned take over, exactly once: only while the row is still as
    /// <see cref="ScanAbandonedTakesAsync"/> read it, and re-stamped so the next scan does not offer it again
    /// before the cutoff has passed once more. Null means somebody else took it first, or it moved on.</summary>
    /// <remarks>Answers null by default: a store that keeps it takes no abandoned wait over.</remarks>
    Task<ResumeContext?> TryRetakeAsync(WorkflowSuspensionCandidate abandoned, CancellationToken cancellationToken) =>
        Task.FromResult<ResumeContext?>(null);

    /// <summary>Every parked wait a host could wake, with what each one's condition depends on, so a host can
    /// index them and wake the ones an event touches. Paged; the cursor is the waker's own.</summary>
    /// <param name="after">Where the previous page got, from its <see cref="WorkflowParkedWaitScan.NextCursor"/>;
    /// <see cref="WorkflowSuspensionScanCursor.Start"/> reads from the oldest parked wait.</param>
    /// <param name="limit">The most waits one page reads.</param>
    /// <param name="kinds">Narrows what is read, or null for every kind a host could wake. A kind whose wait
    /// ends at its own deadline — a <see cref="WorkflowSuspensionKind.Sleep"/> — is never in the page, asked
    /// for or not: there is nothing a host could publish for one.</param>
    /// <param name="cancellationToken">Ends the read.</param>
    /// <remarks>Answers an empty page by default: a host that does not override it has no index to build and stays on the poll.
    ///
    /// The keys come from the store's own reading of the row's config rather than from the host's re-parse of
    /// it, because a key the library spells one way and the host another is a wake that never arrives — and
    /// nothing anywhere reports a run nobody woke.</remarks>
    Task<WorkflowParkedWaitScan> ScanParkedWaitsAsync(
        WorkflowSuspensionScanCursor after, int limit, IReadOnlyCollection<WorkflowSuspensionKind>? kinds,
        CancellationToken cancellationToken) =>
        after is null
            ? throw new ArgumentNullException(nameof(after))
            : Task.FromResult(new WorkflowParkedWaitScan([], WorkflowSuspensionScanCursor.Start));

    /// <summary>Reads the parked waits a fire for <paramref name="dependencyKey"/> can wake, using the cursor
    /// returned by the prior keyed page.</summary>
    async Task<WorkflowParkedWaitScan> ScanParkedWaitsByDependencyKeyAsync(
        string dependencyKey, WorkflowSuspensionScanCursor after, int limit, CancellationToken cancellationToken)
    {
        var page = await ScanParkedWaitsAsync(after, limit, null, cancellationToken).ConfigureAwait(false);
        return new WorkflowParkedWaitScan(
            page.Waits.Where(wait => wait.DependencyKeys.Contains(dependencyKey, StringComparer.Ordinal)).ToArray(),
            page.NextCursor);
    }
}

/// <summary>The cheap, single-read summary used to skip suspension scans while every parked wait is in the future.</summary>
public sealed record WorkflowSuspensionWakeProbe(
    DateTimeOffset AsOfUtc,
    DateTimeOffset? NextTimeoutAtUtc,
    bool HasTemplateOrSubWorkflow,
    bool HasAbandonedTake);

/// <summary>A parked wait as an index builder reads it: which row, of what kind, and what its condition reads.</summary>
/// <remarks>Deliberately not <see cref="WorkflowSuspensionCandidate"/>: that record carries the
/// run's whole state payload, and an index built over tens of thousands of parked rows needs none of it.
///
/// <see cref="DependencyKeys"/> is handed back as the expression walker spells it and is not interpreted here.
/// What a key means — which of them a host's event feed can match exactly, which it can only subscribe to
/// wholesale — is the host's own question, and a library that answered it would be answering for one host.</remarks>
public sealed record WorkflowParkedWait(
    Guid SuspensionId,
    Guid ExecutionLogId,
    WorkflowSuspensionKind Kind,
    IReadOnlySet<string> DependencyKeys,
    DateTimeOffset SuspendedAtUtc);

/// <summary>One page of parked waits and where the next reads on from, spelled with the waker's own cursor so
/// that a host paging these pages them as the waker pages its rows.</summary>
public sealed record WorkflowParkedWaitScan(
    IReadOnlyList<WorkflowParkedWait> Waits,
    WorkflowSuspensionScanCursor NextCursor);

/// <summary>The one dependency key this library mints rather than reads, and therefore the one it has to
/// spell out.</summary>
/// <remarks>Every other key is the condition's own path with its dots turned into colons —
/// <c>${a.b.c}</c> is <c>a:b:c</c> — so what the first segment can be is whatever
/// <see cref="Expressions.IExpressionNamespaceResolver"/>s the host's expressions are evaluated against
/// answer to. A <see cref="WorkflowSuspensionKind.WaitForTrigger"/> row has no condition to walk, so its keys
/// are made here, and a host cannot match them without being told how.
///
/// The prefix carries a hyphen because no expression path can: a trigger id under a bare <c>trigger:</c> would
/// share an index entry with any condition reading a path whose first segment is <c>trigger</c>.</remarks>
public static class WorkflowDependencyKeys
{
    /// <summary>What a <see cref="WorkflowSuspensionKind.WaitForTrigger"/> row's trigger ids begin with. The id
    /// follows exactly as the row's config spells it: no trimming, no case folding, compared ordinally.</summary>
    public const string TriggerIdPrefix = "trigger-id:";

    /// <summary>What each of a <see cref="WorkflowSuspensionKind.Predicate"/> row's <see cref="Execution.WorkflowPredicateWaitConfig.Keys"/>
    /// begins with; the key follows as the config spells it.</summary>
    public const string PredicatePrefix = "predicate-key:";
}

/// <summary>One parked row as a wake reads it, beside the instant the store judged it at. The row is null where there is none parked under either id.</summary>
public sealed record WorkflowParkedRowRead(DateTimeOffset AsOfUtc, WorkflowSuspensionCandidate? Row)
{
    /// <summary>With no <see cref="Row"/>: the wait the read names had already been answered — taken, resolved or closed, or its run no longer running — rather than not landed yet. False where the store cannot tell.</summary>
    /// <remarks>It describes the read, not what comes after it: a take can be put back later. This package's waker uses it for trigger wakes only. A store reading by execution id alone should set it only for a run that has ended, since a running one may be parking on its next wait.</remarks>
    public bool NoLongerWaiting { get; init; }
}

/// <summary>What one pass of a waker reads. The instant is the store's own, so every replica measures the
/// same clock — a row's deadline, its settle window and its template are all read against this one value.</summary>
/// <remarks><paramref name="NextPosition"/> is how far the next pass skips into the speculative rows, or zero
/// when this pass reached the end of them. A position drifts as rows resolve, and wrapping is what covers it.</remarks>
public sealed record WorkflowSuspensionScan(
    DateTimeOffset AsOfUtc,
    IReadOnlyList<WorkflowSuspensionCandidate> Candidates,
    int NextPosition)
{
    /// <summary>Where the next pass reads on from. By default the position alone.</summary>
    public WorkflowSuspensionScanCursor NextCursor { get; init; } = new(NextPosition);
}

/// <summary>How far into the speculative rows — not ready, but re-read every tick — a waker's passes have got.
/// A store reads back whichever half it writes: a position, or the key of the last row it handed out.</summary>
/// <remarks>The key is the row's <c>(SuspendedAtUtc, SuspensionId)</c>, both, because a burst of
/// parks shares an instant to the tick and a cursor on the instant alone steps over the rest of the burst.</remarks>
public sealed record WorkflowSuspensionScanCursor(int Position)
{
    /// <summary>The oldest speculative row.</summary>
    public static WorkflowSuspensionScanCursor Start { get; } = new(0);

    /// <summary>When the last row handed out was parked, for a store that pages by key.</summary>
    public DateTimeOffset? AfterSuspendedAtUtc { get; init; }

    /// <summary>The last row handed out, for a store that pages by key.</summary>
    public Guid? AfterSuspensionId { get; init; }
}

/// <summary>What one look for abandoned takes found, and where the next look reads on from.</summary>
public sealed record WorkflowAbandonedTakeScan(
    IReadOnlyList<WorkflowSuspensionCandidate> Candidates,
    WorkflowAbandonedTakeCursor NextCursor);

/// <summary>How far through the takes, in the order they were taken, a waker's looks have got.</summary>
/// <remarks>The key is when the wait was taken and its id, both, because a pass stamps every take it
/// makes with one instant. A retake re-stamps the wait, so a takeover a slow run refuses comes round again.</remarks>
public sealed record WorkflowAbandonedTakeCursor
{
    /// <summary>Every take there is.</summary>
    public static WorkflowAbandonedTakeCursor Start { get; } = new();

    /// <summary>When the last take read was taken; with no id, every take up to and including this instant.</summary>
    public DateTimeOffset? AfterTakenAtUtc { get; init; }

    /// <summary>The last take read at <see cref="AfterTakenAtUtc"/>.</summary>
    public Guid? AfterSuspensionId { get; init; }
}

/// <summary>A parked run as a waker reads it, before it decides whether this one moves.</summary>
public sealed record WorkflowSuspensionCandidate(
    Guid SuspensionId,
    Guid ExecutionLogId,
    Guid WorkflowId,
    string BlockId,
    WorkflowSuspensionKind Kind,
    string ConfigJson,
    string StatePayloadJson,
    DateTimeOffset SuspendedAtUtc,
    DateTimeOffset TimeoutAtUtc)
{
    /// <summary>Suspended for a wait still parked; the status it was taken with for an abandoned take.</summary>
    public WorkflowSuspensionStatus Status { get; init; } = WorkflowSuspensionStatus.Suspended;

    /// <summary>The answer an abandoned take was taken with, which is the one its run is re-entered with.</summary>
    public string? OutcomeJson { get; init; }

    /// <summary>How many times the row has been taken, as it was read: the version a retake is fenced on.</summary>
    public int TakeCount { get; init; }

    /// <summary>When an abandoned take was taken, as it was read; null for a wait still parked.</summary>
    public DateTimeOffset? TakenAtUtc { get; init; }
}
