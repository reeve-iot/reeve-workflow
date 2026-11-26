using System.Collections.Concurrent;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunHeartbeatTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task A_block_longer_than_the_beat_keeps_its_row_claimed_while_it_runs()
    {
        var world = new RunLifecycleWorld();
        using var engine = world.Engine(heartbeat: Beat);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        await world.Recovery.FirstRenewal.Task.WaitAsync(Patience);
        world.Release.SetResult();
        await run.WaitAsync(Patience);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_run_whose_row_a_peer_took_mid_block_stops_inside_that_block()
    {
        var world = new RunLifecycleWorld();
        using var engine = world.Engine(heartbeat: Beat);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        var rowId = world.RunState.LastBegun!.Value;
        world.RunState.TakeOver(rowId, "replica-2");

        var thrown = await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.ReclaimedByPeer);
        world.Release.Task.IsCompleted.Should().BeFalse("the block was stopped, not allowed to finish");
        world.RunState.Row(rowId).OwnedByReplicaId.Should().Be("replica-2");
        world.RunState.Completions.Should().BeEmpty("the row is the peer's to end");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Running,
            "the entry is the peer's now: it carries the run on under it, or closes it");
    }

    [Fact]
    public async Task A_run_that_finished_stops_beating()
    {
        var world = new RunLifecycleWorld();
        using var engine = world.Engine(heartbeat: Beat);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        await world.Recovery.FirstRenewal.Task.WaitAsync(Patience);
        world.Release.SetResult();
        await run.WaitAsync(Patience);
        var afterTheRun = Volatile.Read(ref world.Recovery.Renewals);
        await Task.Delay(Beat * 10);

        Volatile.Read(ref world.Recovery.Renewals).Should().Be(afterTheRun, "a heartbeat must not outlive its run");
    }

    [Fact]
    public async Task A_run_that_parked_stops_beating_although_its_parked_row_is_still_in_its_name()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        using var engine = world.Engine(heartbeat: Beat);

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);
        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        var afterParking = Volatile.Read(ref world.Recovery.Renewals);
        await Task.Delay(Beat * 10);

        row.Status.Should().Be(WorkflowRunStateStatus.Suspended);
        row.OwnedByReplicaId.Should().Be(RunLifecycleWorld.ReplicaId);
        Volatile.Read(ref world.Recovery.Renewals).Should().Be(afterParking, "a heartbeat must not outlive its run");
    }

    [Fact]
    public async Task An_engine_told_no_interval_renews_nothing_even_with_somewhere_to_renew()
    {
        var world = new RunLifecycleWorld();
        using var engine = world.Engine(heartbeat: null, renewals: world.Recovery);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        await Task.Delay(Beat * 10);
        world.Release.SetResult();
        await run.WaitAsync(Patience);

        world.Recovery.Renewals.Should().Be(0);
    }

    [Fact]
    public async Task A_resumed_segment_whose_row_a_peer_took_mid_block_stops_too()
    {
        var world = new RunLifecycleWorld();
        var parked = world.Parked();

        var resume = world.Resumer(heartbeat: Beat).ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await world.Entered.Task.WaitAsync(Patience);
        world.RunState.TakeOver(world.ParkedStateId!.Value, "replica-2");

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.Release.Task.IsCompleted.Should().BeFalse();
        world.RunState.Completions.Should().BeEmpty();
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Running,
            "the entry is the peer's now: it carries the run on under it, or closes it");
    }

    [Fact]
    public async Task A_run_asleep_whose_row_a_peer_took_stops_sleeping_and_leaves_the_row_to_the_peer()
    {
        var world = new RunLifecycleWorld();
        using var engine = world.Engine(durableSleepAtHold: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        var rowId = await AsleepAsync(world);
        world.RunState.TakeOver(rowId, "replica-2");

        var thrown = await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        thrown.Which.Loss.Should().Be(WorkflowRunOwnershipLoss.ReclaimedByPeer);
        world.RunState.Row(rowId).OwnedByReplicaId.Should().Be("replica-2");
        world.RunState.Row(rowId).Status.Should().Be(WorkflowRunStateStatus.Sleeping, "the peer's row, as the peer left it");
        world.RunState.Completions.Should().BeEmpty("the row is the peer's to end");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Running,
            "the entry is the peer's now: it carries the run on under it, or closes it");
    }

    private static async Task<Guid> AsleepAsync(RunLifecycleWorld world)
    {
        using var patience = new CancellationTokenSource(Patience);
        while (world.RunState.LastBegun is not { } id
               || world.RunState.Row(id).Status != WorkflowRunStateStatus.Sleeping)
            await Task.Delay(5, patience.Token);
        return world.RunState.LastBegun!.Value;
    }

    [Fact]
    public async Task A_single_run_whose_cluster_lock_lapsed_and_was_taken_by_a_peer_stops_and_is_handed_over()
    {
        var world = new RunLifecycleWorld();
        var locks = new LapsingLocks { PeerTakesItAfterItLapses = true };
        using var engine = world.Engine(heartbeat: Beat, locks: locks, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        locks.Lapse();

        var thrown = await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowRunBudgetLostException>();
        thrown.Which.WorkflowId.Should().Be(world.WorkflowId);
        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().BeNull("a peer's sweep replays it once the budget is free again");
        world.Journal.Logs.Values.Single().ErrorMessage.Should().StartWith(WorkflowRunHandover.LeaseLostReason);
    }

    [Fact]
    public async Task A_resumed_single_segment_whose_cluster_lock_lapsed_and_was_taken_by_a_peer_stops_and_is_handed_over()
    {
        var world = new RunLifecycleWorld();
        var locks = new LapsingLocks { PeerTakesItAfterItLapses = true };
        using var engine = world.Engine(locks: locks);
        var parked = world.Parked();

        var resume = world.Resumer(heartbeat: Beat, handOverOnStop: true, coordinators: engine.Coordinators)
            .ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await world.Entered.Task.WaitAsync(Patience);
        locks.Lapse();

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.Release.Task.IsCompleted.Should().BeFalse();
        var row = world.RunState.Row(world.ParkedStateId!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().BeNull("a peer's sweep replays it once the budget is free again");
        world.RunState.Completions.Should().BeEmpty();
        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(WorkflowExecutionStatus.Running, "the entry is not over: the sweep that takes the row carries the run on under it");
        log.ErrorMessage.Should().StartWith(WorkflowRunHandover.LeaseLostReason);
        locks.Released.Should().Be(1, "the lease is given back on the way out");
    }

    [Fact]
    public async Task A_single_run_whose_cluster_lock_lapsed_but_is_free_takes_it_again_and_carries_on()
    {
        var world = new RunLifecycleWorld();
        var locks = new LapsingLocks();
        using var engine = world.Engine(heartbeat: Beat, locks: locks, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        locks.Lapse();
        await locks.Retaken.Task.WaitAsync(Patience);
        world.Release.SetResult();
        await run.WaitAsync(Patience);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
        locks.Released.Should().BeGreaterThanOrEqualTo(2, "the lapsed lock and the one taken again are both given back");
    }

    [Fact]
    public async Task A_parallel_run_renews_its_cluster_slot_while_its_block_runs()
    {
        var world = Parallel();
        var slots = new LapsingSlots();
        using var engine = world.Engine(heartbeat: Beat, locks: new LapsingLocks(), slots: slots);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        await slots.FirstRenewal.Task.WaitAsync(Patience);
        world.Release.SetResult();
        await run.WaitAsync(Patience);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
        slots.Renewed.Should().OnlyContain(token => token == slots.Taken.Single(), "the slot renewed is the one taken");
    }

    [Fact]
    public async Task A_parallel_run_whose_slot_lapsed_while_the_budget_filled_elsewhere_stops_and_is_handed_over()
    {
        var world = Parallel();
        var slots = new LapsingSlots { BudgetFullWhenAskedAgain = true };
        using var engine = world.Engine(heartbeat: Beat, locks: new LapsingLocks(), slots: slots, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        slots.Lapse();

        var thrown = await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowRunBudgetLostException>();
        thrown.Which.Mode.Should().Be(nameof(WorkflowMode.Parallel));
        world.Release.Task.IsCompleted.Should().BeFalse();
        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().BeNull();
    }

    [Fact]
    public async Task A_parallel_run_whose_slot_lapsed_with_room_left_takes_a_place_again_and_carries_on()
    {
        var world = Parallel();
        var slots = new LapsingSlots();
        using var engine = world.Engine(heartbeat: Beat, locks: new LapsingLocks(), slots: slots, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        slots.Lapse();
        await slots.Retaken.Task.WaitAsync(Patience);
        world.Release.SetResult();
        await run.WaitAsync(Patience);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Completed);
        slots.Taken.Should().HaveCount(2).And.OnlyContain(token => token == slots.Taken[0],
            "the place is asked for again under the token the lease releases");
    }

    private static RunLifecycleWorld Parallel()
    {
        var world = new RunLifecycleWorld();
        world.Workflow.ConfigureConcurrency(WorkflowMode.Parallel, 2, 0, WorkflowOverflowBehavior.Drop, DateTimeOffset.UtcNow);
        return world;
    }

    private sealed class LapsingSlots : IWorkflowRunSlotGate
    {
        private volatile bool lapsed;

        public bool BudgetFullWhenAskedAgain { get; init; }

        public List<string> Taken { get; } = [];

        public ConcurrentQueue<string> Renewed { get; } = new();

        public TaskCompletionSource FirstRenewal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Retaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Lapse() => lapsed = true;

        public Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct)
        {
            lock (Taken)
            {
                Taken.Add(slotToken);
                if (Taken.Count == 1)
                    return Task.FromResult(true);
            }

            if (BudgetFullWhenAskedAgain)
                return Task.FromResult(false);
            lapsed = false;
            Retaken.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<bool> RenewAsync(Guid workflowId, string slotToken, CancellationToken ct)
        {
            Renewed.Enqueue(slotToken);
            FirstRenewal.TrySetResult();
            return Task.FromResult(!lapsed);
        }

        public Task ReleaseAsync(Guid workflowId, string slotToken) => Task.CompletedTask;
    }

    private sealed class LapsingLocks : IWorkflowRunLockProvider
    {
        private volatile Held? current;
        private int acquisitions;
        public int Released;

        public bool PeerTakesItAfterItLapses { get; init; }

        public TaskCompletionSource Retaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Lapse() => current!.Valid = false;

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref acquisitions) > 1)
            {
                if (PeerTakesItAfterItLapses)
                    return Task.FromResult<IWorkflowRunLock?>(null);
                Retaken.TrySetResult();
            }

            current = new Held(this);
            return Task.FromResult<IWorkflowRunLock?>(current);
        }

        private sealed class Held(LapsingLocks owner) : IWorkflowRunLock
        {
            public volatile bool Valid = true;

            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(Valid);

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Released);
                return ValueTask.CompletedTask;
            }
        }
    }
    [Fact]
    public async Task A_run_whose_row_a_peer_took_is_not_ended_nor_retried_although_its_block_turned_the_stop_into_a_failure()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""") { HoldWrapsItsCancellation = true };
        using var engine = world.Engine(heartbeat: Beat);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        var rowId = world.RunState.LastBegun!.Value;
        world.RunState.TakeOver(rowId, "replica-2");

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowRunOwnershipLostException>();
        world.HoldEntries.Should().Be(1, "a run a peer holds is not tried again here");
        world.RunState.Completions.Should().BeEmpty("the row is the peer's to end");
        world.RunState.Row(rowId).OwnedByReplicaId.Should().Be("replica-2");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Running,
            "the entry is the peer's now: it carries the run on under it, or closes it");
    }

    [Fact]
    public async Task A_failed_attempt_whose_row_a_peer_reclaimed_and_ended_is_not_tried_again()
    {
        RunLifecycleWorld world = null!;
        var first = 0;
        world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () =>
            {
                if (Interlocked.Exchange(ref first, 1) == 0)
                {
                    var row = world.RunState.LastBegun!.Value;
                    world.RunState.TakeOver(row, "replica-2");
                    world.RunState.CompleteAsync(row, WorkflowRunStateStatus.Completed,
                        WorkflowRunRecoverySweep.ReplayedReason, CancellationToken.None).GetAwaiter().GetResult();
                }
                return new InvalidOperationException("a transient failure");
            }
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        world.HoldEntries.Should().Be(1, "the peer replayed this run; a retry here runs it again");
    }

    [Fact]
    public async Task A_failed_attempt_whose_refused_ending_cannot_be_read_back_is_not_tried_again()
    {
        RunLifecycleWorld world = null!;
        var first = 0;
        world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () =>
            {
                if (Interlocked.Exchange(ref first, 1) == 0)
                {
                    world.RunState.TakeOver(world.RunState.LastBegun!.Value, "replica-2");
                    world.RunState.ReadsFail = true;
                }
                return new InvalidOperationException("a transient failure");
            }
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        world.HoldEntries.Should().Be(1, "replica-2 holds the row and runs the run");
    }

    [Fact]
    public async Task A_failed_attempt_whose_ending_write_blipped_once_is_still_tried_again()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""") { FailHoldTimes = 1 };
        world.Release.SetResult();
        world.RunState.NextEndingFails = true;
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_attempt_whose_ending_landed_though_its_write_reported_a_failure_is_still_tried_again()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""") { FailHoldTimes = 1 };
        world.Release.SetResult();
        world.RunState.NextEndingLandsAndFails = true;
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldEntries.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_attempt_whose_ending_could_not_be_written_is_not_tried_again()
    {
        RunLifecycleWorld world = null!;
        world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () =>
            {
                world.RunState.EndingsFail = true;
                return new InvalidOperationException("a transient failure");
            }
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        world.HoldEntries.Should().Be(1, "the row is still Running, and the sweep that replays it is the retry");
        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Running);
    }

    [Fact]
    public async Task A_failed_attempt_whose_ending_landed_before_the_store_went_away_is_still_tried_again()
    {
        RunLifecycleWorld world = null!;
        var first = 0;
        world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () =>
            {
                if (Interlocked.Exchange(ref first, 1) == 0)
                    world.RunState.NextEndingLandsThenFails = 2;
                return new InvalidOperationException("a transient failure");
            }
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        world.HoldEntries.Should().Be(3, "the first attempt's row reads back Failed, so the policy's attempts are still owed");
    }

    [Fact]
    public async Task A_failed_attempt_whose_ending_cannot_be_written_or_read_back_is_not_tried_again()
    {
        RunLifecycleWorld world = null!;
        world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () =>
            {
                world.RunState.EndingsFail = true;
                world.RunState.ReadsFail = true;
                return new InvalidOperationException("a transient failure");
            }
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        world.HoldEntries.Should().Be(1, "a retry here would walk beside the sweep's replay of a row left Running");
    }

    [Fact]
    public async Task A_resumed_segment_whose_row_a_peer_took_is_not_ended_although_its_block_turned_the_stop_into_a_failure()
    {
        var world = new RunLifecycleWorld { HoldWrapsItsCancellation = true };
        var parked = world.Parked();

        var resume = world.Resumer(heartbeat: Beat).ResumeFromSuspensionAsync(parked, CancellationToken.None);
        await world.Entered.Task.WaitAsync(Patience);
        world.RunState.TakeOver(world.ParkedStateId!.Value, "replica-2");

        (await resume.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.HoldEntries.Should().Be(1);
        world.RunState.Completions.Should().BeEmpty();
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Running,
            "the entry is the peer's now: it carries the run on under it, or closes it");
    }
    [Fact]
    public async Task A_run_stops_beating_before_it_parks_its_row()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        world.RunState.LingerAfterParkOrEnding = Beat * 10;
        using var engine = world.Engine(heartbeat: Beat, suspensions: new RunLifecycleWorld.RecordingSuspensions());

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.RunState.RenewedRowsIn.Should().NotContain(WorkflowRunStateStatus.Suspended,
            "a beat against a parked row renews a run that is not running");
    }

    [Fact]
    public async Task A_resumed_segment_stops_beating_before_it_parks_its_row_again()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        world.RunState.LingerAfterParkOrEnding = Beat * 10;
        var parked = world.Parked();

        await world.Resumer(heartbeat: Beat, suspensions: new RunLifecycleWorld.RecordingSuspensions())
            .ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.RunState.RenewedRowsIn.Should().NotContain(WorkflowRunStateStatus.Suspended);
    }

    [Fact]
    public async Task A_resumed_segment_stops_beating_before_it_ends_its_row()
    {
        var world = new RunLifecycleWorld();
        world.RunState.LingerAfterParkOrEnding = Beat * 10;
        using var caller = new CancellationTokenSource();
        var parked = world.Parked();

        var resume = world.Resumer(heartbeat: Beat).ResumeFromSuspensionAsync(parked, caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        await caller.CancelAsync();
        await resume.WaitAsync(Patience);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        world.RunState.RenewedRowsIn.Should().NotContain(WorkflowRunStateStatus.Cancelled);
    }
    [Fact]
    public async Task A_run_whose_block_surfaces_another_runs_lost_row_ends_its_own_row_rather_than_leaving_it_to_a_peer()
    {
        var world = new RunLifecycleWorld
        {
            HoldThrows = () => new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child-node", WorkflowRunOwnershipLoss.ReclaimedByPeer)
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience)).Should().ThrowAsync<Exception>();

        world.RunState.Completions.Should().ContainSingle()
            .Which.Should().Match<(Guid Id, WorkflowRunStateStatus Status, string? Reason)>(
                ending => ending.Id == world.RunState.LastBegun && ending.Status == WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public async Task A_resumed_segment_whose_block_surfaces_another_runs_lost_row_ends_its_own_row()
    {
        var world = new RunLifecycleWorld
        {
            HoldThrows = () => new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child-node", WorkflowRunOwnershipLoss.ReclaimedByPeer)
        };
        var parked = world.Parked();

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public async Task A_run_whose_block_surfaces_a_childs_lost_row_after_a_peer_took_its_own_leaves_that_row_alone()
    {
        RunLifecycleWorld? world = null;
        world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () =>
            {
                world!.RunState.Put(world.RunState.LastBegun!.Value, WorkflowRunStateStatus.Running, "replica-2");
                return new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child-node", WorkflowRunOwnershipLoss.ReclaimedByPeer);
            }
        };
        var captured = world;
        using var engine = captured.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(captured.WorkflowId, null, null).WaitAsync(Patience)).Should().ThrowAsync<Exception>();

        captured.HoldEntries.Should().Be(1, "the peer is running the workflow again");
        captured.RunState.Completions.Should().BeEmpty("the row is the peer's to end");
    }

    [Fact]
    public async Task A_run_whose_block_surfaces_a_childs_lost_row_is_not_tried_again()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () => new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child-node", WorkflowRunOwnershipLoss.ReclaimedByPeer)
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience)).Should().ThrowAsync<Exception>();

        world.HoldEntries.Should().Be(1, "the child's rows are a peer's now, and a retry would run the child beside it");
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public async Task A_run_whose_block_surfaces_a_child_that_finished_but_was_not_recorded_is_not_tried_again()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () => new WorkflowRunNotRecordedException(Guid.NewGuid(), new InvalidOperationException("journal"))
        };
        using var engine = world.Engine();

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowRunNotRecordedException>();

        world.HoldEntries.Should().Be(1);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public async Task A_resumed_segment_whose_block_surfaces_a_child_that_finished_but_was_not_recorded_is_not_tried_again()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () => new WorkflowRunNotRecordedException(Guid.NewGuid(), new InvalidOperationException("journal"))
        };
        var parked = world.Parked();

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public void A_child_that_finished_but_was_not_recorded_escapes_every_error_policy() =>
        WorkflowGraphExecutor.EscapesErrorPolicy(
                new WorkflowRunNotRecordedException(Guid.NewGuid(), new InvalidOperationException("journal")))
            .Should().BeTrue("a node retry or a catch container runs the child again");

    [Fact]
    public async Task A_resumed_segment_whose_block_surfaces_a_childs_lost_row_after_a_peer_took_its_own_leaves_that_row_alone()
    {
        RunLifecycleWorld? world = null;
        world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () =>
            {
                world!.RunState.Put(world.ParkedStateId!.Value, WorkflowRunStateStatus.Running, "replica-2");
                return new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child-node", WorkflowRunOwnershipLoss.ReclaimedByPeer);
            }
        };
        var parked = world.Parked();

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
        world.RunState.Completions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resumed_segment_whose_block_surfaces_a_childs_lost_row_is_not_tried_again()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""")
        {
            HoldThrows = () => new WorkflowRunOwnershipLostException(Guid.NewGuid(), "child-node", WorkflowRunOwnershipLoss.ReclaimedByPeer)
        };
        var parked = world.Parked();

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }
}
