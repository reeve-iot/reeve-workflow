using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunRecoverySweepTests
{
    [Fact]
    public async Task A_sweep_that_claims_nothing_asks_the_engine_nothing()
    {
        var world = new World();

        var claimed = await world.Sweep().SweepAsync(CancellationToken.None);

        claimed.Should().Be(0);
        world.Engine.Resumed.Should().BeEmpty();
        world.RunState.Ended.Should().BeEmpty();
    }

    [Fact]
    public async Task A_claimed_run_is_dispatched_again_and_the_row_it_was_claimed_from_is_ended()
    {
        var world = new World { Idempotent = true };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.Resumed.Should().ContainSingle().Which.Should().Be(row.ExecutionId);
        world.RunState.FencedBy.Should().Equal(["replica-1"], "the row is ended only while the sweep's claim holds it");
        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Completed, WorkflowRunRecoverySweep.ReplayedReason),
                "the re-dispatch opened a row of its own, and this one would age back into every later sweep; "
                + "an operator reading Completed with no reason would think this row is the run that finished");
    }

    [Fact]
    public async Task A_run_that_died_inside_a_block_that_cannot_be_replayed_is_failed_and_says_which_failure()
    {
        var world = new World { Idempotent = false };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.Resumed.Should().BeEmpty("replaying it would take the block's effect twice");
        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Failed, WorkflowRunRecoverySweep.NotIdempotentReason));
    }

    [Fact]
    public async Task A_sleeping_run_is_asked_the_same_question_as_a_running_one()
    {
        var world = new World { Idempotent = false };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Sleeping);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.Resumed.Should().BeEmpty(
            "it parked between blocks, but the blocks before the sleep have already had their effect and a "
            + "recovered run repeats them");
        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Failed, WorkflowRunRecoverySweep.NotIdempotentReason));
    }

    [Fact]
    public async Task A_run_that_died_before_entering_any_block_is_replayed_rather_than_failed()
    {
        var world = new World { Idempotent = true };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running, blockPath: string.Empty);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.Resumed.Should().ContainSingle().Which.Should().Be(row.ExecutionId,
            "nothing ran, so nothing can repeat — and the question is about the workflow, not about a path "
            + "the row never had time to write");
    }

    [Fact]
    public async Task A_resume_that_throws_ends_the_row_rather_than_leaving_it_to_age_back_in()
    {
        var world = new World { Idempotent = true, ResumeThrows = true };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Failed, WorkflowRunRecoverySweep.ResumeFailedReason));
    }

    [Fact]
    public async Task A_replay_that_says_it_stopped_leaves_the_row_it_replays_to_come_back()
    {
        using var stopping = new CancellationTokenSource();
        var world = new World { Idempotent = true, ReplayAnswers = WorkflowReplayOutcome.Stopped, WhileReplaying = stopping.Cancel };
        world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(stopping.Token);

        world.RunState.Ended.Should().BeEmpty("the row keeps this replica's name and ages back into a later sweep");
    }

    [Fact]
    public async Task A_replay_that_finished_as_the_sweep_stopped_ends_the_row_it_replays_all_the_same()
    {
        using var stopping = new CancellationTokenSource();
        var world = new World { Idempotent = true, ReplayAnswers = WorkflowReplayOutcome.Finished, WhileReplaying = stopping.Cancel };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(stopping.Token);

        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Completed, WorkflowRunRecoverySweep.ReplayedReason),
                "ended on no token of the stop's, which would cancel the ending itself");
    }

    [Fact]
    public async Task A_replay_that_says_it_failed_ends_the_row_it_replays_failed()
    {
        var world = new World { Idempotent = true, ReplayAnswers = WorkflowReplayOutcome.Failed };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Failed, WorkflowRunRecoverySweep.ResumeFailedReason));
    }

    [Fact]
    public async Task A_replay_that_throws_something_other_than_the_sweeps_own_stop_ends_the_row_failed_even_during_a_stop()
    {
        using var stopping = new CancellationTokenSource();
        var world = new World
        {
            Idempotent = true,
            ResumeThrowsThis = () =>
            {
                stopping.Cancel();
                return new InvalidOperationException("the journal could not be written");
            }
        };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(stopping.Token);

        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Failed, WorkflowRunRecoverySweep.ResumeFailedReason));
    }

    [Fact]
    public async Task A_sweep_cancelled_mid_run_leaves_the_row_where_it_is()
    {
        var world = new World { Idempotent = true, ResumeThrows = true };
        world.Recovery.Holding(WorkflowRunStateStatus.Running);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await world.Sweep().SweepAsync(cancelled.Token);

        world.RunState.Ended.Should().BeEmpty(
            "the row keeps this replica's name and comes back to the next sweep, which is not a failed run");
    }

    [Fact]
    public async Task The_claim_is_made_in_this_replicas_name_and_on_this_hosts_cutoff()
    {
        var world = new World();
        world.Options.AbandonedAfter = TimeSpan.FromMinutes(2);
        world.Options.BatchSize = 7;

        await world.Sweep().SweepAsync(CancellationToken.None);

        var asked = world.Recovery.Asked.Should().ContainSingle().Subject;
        asked.AbandonedAfter.Should().Be(TimeSpan.FromMinutes(2));
        asked.BatchSize.Should().Be(7);
        asked.ClaimedByReplicaId.Should().Be("replica-1");
    }

    [Fact]
    public async Task Every_row_in_a_batch_is_worked_through()
    {
        var world = new World { Idempotent = true };
        var first = world.Recovery.Holding(WorkflowRunStateStatus.Running);
        var second = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        var claimed = await world.Sweep().SweepAsync(CancellationToken.None);

        claimed.Should().Be(2);
        world.Engine.Resumed.Should().BeEquivalentTo([first.ExecutionId, second.ExecutionId]);
    }

    [Fact]
    public async Task A_recovered_run_that_waits_does_not_hold_up_the_rest_of_the_batch()
    {
        var bothIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var world = new World
        {
            Idempotent = true,
            ParkRow = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                    bothIn.TrySetResult();
                await bothIn.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        };
        var first = world.Recovery.Holding(WorkflowRunStateStatus.Running);
        var second = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Ended.Should().BeEquivalentTo(
            [
                (first.ExecutionId, WorkflowRunStateStatus.Completed, WorkflowRunRecoverySweep.ReplayedReason),
                (second.ExecutionId, WorkflowRunStateStatus.Completed, WorkflowRunRecoverySweep.ReplayedReason)
            ],
            "the first run waits for the second to start; in series it would wait out its deadline and fail");
    }

    [Fact]
    public async Task A_sweep_carries_on_no_more_runs_at_once_than_its_bound()
    {
        var twoIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var inFlight = 0;
        var most = 0;
        var world = new World
        {
            Idempotent = true,
            ParkRow = async (_, cancellationToken) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref most, now);
                if (Interlocked.Increment(ref entered) == 2)
                    twoIn.TrySetResult();
                await twoIn.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                Interlocked.Decrement(ref inFlight);
            }
        };
        world.Options.MaxConcurrentResumes = 2;
        for (var i = 0; i < 3; i++)
            world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.Resumed.Should().HaveCount(3);
        most.Should().Be(2, "the third run waits for a place while two are still in flight");
    }

    [Fact]
    public async Task A_sweep_claims_only_into_free_places_and_leaves_its_runs_carrying_on()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var world = new World
        {
            Idempotent = true,
            ParkRow = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                    bothIn.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        };
        world.Options.MaxConcurrentResumes = 2;
        world.Recovery.TakesWhatItHandsOut = true;
        for (var i = 0; i < 3; i++)
            world.Recovery.Holding(WorkflowRunStateStatus.Running);
        var sweep = world.Sweep();

        (await sweep.ClaimOnceAsync(CancellationToken.None)).Should().Be(2);
        await bothIn.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await sweep.ClaimOnceAsync(CancellationToken.None)).Should().Be(0);
        world.Recovery.Asked.Should().ContainSingle("a sweep with no free place does not claim rows it cannot start")
            .Which.BatchSize.Should().Be(2);

        release.SetResult();
        await sweep.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(10));
        (await sweep.ClaimOnceAsync(CancellationToken.None)).Should().Be(1);
        await sweep.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(10));

        world.Engine.Resumed.Should().HaveCount(3);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen)
        {
            var was = Interlocked.CompareExchange(ref target, value, seen);
            if (was == seen)
                return;
            seen = was;
        }
    }

    [Fact]
    public async Task A_resume_admission_refused_leaves_the_row_rather_than_calling_it_finished()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false };
        world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Ended.Should().BeEmpty(
            "nothing ran — a peer still holds the workflow's one permit, or this host is in safe mode — and "
            + "ending the row would say a run that never started is done, losing it");
    }

    [Fact]
    public async Task A_replay_admission_is_still_refusing_past_the_bound_and_the_run_is_given_up()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false };
        var row = world.Recovery.Holding(
            WorkflowRunStateStatus.Running, startedAtUtc: DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            claimedAtUtc: DateTimeOffset.UtcNow);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Failed, WorkflowRunRecoverySweep.NotAdmittedReason),
                "an hour of refusals is an answer, and a run played in full two hours after its replica died "
                + "is worse than one that says it was given up");
        world.Metrics.Endings.Should().Equal([WorkflowRecoveryEndingReasons.NotAdmitted]);
        world.Audit.OfKind(WorkflowAuditEventKinds.EndedByRecovery).Should().ContainSingle();
    }

    [Fact]
    public async Task A_refused_replay_is_kept_however_old_while_this_host_is_in_safe_mode()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false, ExecutionSuspended = true };
        world.Recovery.Holding(
            WorkflowRunStateStatus.Running, startedAtUtc: DateTimeOffset.UtcNow - TimeSpan.FromDays(2));

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.ReplaysAsked.Should().ContainSingle("the row was claimed and the replay was asked for");
        world.RunState.Ended.Should().BeEmpty(
            "the refusal is this host's and not this run's, and the run is still owed once safe mode is off");
    }

    [Fact]
    public async Task A_replay_refused_inside_the_bound_is_kept_for_a_later_sweep()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false };
        world.Recovery.Holding(
            WorkflowRunStateStatus.Running, startedAtUtc: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1));

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.ReplaysAsked.Should().ContainSingle("the row was claimed and the replay was asked for");
        world.RunState.Ended.Should().BeEmpty(
            "a run a minute old is inside any bound a host would set, and ending it would lose a run whose "
            + "replay is worth having the moment the permit frees");
    }

    [Fact]
    public async Task The_age_is_measured_between_the_two_stamps_the_store_returned()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false };
        var databaseNow = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        world.Recovery.Holding(
            WorkflowRunStateStatus.Running, startedAtUtc: databaseNow - TimeSpan.FromMinutes(1),
            claimedAtUtc: databaseNow);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.ReplaysAsked.Should().ContainSingle("the row was claimed and the replay was asked for");
        world.RunState.Ended.Should().BeEmpty(
            "the run is a minute old; a sweep reading its own clock for the near end would call it two hours "
            + "old and end it, and one NTP step would do that to every run the replica claims");
    }

    [Fact]
    public async Task A_sweep_given_no_gate_applies_the_bound_because_it_cannot_see_safe_mode()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false, Gate = null };
        var row = world.Recovery.Holding(
            WorkflowRunStateStatus.Running, startedAtUtc: DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            claimedAtUtc: DateTimeOffset.UtcNow);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Ended.Should().ContainSingle().Which.Item3.Should()
            .Be(WorkflowRunRecoverySweep.NotAdmittedReason);
        _ = row;
    }

    [Fact]
    public async Task A_host_that_unset_the_bound_keeps_a_refused_run_however_old_it_is()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false };
        world.Options.GiveUpOnRefusalAfter = null;
        world.Recovery.Holding(
            WorkflowRunStateStatus.Running, startedAtUtc: DateTimeOffset.UtcNow - TimeSpan.FromDays(3));

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.ReplaysAsked.Should().ContainSingle("the row was claimed and the replay was asked for");
        world.RunState.Ended.Should().BeEmpty(
            "null is a host saying it would rather have the run late than ended, which is a decision it is "
            + "allowed to make");
    }

    [Fact]
    public async Task A_row_whose_store_never_said_when_it_started_is_never_given_up()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false };
        world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.ReplaysAsked.Should().ContainSingle("the row was claimed and the replay was asked for");
        world.RunState.Ended.Should().BeEmpty(
            "an unknown start is not a long one: the sweep refuses to judge the bound rather than reading the "
            + "year one as a run that has been refused for two thousand years");
    }

    [Fact]
    public async Task A_run_still_in_flight_keeps_its_claim_fresh_in_this_replicas_name()
    {
        var world = new World { Idempotent = true, ParksUntilRenewed = true };
        world.Options.AbandonedAfter = TimeSpan.FromSeconds(1);
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Recovery.Renewed.Should().Contain(row.ExecutionId,
            "the sweep dispatches inline, so a run that outlives the window would age out from under the "
            + "replica that is still running it and be claimed by a peer");
        world.Recovery.RenewedBy.Should().AllBe("replica-1", "a renewal must not take a row back from its owner");
    }

    [Fact]
    public async Task A_claim_that_is_no_longer_ours_is_not_renewed_a_second_time()
    {
        var world = new World { Idempotent = true, ParksUntilRenewed = true, ParkedAfterRenewal = TimeSpan.FromSeconds(1.5) };
        world.Options.AbandonedAfter = TimeSpan.FromSeconds(1);
        world.Recovery.StillOurs = false;
        world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Recovery.Renewed.Should().ContainSingle(
            "the row answered that it belongs to someone else, and writing to it again would take it back "
            + "from them — the run parked long enough for a second renewal to have landed");
    }

    [Fact]
    public async Task A_run_a_block_of_another_run_started_is_ended_rather_than_replayed()
    {
        var world = new World { Idempotent = true };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running, parentExecutionId: Guid.NewGuid());

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.Resumed.Should().BeEmpty(
            "re-dispatching it as a root loses the inputs its parent wired in and hands its outputs to "
            + "nobody — the parent's own replay is what runs it again");
        world.RunState.Ended.Should().ContainSingle().Which.Should()
            .Be((row.ExecutionId, WorkflowRunStateStatus.Failed,
                WorkflowRunRecoverySweep.DispatchedByAnotherRunReason),
                "and it must still be ended: left Running it blocks its workflow's delete for ever and sits "
                + "at the head of every later sweep");
    }

    [Fact]
    public async Task Every_row_the_sweep_still_holds_is_renewed_while_it_works_through_them()
    {
        var world = new World { Idempotent = true, ParksUntilRenewed = true };
        world.Options.AbandonedAfter = TimeSpan.FromSeconds(1);
        world.Options.MaxConcurrentResumes = 1;
        var first = world.Recovery.Holding(WorkflowRunStateStatus.Running);
        var second = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Recovery.Renewed.Should().Contain(second.ExecutionId,
            "a row waiting for a place sits at the claim's timestamp — "
            + "unrenewed they age out under a peer that then runs them while this replica still will too");
        world.Recovery.Renewed.Should().Contain(first.ExecutionId);
    }

    [Fact]
    public async Task A_row_a_peer_takes_back_mid_dispatch_is_neither_finished_nor_kept_running()
    {
        var world = new World { Idempotent = true, ParksUntilCancelled = true };
        world.Options.AbandonedAfter = TimeSpan.FromSeconds(1);
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);
        world.Recovery.Taken.Add(row.ExecutionId);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.WasCancelled.Should().BeTrue(
            "a peer is running this row now, and two replicas executing one run is what the claim exists to stop");
        world.RunState.Ended.Should().BeEmpty(
            "the row is theirs to end — writing it here would terminalise a run somebody else is still executing");
    }

    [Theory]
    [InlineData(WorkflowReplayOutcome.Finished)]
    [InlineData(WorkflowReplayOutcome.Failed)]
    public async Task A_replayed_row_a_peer_claimed_without_the_beat_seeing_it_is_not_ended_here(WorkflowReplayOutcome answer)
    {
        World world = null!;
        WorkflowRunStateRow row = null!;
        world = new World
        {
            Idempotent = true,
            ReplayAnswers = answer,
            WhileReplaying = () => world.RunState.TakenByPeer.Add(row.ExecutionId)
        };
        row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Ended.Should().BeEmpty("the peer's claim holds the row, and the peer ends it");
    }

    [Fact]
    public async Task A_row_a_peer_claimed_before_it_was_judged_unsafe_to_replay_is_not_failed_here()
    {
        var world = new World { Idempotent = false };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);
        world.RunState.TakenByPeer.Add(row.ExecutionId);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Ended.Should().BeEmpty();
        world.RunState.FencedBy.Should().Equal(["replica-1"]);
    }

    [Fact]
    public async Task Every_row_a_sweep_claims_is_recorded_before_anything_is_decided_about_it()
    {
        var world = new World { Idempotent = true };
        var first = world.Recovery.Holding(WorkflowRunStateStatus.Running);
        var second = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Audit.OfKind(WorkflowAuditEventKinds.ClaimedByRecovery)
            .Select(e => RecordingAudit.Field(e, "executionId"))
            .Should().BeEquivalentTo([first.ExecutionId, second.ExecutionId]);
        world.Metrics.Claims.Should().Equal([2]);
    }

    [Fact]
    public async Task A_claim_is_recorded_with_the_rows_it_claimed_already_beating()
    {
        var world = new World { Idempotent = true };
        world.Options.AbandonedAfter = TimeSpan.FromSeconds(1);
        world.Audit.WaitFor = world.Recovery.FirstRenewal.Task;
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        world.Recovery.Renewed.Should().Contain(row.ExecutionId);
        world.Audit.OfKind(WorkflowAuditEventKinds.ClaimedByRecovery).Should().ContainSingle();
    }

    [Fact]
    public async Task A_sweep_that_claims_nothing_records_the_nothing_it_claimed()
    {
        var world = new World();

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Metrics.Claims.Should().Equal([0], "zero claimed is an answer, and no metric at all is not");
        world.Audit.Written.Should().BeEmpty("there was no row to write about");
    }

    [Fact]
    public async Task A_sweep_stopped_before_it_dispatched_still_recorded_what_it_claimed()
    {
        var world = new World { Idempotent = true, ResumeThrows = true };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await world.Sweep().SweepAsync(cancelled.Token);

        world.Audit.OfKind(WorkflowAuditEventKinds.ClaimedByRecovery).Should().ContainSingle()
            .Which.SubjectId.Should().Be(row.ExecutionId);
    }

    [Fact]
    public async Task A_sweep_stopped_while_it_replayed_still_records_the_ending_it_wrote()
    {
        using var stopping = new CancellationTokenSource();
        var world = new World
        {
            Idempotent = true, ReplayAnswers = WorkflowReplayOutcome.Finished, WhileReplaying = stopping.Cancel
        };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(stopping.Token);

        var ended = world.Audit.OfKind(WorkflowAuditEventKinds.EndedByRecovery).Should().ContainSingle().Subject;
        ended.SubjectId.Should().Be(row.ExecutionId);
        RecordingAudit.Field(ended, "reason").Should().Be(WorkflowRunRecoverySweep.ReplayedReason);
    }

    [Fact]
    public async Task A_sweep_whose_meter_throws_still_recovers_its_rows()
    {
        var world = new World { Idempotent = true };
        world.Metrics.Throws = true;
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Engine.Resumed.Should().ContainSingle().Which.Should().Be(row.ExecutionId);
        world.Metrics.Claims.Should().Equal([1], "the meter was asked; it is the asking that threw");
    }

    [Fact]
    public async Task A_claim_carries_both_ids_and_says_which_one_it_is_the_subject_of()
    {
        var world = new World { Idempotent = true };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        var claimed = world.Audit.OfKind(WorkflowAuditEventKinds.ClaimedByRecovery).Should().ContainSingle().Subject;
        claimed.SubjectId.Should().Be(row.ExecutionId);
        RecordingAudit.Field(claimed, "executionId").Should().Be(row.ExecutionId);
        RecordingAudit.Field(claimed, "journalExecutionId").Should().BeNull("this row has no checkpoint");
        RecordingAudit.Field(claimed, "subjectIs").Should().Be("executionId");
        RecordingAudit.Field(claimed, "workflowId").Should().Be(row.WorkflowId);
    }

    [Theory]
    [InlineData(false, null, WorkflowRunRecoverySweep.NotIdempotentReason)]
    [InlineData(true, WorkflowReplayOutcome.Finished, WorkflowRunRecoverySweep.ReplayedReason)]
    [InlineData(true, WorkflowReplayOutcome.Failed, WorkflowRunRecoverySweep.ResumeFailedReason)]
    public async Task An_ending_says_which_rule_ended_the_run(
        bool idempotent, WorkflowReplayOutcome? replay, string reason)
    {
        var world = new World { Idempotent = idempotent, ReplayAnswers = replay };
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        var ended = world.Audit.OfKind(WorkflowAuditEventKinds.EndedByRecovery).Should().ContainSingle().Subject;
        ended.SubjectId.Should().Be(row.ExecutionId);
        RecordingAudit.Field(ended, "reason").Should().Be(reason);
        world.Metrics.Endings.Should().Equal([reason]);
    }

    [Fact]
    public async Task A_run_another_run_started_is_ended_naming_the_run_that_started_it()
    {
        var world = new World { Idempotent = true };
        var parent = Guid.NewGuid();
        var row = world.Recovery.Holding(WorkflowRunStateStatus.Running, parentExecutionId: parent);

        await world.Sweep().SweepAsync(CancellationToken.None);

        var ended = world.Audit.OfKind(WorkflowAuditEventKinds.EndedByRecovery).Should().ContainSingle().Subject;
        RecordingAudit.Field(ended, "reason").Should()
            .Be(WorkflowRunRecoverySweep.DispatchedByAnotherRunReason);
        RecordingAudit.Field(ended, "parentExecutionId").Should().Be(parent);
        world.Metrics.Endings.Should().Equal([WorkflowRecoveryEndingReasons.DispatchedByAnotherRun]);
    }

    [Fact]
    public async Task A_run_that_was_not_ended_is_not_counted_as_an_ending()
    {
        var world = new World { Idempotent = true, ResumeAdmitted = false };
        world.Recovery.Holding(WorkflowRunStateStatus.Running);

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Audit.OfKind(WorkflowAuditEventKinds.EndedByRecovery).Should().BeEmpty();
        world.Metrics.Endings.Should().BeEmpty("it stays claimed and returns to a later sweep");
    }

    [Fact]
    public async Task An_ending_a_peer_took_back_before_it_landed_is_not_recorded()
    {
        var world = new World { Idempotent = false };
        world.Recovery.Holding(WorkflowRunStateStatus.Running);
        world.RunState.RefuseTheEnding = true;

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.Audit.OfKind(WorkflowAuditEventKinds.EndedByRecovery).Should().BeEmpty();
        world.Metrics.Endings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sweep_whose_host_is_stopping_claims_nothing()
    {
        var world = new World { Idempotent = true };
        world.Recovery.Holding(WorkflowRunStateStatus.Running);
        await world.HostStop.CancelAsync();

        var claimed = await world.Sweep().SweepAsync(CancellationToken.None);

        claimed.Should().Be(0);
        world.Recovery.Asked.Should().BeEmpty("a stopping replica would claim the very rows it just handed over");
    }

    [Fact]
    public async Task Rows_a_sweep_holds_when_its_host_stops_are_released_rather_than_kept_in_its_name()
    {
        var world = new World { Idempotent = true, StopsTheHostInsideTheRun = true };
        var first = world.OursRunning();
        var second = world.OursRunning();

        await world.Sweep().SweepAsync(CancellationToken.None);

        world.RunState.Released.Should().BeEquivalentTo([first.ExecutionId, second.ExecutionId],
            "both rows run side by side, and each goes back to the cluster at once when the host stops, whether its run had begun or not");
        world.RunState.Ended.Should().BeEmpty();
        world.Engine.Resumed.Should().BeEmpty();
    }

    private sealed class World
    {
        public bool Idempotent { get; init; }
        public bool ResumeThrows { get; init; }
        public Func<Exception>? ResumeThrowsThis { get; init; }
        public bool ResumeAdmitted { get; init; } = true;
        public WorkflowReplayOutcome? ReplayAnswers { get; init; }
        public Action? WhileReplaying { get; init; }

        public bool ParksUntilRenewed { get; init; }

        public bool ParksUntilCancelled { get; init; }

        public bool StopsTheHostInsideTheRun { get; init; }

        public CancellationTokenSource HostStop { get; } = new();

        public WorkflowRunStateRow OursRunning()
        {
            var row = Recovery.Holding(WorkflowRunStateStatus.Running);
            RunState.Rows[row.ExecutionId] = row with { OwnedByReplicaId = "replica-1" };
            return row;
        }

        public Func<Guid, CancellationToken, Task>? ParkRow { get; init; }

        public TimeSpan ParkedAfterRenewal { get; init; } = TimeSpan.Zero;

        public WorkflowRunRecoveryOptions Options { get; } = new();

        public bool ExecutionSuspended { get; init; }

        public IWorkflowExecutionGate? Gate { get; init; } = AlwaysRunningExecutionGate.Instance;
        public StubEngine Engine { get; private set; } = null!;
        public RecordingRunState RunState { get; } = new();
        public StubRecovery Recovery { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public RecordingMetrics Metrics { get; } = new();

        public WorkflowRunRecoverySweep Sweep()
        {
            Engine = new StubEngine
            {
                Idempotent = Idempotent,
                Throws = ResumeThrows,
                ThrowsThis = ResumeThrowsThis,
                Admitted = ResumeAdmitted,
                Answers = ReplayAnswers,
                WhileReplaying = WhileReplaying,
                Park = ParksUntilRenewed ? ParkAsync
                    : ParksUntilCancelled ? ParkUntilCancelledAsync
                    : StopsTheHostInsideTheRun ? StopTheHostAsync
                    : null,
                ParkRow = ParkRow
            };
            return new WorkflowRunRecoverySweep(
                Engine, RunState, Recovery, Options, "replica-1", NullLogger.Instance, null, Metrics, Audit,
                Gate is null ? null : new DelegateExecutionGate(() => ExecutionSuspended), HostStop.Token);
        }

        private async Task StopTheHostAsync(CancellationToken cancellationToken)
        {
            await HostStop.CancelAsync();
            await ParkUntilCancelledAsync(cancellationToken);
        }

        private async Task ParkAsync(CancellationToken cancellationToken)
        {
            var stopped = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await Task.WhenAny(Recovery.FirstRenewal.Task, stopped, Task.Delay(TimeSpan.FromSeconds(20)));
            if (ParkedAfterRenewal > TimeSpan.Zero && !cancellationToken.IsCancellationRequested)
                await Task.WhenAny(Task.Delay(ParkedAfterRenewal), stopped);
        }

        private static Task ParkUntilCancelledAsync(CancellationToken cancellationToken) =>
            Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken), Task.Delay(TimeSpan.FromSeconds(20)));
    }

    private sealed class RecordingAudit : IWorkflowAuditSink
    {
        public List<WorkflowAuditEvent> Written { get; } = [];

        public Task? WaitFor { get; set; }

        public async Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (WaitFor is { } slow)
                await slow.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);

            lock (Written) Written.Add(auditEvent);
        }

        public IEnumerable<WorkflowAuditEvent> OfKind(string kind) => Written.Where(e => e.Kind == kind);

        public static object? Field(WorkflowAuditEvent auditEvent, string name) =>
            auditEvent.Data is IReadOnlyDictionary<string, object?> data && data.TryGetValue(name, out var value)
                ? value
                : null;
    }

    private sealed class RecordingMetrics : IWorkflowMetrics
    {
        public List<int> Claims { get; } = [];
        public List<string> Endings { get; } = [];

        public bool Throws { get; set; }

        public void RecordRecoveryClaim(int rows)
        {
            Claims.Add(rows);
            if (Throws) throw new InvalidOperationException("this meter is not answering just now");
        }

        public void RecordRecoveryEnding(string reason)
        {
            lock (Endings) Endings.Add(reason);
            if (Throws) throw new InvalidOperationException("this meter is not answering just now");
        }

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class StubRecovery : IWorkflowRunRecoveryStore
    {
        private readonly List<WorkflowRunStateRow> rows = [];

        public List<WorkflowRunRecoveryClaim> Asked { get; } = [];

        public WorkflowRunStateRow Holding(
            WorkflowRunStateStatus status, string blockPath = "work", Guid? parentExecutionId = null,
            DateTimeOffset startedAtUtc = default, DateTimeOffset claimedAtUtc = default)
        {
            var row = new WorkflowRunStateRow
            {
                ExecutionId = Guid.NewGuid(),
                WorkflowId = Guid.NewGuid(),
                Status = status,
                CurrentBlockPath = blockPath,
                ParentExecutionId = parentExecutionId,
                StartedAtUtc = startedAtUtc,
                LastTransitionUtc = claimedAtUtc == default ? DateTimeOffset.UtcNow : claimedAtUtc
            };
            rows.Add(row);
            return row;
        }

        public bool TakesWhatItHandsOut { get; set; }

        public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
            WorkflowRunRecoveryClaim request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            if (!TakesWhatItHandsOut)
                return Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>(rows);

            var taken = rows.Take(request.BatchSize).ToList();
            rows.RemoveRange(0, taken.Count);
            return Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>(taken);
        }

        public List<Guid> Renewed { get; } = [];

        public TaskCompletionSource FirstRenewal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string?> RenewedBy { get; } = [];

        public bool StillOurs { get; set; } = true;

        public HashSet<Guid> Taken { get; } = [];

        public Task<bool> RenewClaimAsync(
            Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken)
        {
            lock (Renewed) Renewed.Add(executionId);
            lock (RenewedBy) RenewedBy.Add(claimedByReplicaId);
            FirstRenewal.TrySetResult();
            return Task.FromResult(StillOurs && !Taken.Contains(executionId));
        }
    }

    private sealed class StubEngine : IWorkflowEngine
    {
        public bool Idempotent { get; init; }
        public bool Throws { get; init; }
        public Func<Exception>? ThrowsThis { get; init; }
        public bool Admitted { get; init; } = true;
        public WorkflowReplayOutcome? Answers { get; init; }
        public Action? WhileReplaying { get; init; }
        public Func<CancellationToken, Task>? Park { get; init; }
        public Func<Guid, CancellationToken, Task>? ParkRow { get; init; }
        public List<Guid> Resumed { get; } = [];
        public List<string> AskedAbout { get; } = [];

        public List<Guid> ReplaysAsked { get; } = [];

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            Guid workflowId, WorkflowFire? fire, IReadOnlyDictionary<string, object?>? inputParameters,
            Guid? parentExecutionId = null, int? iterationIndex = null, string? callStackJson = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("recovery resumes; it does not dispatch directly");

        public Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default) =>
            TryResumeAsync(executionId, workflowId, cancellationToken);

        public bool WasCancelled { get; private set; }

        public async Task<bool> TryResumeAsync(
            Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throws) throw new InvalidOperationException("the workflow could not be re-entered");
            if (ThrowsThis is not null) throw ThrowsThis();
            if (Park is not null) await Park(cancellationToken).ConfigureAwait(false);
            if (ParkRow is not null) await ParkRow(executionId, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!Admitted) return false;
            lock (Resumed) Resumed.Add(executionId);
            return true;
        }

        public async Task<WorkflowReplayOutcome> ReplayAbandonedRunAsync(
            Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
        {
            lock (ReplaysAsked) ReplaysAsked.Add(executionId);
            if (Answers is not { } answer)
                return await TryResumeAsync(executionId, workflowId, cancellationToken).ConfigureAwait(false)
                    ? WorkflowReplayOutcome.Finished
                    : WorkflowReplayOutcome.NotAdmitted;

            lock (Resumed) Resumed.Add(executionId);
            WhileReplaying?.Invoke();
            return answer;
        }

        public Task<bool> IsBlockIdempotentAsync(
            Guid workflowId, string blockPath, Guid? versionId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("recovery asks about the whole run, not about one block");

        public Task<bool> IsRunReplayableAsync(
            Guid workflowId, string currentBlockPath, Guid? versionId = null,
            CancellationToken cancellationToken = default)
        {
            lock (AskedAbout) AskedAbout.Add(currentBlockPath);
            return Task.FromResult(Idempotent);
        }
    }

    private sealed class RecordingRunState : IWorkflowRunStateStore
    {
        public List<(Guid ExecutionId, WorkflowRunStateStatus Status, string? Reason)> Ended { get; } = [];

        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            Task.FromResult(Guid.NewGuid());

        public System.Collections.Concurrent.ConcurrentDictionary<Guid, WorkflowRunStateRow> Rows { get; } = new();

        public List<Guid> Released { get; } = [];

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.GetValueOrDefault(executionId));

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken)
        {
            if (request is { OwnedByReplicaId: null, ExpectedOwnedByReplicaId: "replica-1" }
                && Rows.TryGetValue(request.ExecutionId, out var row) && row.OwnedByReplicaId == "replica-1")
            {
                Rows[request.ExecutionId] = row with { OwnedByReplicaId = null };
                lock (Released) Released.Add(request.ExecutionId);
            }

            return Task.FromResult(true);
        }

        public HashSet<Guid> TakenByPeer { get; } = [];

        public bool RefuseTheEnding { get; set; }

        public List<string?> FencedBy { get; } = [];

        public Task<bool> CompleteAsync(
            Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RefuseTheEnding)
                return Task.FromResult(false);
            lock (Ended) Ended.Add((executionId, terminalStatus, failureReason));
            return Task.FromResult(true);
        }

        public Task<bool> CompleteIfOwnedAsync(
            Guid executionId, string? ownedByReplicaId, WorkflowRunStateStatus terminalStatus, string? failureReason,
            CancellationToken cancellationToken)
        {
            lock (FencedBy) FencedBy.Add(ownedByReplicaId);
            return ownedByReplicaId == "replica-1" && !TakenByPeer.Contains(executionId)
                ? CompleteAsync(executionId, terminalStatus, failureReason, cancellationToken)
                : Task.FromResult(false);
        }
    }
}
