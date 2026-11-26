using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunJournalOwnershipTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_run_handed_over_on_a_stop_leaves_its_entry_open_and_says_why()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };

        await HandOver(world);

        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        log.Status.Should().Be(WorkflowExecutionStatus.Running, "the run is not over: a peer carries it on under this entry");
        log.ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason,
            "whoever reads the history has to be able to tell a deploy from an operator");
    }

    [Fact]
    public async Task A_host_whose_own_sweep_takes_the_row_ends_it_then_closes_the_entry_it_named()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        await HandOver(world);
        var entry = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        entry.Status.Should().Be(WorkflowExecutionStatus.Running, "the handover leaves it open");
        var saves = world.Journal.RunsSaved;

        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        var named = WorkflowRunCheckpoint.Of(row)?.JournalExecutionId;
        named.Should().Be(entry.Id, "a sweep of the host's own has only the row to find the entry from");
        world.RunState.Put(
            row.ExecutionId, WorkflowRunStateStatus.Completed, owner: null, reason: "replayed_by_the_host_s_own_sweep");

        var closed = await world.Resumer().CloseEntryAsync(
            named!.Value, WorkflowExecutionStatus.Cancelled, "replayed_from_its_start_by_the_host_s_own_sweep");

        closed.Should().BeTrue("a host that is told nothing cannot retry the one write that ends the run");
        entry.Status.Should().Be(WorkflowExecutionStatus.Cancelled, "the run is over, under an entry of the replay's");
        entry.ErrorMessage.Should().Be("replayed_from_its_start_by_the_host_s_own_sweep");
        world.Journal.RunsSaved.Should().Be(saves + 1, "and the ending is written, not only set on the object");
    }

    [Fact]
    public async Task Closing_an_entry_a_peer_already_ended_leaves_the_peers_ending_alone()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        await HandOver(world);
        var entry = world.Journal.Logs.Values.Single();
        entry.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow);
        var saves = world.Journal.RunsSaved;

        var closed = await world.Resumer().CloseEntryAsync(entry.Id, WorkflowExecutionStatus.Cancelled, "too late");

        closed.Should().BeTrue("the entry is closed, which is all the caller asked about");
        world.Journal.Logs.Should().ContainKey(entry.Id, "the entry the guard read is this one, not a missing row");
        world.Journal.RunsSaved.Should().Be(saves, "an entry already ended is not written over");
        entry.Status.Should().Be(WorkflowExecutionStatus.Succeeded, "the peer finished the run under this entry");
        entry.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_reason_longer_than_the_column_is_cut_to_fit_rather_than_failing_the_close()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        await HandOver(world);
        var entry = world.Journal.Logs.Values.Single();

        var closed = await world.Resumer()
            .CloseEntryAsync(entry.Id, WorkflowExecutionStatus.Cancelled, new string('x', 3000));

        closed.Should().BeTrue();
        entry.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        entry.ErrorMessage.Should().HaveLength(2000, "which is what the column holds");
    }

    [Fact]
    public async Task A_close_the_journal_refuses_says_so_rather_than_letting_the_host_assume_it_landed()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        await HandOver(world);
        var entry = world.Journal.Logs.Values.Single();
        world.Journal.LoadsFail = true;

        var closed = await world.Resumer().CloseEntryAsync(entry.Id, WorkflowExecutionStatus.Cancelled, "replayed");

        closed.Should().BeFalse("the journal did not answer, so the entry may still be open");
        entry.Status.Should().Be(WorkflowExecutionStatus.Running, "and nothing was written");
    }

    [Fact]
    public async Task An_entry_the_journal_does_not_have_answers_closed_because_nothing_holds_it_open()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        await HandOver(world);
        var saves = world.Journal.RunsSaved;

        var closed = await world.Resumer().CloseEntryAsync(
            Guid.NewGuid(), WorkflowExecutionStatus.Cancelled, "an entry that is not there");

        closed.Should().BeTrue("there is no entry left open, which is what the caller is asking about");
        world.Journal.RunsSaved.Should().Be(saves, "and nothing was written");
    }

    [Fact]
    public void A_checkpoint_naming_another_row_is_not_this_rows_entry_to_close()
    {
        var thisRow = Guid.NewGuid();
        var someoneElses = Guid.NewGuid();
        var checkpoint = WorkflowRunCheckpoint.Build(
            [], walk: TestPrograms.Untyped, nodeInFlight: "hold", journalExecutionId: Guid.NewGuid(), stateExecutionId: someoneElses,
            inputNodeId: null, triggerId: null, triggerEnvelope: null, writeMode: RunStateWriteMode.EveryBlock,
            notReenterableBecause: null, maxBytes: 256 * 1024);
        var row = new WorkflowRunStateRow
        {
            ExecutionId = thisRow,
            WorkflowId = Guid.NewGuid(),
            Status = WorkflowRunStateStatus.Running,
            VariablesJson = checkpoint
        };

        WorkflowRunCheckpoint.Read(checkpoint).Should().NotBeNull("the payload is a checkpoint, and reads as one");
        WorkflowRunCheckpoint.Of(row).Should().BeNull("but not this row's, so this row names no entry");
        WorkflowRunCheckpoint.Of(row with { ExecutionId = someoneElses })
            .Should().NotBeNull("and the row it does name reads it");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_close_with_no_reason_is_refused_because_the_reason_is_what_a_history_reads(string? reason)
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        await HandOver(world);
        var entry = world.Journal.Logs.Values.Single();
        var saves = world.Journal.RunsSaved;

        var resumer = world.Resumer();
        var close = () => { _ = resumer.CloseEntryAsync(entry.Id, WorkflowExecutionStatus.Cancelled, reason!); };

        close.Should().Throw<ArgumentException>()
            .Which.ParamName.Should().Be("reason", "which argument was refused is what the caller has to fix");
        entry.Status.Should().Be(WorkflowExecutionStatus.Running, "a refused call writes nothing");
        world.Journal.RunsSaved.Should().Be(saves);
    }

    [Fact]
    public async Task Closing_an_entry_as_running_is_refused_because_that_is_not_an_ending()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        await HandOver(world);
        var entry = world.Journal.Logs.Values.Single();
        var handedOver = entry.ErrorMessage;
        var saves = world.Journal.RunsSaved;

        var resumer = world.Resumer();
        var close = () => { _ = resumer.CloseEntryAsync(entry.Id, WorkflowExecutionStatus.Running, "carrying on"); };

        close.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("status");
        entry.CompletedAtUtc.Should().BeNull("the ending time is not set by a call that was refused");
        entry.ErrorMessage.Should().Be(handedOver, "and the handover's note is still what the history says");
        world.Journal.RunsSaved.Should().Be(saves);
    }

    [Fact]
    public async Task A_run_handed_over_on_a_stop_is_finished_by_the_peer_under_the_same_entry_from_the_block_it_stopped_in()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        await HandOver(world);
        world.Release.TrySetResult();

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.TickEntries.Should().Be(1, "the tick ran before the handover and is in the checkpoint");
        world.HoldEntries.Should().Be(2, "the block the run stopped in is the one it is re-entered at");
        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        log.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        log.ErrorMessage.Should().BeNull();
        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_replica_that_finds_a_peer_took_its_row_leaves_the_entry_to_the_peer()
    {
        var world = new RunLifecycleWorld(definitionJson: """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"hold","blockTypeId":"Hold"},
                      {"id":"tick","blockTypeId":"Tick"}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"hold","mappings":{}},
                      {"id":"e2","sourceNodeId":"hold","targetNodeId":"tick","mappings":{}}]}
            """) { HoldIdempotent = true };
        using var engine = world.Engine();
        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);

        world.RunState.TakeOver(world.RunState.LastBegun!.Value, "replica-2");
        world.Release.TrySetResult();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowRunOwnershipLostException>();

        world.TickEntries.Should().Be(0);
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Running,
            "replica-2 is re-entering the run under this entry; closing it here would end the run under it");
    }

    [Fact]
    public async Task A_run_handed_over_whose_row_names_no_entry_of_its_own_closes_it_as_before()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };

        await HandOver(world, beforeTheStop: () =>
            world.RunState.Checkpointed(world.RunState.LastBegun!.Value, "{\"carrier\":\"DHL\"}"));

        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_run_handed_over_whose_row_had_already_ended_closes_its_entry_because_no_sweep_takes_that_row()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };

        await HandOver(world, beforeTheStop: () => world.RunState.Put(
            world.RunState.LastBegun!.Value, WorkflowRunStateStatus.Cancelled, RunLifecycleWorld.ReplicaId));

        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
    }

    [Theory]
    [InlineData(WorkflowRunStateStatus.Completed, WorkflowExecutionStatus.Succeeded)]
    [InlineData(WorkflowRunStateStatus.Suspended, WorkflowExecutionStatus.Running)]
    public async Task A_replica_that_finds_a_peer_already_ended_or_parked_its_row_writes_nothing_to_the_peer_s_entry(
        WorkflowRunStateStatus peerLeftTheRow, WorkflowExecutionStatus peerLeftTheEntry)
    {
        var world = new RunLifecycleWorld(definitionJson: HoldThenTick) { HoldIdempotent = true };
        using var engine = world.Engine();
        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);

        var rowId = world.RunState.LastBegun!.Value;
        world.RunState.TakeOver(rowId, "replica-2");
        world.RunState.Put(rowId, peerLeftTheRow, "replica-2");
        if (peerLeftTheRow == WorkflowRunStateStatus.Suspended)
            world.RunState.Checkpointed(rowId, "{\"carrier\":\"DHL\"}");
        var entry = world.Journal.Logs.Values.Single();
        if (peerLeftTheEntry != WorkflowExecutionStatus.Running)
            entry.Complete(peerLeftTheEntry, DateTimeOffset.UtcNow);
        world.Release.TrySetResult();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowRunOwnershipLostException>();

        entry.Status.Should().Be(peerLeftTheEntry, "the entry is the peer's; closing it here overwrites the peer's ending");
        entry.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_replica_that_lost_its_row_before_any_checkpoint_of_its_landed_closes_its_entry()
    {
        var world = new RunLifecycleWorld(definitionJson: HoldThenTick) { HoldIdempotent = true };
        world.RunState.FailWritesBefore = "hold";
        using var engine = world.Engine();
        var run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);

        world.RunState.TakeOver(world.RunState.LastBegun!.Value, "replica-2");
        world.Release.TrySetResult();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowRunOwnershipLostException>();

        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_re_entered_run_that_loses_its_row_before_writing_anything_leaves_the_entry_to_the_peer()
    {
        var world = new RunLifecycleWorld(definitionJson: HoldThenTick) { HoldIdempotent = true };
        await HandOver(world);
        var entry = world.Journal.Logs.Values.Single();
        entry.Status.Should().Be(WorkflowExecutionStatus.Running);
        world.RunState.PeerTakesRowBefore = RunLifecycleWorld.HoldNodeId;
        world.RunState.PeerTakesRowAtWrite = 2;
        world.Release.TrySetResult();

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(1, "the row was the peer's before the re-entry's hold");
        world.RunState.Row(world.RunState.LastBegun!.Value).OwnedByReplicaId.Should().Be("replica-2");
        entry.Status.Should().Be(WorkflowExecutionStatus.Running,
            "replica-2 holds the row, which names this entry; closing it here drops the run the peer is carrying on");
    }

    [Fact]
    public async Task A_resumed_run_whose_next_attempt_loses_the_row_before_writing_leaves_the_entry_to_the_peer()
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":2}""", tickBeforeHold: true)
        {
            FailHoldTimes = 1, TickIdempotent = true
        };
        var parked = world.Parked();
        world.RunState.PeerTakesRowBefore = "tick";
        world.RunState.PeerTakesRowAtWrite = 2;

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldEntries.Should().Be(1, "the second attempt found the row the peer's before the tick");
        world.RunState.Row(world.ParkedStateId!.Value).OwnedByReplicaId.Should().Be("replica-2");
        world.Journal.Logs[parked.ExecutionLogId].Status.Should().Be(WorkflowExecutionStatus.Running,
            "replica-2 holds the row, which names this entry");
    }

    private const string HoldThenTick = """
        {"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"hold","blockTypeId":"Hold"},
                  {"id":"tick","blockTypeId":"Tick"}],
         "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"hold","mappings":{}},
                  {"id":"e2","sourceNodeId":"hold","targetNodeId":"tick","mappings":{}}]}
        """;

    private static async Task HandOver(RunLifecycleWorld world, Action? beforeTheStop = null)
    {
        using var stopping = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var engine = world.Engine(stopping.Token, handOverOnStop: true);

        var run = engine.ExecuteAsync(world.WorkflowId, null, null, cancellationToken: caller.Token);
        await world.Entered.Task.WaitAsync(Patience);
        beforeTheStop?.Invoke();
        await stopping.CancelAsync();
        await caller.CancelAsync();
        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
    }

    private static WorkflowRunRecoverySweep Sweep(RunLifecycleWorld world) =>
        new(world.Engine(), world.RunState, new WorkflowRunReentryTests.ClaimsEverythingRunning(world.RunState),
            new WorkflowRunRecoveryOptions(), RunLifecycleWorld.ReplicaId, NullLogger.Instance, world.Resumer());
}
