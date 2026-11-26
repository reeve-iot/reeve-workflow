using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What moves a parked run again: one pass over the rows that are ready, taking each exactly once.
/// Safe on every replica at the same time — two that see one row both try it and exactly one wins.</summary>
/// <param name="timeProvider">Leave unset to use the system clock; pass a test clock to control settling waits.</param>
/// <remarks>A row is ready when its timeout has passed, or when it parked on a template this pass can
/// evaluate to true. A row parked on a trigger is nobody's here: the host that fires the trigger satisfies it.</remarks>
public sealed class WorkflowSuspensionWaker(
    IWorkflowSuspensionResumeStore store,
    IWorkflowRunResumer resumer,
    ILogger logger,
    WorkflowCompilationService compiler,
    WorkflowSuspensionWakeOptions? options = null,
    IHelperResolver? helperResolver = null,
    IWorkflowRunScopeFactory? scopes = null,
    IWorkflowMetrics? metrics = null,
    IWorkflowAuditSink? audit = null,
    TimeProvider? timeProvider = null)
{
    private static readonly IReadOnlyDictionary<HelperPath, object?> EmptyHelperValues = new Dictionary<HelperPath, object?>();
    private static readonly JsonSerializerOptions JsonOptions = WorkflowGraphHelpers.JsonOptions;

    private readonly WorkflowSuspensionWakeOptions options = Checked(options ?? new WorkflowSuspensionWakeOptions());
    private bool probeNextPass = true;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private static readonly TimeSpan UnwokenSleepCheckEvery = TimeSpan.FromMinutes(1);

    private long lastUnwokenSleepCheck = long.MinValue;

    private WorkflowSuspensionScanCursor cursor = WorkflowSuspensionScanCursor.Start;

    private WorkflowAbandonedTakeCursor abandonedCursor = WorkflowAbandonedTakeCursor.Start;

    private readonly SemaphoreSlim workers = new(Math.Max(1, (options ?? new WorkflowSuspensionWakeOptions()).MaxConcurrentResumes));
    private readonly Lock inFlightGate = new();
    private readonly HashSet<Task<bool>> inFlight = [];

    private readonly SemaphoreSlim wakeWorkers = new(Math.Max(1, (options ?? new WorkflowSuspensionWakeOptions()).MaxConcurrentWakes));

    private readonly Lock heldGate = new();
    private readonly Dictionary<(Guid? Suspension, Guid? Execution), HeldWake> heldWakes = [];
    private long heldSequence;

    private long replayCursor;

    private long attempting = -1;

    private long lastStoreTicks;
    private long nextPassDelayTicks;

    /// <summary>The next idle interval, capped by the configured poll interval for parks on peer replicas.</summary>
    public TimeSpan NextPassDelay => Interlocked.Read(ref nextPassDelayTicks) is var ticks && ticks > 0
        ? TimeSpan.FromTicks(ticks)
        : options.PollInterval;

    private DateTimeOffset? LastStoreInstant
    {
        get => Interlocked.Read(ref lastStoreTicks) is var ticks && ticks != 0
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;
        set => Interlocked.Exchange(ref lastStoreTicks, value?.UtcTicks ?? 0);
    }

    private TimeSpan HoldWakesFor =>
        options.HoldWakesFor ?? (options.SettleFor * 2 > options.PollInterval * 3
            ? options.SettleFor * 2
            : options.PollInterval * 3);

    private sealed record HeldWake(
        WorkflowSuspensionWake Wake, DateTimeOffset HeldSinceUtc, long Sequence, bool Tried = false,
        Guid? ExecutionId = null, string? Cause = null);

    private sealed class TakeReport
    {
        public string? Outcome { get; set; }

        public string? Cause { get; set; }
    }

    private readonly record struct WakeAttempt(
        Task<bool>? Started, bool Holdable, DateTimeOffset? AsOfUtc, bool Read = true,
        WorkflowSuspensionWake? HoldAs = null, Guid? ExecutionId = null, string? Cause = null);

    private sealed record ProgramResolution(WorkflowProgram? Program, ExceptionDispatchInfo? Failure);

    private sealed class SweepResolutionCache
    {
        private readonly Dictionary<(Guid VersionId, string Manifest), ProgramResolution> programs = [];
        public async Task<ProgramResolution> ProgramAsync(
            WorkflowSuspensionCandidate candidate, WorkflowSuspensionStatePayload payload,
            IWorkflowRunScopeFactory scopes, WorkflowCompilationService compiler, CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var log = await scope.Journal.LoadRunAsync(candidate.ExecutionLogId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"execution log {candidate.ExecutionLogId} not found");
            var versionId = log.WorkflowVersionId;
            var manifest = WorkflowResumeDecisions.ResolveResumeManifest(
                payload.PinnedVersionsJson, candidate.WorkflowId, versionId, JsonOptions);
            var key = (versionId, ManifestKey(manifest));
            if (programs.TryGetValue(key, out var resolved))
                return resolved;

            try
            {
                var version = await scope.Store.LoadVersionAsync(versionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"pinned version {versionId} not found");
                using var pins = WorkflowVersionPinScope.Push(manifest);
                var program = (await compiler.EnsureWithProgramAsync(version, manifest, cancellationToken).ConfigureAwait(false)).Program;
                resolved = new ProgramResolution(program, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resolved = new ProgramResolution(null, ExceptionDispatchInfo.Capture(ex));
            }

            programs.Add(key, resolved);
            return resolved;
        }

        private static string ManifestKey(IReadOnlyDictionary<Guid, Guid> manifest) => string.Join(";",
            manifest.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key:N}:{pair.Value:N}"));
    }

    /// <summary>One pass, awaited to the end of every run it re-entered. Answers how many runs it moved, which is
    /// what a caller logs and a test reads.</summary>
    public async Task<int> WakeOnceAsync(CancellationToken cancellationToken)
    {
        var outcomes = await Task.WhenAll(await PassAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        return outcomes.Count(resumed => resumed);
    }

    /// <summary>One pass that returns once its takes are done, leaving the runs it took to re-enter on their
    /// workers. Answers how many it took. What the hosted service calls: a long resumed segment must not hold up
    /// the next pass, and the next pass takes only what a free worker can start at once.</summary>
    /// <remarks><see cref="WaitForResumesAsync"/> is how a stopping host waits for what it took.</remarks>
    public async Task<int> TakeOnceAsync(CancellationToken cancellationToken) =>
        (await PassAsync(cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>The dependency keys a parked wait's condition reads, as <see cref="IExpressionDependencyWalker"/>
    /// spells them: <c>device:kitchen:temperature</c>, <c>helper:evening</c>. A
    /// <see cref="WorkflowSuspensionKind.WaitForTrigger"/>'s keys are its trigger ids behind
    /// <see cref="WorkflowDependencyKeys.TriggerIdPrefix"/>, whose hyphen is the whole point of it — see the
    /// remarks there; a <see cref="WorkflowSuspensionKind.Predicate"/>'s are its config's keys behind
    /// <see cref="WorkflowDependencyKeys.PredicatePrefix"/>. Empty for a sleep, a sub-workflow park, and a condition that reads nothing.</summary>
    /// <param name="kind">Which kind of wait the row is parked on.</param>
    /// <param name="configJson">The row's config, as the store holds it.</param>
    /// <param name="walker">What reads an expression's dependencies; the reading the evaluator makes of the
    /// same expression, which is why a host indexes these rather than its own parse of the workflow.</param>
    /// <param name="jsonOptions">How the config is spelled; <see cref="WorkflowGraphHelpers.JsonOptions"/> is
    /// what wrote it.</param>
    /// <remarks>Takes the config rather than a candidate because a candidate carries the run's
    /// whole state payload, and an index builder reading tens of thousands of parked rows needs none of it.
    ///
    /// A config this library cannot read answers empty rather than throwing. Such a row is parked until its
    /// timeout and the waker says so; a page of an index build is not where that
    /// is reported, and one unreadable row must not be the end of the page.</remarks>
    public static IReadOnlySet<string> DependencyKeysOf(
        WorkflowSuspensionKind kind, string configJson, IExpressionDependencyWalker walker,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(walker);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        if (string.IsNullOrWhiteSpace(configJson))
            return Nothing;

        try
        {
            switch (kind)
            {
                case WorkflowSuspensionKind.WaitTemplate:
                    var template = JsonSerializer.Deserialize<WorkflowStoredTemplateConfig>(configJson, jsonOptions);
                    return template?.Expression is not { } text ? Nothing : walker.DependenciesFromSource(text);

                case WorkflowSuspensionKind.WaitForTrigger:
                    var trigger = JsonSerializer.Deserialize<WorkflowWaitForTriggerConfig>(configJson, jsonOptions);
                    if (trigger?.TriggerIds is not { Count: > 0 } ids)
                        return Nothing;

                    return ids
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Select(id => WorkflowDependencyKeys.TriggerIdPrefix + id)
                        .ToHashSet(StringComparer.Ordinal);

                case WorkflowSuspensionKind.Predicate:
                    var predicate = JsonSerializer.Deserialize<WorkflowPredicateWaitConfig>(configJson, jsonOptions);
                    return predicate is null
                        ? Nothing
                        : predicate.Keys.Select(key => WorkflowDependencyKeys.PredicatePrefix + key).ToHashSet(StringComparer.Ordinal);

                case WorkflowSuspensionKind.Sleep:
                case WorkflowSuspensionKind.SubWorkflow:
                    return Nothing;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind), kind, "This kind of parked wait has no dependency keys defined for it.");
            }
        }
        catch (Exception ex) when (ex is JsonException or ExpressionSyntaxException
                                   || (ex is ArgumentException && ex is not ArgumentOutOfRangeException))
        {
            return Nothing;
        }
    }

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>One pass over one parked row, because a host says it is ready now. True when this call is the one
    /// that took it; a row that is not there, not ready, or taken by somebody else answers false and is not an
    /// error.</summary>
    /// <remarks>Everything a pass does to a row it takes, this does — the settle window, the
    /// condition, the take and the re-entry. What it skips is the page. A wake whose row is not takeable <em>yet</em>
    /// — not parked, or parked inside the settle window, or with no wake worker free — is held, except a trigger wake
    /// whose wait the store says was answered, which is counted lost; a held one is tried again on a
    /// pass that has read budget for it — <see cref="WorkflowSuspensionWakeOptions.BatchSize"/> reads a pass,
    /// from a cursor that rotates — until it has been tried and
    /// <see cref="WorkflowSuspensionWakeOptions.HoldWakesFor"/> is up. It still answers false: the take that the
    /// hold later makes belongs to that pass. A row inside its settle window is waited for while the next wait fits
    /// in twice <see cref="WorkflowSuspensionWakeOptions.SettleFor"/>, so the call can wait that long before its
    /// last read; a take is then answered when the re-entry finishes. A trigger wake naming only its run is held
    /// only for the row its read found; see the remarks on <see cref="WorkflowSuspensionWake"/>.</remarks>
    public async Task<bool> WakeNowAsync(WorkflowSuspensionWake wake, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wake);

        if (wake.SuspensionId is null && wake.ExecutionLogId is null)
        {
            throw new ArgumentException(
                "A wake names neither a suspension nor an execution, so there is no row to look at.", nameof(wake));
        }

        if (!WorkflowSuspensionWakeReasons.IsKnown(wake.Reason))
        {
            throw new ArgumentException(
                $"A wake gives the reason '{wake.Reason}', which is none of "
                + $"{string.Join(", ", WorkflowSuspensionWakeReasons.All)}; the reason is what the resumed run's "
                + "audit kind is read from, so one this package does not read is refused rather than guessed at.",
                nameof(wake));
        }

        var attempt = await AttemptAsync(wake, mayWaitOutTheWindow: true, cancellationToken).ConfigureAwait(false);

        if (attempt is { Holdable: true, AsOfUtc: { } asOfUtc })
        {
            Hold(attempt.HoldAs ?? wake, asOfUtc, tried: attempt.Read, attempt.ExecutionId, attempt.Cause);
            return false;
        }

        if (attempt.Holdable)
        {
            Count(WorkflowSuspensionWakeOutcomes.Dropped);
        }

        return attempt.Started is { } resume && await resume.ConfigureAwait(false);
    }

    private async Task<WakeAttempt> AttemptAsync(
        WorkflowSuspensionWake wake, bool mayWaitOutTheWindow, CancellationToken cancellationToken,
        SweepResolutionCache? resolutions = null)
    {
        long? waitingSince = null;
        DateTimeOffset? lastWaitedFrom = null;
        TimeSpan? lastBackOff = null;
        WorkflowSuspensionWake? pinned = null;

        while (true)
        {
            var look = await LookAsync(pinned ?? wake, cancellationToken, resolutions).ConfigureAwait(false);
            var attempt = look.Attempt.Holdable && pinned is not null ? look.Attempt with { HoldAs = pinned } : look.Attempt;
            if (look.Settling is not { } settling || !mayWaitOutTheWindow)
                return attempt;

            waitingSince ??= clock.GetTimestamp();
            var stalled = attempt.AsOfUtc <= lastWaitedFrom;
            var wait = stalled
                ? lastBackOff is { } previous ? previous * 2 : TimeSpan.FromMilliseconds(1)
                : TimeSpan.FromMilliseconds(Math.Ceiling(settling.TotalMilliseconds));
            if (clock.GetElapsedTime(waitingSince.Value) + wait > options.SettleFor * 2)
                return attempt;

            try
            {
                await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return attempt;
            }

            lastWaitedFrom = attempt.AsOfUtc;
            lastBackOff = stalled ? wait : null;
            pinned ??= attempt.HoldAs;
        }
    }

    private readonly record struct Look(WakeAttempt Attempt, TimeSpan? Settling)
    {
        public static Look Answer(WakeAttempt attempt) => new(attempt, null);

        public static Look Settle(WakeAttempt held, TimeSpan left) => new(held, left);
    }

    private async Task<Look> LookAsync(
        WorkflowSuspensionWake wake, CancellationToken cancellationToken, SweepResolutionCache? resolutions = null)
    {
        WorkflowParkedRowRead? read;
        try
        {
            read = await store.GetParkedAsync(wake.SuspensionId, wake.ExecutionLogId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (NamesNoWait(wake))
                return Look.Answer(GivenUp(wake, "its read of the run failed", ex));

            logger.LogWarning(ex, "A wake could not read its parked row; a pass tries it again.");
            return Look.Answer(new WakeAttempt(null, Holdable: true, LastStoreInstant, Read: false));
        }

        if (read is null)
        {
            Count(WorkflowSuspensionWakeOutcomes.NotMine);
            return Look.Answer(default);
        }

        LastStoreInstant = read.AsOfUtc;

        if (read.Row is not { } candidate)
        {
            if (read.NoLongerWaiting && wake.Reason == WorkflowSuspensionWakeReasons.Trigger)
            {
                Count(WorkflowSuspensionWakeOutcomes.Lost);
                return Look.Answer(new WakeAttempt(null, Holdable: false, read.AsOfUtc));
            }

            if (NamesNoWait(wake))
                return Look.Answer(GivenUp(wake, "its run is parked on no wait it could name", null));
            return Look.Answer(new WakeAttempt(null, Holdable: true, read.AsOfUtc));
        }

        if (options.Kinds is { } kinds && !kinds.Contains(candidate.Kind))
        {
            Count(WorkflowSuspensionWakeOutcomes.NotMine);
            return Look.Answer(new WakeAttempt(null, Holdable: false, read.AsOfUtc));
        }

        var settling = options.SettleFor - (read.AsOfUtc - candidate.SuspendedAtUtc);
        if (settling > TimeSpan.Zero)
            return Look.Settle(new WakeAttempt(null, Holdable: true, read.AsOfUtc, HoldAs: Pinned(wake, candidate),
                ExecutionId: candidate.ExecutionLogId), settling);

        if (!wakeWorkers.Wait(0))
        {
            logger.LogDebug("Every wake worker on this replica is busy; this wake is held for a pass to try again.");
            return Look.Answer(new WakeAttempt(null, Holdable: true, read.AsOfUtc, HoldAs: Pinned(wake, candidate),
                ExecutionId: candidate.ExecutionLogId));
        }

        var handedOff = false;
        try
        {
            var report = new TakeReport();
            if (await TakeAsync(candidate, read.AsOfUtc, cancellationToken, wake, report, resolutions).ConfigureAwait(false)
                is not { } take)
            {
                var outcome = report.Outcome ?? WorkflowSuspensionWakeOutcomes.NotReady;
                Count(outcome);
                if (outcome == WorkflowSuspensionWakeOutcomes.Failed)
                {
                    return Look.Answer(new WakeAttempt(null, Holdable: true, read.AsOfUtc, HoldAs: Pinned(wake, candidate),
                        ExecutionId: candidate.ExecutionLogId, Cause: report.Cause));
                }

                return Look.Answer(new WakeAttempt(null, Holdable: false, read.AsOfUtc));
            }

            Count(WorkflowSuspensionWakeOutcomes.Taken);

            var resume = StartOnWorker(
                candidate, take.Status, take.Context, retake: false, wakeWorkers, wake, cancellationToken);
            handedOff = true;
            return Look.Answer(new WakeAttempt(resume, Holdable: false, read.AsOfUtc));
        }
        finally
        {
            if (!handedOff)
                wakeWorkers.Release();
        }
    }

    private static bool NamesNoWait(WorkflowSuspensionWake wake) =>
        wake.SuspensionId is null && wake.Reason == WorkflowSuspensionWakeReasons.Trigger;

    private static WorkflowSuspensionWake? Pinned(WorkflowSuspensionWake wake, WorkflowSuspensionCandidate candidate) =>
        NamesNoWait(wake) ? wake with { SuspensionId = candidate.SuspensionId } : null;

    private WakeAttempt GivenUp(WorkflowSuspensionWake wake, string because, Exception? ex)
    {
        Count(WorkflowSuspensionWakeOutcomes.Dropped);
        logger.Log(ex is null ? LogLevel.Debug : LogLevel.Warning, ex,
            "A trigger wake for execution {ExecutionLogId} names no suspension and {Because}, so it is dropped rather "
            + "than held for whichever wait that run parks on next; a host that wants it held names the suspension.",
            wake.ExecutionLogId, because);
        return new WakeAttempt(null, Holdable: false, LastStoreInstant, Read: ex is null);
    }

    private void Hold(WorkflowSuspensionWake wake, DateTimeOffset asOfUtc, bool tried, Guid? executionId, string? cause)
    {
        if (options.MaxHeldWakes <= 0)
        {
            Count(WorkflowSuspensionWakeOutcomes.Dropped);
            return;
        }

        Count(WorkflowSuspensionWakeOutcomes.Held);

        var dropped = 0;
        var full = false;
        lock (heldGate)
        {
            var key = (wake.SuspensionId, wake.ExecutionLogId);

            if (heldWakes.TryGetValue(key, out var replaced) && replaced.Sequence != attempting)
                dropped++;

            heldWakes[key] = new HeldWake(wake, asOfUtc, heldSequence++, tried, executionId ?? wake.ExecutionLogId, cause);

            if (heldWakes.Count > options.MaxHeldWakes)
            {
                var oldest = heldWakes.MinBy(entry => entry.Value.Sequence);
                if (oldest.Value.Sequence != attempting)
                    dropped++;
                heldWakes.Remove(oldest.Key);
                full = true;
            }
        }

        for (var i = 0; i < dropped; i++)
            Count(WorkflowSuspensionWakeOutcomes.Dropped);

        if (full)
            WarnOnceThatTheHoldIsFull();
    }

    private async Task ReplayHeldWakesAsync(
        List<Task<bool>> started, DateTimeOffset now, CancellationToken cancellationToken, SweepResolutionCache resolutions)
    {
        HeldWake[] due;
        lock (heldGate)
        {
            if (heldWakes.Count == 0)
                return;

            var ordered = heldWakes.Values.OrderBy(entry => entry.Sequence).ToList();
            due =
            [
                .. ordered.Where(entry => entry.Sequence >= replayCursor),
                .. ordered.Where(entry => entry.Sequence < replayCursor)
            ];
        }

        var budget = Math.Max(1, options.BatchSize);
        foreach (var entry in due)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            if (entry.Tried && now - entry.HeldSinceUtc >= HoldWakesFor)
            {
                Forget(entry);
                Count(WorkflowSuspensionWakeOutcomes.Dropped);
                LogTheDroppedWake(entry);
                await TellTheRunAsync(entry).ConfigureAwait(false);
                continue;
            }

            if (budget == 0)
                continue;
            budget--;
            replayCursor = entry.Sequence + 1;

            lock (heldGate)
                attempting = entry.Sequence;

            WakeAttempt attempt;
            try
            {
                attempt = await AttemptAsync(entry.Wake, mayWaitOutTheWindow: false, cancellationToken, resolutions)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (heldGate)
                    attempting = -1;
            }

            if (attempt.Holdable)
            {
                if (attempt.Read)
                    MarkTried(entry, attempt.Cause);

                if (!StillHeld(entry))
                    Count(WorkflowSuspensionWakeOutcomes.Dropped);
                continue;
            }

            Forget(entry);
            if (attempt.Started is { } resume)
                started.Add(resume);
        }
    }

    private void Count(string outcome)
    {
        try
        {
            metrics?.RecordSuspensionWake(outcome);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to record a wake as {Outcome}.", outcome);
        }
    }

    private void MarkTried(HeldWake entry, string? cause)
    {
        lock (heldGate)
        {
            var key = (entry.Wake.SuspensionId, entry.Wake.ExecutionLogId);
            if (heldWakes.TryGetValue(key, out var current) && current.Sequence == entry.Sequence)
                heldWakes[key] = current with { Tried = true, Cause = cause };
        }
    }

    private bool StillHeld(HeldWake entry)
    {
        lock (heldGate)
            return heldWakes.TryGetValue((entry.Wake.SuspensionId, entry.Wake.ExecutionLogId), out var current)
                   && current.Sequence == entry.Sequence;
    }

    private void Forget(HeldWake entry)
    {
        lock (heldGate)
        {
            var key = (entry.Wake.SuspensionId, entry.Wake.ExecutionLogId);
            if (heldWakes.TryGetValue(key, out var current) && current.Sequence == entry.Sequence)
                heldWakes.Remove(key);
        }
    }

    private void SayTheRowStaysTaken(WorkflowSuspensionCandidate candidate, string because)
    {
        if (options.RetakeAbandonedTakesAfter is { } after)
        {
            logger.LogWarning(
                "Suspension {SuspensionId} for run {ExecutionLogId} was taken by a trigger wake and {Because}, so "
                + "it stays taken with the answer its host gave: a put-back would clear an answer nothing will "
                + "give again. A look for abandoned takes picks it up after {RetakeAbandonedTakesAfter}.",
                candidate.SuspensionId, candidate.ExecutionLogId, because, after);
            return;
        }

        logger.LogError(
            "Suspension {SuspensionId} for run {ExecutionLogId} was taken by a trigger wake and {Because}. It stays "
            + "taken with the answer its host gave, because a put-back would clear an answer nothing will give "
            + "again — but Waker.RetakeAbandonedTakesAfter is null on this waker, so nothing will come back for "
            + "it and that run is parked for good. Set it wherever a wake source is filled.",
            candidate.SuspensionId, candidate.ExecutionLogId, because);
    }

    private void LogTheDroppedWake(HeldWake entry)
    {
        if (entry.Wake.Reason == WorkflowSuspensionWakeReasons.Trigger)
        {
            logger.LogWarning(
                "A trigger wake for suspension {SuspensionId} / execution {ExecutionLogId} was held {HoldWakesFor} "
                + "and is dropped without having taken its wait. If nothing took that wait, the event is lost.",
                entry.Wake.SuspensionId, entry.Wake.ExecutionLogId, HoldWakesFor);
            return;
        }

        logger.LogDebug(
            "A wake for suspension {SuspensionId} / execution {ExecutionLogId} waited {HoldWakesFor} without its "
            + "row becoming takeable and is dropped; the page is what looks at that row now.",
            entry.Wake.SuspensionId, entry.Wake.ExecutionLogId, HoldWakesFor);
    }

    private const int EntryMessageLimit = 4000;

    private async Task TellTheRunAsync(HeldWake entry)
    {
        if (scopes is null || entry.ExecutionId is not { } runId)
            return;

        var level = entry.Cause is null ? LogEntryLevel.Warning : LogEntryLevel.Error;
        var message = entry.Cause is { } cause
            ? $"wake-dropped: A wake for suspension {entry.Wake.SuspensionId} was held for the whole of the "
              + $"waker's hold, and its last take failed with {cause}. Nothing has taken that wait, and this run "
              + "ends timed_out at its deadline unless the page reaches it first."
            : $"wake-dropped: A wake for suspension {entry.Wake.SuspensionId} was held for the whole of the "
              + "waker's hold without its row becoming takeable and is dropped. This run ends timed_out at its "
              + "deadline unless the page reaches that wait first.";

        var note = new WorkflowExecutionLogEntry(
            Guid.NewGuid(), runId, string.Empty, string.Empty, level, Cut(message, EntryMessageLimit),
            JsonSerializer.Serialize(new { entry.Wake.SuspensionId, entry.Wake.Reason, entry.Cause }, JsonOptions),
            null, null, DateTimeOffset.UtcNow);

        try
        {
            using var scope = scopes.CreateScope();
            await scope.Journal
                .SaveRunDetailsAsync(runId, recordLogEntries: true, [note], [], CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "The drop of the wake for suspension {SuspensionId} could not be written onto run {ExecutionId}; "
                + "the warning above stands and the hold is dropped either way.",
                entry.Wake.SuspensionId, runId);
        }
    }

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;

    private int heldWakesFullLogged;

    private void WarnOnceThatTheHoldIsFull()
    {
        if (Interlocked.Exchange(ref heldWakesFullLogged, 1) != 0)
            return;

        logger.LogWarning(
            "More than MaxHeldWakes ({MaxHeldWakes}) wakes are waiting for rows that have not become takeable, so "
            + "the oldest are being dropped. A host publishing wakes for runs that never park is the usual cause. "
            + "Said once for the life of this waker.", options.MaxHeldWakes);
    }

    /// <summary>Completes when every run any pass took has finished re-entering or been put back. Never throws:
    /// each of them has logged or answered its own ending.</summary>
    public async Task WaitForResumesAsync()
    {
        Task<bool>[] running;
        lock (inFlightGate)
            running = [.. inFlight];

        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task<List<Task<bool>>> PassAsync(CancellationToken cancellationToken)
    {
        var started = new List<Task<bool>>();
        var resolutions = new SweepResolutionCache();
        try
        {
            var probe = probeNextPass
                ? await store.ProbeWakeAsync(options.RetakeAbandonedTakesAfter, abandonedCursor, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            if (probe is { HasTemplateOrSubWorkflow: false, HasAbandonedTake: false }
                && (probe.NextTimeoutAtUtc is null || probe.NextTimeoutAtUtc > probe.AsOfUtc))
            {
                LastStoreInstant = probe.AsOfUtc;
                if (options.RetakeAbandonedTakesAfter is { } retakeAfter)
                    abandonedCursor = new WorkflowAbandonedTakeCursor
                    {
                        AfterTakenAtUtc = probe.AsOfUtc - retakeAfter
                    };
                var untilDue = probe.NextTimeoutAtUtc is { } due
                    ? due - probe.AsOfUtc + TimeSpan.FromMilliseconds(1)
                    : options.PollInterval;
                Interlocked.Exchange(ref nextPassDelayTicks,
                    Math.Clamp(untilDue.Ticks, 1, options.PollInterval.Ticks));
                await ReplayHeldWakesAsync(started, probe.AsOfUtc, cancellationToken, resolutions).ConfigureAwait(false);
                await WarnOfUnwokenSleepsAsync(cancellationToken).ConfigureAwait(false);
                return started;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "The suspension waker's cheap probe failed; reading the normal pages.");
        }
        Interlocked.Exchange(ref nextPassDelayTicks, options.PollInterval.Ticks);
        await WarnOfUnwokenSleepsAsync(cancellationToken).ConfigureAwait(false);

        WorkflowSuspensionScan scan;
        try
        {
            scan = await store.ScanAsync(cursor, options.BatchSize, options.Kinds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The suspension waker could not read the parked runs; the next pass tries again.");
            return started;
        }

        var now = scan.AsOfUtc;
        probeNextPass = scan.Candidates.Count == 0 && scan.NextCursor.Position == 0
            && scan.NextCursor.AfterSuspendedAtUtc is null && scan.NextCursor.AfterSuspensionId is null;
        LastStoreInstant = now;

        try
        {
            await ReplayHeldWakesAsync(started, now, cancellationToken, resolutions).ConfigureAwait(false);

            foreach (var candidate in scan.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var age = now - candidate.SuspendedAtUtc;
                if (age < options.SettleFor)
                {
                    var untilSettled = options.SettleFor - age + TimeSpan.FromMilliseconds(1);
                    var currentDelay = NextPassDelay;
                    if (untilSettled < currentDelay)
                        Interlocked.Exchange(ref nextPassDelayTicks, Math.Max(1, untilSettled.Ticks));
                    continue;
                }

                if (options.Kinds is { } kinds && !kinds.Contains(candidate.Kind))
                    continue;

                if (!workers.Wait(0))
                {
                    logger.LogDebug("Every resume worker on this replica is busy; the rest of this pass waits for the next.");
                    return started;
                }

                var handedOff = false;
                try
                {
                    if (await TakeAsync(candidate, now, cancellationToken, resolutions: resolutions).ConfigureAwait(false) is { } take)
                    {
                        started.Add(StartOnWorker(
                            candidate, take.Status, take.Context, retake: false, workers, wake: null, cancellationToken));
                        handedOff = true;
                    }
                }
                finally
                {
                    if (!handedOff)
                        workers.Release();
                }
            }

            cursor = scan.NextCursor;

            if (options.RetakeAbandonedTakesAfter is { } abandonedAfter
                && await ScanAbandonedTakesAsync(abandonedAfter, cancellationToken).ConfigureAwait(false) is { } look)
            {
                WorkflowAbandonedTakeCursor? rereadFrom = null;
                var readUpTo = abandonedCursor;
                foreach (var abandoned in look.Candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (options.Kinds is { } retakeKinds && !retakeKinds.Contains(abandoned.Kind))
                        continue;

                    if (!workers.Wait(0))
                    {
                        abandonedCursor = rereadFrom ?? abandonedCursor;
                        return started;
                    }

                    var handedOff = false;
                    try
                    {
                        var retake = await RetakeAsync(abandoned, cancellationToken).ConfigureAwait(false);
                        if (retake.Failed)
                            rereadFrom ??= readUpTo;
                        if (retake.Context is { } context)
                        {
                            started.Add(StartOnWorker(
                            abandoned, abandoned.Status, context, retake: true, workers, wake: null, cancellationToken));
                            handedOff = true;
                        }
                    }
                    finally
                    {
                        if (!handedOff)
                            workers.Release();
                    }

                    readUpTo = new WorkflowAbandonedTakeCursor
                    {
                        AfterTakenAtUtc = abandoned.TakenAtUtc, AfterSuspensionId = abandoned.SuspensionId
                    };
                }

                abandonedCursor = rereadFrom ?? look.NextCursor;
            }
        }
        catch (OperationCanceledException)
        {
            await DrainAsync(started).ConfigureAwait(false);
            throw;
        }

        return started;
    }

    private static async Task DrainAsync(List<Task<bool>> resumes)
    {
        try { await Task.WhenAll(resumes).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private Task<bool> StartOnWorker(
        WorkflowSuspensionCandidate candidate, WorkflowSuspensionStatus status, ResumeContext context, bool retake,
        SemaphoreSlim budget, WorkflowSuspensionWake? wake, CancellationToken cancellationToken)
    {
        var resume = RunOnWorkerAsync(candidate, status, context, retake, budget, wake, cancellationToken);
        lock (inFlightGate)
            inFlight.Add(resume);
        _ = resume.ContinueWith(
            finished => { lock (inFlightGate) inFlight.Remove(finished); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return resume;
    }

    private async Task<bool> RunOnWorkerAsync(
        WorkflowSuspensionCandidate candidate, WorkflowSuspensionStatus status, ResumeContext context, bool retake,
        SemaphoreSlim budget, WorkflowSuspensionWake? wake, CancellationToken cancellationToken)
    {
        try
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            return await WakeAsync(candidate, status, context, retake, wake, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            budget.Release();
        }
    }

    private async Task<WorkflowAbandonedTakeScan?> ScanAbandonedTakesAsync(
        TimeSpan abandonedAfter, CancellationToken cancellationToken)
    {
        try
        {
            return await store.ScanAbandonedTakesAsync(abandonedCursor, abandonedAfter, options.BatchSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The suspension waker could not look for abandoned takes; the next pass tries again.");
            return null;
        }
    }

    private async Task<(ResumeContext? Context, bool Failed)> RetakeAsync(
        WorkflowSuspensionCandidate abandoned, CancellationToken cancellationToken)
    {
        if (!HasStateRow(abandoned))
        {
            logger.LogWarning(
                "Parked run {ExecutionLogId} was taken and never re-entered, but it has no state row to fence a "
                + "second resume on; it is left as it is.", abandoned.ExecutionLogId);
            return (null, false);
        }

        try
        {
            var context = await store.TryRetakeAsync(abandoned, CancellationToken.None).ConfigureAwait(false);
            if (context is not null)
            {
                logger.LogDebug(
                    "Parked run {ExecutionLogId} was taken and looks never re-entered; this replica offers it a resume.",
                    abandoned.ExecutionLogId);
            }

            return (context, false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to take over abandoned wait {SuspensionId}; the next pass tries again.", abandoned.SuspensionId);
            return (null, true);
        }
    }

    private async Task SayRetakenAsync(WorkflowSuspensionCandidate abandoned)
    {
        logger.LogWarning(
            "Parked run {ExecutionLogId} was taken and never re-entered; this replica took it over.",
            abandoned.ExecutionLogId);

        if (audit is null)
            return;

        try
        {
            await audit.WriteAsync(
                new WorkflowAuditEvent(
                    Kind: WorkflowAuditEventKinds.RetakenByWaker,
                    ActorId: null,
                    SubjectId: abandoned.ExecutionLogId,
                    Data: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["suspensionId"] = abandoned.SuspensionId,
                        ["executionId"] = abandoned.ExecutionLogId,
                        ["workflowId"] = abandoned.WorkflowId,
                        ["blockId"] = abandoned.BlockId,
                        ["takenAtUtc"] = abandoned.TakenAtUtc,
                        ["takeCount"] = abandoned.TakeCount,
                        ["takenAs"] = abandoned.Status.ToString()
                    }),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "failed to write the takeover audit event for wait {SuspensionId}.", abandoned.SuspensionId);
        }
    }

    private bool HasStateRow(WorkflowSuspensionCandidate candidate)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(candidate.StatePayloadJson, JsonOptions)
                ?.ExecutionStateId is not null;
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "The state payload of wait {SuspensionId} could not be read.", candidate.SuspensionId);
            return false;
        }
    }

    private async Task<(WorkflowSuspensionStatus Status, ResumeContext Context)?> TakeAsync(
        WorkflowSuspensionCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken,
        WorkflowSuspensionWake? wake = null, TakeReport? report = null, SweepResolutionCache? resolutions = null)
    {
        if (wake is { Variables: not null } && candidate.Kind != WorkflowSuspensionKind.Predicate)
            return Dropped(candidate, report, "it carries variables for a row that is not a predicate wait");

        if (candidate.Kind == WorkflowSuspensionKind.WaitForTrigger
            && candidate.TimeoutAtUtc > now
            && wake is { Reason: WorkflowSuspensionWakeReasons.Trigger })
        {
            return await ResolveAsync(candidate, WorkflowSuspensionStatus.Resumed,
                OutcomeJson(WorkflowWaitOutcomes.Satisfied, wake), now, report).ConfigureAwait(false);
        }

        if (candidate.Kind == WorkflowSuspensionKind.SubWorkflow)
        {
            return await EndedChildAsync(candidate, now, cancellationToken, report).ConfigureAwait(false);
        }

        if (candidate.Kind == WorkflowSuspensionKind.Sleep)
        {
            return candidate.TimeoutAtUtc <= now
                ? await ResolveAsync(candidate, WorkflowSuspensionStatus.Resumed,
                    WorkflowDurableSleep.OutcomeJson(candidate.ConfigJson, candidate.SuspendedAtUtc, candidate.TimeoutAtUtc),
                    now, report).ConfigureAwait(false)
                : null;
        }

        if (candidate.TimeoutAtUtc <= now)
        {
            return await ResolveAsync(candidate, WorkflowSuspensionStatus.TimedOut,
                OutcomeJson(WorkflowWaitOutcomes.TimedOut, wake), now, report).ConfigureAwait(false);
        }

        if (candidate.Kind == WorkflowSuspensionKind.Predicate)
        {
            if (wake is null)
                return null;
            if (wake.Reason != WorkflowSuspensionWakeReasons.Poll)
                return Dropped(candidate, report, $"its reason is '{wake.Reason}', where a predicate wait is woken with '{WorkflowSuspensionWakeReasons.Poll}'");
            if (wake.Variables is null)
                return Dropped(candidate, report, "it carries no variables for a predicate wait");
        }

        return await ConditionOutcomeAsync(candidate, now, wake, report, cancellationToken, resolutions).ConfigureAwait(false) is { } answered
            ? await ResolveAsync(candidate, WorkflowSuspensionStatus.Resumed, answered, now, report).ConfigureAwait(false)
            : null;
    }

    private (WorkflowSuspensionStatus Status, ResumeContext Context)? Dropped(
        WorkflowSuspensionCandidate candidate, TakeReport? report, string because)
    {
        logger.LogWarning("A wake for parked row {SuspensionId} of execution {ExecutionLogId} is dropped: {Because}; the row stays parked.",
            candidate.SuspensionId, candidate.ExecutionLogId, because);
        if (report is not null)
            report.Outcome = WorkflowSuspensionWakeOutcomes.Dropped;
        return null;
    }

    private async Task<(WorkflowSuspensionStatus Status, ResumeContext Context)?> EndedChildAsync(
        WorkflowSuspensionCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken,
        TakeReport? report = null)
    {
        if (scopes is null)
        {
            if (report is not null)
                report.Outcome = WorkflowSuspensionWakeOutcomes.NotMine;
            WarnOnceThatSubWorkflowsAreNotRead();
            return null;
        }

        if (WorkflowSubRunOutcomes.WaitingOn(candidate.ConfigJson) is not { } childId)
        {
            WarnOnceThatASubWorkflowRowNamesNothing(candidate.SuspensionId);

            if (report is not null)
                report.Outcome = WorkflowSuspensionWakeOutcomes.Dropped;
            return null;
        }

        WorkflowExecutionLog? child;
        try
        {
            using var scope = scopes.CreateScope();
            child = await scope.Journal.LoadRunAsync(childId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not read the run {ChildExecutionLogId} that parked run {SuspensionId} is waiting on; the next "
                + "pass tries again.", childId, candidate.SuspensionId);

            if (report is not null)
            {
                report.Outcome = WorkflowSuspensionWakeOutcomes.Failed;
                report.Cause = Cut($"{ex.GetType().Name}: {ex.Message}", EntryMessageLimit);
            }

            return null;
        }

        if (child is null || child.Status == WorkflowExecutionStatus.Running)
            return null;

        return await ResolveAsync(candidate, WorkflowSuspensionStatus.Resumed,
            WorkflowSubRunOutcomes.Resolved(
                childId,
                succeeded: child.Status == WorkflowExecutionStatus.Succeeded,
                child.ResultOutputsJson,
                child.Status == WorkflowExecutionStatus.Succeeded
                    ? null
                    : child.ErrorMessage ?? $"The run it started ended {child.Status}."),
            now, report).ConfigureAwait(false);
    }

    private int subWorkflowsUnreadLogged;

    private int subWorkflowRowUnnamedLogged;

    private void WarnOnceThatASubWorkflowRowNamesNothing(Guid suspensionId)
    {
        if (Interlocked.Exchange(ref subWorkflowRowUnnamedLogged, 1) != 0)
            return;

        logger.LogWarning(
            "Parked run {SuspensionId} waits on a run its configuration does not name, and nothing will end that "
            + "wait. Said once for the life of this waker, however many rows are like it.", suspensionId);
    }

    private void WarnOnceThatSubWorkflowsAreNotRead()
    {
        if (Interlocked.Exchange(ref subWorkflowsUnreadLogged, 1) != 0)
            return;

        logger.LogWarning(
            "This waker has no way to read the runs that parked runs are waiting on, so a run parked on a run it "
            + "started is not moved by it. Said once for the life of this waker.");
    }

    private async Task<(WorkflowSuspensionStatus Status, ResumeContext Context)?> ResolveAsync(
        WorkflowSuspensionCandidate candidate, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset now,
        TakeReport? report = null)
    {
        ResumeContext? context;
        try
        {
            context = await store.TryResolveAsync(
                candidate.SuspensionId, status, outcomeJson, now, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to take parked run {SuspensionId}; the next pass tries again.", candidate.SuspensionId);
            if (report is not null)
            {
                report.Outcome = WorkflowSuspensionWakeOutcomes.Failed;
                report.Cause = Cut($"{ex.GetType().Name}: {ex.Message}", EntryMessageLimit);
            }

            return null;
        }

        if (context is null && report is not null)
            report.Outcome = WorkflowSuspensionWakeOutcomes.Lost;

        return context is null ? null : (status, context);
    }

    private async Task WarnOfUnwokenSleepsAsync(CancellationToken cancellationToken)
    {
        if (options.Kinds is not { } kinds)
            return;

        var missing = UnreadKinds.Where(kind => !kinds.Contains(kind)).ToArray();
        if (missing.Length == 0)
            return;

        var now = Environment.TickCount64;
        if (lastUnwokenSleepCheck != long.MinValue && now - lastUnwokenSleepCheck < UnwokenSleepCheckEvery.TotalMilliseconds)
            return;
        lastUnwokenSleepCheck = now;

        foreach (var kind in missing)
        {
            try
            {
                var scan = await store.ScanAsync(
                    WorkflowSuspensionScanCursor.Start, 1, [kind], cancellationToken).ConfigureAwait(false);
                if (scan.Candidates.Any(candidate => candidate.Kind == kind))
                {
                    logger.LogWarning(
                        "A parked {Kind} is waiting, and this waker's Kinds leave out {Kind}, so nothing here moves it. "
                        + "Add {Kind} to Waker.Kinds until no {Kind} rows remain; see 'Turning parking on and off' in "
                        + "docs/operations/parking.md.", kind, kind, kind, kind);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "The suspension waker could not look for parked {Kind} rows it leaves out.", kind);
            }
        }
    }

    private static readonly WorkflowSuspensionKind[] UnreadKinds =
        [WorkflowSuspensionKind.Sleep, WorkflowSuspensionKind.SubWorkflow, WorkflowSuspensionKind.Predicate];

    private static WorkflowSuspensionWakeOptions Checked(WorkflowSuspensionWakeOptions options)
    {
        if (options.Kinds is { Count: 0 })
            throw new ArgumentException(
                "WorkflowSuspensionWakeOptions.Kinds is empty, so no pass would move anything. Use null for every kind.",
                nameof(options));
        if (options.ExpressionNamespaces is null)
            throw new ArgumentException(
                "WorkflowSuspensionWakeOptions.ExpressionNamespaces is null, so a parked template cannot read its "
                + "namespace registrations. Use an empty list for no host namespaces.",
                nameof(options));
        if (options.ExpressionNamespaces.Count > 0 && options.NamespaceScopeFactory is null)
            throw new ArgumentException(
                "WorkflowSuspensionWakeOptions.ExpressionNamespaces is set without a NamespaceScopeFactory, so a parked "
                + "template could never read those namespaces. Set both.",
                nameof(options));
        return options;
    }

    private static bool MayPutBack(
        WorkflowSuspensionCandidate candidate, WorkflowSuspensionStatus status, WorkflowSuspensionWake? wake) =>
        status != WorkflowSuspensionStatus.Resumed
        || candidate.Kind switch
        {
            WorkflowSuspensionKind.WaitForTrigger => wake is not { Reason: WorkflowSuspensionWakeReasons.Trigger },
            WorkflowSuspensionKind.Predicate => false,
            _ => true,
        };

    private async Task<bool> WakeAsync(
        WorkflowSuspensionCandidate candidate, WorkflowSuspensionStatus status, ResumeContext context, bool retake,
        WorkflowSuspensionWake? wake, CancellationToken cancellationToken)
    {
        WorkflowResumeOutcome resumed;
        try
        {
            resumed = await resumer.ResumeFromSuspensionAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex,
                "Parked run {ExecutionLogId} was taken and its resume at block {BlockId} let a cancellation out, which "
                + "does not say whether the run was entered; the row stays taken rather than be put back over it.",
                candidate.ExecutionLogId, candidate.BlockId);
            if (cancellationToken.IsCancellationRequested)
                throw;
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Parked run {ExecutionLogId} was taken but could not be resumed at block {BlockId}.",
                candidate.ExecutionLogId, candidate.BlockId);
            return false;
        }

        if (resumed == WorkflowResumeOutcome.Deferred)
        {
            if (!retake && MayPutBack(candidate, status, wake))
                await ReleaseAsync(candidate, status, cancellationToken).ConfigureAwait(false);
            else if (!retake)
                SayTheRowStaysTaken(candidate, "the resume did nothing to the run and answered Deferred");
            return false;
        }

        if (retake && resumed == WorkflowResumeOutcome.Resumed)
            await SayRetakenAsync(candidate).ConfigureAwait(false);

        return resumed == WorkflowResumeOutcome.Resumed;
    }

    private async Task ReleaseAsync(
        WorkflowSuspensionCandidate candidate, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
    {
        try
        {
            if (!await store.ReleaseAsync(candidate.SuspensionId, takenAs, CancellationToken.None).ConfigureAwait(false))
                return;

            logger.LogDebug(
                "Parked run {ExecutionLogId} was put back; a later pass tries it again.", candidate.ExecutionLogId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Parked run {ExecutionLogId} was taken, not resumed, and could not be put back.",
                candidate.ExecutionLogId);
        }
    }

    private async Task<string?> ConditionOutcomeAsync(
        WorkflowSuspensionCandidate candidate, DateTimeOffset now, WorkflowSuspensionWake? wake, TakeReport? report,
        CancellationToken cancellationToken, SweepResolutionCache? resolutions = null)
    {
        var predicate = candidate.Kind == WorkflowSuspensionKind.Predicate;
        if (!predicate && candidate.Kind != WorkflowSuspensionKind.WaitTemplate)
            return null;
        if (scopes is null)
        {
            WarnOnceThatTemplatesAreNotRead();
            return null;
        }

        if (predicate)
            wake = wake! with { Data = null };

        string? input;
        try
        {
            input = predicate
                ? JsonSerializer.Deserialize<WorkflowPredicateWaitConfig>(candidate.ConfigJson, JsonOptions)?.Input
                : JsonSerializer.Deserialize<WorkflowStoredTemplateConfig>(candidate.ConfigJson, JsonOptions)?.Input;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            logger.LogWarning(ex, "Parked run {SuspensionId} carries a {Kind} config this waker cannot read; it waits for its timeout.",
                candidate.SuspensionId, candidate.Kind);
            return null;
        }

        if (string.IsNullOrWhiteSpace(input))
            return null;

        var resolutionCache = resolutions ?? new SweepResolutionCache();

        WorkflowSuspensionStatePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(candidate.StatePayloadJson, JsonOptions)
                ?? throw new JsonException("The state payload is null.");
        }
        catch (JsonException ex)
        {
            return Unevaluable(WorkflowAbandonmentReasons.ResumeValueInvalid, ex);
        }

        WorkflowProgram? program;
        try
        {
            var resolved = await resolutionCache.ProgramAsync(
                candidate, payload, scopes, compiler, cancellationToken).ConfigureAwait(false);
            program = resolved.Failure is { } failure
                ? throw new InvalidOperationException("Pinned version resolution failed for this parked row.", failure.SourceException)
                : resolved.Program;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Parked run {SuspensionId}: its pinned version could not be compiled; the next pass tries again.",
                candidate.SuspensionId);
            return null;
        }

        if (program is null)
            return Unevaluable(WorkflowAbandonmentReasons.PinnedVersionUncompilable, null);

        CompiledNode node;
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> outputs;
        IReadOnlyDictionary<string, object?>? parameters;
        TriggerEnvelope? envelope;
        Guid? triggerId;
        try
        {
            (node, outputs, parameters, triggerId, envelope) = ReadParked(candidate, payload, program);
        }
        catch (PersistedPayloadException ex)
        {
            return Unevaluable(WorkflowAbandonmentReasons.ResumeValueInvalid, ex);
        }

        CompiledExpression condition;
        try
        {
            condition = WorkflowParkCheck.ConditionOf(node, input);
        }
        catch (WorkflowSuspendConfigInvalidException ex)
        {
            return Unevaluable(WorkflowAbandonmentReasons.ResumeValueInvalid, ex);
        }

        var variables = NoVariables;
        Dictionary<string, JsonElement>? written = null;
        if (predicate)
        {
            var port = node.Shape.Inputs.First(p => string.Equals(p.Name, input, StringComparison.Ordinal));
            if (BlockExpressions.Admitted(node, port, wake!.Variables!, out var admitted) is { } refused)
                return DropWake(refused);
            try
            {
                written = PersistedValueCodec.WriteVariables(admitted, node.ScopeVariables.GetValueOrDefault(input) ?? NoTerms);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return DropWake($"a variable will not write: {ex.Message}");
            }

            variables = admitted;
        }

        var zone = condition.Text.Contains("now.local", StringComparison.OrdinalIgnoreCase)
            ? await ZoneForAsync(candidate, cancellationToken).ConfigureAwait(false)
            : options.TimeZone;
        if (zone is null)
            return null;

        ExpressionEvaluationScope evaluation;
        IReadOnlyCollection<ExpressionNamespaceBinding> hostNamespaces;
        WorkflowTypeVocabulary vocabulary;
        try
        {
            hostNamespaces = [];
            if (options.ExpressionNamespaces.Count > 0 && options.NamespaceScopeFactory is { } scopeFactory)
            {
                using var scope = scopeFactory.CreateScope();
                hostNamespaces = await WorkflowExpressionNamespaces.MergeResolversAsync(
                    options.ExpressionNamespaces,
                    new ExpressionNamespaceScope(scope.ServiceProvider, candidate.ExecutionLogId, triggerId, envelope),
                    cancellationToken).ConfigureAwait(false);
            }

            vocabulary = options.TypeVocabulary?.Invoke() ?? compiler.Options.Vocabulary;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Parked run {SuspensionId}: the host's expression namespaces or vocabulary could not be read; "
                + "the next pass tries again.", candidate.SuspensionId);
            return null;
        }

        IReadOnlyDictionary<HelperPath, object?> helperValues;
        try
        {
            var helperPaths = condition.Tree.HelperPaths;
            helperValues = helperPaths.Count == 0 ? EmptyHelperValues
                : await (helperResolver ?? NullHelperResolver.Instance).ReadAsync(helperPaths, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new ExpressionEvaluationException(
                $"Reading helper values for the parked candidate failed: {ex.GetType().Name}: {ex.Message}",
                condition.Tree.Position, ex, null);
            return Faulted(new WorkflowFailureCode(WorkflowErrorCodes.ExpressionRuntimeError, null), failure.Message, wake);
        }

        evaluation = WorkflowGraphExecutor.EvaluationScope(helperValues, outputs, parameters, zone, hostNamespaces, vocabulary, now);

        object? answer;
        try
        {
            answer = new BlockExpressions(node, () => evaluation).Evaluate(input, variables);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Faulted(new WorkflowFailureCode(WorkflowErrorCodes.ExpressionRuntimeError, WorkflowFailureCoding.Of(ex).Category),
                ex.Message, wake);
        }

        return answer switch
        {
            true when written is not null => OutcomeJson(WorkflowWaitOutcomes.Satisfied, wake,
                extra: written.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal)),
            true => OutcomeJson(WorkflowWaitOutcomes.Satisfied, wake, lastEvaluation: true),
            false => null,
            _ => Faulted(new WorkflowFailureCode(WorkflowErrorCodes.ExpressionRuntimeError, null),
                "the condition answered no Boolean", wake),
        };

        string Unevaluable(string reason, Exception? ex)
        {
            logger.LogWarning(ex, "Parked run {SuspensionId} is unevaluable ({Reason}); the resumer abandons it.",
                candidate.SuspensionId, reason);
            return OutcomeJson(WorkflowWaitOutcomes.Unevaluable, wake, extra: new() { ["reason"] = reason });
        }

        string? DropWake(string because)
        {
            Dropped(candidate, report, because);
            return null;
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> NoVariables = new Dictionary<string, object?>();

    private static readonly IReadOnlyDictionary<string, ClosedPortType> NoTerms = new Dictionary<string, ClosedPortType>();

    private string Faulted(WorkflowFailureCode failure, string message, WorkflowSuspensionWake? wake)
    {
        var error = new Dictionary<string, object?> { ["code"] = failure.Code, ["message"] = message };
        if (failure.Category is { } category)
            error["category"] = category;
        return OutcomeJson(WorkflowWaitOutcomes.Faulted, wake, extra: new() { ["error"] = error });
    }

    private (CompiledNode Node, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Outputs,
        IReadOnlyDictionary<string, object?>? Parameters, Guid? TriggerId, TriggerEnvelope? Envelope) ReadParked(
        WorkflowSuspensionCandidate candidate, WorkflowSuspensionStatePayload payload, WorkflowProgram program)
    {
        var root = CompiledWalk.Root(program);
        var outputs = PersistedValueCodec.ReadNodeOutputs(payload.PreviousOutputsJson, root.Canvas, "outputs");
        var (triggerId, envelope) = PersistedValueCodec.ReadTriggerEnvelope(payload.TriggerEnvelopeJson,
            WorkflowStartArguments.PayloadTerms(compiler.Options));
        var frames = WorkflowRunCheckpoint.Continuation(payload);
        var rootNode = payload.NodeInFlight ?? candidate.BlockId;
        PersistedValueCodec.ReadPosition(frames, payload.ContinuationJson, root, rootNode);
        var (node, scopeOutputs, parameters) = ParkedScope.Of(
            root, outputs, frames, rootNode, candidate.BlockId, payload.InputNodeId);
        return (node, scopeOutputs, parameters, triggerId, envelope);
    }

    private int templatesUnreadLogged;

    private void WarnOnceThatTemplatesAreNotRead()
    {
        if (Interlocked.Exchange(ref templatesUnreadLogged, 1) == 0)
            logger.LogWarning("This waker was built without run scopes, so a parked template or predicate waits for its timeout.");
    }

    private async Task<TimeZoneInfo?> ZoneForAsync(
        WorkflowSuspensionCandidate candidate, CancellationToken cancellationToken)
    {
        if (options.ResolveTimeZone is not { } resolve)
            return options.TimeZone;

        try
        {
            var zone = await resolve(candidate, cancellationToken).ConfigureAwait(false);
            if (zone is not null)
                return zone;

            logger.LogWarning(
                "No time zone for parked run {ExecutionLogId}; its template waits for its timeout.",
                candidate.ExecutionLogId);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Could not resolve the time zone for parked run {ExecutionLogId}; its template waits.",
                candidate.ExecutionLogId);
            return null;
        }
    }

    private static string OutcomeJson(
        string outcome, WorkflowSuspensionWake? wake = null, bool? lastEvaluation = null, Dictionary<string, object?>? extra = null)
    {
        var payload = wake?.Data is { } sent
            ? new Dictionary<string, object?>(sent, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        payload[WorkflowResumeDecisions.OutcomeKey] = outcome;
        payload[WorkflowResumeDecisions.ResumeReasonKey] = outcome == WorkflowWaitOutcomes.TimedOut
            ? WorkflowResumeDecisions.TimeoutReason
            : wake?.Reason ?? WorkflowResumeDecisions.PollReason;

        if (lastEvaluation is { } evaluated)
            payload[WorkflowResumeDecisions.LastEvaluationKey] = evaluated;
        foreach (var (key, value) in extra ?? [])
            payload[key] = value;

        return JsonSerializer.Serialize(payload, JsonOptions);
    }
}

/// <summary>How hard the waker looks, and what a local wall-clock field means while it is looking.</summary>
public sealed record WorkflowSuspensionWakeOptions
{
    /// <summary>How often a pass runs. A parked run waits at most this long past the moment it was ready.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many parked rows one pass reads: due rows oldest timeout first, then templates and
    /// sub-workflow parks. The same cap bounds the abandoned takes a pass looks at and the held wakes it retries.</summary>
    /// <remarks>The next pass still waits its <see cref="PollInterval"/> (a park or a host wake starts it sooner),
    /// so an overdue backlog drains at most this many rows per interval, and a pass stops taking once every resume
    /// worker is busy. A due row a pass reads but leaves parked keeps its timeout, so it heads the next pass's page
    /// again.</remarks>
    public int BatchSize { get; init; } = 50;

    /// <summary>How long a suspension row is left alone after it is written. It exists before the run it
    /// belongs to has finished parking, and a pass inside that window resumes a run that then parks behind it.</summary>
    /// <remarks>It delays the first look at a freshly parked run by at most this, and nothing else: a row past
    /// its timeout is older than this by construction.</remarks>
    public TimeSpan SettleFor { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether the waker runs at all. False leaves the rows to whatever else the host wakes them with.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How many taken runs one pass re-enters at the same time. The rows are still taken one by one;
    /// this caps how much of a dead-quiet replica a burst of due timers may claim at once.</summary>
    public int MaxConcurrentResumes { get; init; } = 8;

    /// <summary>How many wakes this replica re-enters runs for at the same time. A budget of its own and not the
    /// page's: <see cref="MaxConcurrentResumes"/> is what a pass returns early on, without advancing its cursor,
    /// so a stream of wakes sharing it would stop the page reaching overdue timeouts and durable sleeps at all.</summary>
    public int MaxConcurrentWakes { get; init; } = 4;

    /// <summary>How many wakes that named no row this replica could take yet are kept, waiting for the row to
    /// land. Past it the oldest goes. Zero holds nothing, which is a host saying its wakes never outrun its
    /// parks.</summary>
    public int MaxHeldWakes { get; init; } = 1024;

    /// <summary>How long such a wake waits for its row before it is dropped and the page is left to answer for
    /// it. Null derives it as the longer of twice <see cref="SettleFor"/> and three <see cref="PollInterval"/>s:
    /// the hold is re-checked once a pass, so one shorter than a pass would never be re-checked at all.</summary>
    public TimeSpan? HoldWakesFor { get; init; }

    /// <summary>How long a taken wait may go without its run being re-entered before a pass takes it over. Null —
    /// the default for a waker composed by hand — never does. A taker that died between the take and the
    /// re-entry otherwise leaves its run parked for ever.</summary>
    /// <remarks>Only for a run with a state row: the swap that moves that row back to Running, fenced on the
    /// node the run is parked at, is what stops a retake of a run somebody is still resuming, so a run without
    /// one is never retaken. A retake of a run that is merely slow is refused before the budget is asked, by a read
    /// of that row, and stays taken for the look after the cutoff. A retake the resume defers stays taken too, with
    /// the answer it was taken with. Neither is a takeover, and neither is logged or audited as one.</remarks>
    public TimeSpan? RetakeAbandonedTakesAfter { get; init; }

    /// <summary>Which kinds of parked row this pass moves, taking over an abandoned take of them included. Null moves
    /// every kind, and empty is refused; a host that wakes its own templates and triggers names
    /// <see cref="WorkflowSuspensionKind.Sleep"/> alone.</summary>
    public IReadOnlyCollection<WorkflowSuspensionKind>? Kinds { get; init; }

    /// <summary>The zone a template's <c>${now.local*}</c> fields are read in while it waits.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>The same question asked of the row, for a host whose zone is a lookup rather than a setting.
    /// Called once per template row per pass, outside any run's scope: the row is all there is to read.</summary>
    public Func<WorkflowSuspensionCandidate, CancellationToken, Task<TimeZoneInfo?>>? ResolveTimeZone { get; init; }

    /// <summary>The registration snapshot captured when the host is built. Refused by the waker without a
    /// <see cref="NamespaceScopeFactory"/>.</summary>
    public IReadOnlyList<WorkflowExpressionNamespace> ExpressionNamespaces { get; init; } = [];

    /// <summary>Creates a scope for the registration factories used to evaluate a parked row. Required when
    /// <see cref="ExpressionNamespaces"/> is not empty.</summary>
    public IServiceScopeFactory? NamespaceScopeFactory { get; init; }

    /// <summary>The port-type vocabulary a host namespace's value is held to while a template waits: the one the compile
    /// used. Read at each template. The library's own types when not set.</summary>
    public Func<WorkflowTypeVocabulary>? TypeVocabulary { get; init; }
}
