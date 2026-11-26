using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>One pass over the runs nobody is running: those a dead replica left behind, and those a stopping replica
/// handed over. Claim them, and either carry each on from the block its checkpoint names, replay it from its start
/// where it has no checkpoint to re-enter, or fail it because a block that would run again cannot be run
/// twice.</summary>
/// <remarks>Safe on every replica at once — the claim is what makes it so, and it belongs to the store. This
/// class only decides what to do with a row once it holds it.
/// <para>Once <c>hostStopping</c> fires a sweep claims nothing more, and a row it holds but did not carry to an
/// ending is released as a stopping run is: its owner is cleared, so the next sweep on a peer takes it at once
/// instead of waiting out <see cref="WorkflowRunRecoveryOptions.AbandonedAfter"/>.</para></remarks>
/// <param name="hostStopping">The host's stop signal, <c>IHostApplicationLifetime.ApplicationStopping</c>; it fires
/// before the sweep's own token, while the engine is handing its runs over. <c>AddWorkflowEngine</c> passes it.</param>
public sealed class WorkflowRunRecoverySweep(
    IWorkflowEngine engine,
    IWorkflowRunStateStore runState,
    IWorkflowRunRecoveryStore recovery,
    WorkflowRunRecoveryOptions options,
    string? replicaId,
    ILogger logger,
    WorkflowRunResumer? resumer = null,
    IWorkflowMetrics? metrics = null,
    IWorkflowAuditSink? audit = null,
    IWorkflowExecutionGate? executionGate = null,
    CancellationToken hostStopping = default)
{
    private readonly SemaphoreSlim places = new(Math.Max(1, options.MaxConcurrentResumes));
    private readonly Lock inFlightGate = new();
    private readonly List<Task> inFlight = [];

    /// <summary>One pass, awaited until every run it claimed has been carried to an ending or left. Returns how many
    /// rows this pass took.</summary>
    /// <remarks>It claims no more rows than this sweep has free places (<see cref="WorkflowRunRecoveryOptions.MaxConcurrentResumes"/>),
    /// and none while every place is taken.</remarks>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var (claimed, batch) = await ClaimAsync(cancellationToken).ConfigureAwait(false);
        await batch.ConfigureAwait(false);
        return claimed;
    }

    /// <summary>One pass that returns once its claim is made, leaving the runs it claimed to carry on in the
    /// background. Returns how many rows it took. What the hosted service calls: a recovered run that sleeps or waits
    /// on a slow block holds only its own place, and the next pass claims into the places that are free.</summary>
    /// <remarks><see cref="WaitForResumesAsync"/> is how a stopping host waits for what it claimed.</remarks>
    public async Task<int> ClaimOnceAsync(CancellationToken cancellationToken)
    {
        var (claimed, batch) = await ClaimAsync(cancellationToken).ConfigureAwait(false);
        if (claimed == 0)
            return 0;

        lock (inFlightGate)
            inFlight.Add(batch);
        _ = batch.ContinueWith(
            done =>
            {
                lock (inFlightGate)
                    inFlight.Remove(done);
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return claimed;
    }

    /// <summary>Completes when every run a <see cref="ClaimOnceAsync"/> claimed has been carried to an ending, left,
    /// or released. Never throws: each of them has logged its own ending.</summary>
    public async Task WaitForResumesAsync()
    {
        Task[] running;
        lock (inFlightGate)
            running = [.. inFlight];

        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task<(int Claimed, Task Batch)> ClaimAsync(CancellationToken cancellationToken)
    {
        if (hostStopping.IsCancellationRequested)
            return (0, Task.CompletedTask);

        var free = places.CurrentCount;
        if (free == 0)
        {
            Count(() => metrics?.RecordRecoveryClaim(0), "a claim of 0");
            return (0, Task.CompletedTask);
        }

        var claimed = await recovery.ClaimAbandonedRunsAsync(new WorkflowRunRecoveryClaim
        {
            AbandonedAfter = options.AbandonedAfter,
            BatchSize = Math.Min(options.BatchSize, free),
            ClaimedByReplicaId = replicaId
        }, cancellationToken).ConfigureAwait(false);

        Count(() => metrics?.RecordRecoveryClaim(claimed.Count), "a claim of " + claimed.Count);

        if (claimed.Count == 0)
            return (0, Task.CompletedTask);

        logger.LogInformation("Claimed {Count} run(s) nobody was running.", claimed.Count);

        return (claimed.Count, Task.Run(() => CarryOnAsync(claimed, cancellationToken), CancellationToken.None));
    }

    private async Task CarryOnAsync(IReadOnlyList<WorkflowRunStateRow> claimed, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hostStopping);
        using var held = new HeldRows(claimed.Select(row => row.ExecutionId));
        using var stopBeating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var beat = BeatAsync(held, stopBeating.Token);
        try
        {
            await Task.WhenAll(claimed.Select(row => InAPlaceAsync(row, held, stopping.Token))).ConfigureAwait(false);
        }
        finally
        {
            await stopBeating.CancelAsync().ConfigureAwait(false);
            await beat.ConfigureAwait(false);
        }
    }

    private async Task InAPlaceAsync(WorkflowRunStateRow row, HeldRows held, CancellationToken cancellationToken)
    {
        var placed = false;
        try
        {
            await places.WaitAsync(cancellationToken).ConfigureAwait(false);
            placed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        try
        {
            await SayClaimedAsync(row).ConfigureAwait(false);

            await ResumeClaimedRunAsync(row, held, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (placed)
                places.Release();
        }
    }

    private async Task ResumeClaimedRunAsync(
        WorkflowRunStateRow row, HeldRows held, CancellationToken cancellationToken)
    {
        if (held.Working(row.ExecutionId, cancellationToken) is not { } runToken)
        {
            logger.LogWarning("Lost the claim on run {ExecutionId} before it was dispatched.", row.ExecutionId);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ReenterAsync(row, held, runToken, cancellationToken).ConfigureAwait(false))
                return;

            if (row.ParentExecutionId is { } parent)
            {
                logger.LogInformation(
                    "Run {ExecutionId} was dispatched by run {ParentExecutionId} and cannot be re-entered, so it "
                    + "is ended here rather than replayed from the start.", row.ExecutionId, parent);
                if (await runState.CompleteIfOwnedAsync(
                        row.ExecutionId, replicaId, WorkflowRunStateStatus.Failed, DispatchedByAnotherRunReason, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await CloseNamedEntryAsync(row, WorkflowExecutionStatus.Cancelled, DispatchedByAnotherRunReason).ConfigureAwait(false);
                    await SayEndedAsync(row, DispatchedByAnotherRunReason, ("parentExecutionId", parent))
                        .ConfigureAwait(false);
                }
                return;
            }

            if (!await IsSafeToReplayAsync(row, cancellationToken).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "Run {ExecutionId} of workflow {WorkflowId} cannot be replayed from the start; failing it rather than running a block twice.",
                    row.ExecutionId, row.WorkflowId);
                if (await runState.CompleteIfOwnedAsync(
                        row.ExecutionId, replicaId, WorkflowRunStateStatus.Failed, NotIdempotentReason, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await CloseNamedEntryAsync(row, WorkflowExecutionStatus.Failed, NotIdempotentReason).ConfigureAwait(false);
                    await SayEndedAsync(row, NotIdempotentReason).ConfigureAwait(false);
                }
                return;
            }

            var replay = await engine.ReplayAbandonedRunAsync(row.ExecutionId, row.WorkflowId, runToken)
                .ConfigureAwait(false);

            if (held.WasLost(row.ExecutionId))
            {
                logger.LogWarning(
                    "A peer reclaimed run {ExecutionId} while it was being resumed here; the row is theirs to end.",
                    row.ExecutionId);
                return;
            }

            switch (replay)
            {
                case WorkflowReplayOutcome.NotAdmitted when GivingUpOn(row) is { } age:
                    logger.LogWarning(
                        "Run {ExecutionId} of workflow {WorkflowId} was {Age} old when it was claimed and its "
                        + "replay is refused, past this host's bound of {Bound}; it is given up rather than retried.",
                        row.ExecutionId, row.WorkflowId, age, options.GiveUpOnRefusalAfter);
                    if (await CompleteQuietlyAsync(row.ExecutionId, WorkflowRunStateStatus.Failed, NotAdmittedReason)
                            .ConfigureAwait(false))
                    {
                        await CloseNamedEntryAsync(row, WorkflowExecutionStatus.Failed, NotAdmittedReason)
                            .ConfigureAwait(false);
                        await SayEndedAsync(row, NotAdmittedReason).ConfigureAwait(false);
                    }
                    return;
                case WorkflowReplayOutcome.NotAdmitted:
                    logger.LogInformation(
                        "Abandoned run {ExecutionId} of workflow {WorkflowId} was not admitted; it stays claimed and returns to a later sweep.",
                        row.ExecutionId, row.WorkflowId);
                    return;
                case WorkflowReplayOutcome.Stopped:
                    logger.LogInformation(
                        "The replay of run {ExecutionId} was stopped before its end; the row returns to a later sweep.",
                        row.ExecutionId);
                    return;
                case WorkflowReplayOutcome.Finished:
                    if (await CompleteQuietlyAsync(row.ExecutionId, WorkflowRunStateStatus.Completed, ReplayedReason)
                            .ConfigureAwait(false))
                    {
                        await CloseNamedEntryAsync(row, WorkflowExecutionStatus.Cancelled,
                            $"{ReplayedReason}: the run was replayed from its start under a journal entry of its own.")
                            .ConfigureAwait(false);
                        await SayEndedAsync(row, ReplayedReason).ConfigureAwait(false);
                    }
                    return;
                default:
                    if (await CompleteQuietlyAsync(row.ExecutionId, WorkflowRunStateStatus.Failed, ResumeFailedReason)
                            .ConfigureAwait(false))
                    {
                        await CloseNamedEntryAsync(row, WorkflowExecutionStatus.Failed, ResumeFailedReason).ConfigureAwait(false);
                        await SayEndedAsync(row, ResumeFailedReason).ConfigureAwait(false);
                    }
                    return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (held.WasLost(row.ExecutionId))
        {
            logger.LogWarning(ex,
                "The resume of run {ExecutionId} ended after a peer reclaimed the row; leaving it to them.",
                row.ExecutionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resume abandoned run {ExecutionId}.", row.ExecutionId);
            if (await CompleteQuietlyAsync(row.ExecutionId, WorkflowRunStateStatus.Failed, ResumeFailedReason)
                    .ConfigureAwait(false))
            {
                await CloseNamedEntryAsync(row, WorkflowExecutionStatus.Failed, ResumeFailedReason).ConfigureAwait(false);
                await SayEndedAsync(row, ResumeFailedReason).ConfigureAwait(false);
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested && !held.WasLost(row.ExecutionId)
                && await WorkflowRunHandover.ReleaseAsync(runState, row.ExecutionId, replicaId, logger).ConfigureAwait(false))
                logger.LogInformation("Released run {ExecutionId} for a peer: this replica is stopping.", row.ExecutionId);
            held.Done(row.ExecutionId);
        }
    }

    private async Task BeatAsync(HeldRows held, CancellationToken cancellationToken)
    {
        var every = RenewalInterval(options.AbandonedAfter);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(every, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            foreach (var executionId in held.Owed())
            {
                try
                {
                    if (await recovery.RenewClaimAsync(executionId, replicaId, cancellationToken)
                        .ConfigureAwait(false))
                        continue;

                    logger.LogWarning("Lost the claim on run {ExecutionId}: a peer has taken it back.", executionId);
                    held.Lose(executionId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not renew the claim on run {ExecutionId}.", executionId);
                }
            }
        }
    }

    private static TimeSpan RenewalInterval(TimeSpan abandonedAfter) =>
        TimeSpan.FromMilliseconds(Math.Max(1000d, abandonedAfter.TotalMilliseconds / 3d));

    private Task SayClaimedAsync(WorkflowRunStateRow row) =>
        WriteAsync(row, WorkflowAuditEventKinds.ClaimedByRecovery, null, null);

    private async Task SayEndedAsync(
        WorkflowRunStateRow row, string reason, (string Name, object? Value)? detail = null)
    {
        Count(() => metrics?.RecordRecoveryEnding(reason), "an ending of " + reason);
        await WriteAsync(row, WorkflowAuditEventKinds.EndedByRecovery, reason, detail).ConfigureAwait(false);
    }

    private void Count(Action record, string what)
    {
        try
        {
            record();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to record {What}.", what);
        }
    }

    private async Task WriteAsync(
        WorkflowRunStateRow row, string kind, string? reason, (string Name, object? Value)? detail)
    {
        if (audit is null)
            return;

        var journalId = WorkflowRunCheckpoint.Of(row)?.JournalExecutionId;

        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["executionId"] = row.ExecutionId,
            ["journalExecutionId"] = journalId,
            ["subjectIs"] = journalId is null ? "executionId" : "journalExecutionId",
            ["workflowId"] = row.WorkflowId,
            ["replicaId"] = replicaId,
            ["reason"] = reason
        };

        if (detail is { } named)
            data[named.Name] = named.Value;

        try
        {
            await audit.WriteAsync(
                new WorkflowAuditEvent(
                    Kind: kind,
                    ActorId: null,
                    SubjectId: journalId ?? row.ExecutionId,
                    Data: data),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "failed to write the {Kind} audit event for run {ExecutionId}.", kind, row.ExecutionId);
        }
    }

    /// <summary>What a run's row says when recovery refused to replay the block it died inside.</summary>
    public const string NotIdempotentReason = "block_not_idempotent_during_recovery";

    private const int RowReasonLength = 2000;

    /// <summary>What a run's row says when the re-dispatch itself threw.</summary>
    public const string ResumeFailedReason = "resume_failed";

    /// <summary>What the old row says once recovery has re-dispatched it. The row is closed so later sweeps
    /// stop picking it up, and this says the run it describes is a different row's now — not one that ran to
    /// the end here.</summary>
    public const string ReplayedReason = "replayed_by_recovery";

    /// <summary>What a run's row says when it was a sub-workflow of a run that is itself being recovered.</summary>
    public const string DispatchedByAnotherRunReason = "dispatched_by_another_run";

    /// <summary>What a run's row says when admission kept refusing its replay until
    /// <see cref="WorkflowRunRecoveryOptions.GiveUpOnRefusalAfter"/> had passed.</summary>
    public const string NotAdmittedReason = "not_admitted";

    /// <summary>What a re-entry refusing the block a run died inside is <em>counted</em> as. Unlike the five
    /// above this is never written on a row: the row gets <c>"{NotIdempotentReason}: {block}"</c>, and a label
    /// carrying a block id is one metric series per node on a fleet recovering a few hundred runs. So the
    /// count is this bare constant and the block goes in the audit event's data — which means the row's reason
    /// and this label do not match, on purpose, and a host joining the two matches on the audit event.</summary>
    public const string RefusedAtReentryReason = "block_not_idempotent_at_reentry";

    private TimeSpan? GivingUpOn(WorkflowRunStateRow row)
    {
        if (options.GiveUpOnRefusalAfter is not { } bound || row.StartedAtUtc == default)
            return null;

        if (executionGate?.IsExecutionSuspended == true)
            return null;

        var age = row.LastTransitionUtc - row.StartedAtUtc;
        return age > bound ? age : null;
    }

    private async Task<bool> ReenterAsync(
        WorkflowRunStateRow row, HeldRows held, CancellationToken runToken, CancellationToken cancellationToken)
    {
        if (resumer is null || engine is not IWorkflowRunReentry reentry
            || WorkflowRunCheckpoint.Of(row) is not { NotReenterableBecause: null } checkpoint)
            return false;

        var decision = await reentry.DecideReentryAsync(row, checkpoint, cancellationToken).ConfigureAwait(false);
        switch (decision.Verdict)
        {
            case WorkflowRunReentryVerdict.Replay:
                return false;
            case WorkflowRunReentryVerdict.Abandon or WorkflowRunReentryVerdict.Refuse:
                var abandoned = decision.Verdict == WorkflowRunReentryVerdict.Abandon;
                var reason = abandoned
                    ? $"{WorkflowAbandonmentReasons.ResumeValueInvalid}: {decision.InvalidValue}"
                    : $"{NotIdempotentReason}: {decision.RefusedNodeId}";
                if (reason.Length > RowReasonLength)
                    reason = reason[..RowReasonLength];
                if (abandoned)
                    logger.LogWarning(
                        "Run {ExecutionId} of workflow {WorkflowId} has a checkpoint that does not read; abandoning it: {Reason}",
                        row.ExecutionId, row.WorkflowId, reason);
                else
                    logger.LogWarning(
                        "Run {ExecutionId} of workflow {WorkflowId} died inside block {Block}, which may not run twice; failing it.",
                        row.ExecutionId, row.WorkflowId, decision.RefusedNodeId);
                if (await runState.CompleteIfOwnedAsync(
                        row.ExecutionId, replicaId, WorkflowRunStateStatus.Failed, reason, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await CloseNamedEntryAsync(row, WorkflowExecutionStatus.Failed, reason).ConfigureAwait(false);
                    await (abandoned
                            ? SayEndedAsync(row, WorkflowAbandonmentReasons.ResumeValueInvalid, ("invalid", decision.InvalidValue))
                            : SayEndedAsync(row, RefusedAtReentryReason, ("refusedNodeId", decision.RefusedNodeId)))
                        .ConfigureAwait(false);
                }
                return true;
        }

        var outcome = await resumer.ReenterFromCheckpointAsync(row, checkpoint, runToken).ConfigureAwait(false);
        if (outcome != WorkflowResumeOutcome.Resumed && held.WasLost(row.ExecutionId))
            logger.LogWarning(
                "A peer reclaimed run {ExecutionId} while it was being re-entered here; the row is theirs to end.",
                row.ExecutionId);
        else if (outcome is WorkflowResumeOutcome.Deferred or WorkflowResumeOutcome.HeldElsewhere)
            logger.LogInformation("Run {ExecutionId} was not re-entered ({Outcome}); it is left as it is.",
                row.ExecutionId, outcome);
        return true;
    }

    private Task<bool> IsSafeToReplayAsync(WorkflowRunStateRow row, CancellationToken cancellationToken) =>
        engine.IsRunReplayableAsync(
            row.WorkflowId, row.CurrentBlockPath, WorkflowEngine.PinOf(row, row.WorkflowId).VersionId,
            cancellationToken);

    private async Task<bool> CompleteQuietlyAsync(Guid executionId, WorkflowRunStateStatus status, string reason)
    {
        try
        {
            if (await runState.CompleteIfOwnedAsync(executionId, replicaId, status, reason, CancellationToken.None)
                    .ConfigureAwait(false))
                return true;

            logger.LogInformation(
                "Run {ExecutionId} was not ended as {Status} after its replay: it is no longer this sweep's.",
                executionId, status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to end run {ExecutionId} as {Status} after its replay.", executionId, status);
        }

        return false;
    }

    private async Task CloseNamedEntryAsync(WorkflowRunStateRow row, WorkflowExecutionStatus status, string reason)
    {
        if (resumer is not null && WorkflowRunCheckpoint.Of(row)?.JournalExecutionId is { } entry)
            await resumer.CloseEntryAsync(entry, status, reason).ConfigureAwait(false);
    }

    private sealed class HeldRows(IEnumerable<Guid> executionIds) : IDisposable
    {
        private readonly Lock gate = new();
        private readonly HashSet<Guid> owed = [.. executionIds];
        private readonly HashSet<Guid> lost = [];
        private readonly List<CancellationTokenSource> tokens = [];
        private readonly Dictionary<Guid, CancellationTokenSource> working = [];

        public Guid[] Owed()
        {
            lock (gate)
                return [.. owed];
        }

        public CancellationToken? Working(Guid executionId, CancellationToken sweep)
        {
            lock (gate)
            {
                if (lost.Contains(executionId))
                    return null;

                var stop = CancellationTokenSource.CreateLinkedTokenSource(sweep);
                tokens.Add(stop);
                working[executionId] = stop;
                return stop.Token;
            }
        }

        public bool WasLost(Guid executionId)
        {
            lock (gate)
                return lost.Contains(executionId);
        }

        public void Lose(Guid executionId)
        {
            CancellationTokenSource? stop = null;
            lock (gate)
            {
                owed.Remove(executionId);
                lost.Add(executionId);
                working.TryGetValue(executionId, out stop);
            }

            stop?.Cancel();
        }

        public void Done(Guid executionId)
        {
            lock (gate)
            {
                owed.Remove(executionId);
                working.Remove(executionId);
            }
        }

        public void Dispose()
        {
            foreach (var token in tokens)
                token.Dispose();
        }
    }
}

/// <summary>What a host says about picking up runs whose replica died.</summary>
public sealed class WorkflowRunRecoveryOptions
{
    /// <summary>Whether this host sweeps at all. Off unless asked for: a recovered run repeats the block it died in
    /// (a run with no checkpoint to re-enter, the whole run from its start), and what that costs depends
    /// on what the workflows do — see the limits on the two properties below. A host that has
    /// read them turns it on in one line.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often this replica looks for runs abandoned by a dead peer.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a run's row may go untouched before its owner is presumed dead.</summary>
    /// <remarks>A live run renews its row on the engine's run heartbeat, so this must outlast a few beats — not
    /// the longest block. A host that turns the heartbeat off writes a row before
    /// each block and not while it runs, and this must then exceed the longest block any workflow contains, or a
    /// live replica's own run is reclaimed and run twice. A block's write stamps the row with
    /// the writing replica's clock, so leave room for the drift between hosts too.</remarks>
    public TimeSpan AbandonedAfter { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The most abandoned runs one sweep claims, so a replica coming up after a long outage does not
    /// take the whole backlog at once. A sweep claims fewer when it has fewer free places
    /// (<see cref="MaxConcurrentResumes"/>).</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>How many recovered runs this replica carries on at the same time, across every sweep. A sweep claims
    /// only as many rows as there are free places and none while every place is taken; the runs it claimed carry on
    /// in the background, so one that sleeps or waits on a slow block holds only its own place, and the next sweep
    /// claims into the rest. A value below one counts as one.</summary>
    /// <remarks>The rows of one claim start in the order the store returned them, oldest first, but carry on side by
    /// side, so they do not end in that order, and a later sweep can start a newer row while an older one still
    /// runs.</remarks>
    public int MaxConcurrentResumes { get; set; } = 8;

    /// <summary>How long after a run started a sweep keeps retrying a replay admission refuses, before ending
    /// the run <see cref="WorkflowRunRecoverySweep.NotAdmittedReason"/> instead. An hour by default; unset keeps such a run for ever, re-dispatched every
    /// <see cref="SweepInterval"/>.</summary>
    /// <remarks>**Every refusal counts, not only the ones that are about this run.** A replay is refused
    /// whenever admission does not start it: a <c>Single</c> or <c>Restart</c> permit held by a peer, a
    /// cluster whose run-slot gate is full, a fire already collapsed as a duplicate. A cluster that stays
    /// saturated for longer than the bound therefore ends the runs it could not fit, and a host that would
    /// rather have them wait sets this to null.
    ///
    /// **A host that has turned the run heartbeat off is agreeing to more than that.** There the row is
    /// written only between blocks, a sweep can claim a run a live peer is executing, and the peer's own
    /// permit is what refuses the replay — so past the bound this ends a run that is fine. See
    /// <see cref="AbandonedAfter"/>, which is what such a host sets to exceed its longest block.
    ///
    /// Null turns the bound off: a host whose admission can be
    /// refused for longer than any bound it would set — a maintenance window measured in days, say — would
    /// rather have its runs late than ended, and says so here.
    ///
    /// Measured from when the run started to when this sweep claimed it, and not from the first refusal,
    /// because the first refusal is not written down anywhere: a sweep holds no memory between passes and a
    /// replica that dies mid-bound would otherwise start the clock again. So a run whose replica died an hour
    /// into it is given up on its first refusal, and what the bound is really asking is how late a replay is
    /// still worth having rather than how long the refusing has gone on.
    ///
    /// No bound applies while <see cref="IWorkflowExecutionGate.IsExecutionSuspended"/>: safe mode refuses
    /// every replay for the whole boot and says nothing about any one run.</remarks>
    public TimeSpan? GiveUpOnRefusalAfter { get; set; } = TimeSpan.FromHours(1);
}
