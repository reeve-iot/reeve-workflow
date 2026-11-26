using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunReentryTests
{
    private const string DeadReplica = "replica-dead";

    [Fact]
    public async Task A_run_that_died_in_an_idempotent_block_is_finished_from_that_block_under_its_own_entry()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        world.Release.TrySetResult();
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.TickEntries.Should().Be(0, "the tick ran before the checkpoint and its outputs are in it");
        world.HoldEntries.Should().Be(1, "the block the replica died in is the one block run again");
        world.Journal.Logs.Should().ContainSingle("the run goes on under the entry it was writing, not a new one");
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.RunState.Row(row).FailureReason.Should().BeNull("the run ended itself; nothing replayed it");
    }

    [Fact]
    public async Task A_checkpoint_the_previous_release_wrote_still_resumes()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        world.Release.TrySetResult();
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["start"] = new Dictionary<string, object?>(),
            ["tick"] = new Dictionary<string, object?> { ["seen"] = "é<1>" }
        };
        world.RunState.Checkpointed(row, PreviousRelease.MainCheckpoint.Build(
            outputs, RunLifecycleWorld.HoldNodeId, journal, row, "start", null, null, RunStateWriteMode.EveryBlock, null, 1 << 20));

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.TickEntries.Should().Be(0, "the previous release's checkpoint says tick already completed");
        world.HoldEntries.Should().Be(1);
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_run_that_died_in_a_block_that_is_not_idempotent_fails_naming_the_block()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = false };
        world.Release.TrySetResult();
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(0);
        world.TickEntries.Should().Be(0);
        var ended = world.RunState.Row(row);
        ended.Status.Should().Be(WorkflowRunStateStatus.Failed);
        ended.FailureReason.Should().Be($"{WorkflowRunRecoverySweep.NotIdempotentReason}: {RunLifecycleWorld.HoldNodeId}");
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Failed,
            "the entry the dead replica left Running is closed with the run");
        world.Journal.Logs[journal].ErrorMessage.Should().Contain(RunLifecycleWorld.HoldNodeId);
    }

    [Fact]
    public async Task A_run_whose_checkpoint_says_it_cannot_be_re_entered_is_dealt_with_from_its_start()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        world.Release.TrySetResult();
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId, why: WorkflowRunCheckpoint.TooLarge);

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(0);
        world.RunState.Row(row).FailureReason.Should().Be(WorkflowRunRecoverySweep.NotIdempotentReason,
            "replayed from the start, the tick would run again, and it does not say it is idempotent");
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Failed,
            "the row names the entry, so whoever ends the row ends the entry");
    }

    [Fact]
    public async Task A_run_replayed_from_its_start_closes_the_entry_its_row_named_and_goes_on_in_a_new_one()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = true };
        world.Release.TrySetResult();
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId, why: WorkflowRunCheckpoint.TooLarge);

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.TickEntries.Should().Be(1, "a replay starts over");
        world.RunState.Row(row).FailureReason.Should().Be(WorkflowRunRecoverySweep.ReplayedReason);
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.Journal.Logs[journal].ErrorMessage.Should().StartWith(WorkflowRunRecoverySweep.ReplayedReason);
        world.Journal.Logs.Values.Should().ContainSingle(log => log.Id != journal)
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Theory]
    [InlineData(true, "ghost", RunLifecycleWorld.HoldNodeId)]
    [InlineData(false, "ghost", RunLifecycleWorld.HoldNodeId)]
    [InlineData(true, "start", "ghost")]
    public async Task A_run_whose_checkpoint_does_not_read_at_the_root_is_abandoned_not_replayed(
        bool idempotent, string outputOf, string nodeInFlight)
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = idempotent, HoldIdempotent = idempotent };
        world.Release.TrySetResult();
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["start"] = new Dictionary<string, object?>(),
            ["tick"] = new Dictionary<string, object?>(),
            [outputOf] = new Dictionary<string, object?>(),
        };
        world.RunState.Checkpointed(row, WorkflowRunCheckpoint.Build(
            outputs, TestPrograms.Untyped, nodeInFlight, journal, row, "start", null, null, RunStateWriteMode.EveryBlock, null, 1 << 20));

        var metrics = new EndingMetrics();
        await Sweep(world, metrics).SweepAsync(CancellationToken.None);

        world.TickEntries.Should().Be(0, "an unreadable checkpoint is not replayed");
        world.HoldEntries.Should().Be(0);
        var ended = world.RunState.Row(row);
        ended.Status.Should().Be(WorkflowRunStateStatus.Failed);
        ended.FailureReason.Should().MatchRegex("^resume_value_invalid: outputs, node 'ghost': .+");
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Failed);
        world.Journal.Logs[journal].ErrorMessage.Should().Be(ended.FailureReason);
        world.Journal.Logs.Should().ContainSingle("no replay opens an entry of its own");
        AssertEndedAsInvalid(world, metrics);
    }

    private static void AssertEndedAsInvalid(RunLifecycleWorld world, EndingMetrics metrics)
    {
        metrics.Endings.Should().Equal([WorkflowRecoveryEndingReasons.ResumeValueInvalid]);
        var ended = world.Audit.Events.Where(e => e.Kind == WorkflowAuditEventKinds.EndedByRecovery).Should().ContainSingle().Which;
        var data = (IDictionary<string, object?>)ended.Data!;
        data["reason"].Should().Be(WorkflowAbandonmentReasons.ResumeValueInvalid);
        ((string)data["invalid"]!).Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("frame", "frame, node 'ghost'", true)]
    [InlineData("call", "call, node 'loop'", true)]
    [InlineData("last", "frame, node 'ghost'", true)]
    [InlineData("root", "frame, node 'ghost'", true)]
    [InlineData("text", "frame, node 'loop'", true)]
    [InlineData("frame", "frame, node 'ghost'", false)]
    public async Task A_run_whose_position_does_not_read_is_abandoned_not_replayed(string corrupt, string where, bool idempotent)
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundHold)
        {
            LoopIterations = 3,
            HoldsAtEntry = 2,
            HoldIdempotent = idempotent
        };
        var (row, journal) = await DiedInsideAContainer(world);
        var beforeTheSweep = world.HoldEntries;
        world.RunState.Checkpointed(row, CorruptFirstFrame(world.RunState.Row(row).VariablesJson!, corrupt));

        var metrics = new EndingMetrics();
        await Sweep(world, metrics).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(beforeTheSweep, "the container is neither re-entered nor run again");
        var ended = world.RunState.Row(row);
        ended.Status.Should().Be(WorkflowRunStateStatus.Failed);
        ended.FailureReason.Should().MatchRegex($"^resume_value_invalid: {where}: .+");
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Failed);
        world.Journal.Logs[journal].ErrorMessage.Should().Be(ended.FailureReason);
        world.Journal.Logs.Should().ContainSingle("no replay opens an entry of its own");
        AssertEndedAsInvalid(world, metrics);
    }

    internal static string CorruptFirstFrame(string payloadJson, string corrupt)
    {
        var payload = System.Text.Json.Nodes.JsonNode.Parse(payloadJson)!.AsObject();
        var frames = System.Text.Json.Nodes.JsonNode.Parse(payload["ContinuationJson"]!.GetValue<string>())!.AsArray();
        var frame = frames[0]!.AsObject();
        switch (corrupt)
        {
            case "frame":
                frame["PreviousOutputsJson"] = """{"ghost":{}}""";
                break;
            case "call":
                frame["CompletedBySubCanvas"]!.AsObject().First().Value!.AsArray()[0]!["OutputsJson"] = "not json";
                break;
            case "last":
                frames[^1]!.AsObject()["NodeInFlight"] = "ghost";
                break;
            case "root":
                payload["NodeInFlight"] = "ghost";
                break;
        }

        payload["ContinuationJson"] = corrupt == "text" ? "not json" : frames.ToJsonString();
        return payload.ToJsonString();
    }

    [Fact]
    public async Task A_run_that_died_inside_a_container_is_refused_for_a_block_the_container_would_repeat()
    {
        var world = new RunLifecycleWorld(definitionJson: BoxAroundHold) { HoldIdempotent = false };
        world.Release.TrySetResult();
        var (row, _) = Abandoned(world, checkpointAt: "box");

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(0, "re-entering at the container runs everything inside it again");
        world.RunState.Row(row).FailureReason.Should().Be($"{WorkflowRunRecoverySweep.NotIdempotentReason}: box");
    }

    [Fact]
    public async Task A_run_whose_container_holds_a_block_that_may_not_run_twice_is_not_replayed_from_its_start()
    {
        var world = new RunLifecycleWorld(definitionJson: BoxAroundHold) { HoldIdempotent = false };
        world.Release.TrySetResult();
        var (row, _) = Abandoned(world, checkpointAt: "box");
        world.RunState.Checkpointed(row, "{}");

        (await world.Engine().IsRunReplayableAsync(world.WorkflowId, "box", world.Version.Id)).Should().BeFalse();
        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(0);
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    [Fact]
    public async Task A_run_that_died_asleep_past_its_wake_up_time_wakes_at_once_rather_than_sleeping_again()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);
        world.RunState.Asleep(row, DateTimeOffset.UtcNow.AddSeconds(-1));
        var sweep = new WorkflowRunRecoverySweep(world.Engine(durableSleepAtHold: true), world.RunState,
            new ClaimsEverythingRunning(world.RunState), new WorkflowRunRecoveryOptions(), RunLifecycleWorld.ReplicaId,
            NullLogger.Instance, world.Resumer(durableSleepAtHold: true));

        await sweep.SweepAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        world.TickEntries.Should().Be(0);
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed,
            "its minute was up when the replica died, so the sleep it is re-entered at is over");
    }

    [Fact]
    public async Task A_run_that_died_asleep_before_its_wake_up_time_sleeps_only_what_was_left()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        var (row, _) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);
        world.RunState.Asleep(row, DateTimeOffset.UtcNow.AddSeconds(1));
        var sweep = new WorkflowRunRecoverySweep(world.Engine(durableSleepAtHold: true), world.RunState,
            new ClaimsEverythingRunning(world.RunState), new WorkflowRunRecoveryOptions(), RunLifecycleWorld.ReplicaId,
            NullLogger.Instance, world.Resumer(durableSleepAtHold: true));

        await sweep.SweepAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed,
            "a second of its minute was left, and that is all it sleeps");
    }

    [Fact]
    public async Task A_sleep_re_entered_long_enough_to_park_parks_until_its_recorded_wake_up_time()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMinutes(1) };
        world.SuspensionSeam = world.Suspensions;
        var workflowId = world.AddWorkflow(sleepMs: (int)TimeSpan.FromMinutes(10).TotalMilliseconds);
        var version = (await world.Store.LoadAsync(workflowId, CancellationToken.None))!.CurrentVersion!.Id;
        var journal = Guid.NewGuid();
        world.Journal.Runs[journal] = new WorkflowExecutionLog(
            journal, null, workflowId, version, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var rowId = Guid.NewGuid();
        var wakesAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var checkpoint = WorkflowRunCheckpoint.Build(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["start"] = new Dictionary<string, object?>() },
            TestPrograms.Untyped, DurableSleepWorld.SleepNode, journal, rowId, "start", null, null, RunStateWriteMode.EveryBlock, null, 1 << 20);
        var row = new WorkflowRunStateRow
        {
            ExecutionId = rowId, WorkflowId = workflowId, Status = WorkflowRunStateStatus.Sleeping,
            OwnedByReplicaId = "replica-1", CurrentBlockPath = DurableSleepWorld.SleepNode, WaitUntilUtc = wakesAt,
            VariablesJson = checkpoint, LastTransitionUtc = DateTimeOffset.UtcNow
        };
        world.RunState.Rows[rowId] = row;
        using var engine = world.Engine();

        var outcome = await world.Resumer(engine).ReenterFromCheckpointAsync(
            row, WorkflowRunCheckpoint.Read(checkpoint)!, CancellationToken.None);

        outcome.Should().Be(WorkflowResumeOutcome.Resumed);
        world.Suspensions.SingleRow.TimeoutAtUtc.Should().BeCloseTo(wakesAt, TimeSpan.FromSeconds(1),
            "five of its ten minutes were left when the replica died");
    }

    [Fact]
    public async Task A_heartbeat_checkpoint_is_refused_for_a_block_after_it_that_is_not_idempotent()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        var (row, _) = Abandoned(world, checkpointAt: "tick", mode: RunStateWriteMode.Heartbeat(TimeSpan.FromMinutes(1)));

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.TickEntries.Should().Be(0);
        world.HoldEntries.Should().Be(0, "a heartbeat checkpoint may be blocks behind: the hold may have run already");
        world.RunState.Row(row).FailureReason.Should().Be($"{WorkflowRunRecoverySweep.NotIdempotentReason}: {RunLifecycleWorld.HoldNodeId}");
    }

    [Fact]
    public async Task The_same_checkpoint_written_before_every_block_is_re_entered_because_the_hold_had_not_started()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        var (row, _) = Abandoned(world, checkpointAt: "tick");

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.TickEntries.Should().Be(1);
        world.HoldEntries.Should().Be(1, "a write before every block would have named the hold had it started");
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_checkpoint_that_names_another_row_is_not_this_row_s_to_re_enter()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        world.Release.TrySetResult();
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);
        world.RunState.Checkpointed(row, WorkflowRunCheckpoint.Build(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["start"] = new Dictionary<string, object?>() },
            TestPrograms.Untyped, RunLifecycleWorld.HoldNodeId, journal, Guid.NewGuid(), "start", null, null, RunStateWriteMode.EveryBlock, null, 1 << 20));

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(0);
        world.RunState.Row(row).FailureReason.Should().Be(WorkflowRunRecoverySweep.NotIdempotentReason,
            "a row with no checkpoint of its own is refused as not idempotent");
    }

    [Fact]
    public async Task A_row_whose_journal_entry_is_gone_is_ended_rather_than_claimed_by_every_sweep()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        var (row, journal) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);
        world.Journal.Logs.TryRemove(journal, out _);

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(0);
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Failed);
        world.RunState.Row(row).FailureReason.Should().Be(WorkflowRunResumer.JournalEntryMissingReason);
    }

    [Fact]
    public async Task The_question_is_asked_of_the_version_the_run_is_walked_on()
    {
        var world = new RunLifecycleWorld(definitionJson: OneNode("Tick")) { TickIdempotent = true, HoldIdempotent = false };
        world.Release.TrySetResult();
        var older = new WorkflowVersion(Guid.NewGuid(), world.WorkflowId, 7, OneNode("Hold"), null, null, Guid.NewGuid(),
            DateTimeOffset.UtcNow, isPublished: true);
        world.Store.Older = older;
        var journal = Guid.NewGuid();
        world.Journal.Logs[journal] = new WorkflowExecutionLog(
            journal, null, world.WorkflowId, older.Id, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var row = world.RunState.Seed(world.WorkflowId, WorkflowRunStateStatus.Running, DeadReplica, "x");
        world.RunState.Checkpointed(row, WorkflowRunCheckpoint.Build(
            new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["start"] = new Dictionary<string, object?>() },
            TestPrograms.Untyped, "x", journal, row, "start", null, null, RunStateWriteMode.EveryBlock, null, 1 << 20));

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(0);
        world.RunState.Row(row).FailureReason.Should().Be($"{WorkflowRunRecoverySweep.NotIdempotentReason}: x");
    }

    [Fact]
    public async Task A_run_whose_journal_entry_cannot_be_read_is_dealt_with_from_its_start()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        world.Release.TrySetResult();
        var (row, _) = Abandoned(world, checkpointAt: RunLifecycleWorld.HoldNodeId);
        world.Journal.LoadsFail = true;

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.RunState.Row(row).FailureReason.Should().Be(WorkflowRunRecoverySweep.NotIdempotentReason,
            "replayed from the start, the tick would run again, and it does not say it is idempotent");
    }

    [Fact]
    public async Task A_container_that_only_drives_its_body_is_re_entered_when_everything_in_that_body_may_run_twice()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundHold) { HoldIdempotent = true };
        world.Release.TrySetResult();

        (await world.Engine().IsRunReplayableAsync(world.WorkflowId, "loop", world.Version.Id)).Should().BeTrue(
            "the loop's own effect is its body, and the body may run twice");
    }

    [Fact]
    public async Task A_container_that_only_drives_its_body_is_refused_as_soon_as_one_block_in_that_body_may_not()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundHold) { HoldIdempotent = false };
        world.Release.TrySetResult();

        (await world.Engine().IsRunReplayableAsync(world.WorkflowId, "loop", world.Version.Id)).Should().BeFalse(
            "re-entering at the loop runs the hold again, whatever the loop says about itself");
    }

    [Fact]
    public async Task A_run_that_died_at_a_loop_of_blocks_that_may_run_twice_is_finished_rather_than_failed()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundHold) { HoldIdempotent = true };
        world.Release.TrySetResult();
        var (row, _) = Abandoned(world, checkpointAt: "loop");

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed,
            "the loop's blocks may run twice, so the row is finished rather than refused");
        world.HoldEntries.Should().Be(1, "the body ran once, on the surviving replica");
    }

    [Fact]
    public async Task A_block_with_no_sub_canvases_is_not_let_off_by_declaring_that_it_re_enters_them()
    {
        var world = new RunLifecycleWorld(definitionJson: OneNode(RunLifecycleWorld.BareType));
        world.Release.TrySetResult();

        (await world.Engine().IsRunReplayableAsync(world.WorkflowId, "x", world.Version.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_container_whose_declared_body_is_left_out_is_answered_for_as_the_empty_body_it_runs()
    {
        var world = new RunLifecycleWorld(definitionJson: OneNode(RunLifecycleWorld.LoopType));
        world.Release.TrySetResult();

        (await world.Engine().IsRunReplayableAsync(world.WorkflowId, "x", world.Version.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task A_run_that_died_after_a_left_out_branch_replays_that_call_and_goes_on_from_the_block_it_died_in()
    {
        var world = new RunLifecycleWorld(definitionJson: TryLeftOutThenHolds) { HoldsAtEntry = 1, HoldIdempotent = true };
        var (row, journal) = await DiedInsideAContainer(world);
        var frame = WorkflowRunCheckpoint.Continuation(WorkflowRunCheckpoint.Read(world.RunState.Row(row).VariablesJson)).Single();

        await Sweep(world).SweepAsync(CancellationToken.None);

        frame.CompletedBySubCanvas["Try"].Should().ContainSingle().Which.Error.Should().BeNull(
            "the left-out Try ran as the empty branch, which fails at nothing");
        world.HoldEntries.Should().Be(2, "the call into the left-out Try is replayed from the record, the hold is run again");
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_run_that_died_in_the_second_pass_of_a_loop_is_finished_without_the_first_pass_running_again()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundHold)
        {
            LoopIterations = 3,
            HoldsAtEntry = 2,
            HoldIdempotent = true
        };
        var (row, journal) = await DiedInsideAContainer(world);
        var beforeTheSweep = world.HoldEntries;

        await Sweep(world).SweepAsync(CancellationToken.None);

        beforeTheSweep.Should().Be(2, "the first pass finished and the second was in flight when the replica died");
        world.HoldEntries.Should().Be(4,
            "the peer replays the first pass from the record and runs only the second and third");
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Succeeded,
            "the run goes on under the entry it was writing");
    }

    [Fact]
    public async Task A_loop_resumes_in_its_fourth_pass_and_returns_each_distinct_item_once_in_order()
    {
        var definition = LoopWithItemOutputs();
        var world = new RunLifecycleWorld(definitionJson: definition,
            outputParametersJson: "[{\"name\":\"Results\",\"type\":\"Json\"}]")
        {
            LoopIterations = 5,
            HoldsAtEntry = 4,
            HoldIdempotent = true,
            CollectLoopResults = true
        };
        using var kill = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, null, cancellationToken: kill.Token);
        if (await Task.WhenAny(world.Entered.Task, run) == run)
            await run;
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var row = world.RunState.LastBegun!.Value;
        var checkpoint = world.RunState.Row(row).VariablesJson;
        var journal = world.Journal.Logs.Keys.Single();
        var path = world.RunState.Row(row).CurrentBlockPath;
        await kill.CancelAsync();
        try { await run; } catch (Exception) { }
        world.Journal.Logs[journal] = new WorkflowExecutionLog(
            journal, null, world.WorkflowId, world.Version.Id, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        world.RunState.Put(row, WorkflowRunStateStatus.Running, DeadReplica, path);
        world.RunState.Checkpointed(row, checkpoint);
        world.LoopValueEntries.Should().Equal([0, 1, 2, 3],
            "the dead attempt evaluated the first four distinct body values");
        world.Release.TrySetResult();

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(6, "the first three calls replay, then only passes four and five execute");
        world.LoopValueEntries.Should().Equal([0, 1, 2, 3, 4],
            "replay answers passes one through three and the checkpoint carries pass four's value");
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed,
            world.RunState.Row(row).FailureReason);
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        var resumed = System.Text.Json.Nodes.JsonNode.Parse(world.Journal.Logs[journal].OutputsJson!)!["Results"];

        var straight = new RunLifecycleWorld(definitionJson: definition,
            outputParametersJson: "[{\"name\":\"Results\",\"type\":\"Json\"}]")
        {
            LoopIterations = 5,
            CollectLoopResults = true
        };
        straight.Release.TrySetResult();
        var expected = await straight.Engine().ExecuteAsync(straight.WorkflowId, null, null);

        resumed!.ToJsonString().Should().Be(System.Text.Json.JsonSerializer.Serialize(expected["Results"]));
        resumed!.AsArray().Select(value => value!.GetValue<int>()).Should().Equal(0, 1, 2, 3, 4);
    }

    private static string LoopWithItemOutputs() => System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "loop", blockTypeId = RunLifecycleWorld.LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "value", blockTypeId = RunLifecycleWorld.LoopValueType, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = RunLifecycleWorld.HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "out", blockTypeId = WorkflowGraphHelpers.WorkflowOutputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[]
                        {
                            new { id = "input-value", sourceNodeId = "in", targetNodeId = "value", mappings = new { Index = new { type = "direct", sourceOutputName = "Index" } } },
                            new { id = "value-hold", sourceNodeId = "value", targetNodeId = RunLifecycleWorld.HoldNodeId, mappings = new { } },
                            new { id = "value-out", sourceNodeId = "value", targetNodeId = "out", mappings = new { Item = new { type = "direct", sourceOutputName = "Value" } } }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            },
            new { id = "out", blockTypeId = WorkflowGraphHelpers.WorkflowOutputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "start-loop", sourceNodeId = "start", targetNodeId = "loop", mappings = new { } },
            new { id = "loop-out", sourceNodeId = "loop", targetNodeId = "out", mappings = new { Results = new { type = "direct", sourceOutputName = "Results", projection = new { path = "$" } } } }
        }
    });

    [Fact]
    public async Task A_run_that_died_inside_a_loop_is_refused_for_the_block_it_died_in_rather_than_for_the_loop()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundHold)
        {
            LoopIterations = 3,
            HoldsAtEntry = 2,
            HoldIdempotent = false
        };
        var (row, _) = await DiedInsideAContainer(world);

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.RunState.Row(row).FailureReason.Should().Be(
            $"{WorkflowRunRecoverySweep.NotIdempotentReason}: {RunLifecycleWorld.HoldNodeId}",
            "the question is asked of the block inside the loop the run died in, not of the loop");
    }

    [Fact]
    public async Task A_pass_re_entered_halfway_runs_only_what_it_had_not_and_the_passes_after_it_run_whole()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundTickThenHold)
        {
            LoopIterations = 3,
            HoldsAtEntry = 2,
            HoldIdempotent = true,
            TickIdempotent = true
        };
        var (row, _) = await DiedInsideAContainer(world);
        var ticksBefore = world.TickEntries;

        await Sweep(world).SweepAsync(CancellationToken.None);

        ticksBefore.Should().Be(2, "the first pass ran the tick, and so did the pass the replica died in");
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.TickEntries.Should().Be(3,
            "the first pass is replayed from the record, the second is re-entered past its tick, and the third runs whole");
        world.HoldEntries.Should().Be(4);
    }

    [Fact]
    public async Task A_second_attempt_at_a_re_entered_run_replays_the_finished_passes_again_rather_than_making_them()
    {
        var world = new RunLifecycleWorld(
            retryPolicyJson: """{"maxAttempts":2}""", definitionJson: RunLifecycleWorld.LoopAroundHold)
        {
            LoopIterations = 3,
            HoldsAtEntry = 2,
            HoldThrowsAtEntry = 3,
            HoldIdempotent = true
        };
        var (row, _) = await DiedInsideAContainer(world);

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.HoldEntries.Should().Be(5,
            "two before the replica died, one the failed attempt made, and two the second attempt made — the first "
            + "pass is replayed from the record on both attempts and never runs again");
    }

    private static async Task<(Guid Row, Guid Journal)> DiedInsideAContainer(RunLifecycleWorld world)
    {
        using var kill = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, null, cancellationToken: kill.Token);
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var row = world.RunState.LastBegun!.Value;
        var checkpoint = world.RunState.Row(row).VariablesJson;
        var journal = world.Journal.Logs.Keys.Single();
        var path = world.RunState.Row(row).CurrentBlockPath;

        await kill.CancelAsync();
        try { await run; } catch (Exception) { }

        world.Journal.Logs[journal] = new WorkflowExecutionLog(
            journal, null, world.WorkflowId, world.Version.Id, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        world.RunState.Put(row, WorkflowRunStateStatus.Running, DeadReplica, path);
        world.RunState.Checkpointed(row, checkpoint);
        return (row, journal);
    }

    private static string OneNode(string blockTypeId) => $$"""
        {"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"x","blockTypeId":"{{blockTypeId}}"}],
         "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"x","mappings":{} }]}
        """;

    private static readonly string TryLeftOutThenHolds = System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "attempt", blockTypeId = RunLifecycleWorld.TryType,
                subCanvases = new Dictionary<string, object>
                {
                    ["After"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "afterIn", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = RunLifecycleWorld.HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "e-hold", sourceNodeId = "afterIn", targetNodeId = RunLifecycleWorld.HoldNodeId, mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-attempt", sourceNodeId = "start", targetNodeId = "attempt", mappings = new { } } }
    });

    private static readonly string BoxAroundHold = System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = RunLifecycleWorld.BoxType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = RunLifecycleWorld.HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "in-hold", sourceNodeId = "in", targetNodeId = RunLifecycleWorld.HoldNodeId, mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } }
    });

    [Fact]
    public async Task A_run_another_run_started_is_re_entered_from_its_checkpoint_rather_than_ended()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        world.Release.TrySetResult();
        var (row, journal) = Abandoned(
            world, checkpointAt: RunLifecycleWorld.HoldNodeId, startedByRun: Guid.NewGuid());

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(1, "the block the replica died in is the one block run again");
        world.TickEntries.Should().Be(0, "and everything before the checkpoint is answered from it");
        world.RunState.Row(row).Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.RunState.Row(row).FailureReason.Should().BeNull();
        world.Journal.Logs[journal].Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_run_another_run_started_that_cannot_be_re_entered_is_still_ended_rather_than_replayed()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true) { HoldIdempotent = true };
        world.Release.TrySetResult();
        var journal = Guid.NewGuid();
        world.Journal.Logs[journal] = new WorkflowExecutionLog(
            journal, null, world.WorkflowId, world.Version.Id, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var row = world.RunState.Seed(
            world.WorkflowId, WorkflowRunStateStatus.Running, DeadReplica, RunLifecycleWorld.HoldNodeId,
            parentExecutionId: Guid.NewGuid());

        await Sweep(world).SweepAsync(CancellationToken.None);

        world.HoldEntries.Should().Be(0, "nothing ran it again");
        var ended = world.RunState.Row(row);
        ended.Status.Should().Be(WorkflowRunStateStatus.Failed);
        ended.FailureReason.Should().Be(WorkflowRunRecoverySweep.DispatchedByAnotherRunReason);
    }

    private static (Guid Row, Guid Journal) Abandoned(
        RunLifecycleWorld world, string checkpointAt, string? why = null, RunStateWriteMode? mode = null,
        Guid? startedByRun = null)
    {
        var journal = Guid.NewGuid();
        world.Journal.Logs[journal] = new WorkflowExecutionLog(
            journal, null, world.WorkflowId, world.Version.Id, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var row = world.RunState.Seed(
            world.WorkflowId, WorkflowRunStateStatus.Running, DeadReplica, checkpointAt, startedByRun);
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["start"] = new Dictionary<string, object?>() };
        if (checkpointAt == RunLifecycleWorld.HoldNodeId)
            outputs["tick"] = new Dictionary<string, object?>();
        world.RunState.Checkpointed(row, WorkflowRunCheckpoint.Build(
            outputs, TestPrograms.Untyped, checkpointAt, journal, row, "start", null, null, mode ?? RunStateWriteMode.EveryBlock, why, 1 << 20));
        return (row, journal);
    }

    private static WorkflowRunRecoverySweep Sweep(RunLifecycleWorld world, IWorkflowMetrics? metrics = null) =>
        new(world.Engine(), world.RunState, new ClaimsEverythingRunning(world.RunState), new WorkflowRunRecoveryOptions(),
            RunLifecycleWorld.ReplicaId, NullLogger.Instance, world.Resumer(), metrics, world.Audit);

    private sealed class EndingMetrics : IWorkflowMetrics
    {
        public List<string> Endings { get; } = [];

        public void RecordRecoveryClaim(int rows) { }
        public void RecordRecoveryEnding(string reason) => Endings.Add(reason);
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    internal sealed class ClaimsEverythingRunning(RunLifecycleWorld.MemoryRunState rows) : IWorkflowRunRecoveryStore
    {
        private bool claimed;

        public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
            WorkflowRunRecoveryClaim request, CancellationToken cancellationToken)
        {
            if (claimed)
                return Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>([]);
            claimed = true;
            var id = rows.LastSeeded ?? rows.LastBegun!.Value;
            rows.TakeOver(id, request.ClaimedByReplicaId);
            return Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>([rows.Row(id)]);
        }

        public Task<bool> RenewClaimAsync(Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken) =>
            Task.FromResult(rows.Row(executionId).OwnedByReplicaId == claimedByReplicaId);
    }
}
