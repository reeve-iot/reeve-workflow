using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class SubRunOutcomeTests
{
    private const string WaitType = "test.wait";
    private const string SubRunType = "test.subrun";

    [Fact]
    public async Task A_run_that_parks_says_so_to_whoever_started_it()
    {
        var world = Parking();
        var workflowId = world.AddRaw(OneWait());
        using var engine = world.Engine();

        var outcome = new WorkflowSubRunOutcome();
        using (WorkflowSubRunOutcomeScope.Push(outcome))
            await engine.ExecuteAsync(workflowId, null, null);

        outcome.Reported.Should().BeTrue();
        outcome.Parked.Should().BeTrue();
        outcome.ExecutionLogId.Should().Be(world.Journal.Single(workflowId).Id,
            "what a waiter waits on is the run's own entry");
    }

    [Fact]
    public async Task A_run_that_reaches_an_ending_says_that_instead()
    {
        var world = Parking();
        var workflowId = world.AddRaw(NoWait());
        using var engine = world.Engine();

        var outcome = new WorkflowSubRunOutcome();
        using (WorkflowSubRunOutcomeScope.Push(outcome))
            await engine.ExecuteAsync(workflowId, null, null);

        outcome.Reported.Should().BeTrue();
        outcome.Parked.Should().BeFalse();
    }

    [Fact]
    public async Task A_single_run_admission_skips_because_one_is_in_flight_says_it_was_not_started_and_why()
    {
        var world = new DurableSleepWorld();
        var workflowId = world.AddWorkflow(1, WorkflowMode.Single, holdFirst: true);
        using var engine = world.Engine();
        var inFlight = engine.ExecuteAsync(workflowId, null, null);
        var outcome = new WorkflowSubRunOutcome();
        try
        {
            await world.Blocks.HoldReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using (WorkflowSubRunOutcomeScope.Push(outcome))
                await engine.ExecuteAsync(workflowId, null, null);
        }
        finally
        {
            world.Blocks.Release.TrySetResult();
        }
        await inFlight.WaitAsync(TimeSpan.FromSeconds(30));

        outcome.RefusedBecause.Should().Contain("single_mode_already_running");
        outcome.Reported.Should().BeFalse("no run started, so there is no execution-log id to hand anyone");
    }

    [Fact]
    public async Task A_run_refused_by_safe_mode_says_it_was_not_started_and_why()
    {
        var world = new DurableSleepWorld { ExecutionSuspended = true };
        var workflowId = world.AddRaw(NoWait());
        using var engine = world.Engine();

        var outcome = new WorkflowSubRunOutcome();
        using (WorkflowSubRunOutcomeScope.Push(outcome))
            await engine.ExecuteAsync(workflowId, null, null);

        outcome.RefusedBecause.Should().Contain("execution_suspended");
        outcome.Reported.Should().BeFalse();
        world.Journal.Runs.Should().BeEmpty("nothing of the run was started");
    }

    [Fact]
    public async Task A_run_that_reaches_an_ending_is_not_called_refused()
    {
        var world = Parking();
        var workflowId = world.AddRaw(NoWait());
        using var engine = world.Engine();

        var outcome = new WorkflowSubRunOutcome();
        using (WorkflowSubRunOutcomeScope.Push(outcome))
            await engine.ExecuteAsync(workflowId, null, null);

        outcome.RefusedBecause.Should().BeNull();
    }

    [Fact]
    public async Task A_run_a_run_starts_by_another_road_does_not_answer_for_the_run_that_was_started()
    {
        var world = Parking();
        using var engine = world.Engine();
        var innerId = world.AddRaw(NoWait());
        world.Blocks.Extra = id => id == SubRunType
            ? new AsyncLambdaFactory(id, async (context, token) =>
            {
                await engine.ExecuteAsync(innerId, null, null, parentExecutionId: context.ExecutionId, cancellationToken: token);
                return new Dictionary<string, object?>();
            })
            : id == WaitType
                ? new LambdaFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                    WorkflowSuspensionKind.WaitForTrigger, "{}", DateTimeOffset.UtcNow.AddHours(1))))
                : null;
        var outerId = world.AddRaw(SubRunThenWait());

        var outcome = new WorkflowSubRunOutcome();
        using (WorkflowSubRunOutcomeScope.Push(outcome))
            await engine.ExecuteAsync(outerId, null, null);

        outcome.Parked.Should().BeTrue("the run the holder was put in front of is the one that parked");
        outcome.ExecutionLogId.Should().Be(world.Journal.Single(outerId).Id);
    }

    [Fact]
    public async Task A_run_resumed_from_a_child_runs_that_block_again_with_the_child_s_answer()
    {
        var world = Parking();
        var seen = new List<WorkflowSubRunAnswer?>();
        var starts = 0;
        world.Blocks.Extra = id => id == SubRunType
            ? new LambdaFactory(id, _ =>
            {
                var answer = WorkflowSubRunResumeScope.TakeFor("subrun");
                seen.Add(answer);
                if (answer is null)
                {
                    starts++;
                    throw new WorkflowSuspendException(new SuspendResult(
                        WorkflowSuspensionKind.SubWorkflow,
                        JsonSerializer.Serialize(new { ChildExecutionLogId = Child }),
                        DateTimeOffset.MaxValue));
                }

                if (!answer.Succeeded)
                    throw new InvalidOperationException(answer.Error ?? "the run it started did not succeed");
                return new Dictionary<string, object?> { ["ran"] = answer.OutputsJson };
            })
            : null;
        var workflowId = world.AddRaw(OneSubRun());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        row.Kind.Should().Be(WorkflowSuspensionKind.SubWorkflow);
        var resumed = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
                WorkflowSubRunOutcomes.Resolved(Child, succeeded: true, """{"total":7}""", null),
                DateTimeOffset.UtcNow, default))!,
            default);

        resumed.Should().Be(WorkflowResumeOutcome.Resumed);
        starts.Should().Be(1, "the resumed pass is handed the answer instead of starting the run again");
        seen.Should().HaveCount(2);
        seen[1]!.OutputsJson.Should().Be("""{"total":7}""");
        world.Blocks.AfterInputs.Should().ContainSingle("the run went on past the block");
    }

    [Fact]
    public async Task A_parent_whose_child_left_no_result_is_abandoned_and_never_answered_from_the_execution_log()
    {
        var world = Parking();
        var answered = 0;
        world.Blocks.Extra = id => id == SubRunType
            ? new LambdaFactory(id, _ =>
            {
                if (WorkflowSubRunResumeScope.TakeFor("subrun") is not null)
                    answered++;
                else
                    throw new WorkflowSuspendException(new SuspendResult(WorkflowSuspensionKind.SubWorkflow,
                        JsonSerializer.Serialize(new { ChildExecutionLogId = Child }), DateTimeOffset.MaxValue));
                return new Dictionary<string, object?>();
            })
            : null;
        var workflowId = world.AddRaw(OneSubRun());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        var resumed = await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
                WorkflowSubRunOutcomes.Resolved(Child, succeeded: true, null, null), DateTimeOffset.UtcNow, default))!,
            default);

        resumed.Should().Be(WorkflowResumeOutcome.Abandoned, "a parent whose child's raw result is missing is abandoned");
        answered.Should().Be(0);
        world.Journal.Single(workflowId).ErrorMessage.Should().StartWith(WorkflowAbandonmentReasons.ResumeValueInvalid);
    }

    [Fact]
    public async Task A_child_that_did_not_succeed_fails_the_block_and_the_node_s_error_policy_sees_it()
    {
        var world = Parking();
        world.Blocks.Extra = id => id == SubRunType
            ? new LambdaFactory(id, _ =>
            {
                var answer = WorkflowSubRunResumeScope.TakeFor("subrun");
                if (answer is null)
                {
                    throw new WorkflowSuspendException(new SuspendResult(
                        WorkflowSuspensionKind.SubWorkflow,
                        JsonSerializer.Serialize(new { ChildExecutionLogId = Child }),
                        DateTimeOffset.MaxValue));
                }

                if (!answer.Succeeded)
                    throw new InvalidOperationException(answer.Error ?? "the run it started did not succeed");
                return new Dictionary<string, object?>();
            })
            : null;
        var workflowId = world.AddRaw(OneSubRun(continuePast: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
                WorkflowSubRunOutcomes.Resolved(Child, succeeded: false, null, "the child threw"),
                DateTimeOffset.UtcNow, default))!,
            default);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded,
            "the node says to carry on with default outputs, and it is the node that answers for the failure");
        world.Blocks.AfterInputs.Should().ContainSingle("the block after it ran, which is what that policy means");
    }

    [Fact]
    public async Task A_child_s_answer_that_will_not_read_fails_the_resume_rather_than_carrying_the_run_past_the_block()
    {
        var world = Parking();
        var ran = 0;
        world.Blocks.Extra = id => id == SubRunType
            ? new LambdaFactory(id, _ =>
            {
                ran++;
                var answer = WorkflowSubRunResumeScope.TakeFor("subrun");
                if (answer is null)
                {
                    throw new WorkflowSuspendException(new SuspendResult(
                        WorkflowSuspensionKind.SubWorkflow,
                        JsonSerializer.Serialize(new { ChildExecutionLogId = Child }),
                        DateTimeOffset.MaxValue));
                }

                return new Dictionary<string, object?>();
            })
            : null;
        var workflowId = world.AddRaw(OneSubRun());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
                "this is not an outcome", DateTimeOffset.UtcNow, default))!,
            default);

        ran.Should().Be(1, "the block ran once, when it parked, and the resume never reached it");
        world.Blocks.AfterInputs.Should().BeEmpty("nothing carried the run past the block");
        var log = world.Journal.Single(workflowId);
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().Contain("run it started",
            "the run is failed where it is, naming what could not be read, rather than finished on a lie");
    }

    [Fact]
    public async Task A_second_attempt_at_a_resumed_segment_is_handed_the_child_s_answer_again()
    {
        var world = Parking();
        var answered = 0;
        var started = 0;
        var boom = 0;
        world.Blocks.Extra = id => id == SubRunType
            ? new LambdaFactory(id, _ =>
            {
                var answer = WorkflowSubRunResumeScope.TakeFor("subrun");
                if (answer is null)
                {
                    started++;
                    throw new WorkflowSuspendException(new SuspendResult(
                        WorkflowSuspensionKind.SubWorkflow,
                        JsonSerializer.Serialize(new { ChildExecutionLogId = Child }),
                        DateTimeOffset.MaxValue));
                }

                answered++;
                return new Dictionary<string, object?> { ["ran"] = answer.OutputsJson };
            })
            : id == DurableSleepWorld.AfterType
                ? new LambdaFactory(id, _ => ++boom == 1
                    ? throw new InvalidOperationException("the block after it threw, once")
                    : new Dictionary<string, object?>())
                : null;
        var workflowId = world.AddRaw(
            OneSubRun(), retryPolicyJson: """{"MaxAttempts":2,"BackoffStrategy":"fixed","InitialDelayMs":1}""");
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        await world.Resumer(engine).ResumeFromSuspensionAsync(
            (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
                WorkflowSubRunOutcomes.Resolved(Child, succeeded: true, """{"total":7}""", null),
                DateTimeOffset.UtcNow, default))!,
            default);

        boom.Should().Be(2, "the block after it threw once and ran again on the second attempt");
        started.Should().Be(1, "the child was started once, before the run ever parked");
        answered.Should().Be(2, "and every attempt at the resumed segment is handed what that child returned");
    }

    private static readonly Guid Child = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static string OneSubRun(bool continuePast = false) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            continuePast
                ? new
                {
                    id = "subrun", blockTypeId = SubRunType,
                    errorPolicy = new { policy = nameof(NodeErrorPolicy.ContinueWithDefault) }, position = new { x = 0, y = 0 }, configuration = new { }
                }
                : (object)new { id = "subrun", blockTypeId = SubRunType, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "start-subrun", sourceNodeId = "start", targetNodeId = "subrun", mappings = new { } },
            new { id = "subrun-after", sourceNodeId = "subrun", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        }
    });

    private static DurableSleepWorld Parking()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.Extra = id => id == WaitType
            ? new LambdaFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitForTrigger, "{}", DateTimeOffset.UtcNow.AddHours(1))))
            : null;
        return world;
    }

    private static string OneWait() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[] { new { id = "start-wait", sourceNodeId = "start", targetNodeId = "wait", mappings = new { } } }
    });

    private static string NoWait() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "start-after", sourceNodeId = "start", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        }
    });

    private static string SubRunThenWait() => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "subrun", blockTypeId = SubRunType, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "wait", blockTypeId = WaitType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "start-subrun", sourceNodeId = "start", targetNodeId = "subrun", mappings = new { } },
            new { id = "subrun-wait", sourceNodeId = "subrun", targetNodeId = "wait", mappings = new { } }
        }
    });
}
