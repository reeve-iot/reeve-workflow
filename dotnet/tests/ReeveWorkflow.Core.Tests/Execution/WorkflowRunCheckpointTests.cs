using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunCheckpointTests
{
    [Fact]
    public async Task The_write_before_a_block_leaves_what_a_recovery_re_enters_from()
    {
        var world = new RunLifecycleWorld(tickBeforeHold: true, inputParametersJson: """[{"name":"x","type":"Int"}]""");
        using var stop = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, new Dictionary<string, object?> { ["x"] = 1 },
            cancellationToken: stop.Token);
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        var checkpoint = WorkflowRunCheckpoint.Read(row.VariablesJson);

        checkpoint.Should().NotBeNull("the row of a run inside its second block carries a checkpoint");
        checkpoint!.NodeInFlight.Should().Be(RunLifecycleWorld.HoldNodeId);
        checkpoint.JournalExecutionId.Should().Be(world.Journal.Logs.Keys.Single());
        checkpoint.ExecutionStateId.Should().Be(row.ExecutionId);
        checkpoint.InputNodeId.Should().Be("start", "its outputs are the parameters the run was called with");
        checkpoint.NotReenterableBecause.Should().BeNull();
        PersistedValueCodec.ForDisplay(checkpoint.PreviousOutputsJson).Keys
            .Should().BeEquivalentTo(["start", "tick"], "the block in flight has no outputs yet, and every block before it has");

        world.Release.TrySetResult();
        await run;
    }

    [Fact]
    public void A_checkpoint_past_the_cap_says_so_rather_than_losing_outputs()
    {
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["big"] = new Dictionary<string, object?> { ["blob"] = new string('x', 4096) }
        };

        var json = WorkflowRunCheckpoint.Build(
            outputs, TestPrograms.Untyped, "next", Guid.NewGuid(), Guid.NewGuid(), inputNodeId: null, triggerId: null, triggerEnvelope: null,
            RunStateWriteMode.EveryBlock, notReenterableBecause: null, maxBytes: 1024);

        var checkpoint = WorkflowRunCheckpoint.Read(json)!;
        checkpoint.NotReenterableBecause.Should().Be(WorkflowRunCheckpoint.TooLarge);
        PersistedValueCodec.ForDisplay(checkpoint.PreviousOutputsJson)
            .Should().BeEmpty("a checkpoint missing some outputs would re-enter the run without them");
    }

    [Fact]
    public void A_checkpoint_keeps_secret_output_values_for_a_resumed_run()
    {
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["source"] = new Dictionary<string, object?> { ["password"] = "resume-value" }
        };

        var json = WorkflowRunCheckpoint.Build(
            outputs, TestPrograms.Untyped, "next", Guid.NewGuid(), Guid.NewGuid(), inputNodeId: null, triggerId: null, triggerEnvelope: null,
            RunStateWriteMode.EveryBlock, notReenterableBecause: null, maxBytes: 1 << 20);
        var checkpoint = WorkflowRunCheckpoint.Read(json)!;
        var resumed = PersistedValueCodec.ForDisplay(checkpoint.PreviousOutputsJson);

        ((string)resumed["source"]["password"]!).Should().Be("resume-value");
    }

    [Fact]
    public void A_suspension_payload_round_trips_nested_secret_outputs_unmasked()
    {
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["source"] = new Dictionary<string, object?>
            {
                ["auth"] = new Dictionary<string, object?> { ["token"] = "resume-value" }
            }
        };

        var json = WorkflowExecutionHelpers.BuildSuspensionStatePayload(
            outputs, TestPrograms.Untyped, null, null, inputNodeId: null);
        var payload = System.Text.Json.JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(
            json, WorkflowGraphHelpers.JsonOptions)!;
        var resumed = PersistedValueCodec.ForDisplay(payload.PreviousOutputsJson);

        ((string)((IReadOnlyDictionary<string, object?>)resumed["source"]["auth"]!)["token"]!)
            .Should().Be("resume-value");
    }

    [Theory]
    [MemberData(nameof(NestedWaitTests.Unwritable), MemberType = typeof(NestedWaitTests))]
    public void A_checkpoint_holding_an_output_with_no_json_form_says_so_rather_than_throwing(string kind)
    {
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["src"] = new Dictionary<string, object?> { ["out"] = NestedWaitTests.UnwritableValues[kind]() }
        };

        var json = WorkflowRunCheckpoint.Build(
            outputs, TestPrograms.Untyped, "next", Guid.NewGuid(), Guid.NewGuid(), inputNodeId: null, triggerId: null, triggerEnvelope: null,
            RunStateWriteMode.EveryBlock, notReenterableBecause: null, maxBytes: 1 << 20);

        var checkpoint = WorkflowRunCheckpoint.Read(json)!;
        checkpoint.NotReenterableBecause.Should().Be(WorkflowRunCheckpoint.Unserialisable);
        PersistedValueCodec.ForDisplay(checkpoint.PreviousOutputsJson).Should().BeEmpty();
    }

    [Fact]
    public void A_row_without_a_checkpoint_or_with_one_that_will_not_read_has_none()
    {
        WorkflowRunCheckpoint.Read(null).Should().BeNull();
        WorkflowRunCheckpoint.Read("not json").Should().BeNull();
        WorkflowRunCheckpoint.Read("{\"variablesJson\":\"{}\",\"previousOutputsJson\":\"{}\",\"triggerEnvelopeJson\":\"{}\","
            + "\"executionStateId\":null}").Should().BeNull("a parked run's payload names no node in flight");
    }

    [Fact]
    public void Inputs_shaped_like_a_checkpoint_are_not_one()
    {
        WorkflowRunCheckpoint.Read($$"""
            {"variablesJson":"{}","previousOutputsJson":"{}","triggerEnvelopeJson":"{}","executionStateId":null,
             "nodeInFlight":"label","journalExecutionId":"{{Guid.NewGuid()}}"}
            """).Should().BeNull("only a payload that says it is a checkpoint is read as one");
    }

    [Fact]
    public void A_host_that_shows_a_row_s_variables_gets_the_flattened_scope_from_either_shape()
    {
        var outputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["start"] = new Dictionary<string, object?> { ["who"] = "alice" },
            ["lookup"] = new Dictionary<string, object?> { ["carrier"] = "DHL" }
        };
        var checkpoint = WorkflowRunCheckpoint.Build(
            outputs, TestPrograms.Untyped, "next", Guid.NewGuid(), Guid.NewGuid(), "start", null, null, RunStateWriteMode.EveryBlock, null, 1 << 20);

        WorkflowRunCheckpoint.Variables(checkpoint).Should().BeEquivalentTo(
            new Dictionary<string, object?> { ["who"] = "alice", ["carrier"] = "DHL" });
        WorkflowRunCheckpoint.Variables("{\"who\":\"bob\"}").Should().BeEquivalentTo(
            new Dictionary<string, object?> { ["who"] = "bob" }, "a row with no checkpoint of its own holds the flattened scope itself");
    }


    private static int Finished(WorkflowContinuationFrame frame, string subCanvas) =>
        frame.CompletedBySubCanvas.TryGetValue(subCanvas, out var calls) ? calls.Count : 0;

    [Fact]
    public async Task A_block_inside_a_container_that_re_enters_leaves_a_frame_saying_where_in_it_the_run_is()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundHold)
        {
            LoopIterations = 3,
            HoldsAtEntry = 2
        };
        using var stop = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, null, cancellationToken: stop.Token);
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var row = world.RunState.Row(world.RunState.LastBegun!.Value);
        var checkpoint = WorkflowRunCheckpoint.Read(row.VariablesJson)!;

        checkpoint.NodeInFlight.Should().Be("loop", "the root position is still the container the run is inside");
        checkpoint.ContainerInFlightRepeatsWhole.Should().BeFalse("the frame says where in it the run is");
        var frames = WorkflowRunCheckpoint.Continuation(checkpoint);
        frames.Should().ContainSingle().Which.SubCanvas.Should().Be("Body");
        var frame = frames.Single();
        frame.NodeInFlight.Should().Be(RunLifecycleWorld.HoldNodeId);
        Finished(frame, "Body").Should().Be(1, "the first iteration finished before this one started");
        PersistedValueCodec.ForDisplay(frame.PreviousOutputsJson).Keys
            .Should().BeEquivalentTo(["in"], "the body's input node has run and the hold has not");

        world.Release.TrySetResult();
        await run;
    }

    [Fact]
    public async Task A_live_loop_snapshot_keeps_only_calls_completed_when_it_was_taken()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopAroundHold)
        {
            LoopIterations = 5,
            HoldsAtEntry = 3
        };
        using var stop = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, null, cancellationToken: stop.Token);
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var captured = world.CapturedLiveCompleted;
        captured.Should().NotBeNull();
        captured!["Body"].Select(call => call.OutputsJson).Should().Equal(["{}", "{}"],
            "the live frame at the third call starts after exactly two calls completed");

        world.Release.TrySetResult();
        await run;

        captured["Body"].Select(call => call.OutputsJson).Should().Equal(["{}", "{}"],
            "later body calls append to new immutable histories rather than changing this live frame");
    }

    [Fact]
    public async Task A_call_that_reported_an_error_is_recorded_with_it_so_a_re_entry_takes_the_same_branch()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.TryThatFailsThenHolds);
        using var stop = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, null, cancellationToken: stop.Token);
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var frame = WorkflowRunCheckpoint.Continuation(
            WorkflowRunCheckpoint.Read(world.RunState.Row(world.RunState.LastBegun!.Value).VariablesJson)).Single();

        frame.SubCanvas.Should().Be("After", "the call in flight is the one the run is inside");
        var tried = frame.CompletedBySubCanvas["Try"].Should().ContainSingle().Subject;
        tried.Error.Should().NotBeNull("a try/catch that replays a Try which threw must see that it threw");
        tried.Error!.Message.Should().Contain("boom");
        (tried.Error.Code, tried.Error.Category).Should().Be(("block-failed", null), "a throw that names no code is block-failed");

        world.Release.TrySetResult();
        await run;
    }

    [Fact]
    public void A_recorded_error_keeps_its_code_and_category_through_the_checkpoint_json()
    {
        var failed = new SubCanvasError("m", "ExpressionEvaluationException", "n", "Math", "expression-runtime-error", "division-by-zero");
        var frame = new WorkflowContinuationFrame
        {
            SubCanvas = "After", NodeInFlight = "hold", PreviousOutputsJson = "{}",
            CompletedBySubCanvas = new Dictionary<string, IReadOnlyList<WorkflowCompletedSubCanvasCall>>
            {
                ["Try"] = [new WorkflowCompletedSubCanvasCall { OutputsJson = "{}", Error = WorkflowCompletedSubCanvasError.Of(failed) }]
            }
        };

        var read = WorkflowContinuation.Read(WorkflowContinuation.Write([frame])!).Single();

        var error = read.CompletedBySubCanvas["Try"].Single().Error!;
        (error.Code, error.Category).Should().Be(("expression-runtime-error", "division-by-zero"));
        error.Reported().Should().Be(failed);
    }

    [Fact]
    public void A_recorded_error_from_before_the_code_was_kept_is_replayed_as_block_failed()
    {
        const string old = """
            [{"subCanvas":"After","nodeInFlight":"hold","previousOutputsJson":"{}","completedBySubCanvas":
              {"Try":[{"outputsJson":"{}","error":{"message":"boom","exceptionType":"InvalidOperationException","nodeId":"n","blockTypeId":"Math"}}]}}]
            """;

        var error = WorkflowContinuation.Read(old).Single().CompletedBySubCanvas["Try"].Single().Error!;

        (error.Code, error.Category).Should().Be((null, null));
        error.Reported().Should().Be(new SubCanvasError("boom", "InvalidOperationException", "n", "Math", "block-failed", null));
    }

    [Fact]
    public async Task A_level_that_walked_on_past_a_failed_call_carries_no_frame_from_it()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.TryThatFailsThenHolds);
        using var stop = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, null, cancellationToken: stop.Token);
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var frames = WorkflowRunCheckpoint.Continuation(
            WorkflowRunCheckpoint.Read(world.RunState.Row(world.RunState.LastBegun!.Value).VariablesJson));

        frames.Should().ContainSingle("the Try call is over; only the call the run is inside is a frame")
            .Which.NodeInFlight.Should().Be(RunLifecycleWorld.HoldNodeId);

        world.Release.TrySetResult();
        await run;
    }

    [Fact]
    public async Task A_finished_inner_call_leaves_no_frame_on_the_level_that_made_it()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.SyncInnerLoopThenHold)
        {
            LoopIterations = 2,
            TickIdempotent = true
        };
        using var stop = new CancellationTokenSource();
        var run = world.Engine(writeMode: RunStateWriteMode.BeforeNonIdempotentOnly)
            .ExecuteAsync(world.WorkflowId, null, null, cancellationToken: stop.Token);
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var frames = WorkflowRunCheckpoint.Continuation(
            WorkflowRunCheckpoint.Read(world.RunState.Row(world.RunState.LastBegun!.Value).VariablesJson));

        frames.Should().ContainSingle("the inner loop is over; the run is in the outer loop's body")
            .Which.NodeInFlight.Should().Be(RunLifecycleWorld.HoldNodeId);

        world.Release.TrySetResult();
        await run;
    }

    [Fact]
    public async Task Two_levels_of_nesting_are_two_frames_outermost_first()
    {
        var world = new RunLifecycleWorld(definitionJson: RunLifecycleWorld.LoopInsideLoop)
        {
            LoopIterations = 2,
            HoldsAtEntry = 3
        };
        using var stop = new CancellationTokenSource();
        var run = world.Engine().ExecuteAsync(world.WorkflowId, null, null, cancellationToken: stop.Token);
        await world.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var checkpoint = WorkflowRunCheckpoint.Read(world.RunState.Row(world.RunState.LastBegun!.Value).VariablesJson)!;
        var frames = WorkflowRunCheckpoint.Continuation(checkpoint);

        checkpoint.NodeInFlight.Should().Be("outer");
        frames.Should().HaveCount(2);
        frames[0].NodeInFlight.Should().Be("inner", "the container the second frame is inside is the first frame's node");
        frames[1].NodeInFlight.Should().Be(RunLifecycleWorld.HoldNodeId);
        Finished(frames[0], "Body").Should().Be(1, "the outer loop's first pass is done");
        Finished(frames[1], "Body").Should().Be(0, "the third hold is the inner loop's first of this pass");

        world.Release.TrySetResult();
        await run;
    }

    [Fact]
    public async Task A_block_inside_a_container_that_does_not_re_enter_leaves_the_root_s_checkpoint_in_the_row()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        var workflowId = world.AddRaw(System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new
                {
                    id = "box", blockTypeId = DurableSleepWorld.ContainerType,
                    subCanvases = new Dictionary<string, object>
                    {
                        ["Body"] = new
                        {
                            nodes = new object[]
                            {
                                new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                                new { id = "inside", blockTypeId = DurableSleepWorld.HoldType, position = new { x = 0, y = 0 }, configuration = new { } }
                            },
                            edges = new object[] { new { id = "in-inside", sourceNodeId = "in", targetNodeId = "inside", mappings = new { } } }
                        }
                    }, position = new { x = 0, y = 0 }, configuration = new { }
                }
            },
            edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } }
        }));
        using var engine = world.Engine();

        var run = engine.ExecuteAsync(workflowId, null, null);
        await world.Blocks.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var row = world.RunState.Rows.Values.Single();
        row.CurrentBlockPath.Should().Be("inside", "the path still says where the run is");
        WorkflowRunCheckpoint.Read(row.VariablesJson)!.NodeInFlight.Should().Be("box",
            "a recovery re-enters at the root, and the inner write must not leave it the container's local scope");

        world.Blocks.Release.TrySetResult();
        await run;
    }
}
