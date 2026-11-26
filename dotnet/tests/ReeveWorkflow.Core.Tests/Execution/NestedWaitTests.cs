using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class NestedWaitTests
{
    private const string WaitType = "test.wait";

    [Fact]
    public async Task A_wait_inside_a_loop_parks_the_run_with_the_position_it_was_in()
    {
        var world = Parking(iterations: 3);
        var workflowId = world.AddRaw(LoopAround(reenters: true));
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        var parked = world.Suspensions.SingleRow;
        parked.BlockId.Should().Be("wait", "the block that asked to wait is the block the resume answers");
        var payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(
            parked.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
        var frames = WorkflowRunCheckpoint.Continuation(payload);
        frames.Should().ContainSingle().Which.NodeInFlight.Should().Be("wait");
        frames.Single().SubCanvas.Should().Be("Body");
    }

    [Fact]
    public async Task A_run_resumed_from_a_wait_inside_a_loop_goes_on_from_that_wait_and_finishes_the_loop()
    {
        var waits = 0;
        var world = Parking(iterations: 3, onWait: () => Interlocked.Increment(ref waits) == 1);
        var workflowId = world.AddRaw(LoopAround(reenters: true, withSideAfterTheWait: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.Blocks.SideCalls.Should().Be(0, "the first pass parked before its side block");

        var row = world.Suspensions.SingleRow;
        var resumed = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);

        resumed.Should().Be(WorkflowResumeOutcome.Resumed);
        world.Blocks.SideCalls.Should().Be(3,
            "the pass that parked goes on past its wait, and the two passes after it run whole");
        waits.Should().Be(3, "the wait that parked is answered from the record and the other two passes run it");
    }

    [Theory]
    [InlineData(WorkflowSuspensionKind.WaitTemplate)]
    [InlineData(WorkflowSuspensionKind.WaitForTrigger)]
    public async Task A_late_resume_of_a_wait_the_run_has_parked_at_again_round_a_loop_is_refused(WorkflowSuspensionKind kind)
    {
        var waits = 0;
        var world = Parking(iterations: 3, onWait: () => Interlocked.Increment(ref waits) <= 2, kind: kind);
        var workflowId = world.AddRaw(LoopAround(reenters: true, withSideAfterTheWait: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var first = (await world.Suspensions.TryResolveAsync(
            world.Suspensions.SingleRow.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""",
            DateTimeOffset.UtcNow, default))!;
        (await world.Resumer(engine).ResumeFromSuspensionAsync(first, default)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.Blocks.SideCalls.Should().Be(1, "the first pass went on past its wait and the second parked at it");

        var late = await world.Resumer(engine, "replica-2").ResumeFromSuspensionAsync(first, default);

        late.Should().Be(WorkflowResumeOutcome.HeldElsewhere);
        world.Blocks.SideCalls.Should().Be(1, "the stretch after the first wait ran once, on the replica that took it");
        waits.Should().Be(2);

        var second = world.Suspensions.Rows.Single(row => row.Status == WorkflowSuspensionStatus.Suspended);
        var resumed = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                second.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);
        resumed.Should().Be(WorkflowResumeOutcome.Resumed, "the wait the run is parked on is still its to resume");
        world.Blocks.SideCalls.Should().Be(3);
    }

    [Fact]
    public async Task A_wait_below_the_root_parked_without_a_token_is_still_resumed()
    {
        var waits = 0;
        var world = Parking(iterations: 3, onWait: () => Interlocked.Increment(ref waits) == 1);
        var workflowId = world.AddRaw(LoopAround(reenters: true, withSideAfterTheWait: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var stateId = world.RunState.Rows.Keys.Single();
        world.RunState.Rows[stateId] = world.RunState.Rows[stateId] with { WaitForEventKey = null };

        var resumed = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                world.Suspensions.SingleRow.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""",
                DateTimeOffset.UtcNow, default))!,
            default);

        resumed.Should().Be(WorkflowResumeOutcome.Resumed);
        world.Blocks.SideCalls.Should().Be(3);
    }

    [Fact]
    public async Task A_trigger_wait_below_the_root_whose_swap_throws_walks_on()
    {
        var waits = 0;
        var world = Parking(
            iterations: 3, onWait: () => Interlocked.Increment(ref waits) == 1, kind: WorkflowSuspensionKind.WaitForTrigger);
        var workflowId = world.AddRaw(LoopAround(reenters: true, withSideAfterTheWait: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.RunState.FailNextFlip = SuspendFailure.AfterWrite;

        var resumed = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                world.Suspensions.SingleRow.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""",
                DateTimeOffset.UtcNow, default))!,
            default);

        resumed.Should().Be(WorkflowResumeOutcome.Resumed, "never deferred: nothing puts a trigger's wait back");
        world.Blocks.SideCalls.Should().Be(3);
    }

    [Fact]
    public async Task A_second_resume_of_one_take_below_the_root_is_held_where_the_first_took_the_row_first()
    {
        var waits = 0;
        var world = Parking(iterations: 3, onWait: () => Interlocked.Increment(ref waits) == 1);
        var workflowId = world.AddRaw(LoopAround(reenters: true, withSideAfterTheWait: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var taken = (await world.Suspensions.TryResolveAsync(
            world.Suspensions.SingleRow.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""",
            DateTimeOffset.UtcNow, default))!;
        var stateId = world.RunState.Rows.Keys.Single();
        world.RunState.AfterRead = async _ =>
        {
            world.RunState.AfterRead = null;
            (await world.RunState.TransitionAsync(new WorkflowRunStateTransition
            {
                ExecutionId = stateId,
                FromStatus = WorkflowRunStateStatus.Suspended,
                ToStatus = WorkflowRunStateStatus.Running,
                CurrentBlockPath = "wait",
                VariablesJson = "{}",
                WaitForEventKey = WorkflowDurableSleep.TakenToken(taken.SuspensionId),
                OwnedByReplicaId = "replica-1",
                ExpectedWaitForEventKey = WorkflowDurableSleep.ParkToken(taken.SuspensionId)
            }, default)).Should().BeTrue();
        };

        (await world.Resumer(engine).ResumeFromSuspensionAsync(taken, default))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere);
        world.Blocks.SideCalls.Should().Be(0, "the segment is the first resume's to walk");
    }

    [Fact]
    public async Task A_wait_inside_a_container_that_does_not_re_enter_is_still_refused_and_names_that_container()
    {
        var world = Parking(iterations: 1);
        var workflowId = world.AddRaw(LoopAround(reenters: false));
        using var engine = world.Engine();

        var refusal = await engine.Awaiting(e => e.ExecuteAsync(workflowId, null, null))
            .Should().ThrowAsync<SubCanvasRethrowException>();

        world.Suspensions.Rows.Should().BeEmpty("nothing can put this run back where it was");
        refusal.Which.Message.Should().Contain("box",
            "the refusal names the container that cannot be re-entered, not just the canvas depth");
        refusal.Which.Message.Should().Contain("only drives its sub-canvases",
            "an author told to move the wait cannot tell which block is in the way");
    }

    [Fact]
    public async Task A_wait_inside_a_container_whose_failures_are_swallowed_parks_rather_than_being_swallowed()
    {
        var world = Parking(iterations: 1);
        var workflowId = world.AddRaw(ContinuePast());
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        world.Suspensions.Rows.Should().ContainSingle("the run is waiting, not finished");
        world.Blocks.AfterInputs.Should().BeEmpty("the block after the container runs when the container is done");
    }

    [Fact]
    public async Task A_container_cleaning_up_while_the_run_parks_does_not_run_its_cleanup_twice()
    {
        var waits = 0;
        var world = Parking(iterations: 1, onWait: () => Interlocked.Increment(ref waits) == 1);
        var workflowId = world.AddRaw(CleaningUpAround());
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        world.Blocks.CleanupsAttempted.Should().Be(1, "the container did reach its cleanup on the way out");
        world.Blocks.SideCalls.Should().Be(0, "a run on its way to being parked runs nothing more");

        var row = world.Suspensions.SingleRow;
        var resumed = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);

        resumed.Should().Be(WorkflowResumeOutcome.Resumed);
        world.Blocks.SideCalls.Should().Be(1, "the cleanup the park did not run is the resume's to run, once");
    }

    [Fact]
    public async Task A_run_started_inside_a_container_parks_with_its_own_position_and_not_its_caller_s()
    {
        var world = Parking(iterations: 1);
        using var engine = world.Engine();
        var childId = world.AddRaw(LoopAround(reenters: true));
        world.Blocks.Extra = Extending(world.Blocks.Extra, id => id == SubRunType
            ? new AsyncLambdaFactory(id, async (context, token) =>
            {
                await engine.ExecuteAsync(childId, null, null, parentExecutionId: context.ExecutionId, cancellationToken: token);
                return new Dictionary<string, object?>();
            })
            : null);
        var parentId = world.AddRaw(LoopAroundSubRun());

        await engine.ExecuteAsync(parentId, null, null);

        var payload = JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(
            world.Suspensions.SingleRow.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
        var frames = WorkflowRunCheckpoint.Continuation(payload);
        frames.Should().ContainSingle("the child was one container deep in a workflow of its own");
        frames.Single().NodeInFlight.Should().Be("wait", "and that container is the one the child's own wait is in");
    }

    [Fact]
    public async Task A_run_started_by_a_re_entered_container_does_not_replay_its_caller_s_calls_at_a_node_of_the_same_name()
    {
        var world = Parking(iterations: 1, onWait: () => Interlocked.Increment(ref _waits) == 2);
        world.Blocks.IterationsByNode["inner"] = 2;
        using var engine = world.Engine();
        var childId = world.AddRaw(LoopAroundSide(boxId: "inner"));
        world.Blocks.Extra = Extending(world.Blocks.Extra, id => id == SubRunType
            ? new AsyncLambdaFactory(id, async (context, token) =>
            {
                await engine.ExecuteAsync(childId, null, null, parentExecutionId: context.ExecutionId, cancellationToken: token);
                return new Dictionary<string, object?>();
            })
            : null);
        var parentId = world.AddRaw(SubRunAfterAnInnerLoop());
        await engine.ExecuteAsync(parentId, null, null);
        world.Blocks.SideCalls.Should().Be(0, "the first pass parked inside the inner loop, before the block that runs");

        var row = world.Suspensions.SingleRow;
        await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);

        world.Blocks.SideCalls.Should().Be(2,
            "the run the block started walks both of its own iterations, neither of them answered from its caller");
    }

    [Fact]
    public async Task A_container_that_makes_fewer_calls_than_the_checkpoint_records_fails_the_run()
    {
        var world = Parking(iterations: 3, onWait: () => Interlocked.Increment(ref _waits) == 3);
        var workflowId = world.AddRaw(LoopAround(reenters: true, withSideAfterTheWait: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        world.Blocks.IterationsByNode["box"] = 1;
        var row = world.Suspensions.SingleRow;
        await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);

        var log = world.Journal.Runs.Values.Single();
        log.Status.Should().Be(WorkflowExecutionStatus.Failed,
            "a run whose position was never reached did not do what the checkpoint says it was doing");
        log.ErrorMessage.Should().Contain("box", "the run is failed at the container that did not come back for its position");
        world.Blocks.SideCalls.Should().Be(2, "the two passes the first run finished, and no pass the replay invented");
    }

    [Fact]
    public async Task A_parked_run_whose_position_will_not_read_is_failed_rather_than_parked_again()
    {
        var world = Parking(iterations: 1);
        var workflowId = world.AddRaw(LoopAround(reenters: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        world.Suspensions.Corrupt(row.Id, payload => payload.Replace(
            "\"ContinuationJson\":", "\"ContinuationJson\":\"not json at all\",\"WasContinuationJson\":"));

        var outcome = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);

        outcome.Should().Be(WorkflowResumeOutcome.Abandoned, "there is nothing to put this run back into");
        world.Suspensions.Rows.Should().ContainSingle("the run is not parked on the same block a second time");
        world.Journal.Runs.Values.Single().Status.Should().Be(WorkflowExecutionStatus.Failed);
    }

    [Theory]
    [InlineData("frame", "frame, node 'ghost'")]
    [InlineData("call", "call, node")]
    [InlineData("last", "frame, node 'ghost'")]
    [InlineData("text", "frame, node")]
    public async Task A_parked_run_whose_position_does_not_read_is_abandoned(string corrupt, string where)
    {
        var waits = 0;
        var world = Parking(iterations: 2, onWait: () => Interlocked.Increment(ref waits) == 2);
        var workflowId = world.AddRaw(LoopAround(reenters: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        world.Suspensions.Corrupt(row.Id, payload => WorkflowRunReentryTests.CorruptFirstFrame(payload, corrupt));

        var outcome = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);

        outcome.Should().Be(WorkflowResumeOutcome.Abandoned);
        waits.Should().Be(2, "no block of the run ran again");
        var log = world.Journal.Runs.Values.Single();
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().StartWith(WorkflowAbandonmentReasons.ResumeValueInvalid).And.Contain(where);
    }

    [Fact]
    public async Task A_run_resumed_inside_a_catch_branch_is_told_the_call_failed_and_does_not_take_the_other_branch()
    {
        var world = Parking(iterations: 1);
        var workflowId = world.AddRaw(CatchAroundAWait());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.Blocks.SideCalls.Should().Be(0, "the first pass parked on the wait inside Catch");

        var row = world.Suspensions.SingleRow;
        await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);

        world.Journal.Runs.Values.Single().Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.SideCalls.Should().Be(1, "the resume went on down the Catch branch its first pass was on");
        world.Blocks.AfterInputs.Should().ContainSingle("the run went on past the container, once");
    }

    [Fact]
    public async Task A_container_that_runs_two_sub_canvases_at_once_and_claims_it_only_drives_them_is_refused()
    {
        var world = Parking(iterations: 1);
        var workflowId = world.AddRaw(BothAtOnce());
        using var engine = world.Engine();

        var run = engine.ExecuteAsync(workflowId, null, null);
        await world.Blocks.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        world.Blocks.Release.TrySetResult();

        var refusal = await run.Invoking(r => r).Should().ThrowAsync<SubCanvasRethrowException>();
        refusal.Which.Message.Should().Contain("box", "the refusal names the container that declared what it is not");
        refusal.Which.Message.Should().Contain("two of its sub-canvases at once");
    }

    [Fact]
    public async Task A_second_attempt_at_a_re_entered_container_answers_for_reaching_the_position_itself()
    {
        var world = Parking(iterations: 2, onWait: () => Interlocked.Increment(ref _waits) == 2);
        world.RetryOnlyIdempotentBlocks = false;
        world.Blocks.LoopFailsOnEntry = 2;
        world.Blocks.OnLoopEntered = entry => { if (entry == 3) world.Blocks.IterationsByNode["box"] = 1; };
        var workflowId = world.AddRaw(RetryingLoopAround());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(
                row.Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", DateTimeOffset.UtcNow, default))!,
            default);

        var log = world.Journal.Runs.Values.Single();
        world.Blocks.LoopEntries.Should().Be(3, "the first pass, the attempt that failed, and the retry");
        log.Status.Should().Be(WorkflowExecutionStatus.Failed,
            "the retry replayed the call that had finished and then stopped without reaching the position");
        log.ErrorMessage.Should().Contain("fewer calls than the checkpoint records",
            "the retry answers for reaching the position itself, rather than inheriting the attempt before it");
    }

    private int _waits;

    private const string SubRunType = "test.subrun";

    private static Func<string, IWorkflowBlockFactory?> Extending(
        Func<string, IWorkflowBlockFactory?>? first, Func<string, IWorkflowBlockFactory?> second) =>
        id => first?.Invoke(id) ?? second(id);

    private static DurableSleepWorld Parking(
        int iterations, Func<bool>? onWait = null, WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitTemplate)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.LoopIterations = iterations;
        world.Blocks.Extra = id => id switch
        {
            WaitType => new LambdaFactory(id, _ => onWait is null || onWait()
                ? throw (kind == WorkflowSuspensionKind.WaitTemplate
                    ? TemplateWaits.Park()
                    : new WorkflowSuspendException(new SuspendResult(kind, "{}", DateTimeOffset.UtcNow.AddHours(1))))
                : new Dictionary<string, object?> { ["outcome"] = "satisfied" }, inputs: [TemplateWaits.ConditionPort]),
            _ => null
        };
        return world;
    }

    private static readonly Dictionary<string, object> WaitConfiguration = new() { [TemplateWaits.Input] = "expr:false" };

    private static string LoopAround(bool reenters, bool withSideAfterTheWait = false) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box",
                blockTypeId = reenters ? DurableSleepWorld.LoopType : DurableSleepWorld.ContainerType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = withSideAfterTheWait
                        ? new
                        {
                            nodes = new object[]
                            {
                                new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                                new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = WaitConfiguration },
                                new { id = "side", blockTypeId = DurableSleepWorld.SideType, position = new { x = 0, y = 0 }, configuration = new { } }
                            },
                            edges = new object[]
                            {
                                new { id = "in-wait", sourceNodeId = "in", targetNodeId = "wait", mappings = new { } },
                                new { id = "wait-side", sourceNodeId = "wait", targetNodeId = "side", mappings = new { } }
                            }
                        }
                        : (object)new
                        {
                            nodes = new object[]
                            {
                                new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                                new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = WaitConfiguration }
                            },
                            edges = new object[] { new { id = "in-wait", sourceNodeId = "in", targetNodeId = "wait", mappings = new { } } }
                        }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } }
    });

    private static string RetryingLoopAround() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.LoopType,
                configuration = new { retryEnabled = true, retryCount = 1, retryBackoffSeconds = 0 },
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = WaitConfiguration }
                        },
                        edges = new object[] { new { id = "in-wait", sourceNodeId = "in", targetNodeId = "wait", mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }
            }
        },
        edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } }
    });

    private static string BothAtOnce() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.BothAtOnceType,
                subCanvases = new Dictionary<string, object>
                {
                    ["First"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "fin", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "hold", blockTypeId = DurableSleepWorld.HoldType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "fin-hold", sourceNodeId = "fin", targetNodeId = "hold", mappings = new { } } }
                    },
                    ["Second"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "sin", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "count", blockTypeId = DurableSleepWorld.SideType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "sin-count", sourceNodeId = "sin", targetNodeId = "count", mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } }
    });

    private static string CatchAroundAWait() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.CatchType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Try"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "tin", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "boom", blockTypeId = DurableSleepWorld.BoomType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "tin-boom", sourceNodeId = "tin", targetNodeId = "boom", mappings = new { } } }
                    },
                    ["Catch"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "cin", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = WaitConfiguration },
                            new { id = "recover", blockTypeId = DurableSleepWorld.SideType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[]
                        {
                            new { id = "cin-wait", sourceNodeId = "cin", targetNodeId = "wait", mappings = new { } },
                            new { id = "wait-recover", sourceNodeId = "wait", targetNodeId = "recover", mappings = new { } }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } },
            new { id = "box-after", sourceNodeId = "box", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        }
    });

    private static string LoopAroundSide(string boxId) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = boxId, blockTypeId = DurableSleepWorld.LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "cin", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "count", blockTypeId = DurableSleepWorld.SideType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "cin-count", sourceNodeId = "cin", targetNodeId = "count", mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = boxId, mappings = new { } } }
    });

    private static string SubRunAfterAnInnerLoop() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "outer", blockTypeId = DurableSleepWorld.LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new
                            {
                                id = "inner", blockTypeId = DurableSleepWorld.LoopType,
                                subCanvases = new Dictionary<string, object>
                                {
                                    ["Body"] = new
                                    {
                                        nodes = new object[]
                                        {
                                            new { id = "in2", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                                            new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = WaitConfiguration }
                                        },
                                        edges = new object[] { new { id = "in2-wait", sourceNodeId = "in2", targetNodeId = "wait", mappings = new { } } }
                                    }
                                }, position = new { x = 0, y = 0 }, configuration = new { }
                            },
                            new { id = "post", blockTypeId = SubRunType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[]
                        {
                            new { id = "in-inner", sourceNodeId = "in", targetNodeId = "inner", mappings = new { } },
                            new { id = "inner-post", sourceNodeId = "inner", targetNodeId = "post", mappings = new { } }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-outer", sourceNodeId = "start", targetNodeId = "outer", mappings = new { } } }
    });

    private static string LoopAroundSubRun() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "outer", blockTypeId = DurableSleepWorld.LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "subrun", blockTypeId = SubRunType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "in-subrun", sourceNodeId = "in", targetNodeId = "subrun", mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-outer", sourceNodeId = "start", targetNodeId = "outer", mappings = new { } } }
    });

    private static string CleaningUpAround() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.FinallyType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Try"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = WaitConfiguration }
                        },
                        edges = new object[] { new { id = "in-wait", sourceNodeId = "in", targetNodeId = "wait", mappings = new { } } }
                    },
                    ["Finally"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "cin", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "cleanup", blockTypeId = DurableSleepWorld.SideType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "cin-cleanup", sourceNodeId = "cin", targetNodeId = "cleanup", mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } }
    });

    private static string ContinuePast() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.LoopType,
                errorPolicy = new { policy = nameof(NodeErrorPolicy.ContinueWithDefault) },
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = WaitConfiguration }
                        },
                        edges = new object[] { new { id = "in-wait", sourceNodeId = "in", targetNodeId = "wait", mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } },
            new { id = "box-after", sourceNodeId = "box", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        }
    });
    [Fact]
    public async Task A_sub_canvas_output_that_will_not_serialise_costs_the_position_and_not_the_run()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        var cyclic = new Knot();
        cyclic.Self = cyclic;
        world.CompilationOptions = UnwritableOptions;
        world.Blocks.ContainerOutput = PortValueType.Of("Knot");
        world.Blocks.Extra = id => id == "test.knot"
            ? new LambdaFactory(id, _ => new Dictionary<string, object?> { ["out"] = cyclic }, outputs: [LambdaFactory.Port("out", PortValueType.Of("Knot"))])
            : null;
        using var engine = world.Engine();

        var outputs = await engine.ExecuteAsync(world.AddRaw(KnotInABox()), null, null);

        outputs.Should().NotBeNull("the run finished; only its position could not be recorded");
        world.RunState.Rows.Values.Single().Status.Should().Be(ReeveWorkflow.Core.Hosting.WorkflowRunStateStatus.Completed);
    }

    private sealed class Knot
    {
        public Knot? Self { get; set; }
    }

    private static readonly CompilationOptions UnwritableOptions = new()
    {
        TypeRegistry = new WorkflowTypeRegistry(
        [
            Claimed("Knot", typeof(Knot)),
            Claimed("Address", typeof(Addressed)),
            Claimed("Throwing", typeof(Throwing)),
            new WorkflowTypeRecord("Plain", typeof(Plain)),
        ]),
    };

    private static WorkflowTypeRecord Claimed(string name, Type clr) => new(name, clr)
    {
        JsonTypeInfo = StrictJson.Info(clr),
        Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
    };

    private static PortValueType UnwritableTerm(string kind) =>
        PortValueType.Of(kind switch { "ip-address" => "Address", "cycle" => "Knot", Unclaimed => "Plain", _ => "Throwing" });

    private const string Unclaimed = "unclaimed";

    public static TheoryData<string> Unwritable() => new(UnwritableValues.Keys.Where(kind => kind != Unclaimed));

    internal static readonly Dictionary<string, Func<object>> UnwritableValues = new()
    {
        ["ip-address"] = () => new Addressed { Address = System.Net.IPAddress.Loopback },
        ["getter-throws-null-reference"] = () => new Throwing { Fault = () => new NullReferenceException("getter") },
        ["getter-throws-a-host-exception"] = () => new Throwing { Fault = () => new HostOwn() },
        ["cycle"] = () => { var knot = new Knot(); knot.Self = knot; return knot; },
        [Unclaimed] = () => new Plain(),
    };

    internal sealed class Throwing
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public Func<Exception>? Fault { get; init; }

        public int Value => Fault is { } make ? throw make() : 0;
    }

    internal sealed class Addressed
    {
        public System.Net.IPAddress? Address { get; set; }
    }

    internal sealed class Plain
    {
        public int Value { get; set; }
    }

    private sealed class HostOwn() : Exception("a host's own");

    [Theory]
    [MemberData(nameof(Unwritable))]
    [InlineData(Unclaimed)]
    public async Task An_output_with_no_json_form_costs_the_checkpoint_and_not_the_run(string kind)
    {
        var (outputs, tailRan, world) = await RunUnwritableAsync(UnwritableTerm(kind), UnwritableValues[kind]);

        outputs.Should().NotBeNull();
        tailRan().Should().BeTrue();
        world.RunState.Rows.Values.Single().Status.Should().Be(ReeveWorkflow.Core.Hosting.WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task A_cancellation_thrown_while_a_value_is_written_is_not_taken_for_a_value_with_no_json_form()
    {
        var cancelling = () => new Throwing { Fault = () => new OperationCanceledException("cancelled") };

        await FluentActions.Awaiting(() => RunUnwritableAsync(PortValueType.Of("Throwing"), cancelling))
            .Should().ThrowAsync<OperationCanceledException>().WithMessage("cancelled");
    }

    private static async Task<(IReadOnlyDictionary<string, object?>? Outputs, Func<bool> TailRan, DurableSleepWorld World)> RunUnwritableAsync(
        PortValueType term, Func<object> value)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        var tailRan = false;
        world.CompilationOptions = UnwritableOptions;
        world.Blocks.ContainerOutput = term;
        world.Blocks.Extra = id => id switch
        {
            "test.x" => new LambdaFactory(id, _ => new Dictionary<string, object?> { ["out"] = value() }, outputs: [LambdaFactory.Port("out", term)]),
            "test.tail" => new LambdaFactory(id, _ => { tailRan = true; return new Dictionary<string, object?>(); }),
            "test.inner" => new LambdaFactory(id, _ => new Dictionary<string, object?>()),
            _ => null
        };
        using var engine = world.Engine();

        var outputs = await engine.ExecuteAsync(world.AddRaw(UnwritableThroughEveryWrite()), null, null);

        return (outputs, () => tailRan, world);
    }

    private static string UnwritableThroughEveryWrite() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "src", blockTypeId = "test.x", position = new { x = 0, y = 0 }, configuration = new { } },
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
                            new { id = "x", blockTypeId = "test.x", position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "out", blockTypeId = WorkflowGraphHelpers.WorkflowOutputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[]
                        {
                            new { id = "b1", sourceNodeId = "in", targetNodeId = "x", mappings = new { } },
                            new
                            {
                                id = "b2", sourceNodeId = "x", targetNodeId = "out",
                                mappings = DurableSleepWorld.Direct("out", "out")
                            }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            },
            new
            {
                id = "loop", blockTypeId = DurableSleepWorld.LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "x", blockTypeId = "test.x", position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "inner", blockTypeId = "test.inner", position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[]
                        {
                            new { id = "l1", sourceNodeId = "in", targetNodeId = "x", mappings = new { } },
                            new { id = "l2", sourceNodeId = "x", targetNodeId = "inner", mappings = new { } }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            },
            new { id = "tail", blockTypeId = "test.tail", position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "e1", sourceNodeId = "start", targetNodeId = "src", mappings = new { } },
            new { id = "e2", sourceNodeId = "src", targetNodeId = "box", mappings = new { } },
            new { id = "e3", sourceNodeId = "box", targetNodeId = "loop", mappings = new { } },
            new { id = "e4", sourceNodeId = "loop", targetNodeId = "tail", mappings = new { } }
        }
    });

    private static string KnotInABox() => JsonSerializer.Serialize(new
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
                            new { id = "knot", blockTypeId = "test.knot", position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "out", blockTypeId = WorkflowGraphHelpers.WorkflowOutputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[]
                        {
                            new { id = "b1", sourceNodeId = "in", targetNodeId = "knot", mappings = new { } },
                            new
                            {
                                id = "b2", sourceNodeId = "knot", targetNodeId = "out",
                                mappings = DurableSleepWorld.Direct("out", "out")
                            }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "e1", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } }
    });
    [Fact]
    public async Task A_container_that_calls_one_sub_canvas_many_times_at_once_still_finishes()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        var ran = 0;
        world.Blocks.Extra = id => id == "test.tally"
            ? new LambdaFactory(id, _ =>
            {
                Interlocked.Increment(ref ran);
                return new Dictionary<string, object?>();
            })
            : null;
        using var engine = world.Engine();

        await engine.ExecuteAsync(world.AddRaw(FanOut()), null, null);

        ran.Should().Be(DurableSleepWorld.FanOutCalls, "every call ran, and none of them fell over the record");
        world.RunState.Rows.Values.Single().Status.Should()
            .Be(ReeveWorkflow.Core.Hosting.WorkflowRunStateStatus.Completed);
    }

    private static string FanOut() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "fan", blockTypeId = DurableSleepWorld.FanOutType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "tally", blockTypeId = "test.tally", position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "b1", sourceNodeId = "in", targetNodeId = "tally", mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "e1", sourceNodeId = "start", targetNodeId = "fan", mappings = new { } } }
    });
}
