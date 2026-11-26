using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowSuspensionWakeNowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_wake_for_a_ready_row_takes_it_and_re_enters_its_run()
    {
        var row = Template("1 == 1");
        var world = new World(row);

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        took.Should().BeTrue();
        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        world.Resumer.Resumed.Should().ContainSingle()
            .Which.OutcomeJson.Should().Contain(WorkflowWaitOutcomes.Satisfied);
    }

    [Fact]
    public async Task The_second_wake_for_one_row_takes_nothing_because_the_take_is_what_elects()
    {
        var row = Template("1 == 1");
        var world = new World(row) { TakenByAPeer = true };

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId,
                Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        took.Should().BeFalse();
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wake_the_store_cannot_find_a_row_for_answers_false_rather_than_throwing()
    {
        var world = new World();

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = Guid.NewGuid(),
                Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        took.Should().BeFalse();
    }

    [Fact]
    public async Task A_store_that_does_not_answer_the_read_makes_a_wake_a_no_op()
    {
        var row = Template("1 == 1");
        var waker = new WorkflowSuspensionWaker(
            new StoreWithoutTheRead(new World(row).Store), new RecordingResumer(), NullLogger.Instance, ParkedTemplates.Compiler());

        var took = await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        took.Should().BeFalse();
    }

    [Fact]
    public async Task A_wake_that_names_no_row_at_all_is_refused_at_the_seam()
    {
        var world = new World();

        var refusing = () => world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake { Reason = WorkflowSuspensionWakeReasons.Poll }, default);

        await refusing.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*names neither a suspension nor an execution*");
    }

    [Fact]
    public async Task A_wake_whose_reason_is_not_one_this_package_reads_is_refused_at_the_seam()
    {
        var row = Template("1 == 1");
        var world = new World(row);

        var refusing = () => world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake { ExecutionLogId = row.ExecutionLogId, Reason = "because" }, default);

        await refusing.Should().ThrowAsync<ArgumentException>().WithMessage("*because*");
    }

    [Theory]
    [InlineData(WorkflowSuspensionWakeReasons.HelperChange)]
    [InlineData(WorkflowSuspensionWakeReasons.Trigger)]
    [InlineData(WorkflowSuspensionWakeReasons.Poll)]
    public async Task A_wake_says_why_in_the_outcome_the_resumed_block_reads(string reason)
    {
        var row = Template("1 == 1");
        var world = new World(row);

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake { ExecutionLogId = row.ExecutionLogId, Reason = reason }, default);

        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson);
        outcome.RootElement.GetProperty("_resumeReason").GetString().Should().Be(reason);
    }

    [Theory]
    [InlineData(WorkflowSuspensionWakeReasons.HelperChange, WorkflowAuditEventKinds.ResumedByHelperChange)]
    [InlineData(WorkflowSuspensionWakeReasons.Trigger, WorkflowAuditEventKinds.ResumedByTrigger)]
    public async Task The_reason_a_wake_gave_is_the_audit_kind_the_resumed_run_records(string reason, string auditKind)
    {
        var row = Template("1 == 1");
        var world = new World(row);

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake { ExecutionLogId = row.ExecutionLogId, Reason = reason }, default);

        WorkflowResumeDecisions
            .ResolveResumeAuditKind(row.Kind, Outputs(world.Resumer.Resumed.Single().OutcomeJson))
            .Should().Be(auditKind);
    }

    [Fact]
    public async Task A_wake_for_a_trigger_past_its_deadline_audits_as_the_timeout()
    {
        var row = Parked(kind: WorkflowSuspensionKind.WaitForTrigger, timeoutAtUtc: Now.AddMinutes(-1));
        var world = new World(row);

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        WorkflowResumeDecisions
            .ResolveResumeAuditKind(row.Kind, Outputs(world.Resumer.Resumed.Single().OutcomeJson))
            .Should().Be(WorkflowAuditEventKinds.ResumedByTimeout);
    }

    private static Dictionary<string, object?> Outputs(string outcomeJson) =>
        JsonDocument.Parse(outcomeJson).RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? (object?)property.Value.GetString() : null,
            StringComparer.Ordinal);

    [Fact]
    public async Task What_the_host_sends_with_a_wake_reaches_the_outcome()
    {
        var row = Template("1 == 1");
        var world = new World(row);

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger,
                Data = new Dictionary<string, object?> { ["triggerData"] = "the door opened" }
            },
            default);

        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson);
        outcome.RootElement.GetProperty("triggerData").GetString().Should().Be("the door opened");
    }

    [Fact]
    public async Task What_the_host_sends_cannot_overwrite_the_answer_the_waker_decided()
    {
        var row = Template("1 == 1");
        var world = new World(row);

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger,
                Data = new Dictionary<string, object?>
                {
                    ["outcome"] = WorkflowWaitOutcomes.TimedOut,
                    ["_resumeReason"] = "poll"
                }
            },
            default);

        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson);
        outcome.RootElement.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);
        outcome.RootElement.GetProperty("_resumeReason").GetString()
            .Should().Be(WorkflowSuspensionWakeReasons.Trigger);
    }

    [Fact]
    public async Task A_wake_for_a_trigger_takes_it_satisfied_although_its_deadline_has_not_passed()
    {
        var row = Parked(kind: WorkflowSuspensionKind.WaitForTrigger, timeoutAtUtc: Now.AddHours(1));
        var world = new World(row);

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        took.Should().BeTrue();
        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson);
        outcome.RootElement.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);
    }

    [Fact]
    public async Task A_wake_for_a_template_that_is_still_false_takes_nothing()
    {
        var row = Template("1 == 2");
        var world = new World(row);

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.HelperChange
            },
            default);

        took.Should().BeFalse();
        world.Store.Taken.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wake_for_a_row_still_inside_the_settle_window_waits_the_window_out_and_takes_it()
    {
        var row = Template("1 == 1") with { SuspendedAtUtc = Now };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.FromMilliseconds(600) }
        };
        world.Store.AdvanceOnRead = TimeSpan.FromMilliseconds(400);

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        took.Should().BeTrue("no pass ran, so the take can only be this call waiting the window out");
        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        world.Clock.Waits.Should().Equal(TimeSpan.FromMilliseconds(200));
        world.Store.Reads.Should().Be(2);
    }

    [Fact]
    public async Task A_wake_for_a_row_stamped_ahead_of_the_store_clock_is_held_for_a_pass()
    {
        var row = Template("1 == 1") with { SuspendedAtUtc = Now.AddSeconds(30) };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(50), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();
        world.Clock.Waits.Should().BeEmpty("the requested wait would exceed the settling budget");
        world.Store.Reads.Should().Be(1, "a row further ahead than the call will wait is held without waiting");

        world.Store.Clock = Now.AddSeconds(40);
        (await waker.WakeOnceAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task A_wake_whose_wait_came_back_before_the_window_ended_waits_out_the_rest_and_takes_the_row()
    {
        var row = Parked() with { SuspendedAtUtc = Now };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.FromMilliseconds(600) }
        };
        world.Store.ClocksOnRead.Enqueue(Now.AddMilliseconds(400));
        world.Store.ClocksOnRead.Enqueue(Now.AddMilliseconds(599));
        world.Store.ClocksOnRead.Enqueue(Now.AddMilliseconds(601));

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId,
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        took.Should().BeTrue("no pass ran, so only this call can have taken the row");
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Taken);
        world.Resumer.Resumed.Should().ContainSingle().Which.SuspensionId.Should().Be(row.SuspensionId);
        world.Clock.Waits.Should().Equal(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(1));
        world.Store.Reads.Should().Be(3);
    }

    [Fact]
    public async Task A_wake_for_a_row_stamped_a_little_ahead_of_the_store_clock_waits_it_out_and_takes_the_row()
    {
        var row = Parked() with { SuspendedAtUtc = Now.AddMilliseconds(5) };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.FromSeconds(1) }
        };
        world.Store.ClocksOnRead.Enqueue(Now);
        world.Store.ClocksOnRead.Enqueue(Now.AddMilliseconds(1002));
        world.Store.ClocksOnRead.Enqueue(Now.AddMilliseconds(1006));

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId,
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        took.Should().BeTrue("no pass ran, so only this call can have taken the row");
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Taken);
    }

    [Fact]
    public async Task A_wake_waits_again_on_a_store_clock_that_has_not_moved_and_takes_the_row_once_it_ticks()
    {
        var row = Parked() with { SuspendedAtUtc = Now.AddMilliseconds(-970) };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.FromSeconds(1) }
        };
        world.Store.ClocksOnRead.Enqueue(Now);
        world.Store.ClocksOnRead.Enqueue(Now);
        world.Store.ClocksOnRead.Enqueue(Now);
        world.Store.ClocksOnRead.Enqueue(Now.AddMilliseconds(60));

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        took.Should().BeTrue("no pass ran, so only this call can have taken the row");
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Taken);
        world.Clock.Waits.Should().Equal(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        world.Store.Reads.Should().Be(4);
    }

    [Fact]
    public async Task A_wake_on_a_store_clock_that_never_moves_waits_twice_as_long_each_time_and_is_held_when_the_next_wait_would_not_fit()
    {
        var row = Parked() with { SuspendedAtUtc = Now - TimeSpan.FromMilliseconds(249.5) };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(250), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waking = world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        (await waking).Should().BeFalse();
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Held);
        world.Clock.Waits.Should().Equal(new[] { 1, 1, 2, 4, 8, 16, 32, 64, 128 }
            .Select(value => TimeSpan.FromMilliseconds(value)));
    }

    [Fact]
    public async Task A_wake_whose_store_clock_moves_between_two_stalls_waits_from_one_millisecond_again()
    {
        var row = Parked() with { SuspendedAtUtc = Now - TimeSpan.FromMilliseconds(999.5) };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromSeconds(1), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        for (var read = 0; read < 4; read++)
            world.Store.ClocksOnRead.Enqueue(Now);
        world.Store.ClocksOnRead.Enqueue(Now + TimeSpan.FromMilliseconds(0.1));

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        took.Should().BeFalse("a moved clock followed by a stall stays bounded");
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Held);
        world.Store.Taken.Should().BeEmpty();
        world.Clock.Waits.Should().Equal(new[] { 1, 1, 2, 4, 1, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512 }
            .Select(value => TimeSpan.FromMilliseconds(value)));
    }

    [Fact]
    public async Task A_wake_waits_whole_milliseconds_for_a_remainder_under_one_rather_than_reading_again_at_once()
    {
        var row = Parked() with { SuspendedAtUtc = Now - TimeSpan.FromMilliseconds(49.5) };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(50), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        world.Store.AdvanceOnRead = TimeSpan.FromTicks(1);

        var waking = world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        (await waking).Should().BeFalse();
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Held);
        world.Clock.Waits.Should().NotBeEmpty();
        world.Clock.Waits.Should().OnlyContain(wait => wait == TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task A_wake_holds_rather_than_waits_for_ever_on_a_store_clock_that_hardly_moves()
    {
        var row = Parked() with { SuspendedAtUtc = Now };
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(50), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        world.Store.AdvanceOnRead = TimeSpan.FromTicks(1);

        var waking = world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        (await waking).Should().BeFalse();
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Held);
    }

    [Fact]
    public async Task A_wake_for_a_trigger_whose_deadline_has_passed_resolves_it_timed_out()
    {
        var row = Parked(kind: WorkflowSuspensionKind.WaitForTrigger, timeoutAtUtc: Now.AddMinutes(-1));
        var world = new World(row);

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        took.Should().BeTrue();
        world.Store.Taken.Should().ContainSingle().Which.Status.Should().Be(WorkflowSuspensionStatus.TimedOut);
        var outcome = JsonDocument.Parse(world.Resumer.Resumed.Should().ContainSingle().Subject.OutcomeJson);
        outcome.RootElement.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.TimedOut);
    }

    [Fact]
    public async Task A_trigger_row_taken_by_a_wake_whose_resume_lets_a_stop_out_keeps_its_answer()
    {
        var row = Parked(kind: WorkflowSuspensionKind.WaitForTrigger, timeoutAtUtc: Now.AddHours(1));
        var world = new World(row) { Resumer = new RecordingResumer { Cancels = true } };
        using var stopping = new CancellationTokenSource();

        var waking = world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger,
                Data = new Dictionary<string, object?> { ["triggerData"] = "the door opened" }
            },
            stopping.Token);
        await world.Resumer.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.CancelAsync();

        await FluentActions.Awaiting(() => waking).Should().ThrowAsync<OperationCanceledException>();
        world.Store.PutBack.Should().BeEmpty("the answer a put-back clears is one only the host could give");
    }

    [Fact]
    public async Task A_timed_out_row_taken_by_a_wake_whose_resume_lets_a_stop_out_is_not_put_back()
    {
        var row = Parked(kind: WorkflowSuspensionKind.WaitForTrigger, timeoutAtUtc: Now.AddMinutes(-1));
        var world = new World(row) { Resumer = new RecordingResumer { Cancels = true } };
        using var stopping = new CancellationTokenSource();

        var waking = world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            stopping.Token);
        await world.Resumer.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping.CancelAsync();

        await FluentActions.Awaiting(() => waking).Should().ThrowAsync<OperationCanceledException>();
        world.Store.PutBack.Should().BeEmpty("a put-back would hand a run the resume may have entered to the next pass");
    }

    [Fact]
    public async Task A_trigger_row_taken_by_a_wake_and_deferred_keeps_its_answer()
    {
        var row = Parked(kind: WorkflowSuspensionKind.WaitForTrigger, timeoutAtUtc: Now.AddHours(1));
        var world = new World(row) { Resumer = new RecordingResumer { Defers = true } };

        (await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.Store.PutBack.Should().BeEmpty("a put-back clears an answer only the host could give");
    }

    [Fact]
    public async Task A_timed_out_row_taken_by_a_wake_and_deferred_is_put_back()
    {
        var row = Parked(kind: WorkflowSuspensionKind.WaitForTrigger, timeoutAtUtc: Now.AddMinutes(-1));
        var world = new World(row) { Resumer = new RecordingResumer { Defers = true } };

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.Store.PutBack.Should().ContainSingle().Which.Should().Be(row.SuspensionId);
    }

    [Fact]
    public async Task A_wake_whose_read_fails_is_held_and_taken_once_the_store_answers_again()
    {
        var row = Template("1 == 1");
        var world = new World(row);
        var waker = world.Waker();
        await waker.WakeOnceAsync(default);
        world.Store.FailReads = true;

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.Store.FailReads = false;
        (await waker.WakeOnceAsync(default)).Should().Be(1, "a read that failed is not the row answering no");
    }

    [Fact]
    public async Task A_held_wake_is_tried_once_before_its_hold_is_up()
    {
        var row = Template("1 == 1");
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) }
        };
        var waker = world.Waker();
        await waker.WakeOnceAsync(default);

        world.Store.FailReads = true;
        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.Store.FailReads = false;
        world.Store.Clock = Now.AddSeconds(30);
        world.Store.Candidates = [row];

        (await waker.WakeOnceAsync(default)).Should().Be(1, "nothing had read for this wake even once");
    }

    [Fact]
    public async Task A_pass_whose_read_failed_has_not_given_a_held_wake_its_chance_either()
    {
        var row = Template("1 == 1");
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) }
        };
        var waker = world.Waker();
        await waker.WakeOnceAsync(default);

        world.Store.FailReads = true;
        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.Store.Clock = Now.AddSeconds(5);
        (await waker.WakeOnceAsync(default)).Should().Be(0);

        world.Store.FailReads = false;
        world.Store.Clock = Now.AddSeconds(20);
        world.Store.Candidates = [row];

        (await waker.WakeOnceAsync(default)).Should().Be(1, "no read of this row has answered yet");
    }

    [Fact]
    public async Task The_hold_drops_the_wake_that_arrived_first_although_the_store_clock_stepped_back()
    {
        WorkflowSuspensionCandidate[] rows = [Template("1 == 1"), Template("1 == 1"), Template("1 == 1")];
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                MaxHeldWakes = 2, HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();
        DateTimeOffset[] clocks = [Now.AddSeconds(10), Now, Now.AddSeconds(20)];

        for (var i = 0; i < rows.Length; i++)
        {
            world.Store.Clock = clocks[i];
            await waker.WakeNowAsync(
                new WorkflowSuspensionWake
                {
                    SuspensionId = rows[i].SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
                },
                default);
        }

        world.Store.Candidates = rows;
        world.Store.Clock = Now.AddSeconds(21);
        await waker.WakeOnceAsync(default);

        world.Resumer.Resumed.Select(resumed => resumed.SuspensionId)
            .Should().BeEquivalentTo([rows[1].SuspensionId, rows[2].SuspensionId]);
    }

    [Fact]
    public async Task The_pass_after_reads_for_the_holds_the_one_before_had_no_budget_for()
    {
        WorkflowSuspensionCandidate[] never = [Template("1 == 1"), Template("1 == 1")];
        WorkflowSuspensionCandidate[] landing = [Template("1 == 1"), Template("1 == 1")];
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                BatchSize = 2, MaxHeldWakes = 16, HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();

        foreach (var row in never.Concat(landing))
        {
            await waker.WakeNowAsync(
                new WorkflowSuspensionWake
                {
                    SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
                },
                default);
        }

        world.Store.Candidates = landing;

        (await waker.WakeOnceAsync(default)).Should().Be(0);
        (await waker.WakeOnceAsync(default)).Should().Be(2, "the cursor moved past the two that answered nothing");
    }

    [Fact]
    public async Task A_wake_cancelled_while_it_waits_out_the_window_is_held_rather_than_lost()
    {
        var row = Template("1 == 1") with { SuspendedAtUtc = Now };
        var world = new World(row)
        {
            Clock = new AdvancingClock { FiresTimers = false },
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromSeconds(5), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();
        using var stopping = new CancellationTokenSource();

        var waking = waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            stopping.Token);
        world.Clock.Waits.Should().Equal(new[] { TimeSpan.FromSeconds(5) });
        world.Store.Reads.Should().Be(1);
        await stopping.CancelAsync();

        (await waking).Should().BeFalse();
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Held);

        world.Store.Clock = Now.AddSeconds(10);
        (await waker.WakeOnceAsync(default)).Should().Be(1, "the wake the stop interrupted is still held");
    }

    [Fact]
    public async Task A_wake_that_arrives_while_an_older_one_is_being_tried_is_not_forgotten_with_it()
    {
        var row = Template("1 == 1");
        var world = new World();
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        world.Store.Candidates = [row];
        Task<bool>? fired = null;
        world.Store.OnRead = () =>
        {
            world.Store.OnRead = null;
            world.Store.Candidates = [];
            world.Store.TakenByAPeer = true;
            fired = waker.WakeNowAsync(
                new WorkflowSuspensionWake
                {
                    SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
                },
                default);
        };

        (await waker.WakeOnceAsync(default)).Should().Be(0);
        (await fired!).Should().BeFalse();

        world.Store.Candidates = [row];
        world.Store.TakenByAPeer = false;

        (await waker.WakeOnceAsync(default)).Should().Be(1, "the trigger wake is held, not forgotten with the poll");
    }

    [Fact]
    public async Task A_wake_published_again_while_it_waits_holds_from_the_newer_one()
    {
        var row = Template("1 == 1");
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) }
        };
        var waker = world.Waker();
        var wake = new WorkflowSuspensionWake
        {
            SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
        };

        await waker.WakeNowAsync(wake, default);
        world.Store.Clock = Now.AddSeconds(6);
        await waker.WakeNowAsync(wake, default);

        world.Store.Clock = Now.AddSeconds(11);
        world.Store.Candidates = [row];

        (await waker.WakeOnceAsync(default)).Should().Be(1, "the hold runs from the wake that is actually held");
    }

    [Fact]
    public async Task One_pass_reads_for_no_more_held_wakes_than_its_batch_size()
    {
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions { BatchSize = 3, MaxHeldWakes = 16 }
        };
        var waker = world.Waker();

        for (var i = 0; i < 10; i++)
        {
            await waker.WakeNowAsync(
                new WorkflowSuspensionWake
                {
                    SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger
                },
                default);
        }

        var readsBefore = world.Store.Reads;
        await waker.WakeOnceAsync(default);

        (world.Store.Reads - readsBefore).Should().Be(3);
    }


    [Fact]
    public async Task A_wake_that_arrives_before_the_park_lands_is_held_and_taken_once_it_does()
    {
        var row = Template("1 == 1");
        var world = new World();
        var waker = world.Waker();

        var took = await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        took.Should().BeFalse();

        world.Store.Candidates = [row];
        (await waker.WakeOnceAsync(default)).Should().Be(1);

        world.Resumer.Resumed.Should().ContainSingle().Which.SuspensionId.Should().Be(row.SuspensionId);
    }

    [Fact]
    public async Task A_held_wake_outlives_a_pass_at_the_default_settings()
    {
        var row = Template("1 == 1");
        var world = new World();
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.Store.Clock = Now.AddSeconds(5);
        world.Store.Candidates = [row];

        (await waker.WakeOnceAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task A_held_wake_is_dropped_once_its_hold_is_up()
    {
        var row = Template("1 == 1");
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) }
        };
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.Store.Clock = Now.AddSeconds(11);
        world.Store.Candidates = [row];

        (await waker.WakeOnceAsync(default)).Should().Be(0, "the hold was up before the pass came round to it");
        world.Store.Taken.Should().BeEmpty();
    }

    [Fact]
    public async Task The_hold_never_grows_past_its_bound_and_the_oldest_wake_is_what_goes()
    {
        WorkflowSuspensionCandidate[] rows = [Template("1 == 1"), Template("1 == 1"), Template("1 == 1")];
        var world = new World { Options = new WorkflowSuspensionWakeOptions { MaxHeldWakes = 2 } };
        var waker = world.Waker();

        foreach (var row in rows)
        {
            await waker.WakeNowAsync(
                new WorkflowSuspensionWake
                {
                    SuspensionId = row.SuspensionId,
                    Reason = WorkflowSuspensionWakeReasons.Trigger
                },
                default);
            world.Store.Clock += TimeSpan.FromSeconds(1);
        }

        world.Store.Candidates = rows;
        await waker.WakeOnceAsync(default);

        world.Resumer.Resumed.Select(resumed => resumed.SuspensionId)
            .Should().BeEquivalentTo([rows[1].SuspensionId, rows[2].SuspensionId]);
    }

    [Fact]
    public async Task A_wake_storm_does_not_stop_the_page()
    {
        var woken = Template("1 == 1");
        var sleep = Parked(kind: WorkflowSuspensionKind.Sleep, timeoutAtUtc: Now.AddMinutes(-1));
        var world = new World(woken, sleep)
        {
            Options = new WorkflowSuspensionWakeOptions { MaxConcurrentWakes = 1, MaxConcurrentResumes = 1 }
        };
        world.Store.PageCandidates = [sleep];
        world.Resumer.HoldsUp = woken.ExecutionLogId;
        var waker = world.Waker();

        var waking = waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = woken.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);
        await world.Resumer.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        (await waker.WakeOnceAsync(default)).Should().Be(1, "the sleep is overdue and the page's workers are free");

        world.Resumer.Release();
        (await waking).Should().BeTrue();
    }

    [Fact]
    public async Task A_wake_with_no_worker_free_is_held_rather_than_dropped()
    {
        var held = Parked(kind: WorkflowSuspensionKind.WaitForTrigger, timeoutAtUtc: Now.AddHours(1));
        var busy = Template("1 == 1");
        var world = new World(busy, held)
        {
            Options = new WorkflowSuspensionWakeOptions { MaxConcurrentWakes = 1 }
        };
        world.Resumer.HoldsUp = busy.ExecutionLogId;
        var waker = world.Waker();

        var waking = waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = busy.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);
        await world.Resumer.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = held.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.Resumer.Release();
        (await waking).Should().BeTrue();

        (await waker.WakeOnceAsync(default)).Should().Be(1, "the wake that found no worker waited for one");
        world.Resumer.Resumed.Select(resumed => resumed.SuspensionId).Should().Contain(held.SuspensionId);
    }

    [Fact]
    public async Task A_held_wake_that_has_been_taken_is_not_still_held()
    {
        var row = Template("1 == 1");
        var world = new World();
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.Store.Candidates = [row];
        (await waker.WakeOnceAsync(default)).Should().Be(1);
        var read = world.Store.Reads;

        (await waker.WakeOnceAsync(default)).Should().Be(0);
        world.Store.Reads.Should().Be(read, "a wake that has been answered is not read for again");
    }

    [Fact]
    public async Task A_wake_for_a_kind_this_waker_leaves_alone_is_not_held()
    {
        var row = Template("1 == 1");
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { Kinds = [WorkflowSuspensionKind.Sleep] }
        };
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        (await waker.WakeOnceAsync(default)).Should().Be(0);
        world.Store.Reads.Should().Be(1, "a refused wake is not read for again on every pass");
    }

    [Fact]
    public async Task A_wake_for_a_template_that_is_still_false_is_not_held()
    {
        var row = Template("1 == 2");
        var world = new World(row);
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.HelperChange
            },
            default);

        (await waker.WakeOnceAsync(default)).Should().Be(0);
        world.Store.Reads.Should().Be(1, "the row was read and answered, so there is nothing to come back to");
    }

    [Fact]
    public async Task A_store_that_answers_the_read_with_nothing_holds_nothing()
    {
        var row = Template("1 == 1");
        var store = new StoreWithNoWakePath(new World(row).Store);
        var waker = new WorkflowSuspensionWaker(store, new RecordingResumer(), NullLogger.Instance, ParkedTemplates.Compiler());

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        store.Reads.Should().Be(1);
        await waker.WakeOnceAsync(default);
        store.Reads.Should().Be(1, "a store with no wake path is not read again for a wake it cannot answer");
    }

    [Fact]
    public async Task A_wake_for_a_kind_this_waker_leaves_alone_takes_nothing()
    {
        var row = Template("1 == 1");
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { Kinds = [WorkflowSuspensionKind.Sleep] }
        };

        var took = await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId,
                Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        took.Should().BeFalse();
        world.Store.Taken.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wake_that_takes_its_row_is_counted_as_taken()
    {
        var row = Template("1 == 1");
        var world = new World(row);

        (await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeTrue();

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Taken]);
    }

    [Fact]
    public async Task A_wake_whose_row_answered_no_is_counted_as_not_ready()
    {
        var row = Template("1 == 2");
        var world = new World(row);

        (await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.HelperChange
            },
            default)).Should().BeFalse();

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.NotReady]);
    }

    [Fact]
    public async Task A_wake_a_peer_beat_to_the_row_is_counted_as_lost()
    {
        var row = Template("1 == 1");
        var world = new World(row) { TakenByAPeer = true };

        (await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Lost]);
    }

    [Fact]
    public async Task A_wake_whose_row_is_not_there_yet_is_counted_as_held()
    {
        var world = new World();

        (await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Held]);
    }

    [Fact]
    public async Task A_wake_a_waker_that_holds_nothing_cannot_keep_is_counted_as_dropped()
    {
        var world = new World { Options = new WorkflowSuspensionWakeOptions { MaxHeldWakes = 0 } };

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Dropped]);
    }

    [Fact]
    public async Task A_wake_whose_take_threw_is_counted_as_failed()
    {
        var row = Template("1 == 1");
        var world = new World(row);
        world.Store.FailTakes = true;

        (await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.WakeOutcomes.Should().Equal(
            [WorkflowSuspensionWakeOutcomes.Failed, WorkflowSuspensionWakeOutcomes.Held],
            "a take the store could not make is held for a later pass, not dropped");
    }

    [Fact]
    public async Task A_wake_whose_take_threw_is_taken_by_the_pass_after()
    {
        var row = Template("1 == 1");
        var world = new World(row);
        var waker = world.Waker();
        world.Store.FailTakes = true;

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.Log.Warnings.Should().Contain(warning => warning.Contains("Failed to take parked run"),
            "a store failing takes is said out loud, not only counted");

        world.Store.FailTakes = false;

        (await waker.WakeOnceAsync(default)).Should().Be(1, "the held wake is tried again, and the row is still there");

        world.WakeOutcomes.Should().Equal(
            [WorkflowSuspensionWakeOutcomes.Failed, WorkflowSuspensionWakeOutcomes.Held, WorkflowSuspensionWakeOutcomes.Taken],
            "the failure held the wake; the pass after took the row it could not take before");
    }

    [Fact]
    public async Task A_wake_whose_take_keeps_failing_is_dropped_and_written_onto_its_run()
    {
        var row = Template("1 == 1");
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) },
            Scopes = new RecordingScopes()
        };
        world.Store.FailTakes = true;
        var waker = world.Waker();

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.Store.Clock = Now.AddSeconds(11);
        (await waker.WakeOnceAsync(default)).Should().Be(0, "the hold was up before the pass came round to it");

        world.WakeOutcomes.Should().Equal(
            [
                WorkflowSuspensionWakeOutcomes.Failed, WorkflowSuspensionWakeOutcomes.Held,
                WorkflowSuspensionWakeOutcomes.Dropped
            ],
            "a take that keeps failing is retried until the hold is up and is then dropped, not retried for ever");
        world.Log.Warnings.Should().Contain(warning => warning.Contains("the event is lost"));

        var note = world.Scopes.Entries.Should().ContainSingle(
            "the run is told once, at the drop, and not once per failed take").Subject;
        note.ExecutionLogId.Should().Be(row.ExecutionLogId);
        note.Level.Should().Be(LogEntryLevel.Error, "the store failing this run's wake is an error in this run");
        note.Message.Should().Contain("wake-dropped");
        note.Message.Should().Contain(row.SuspensionId.ToString(), "the note names the wake it is about");
        note.Message.Should().Contain("the store could not take the row",
            "and the cause, which is the half that says whether to look at the store");
    }

    [Fact]
    public async Task The_note_on_a_dropped_hold_names_the_last_attempts_cause()
    {
        var row = Template("1 == 1");
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromMinutes(5) },
            Scopes = new RecordingScopes()
        };
        world.Store.FailTakes = true;
        world.Store.FailTakesWith = "the first outage";
        var waker = world.Waker();

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.Store.FailTakesWith = "the second, which is what the run is owed";
        world.Store.Clock = Now.AddSeconds(30);
        (await waker.WakeOnceAsync(default)).Should().Be(0, "the take failed again, so nothing was taken");

        world.Store.Clock = Now.AddMinutes(6);
        await waker.WakeOnceAsync(default);

        world.WakeOutcomes.Should().Equal(
            [
                WorkflowSuspensionWakeOutcomes.Failed, WorkflowSuspensionWakeOutcomes.Held,
                WorkflowSuspensionWakeOutcomes.Failed, WorkflowSuspensionWakeOutcomes.Dropped
            ],
            "a retried hold is not counted as taken again, and is counted dropped once, when it runs out");

        var note = world.Scopes.Entries.Should().ContainSingle().Subject;
        note.Level.Should().Be(LogEntryLevel.Error);
        note.Message.Should().Contain("the second, which is what the run is owed",
            "the note carries the attempt that was last, which is the one the retry made");
        note.Message.Should().NotContain("the first outage",
            "and not the words the hold was opened with, which the retry has replaced");
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_whose_take_failed_is_held_for_the_wait_it_found()
    {
        var run = Guid.NewGuid();
        var found = Parked() with { ExecutionLogId = run };
        var world = new World(found)
        {
            Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromMinutes(5) }
        };
        world.Store.FailTakes = true;
        var waker = world.Waker();

        (await waker.WakeNowAsync(DoorOpened(run), default)).Should().BeFalse();
        world.WakeOutcomes.Should().Equal(
            [WorkflowSuspensionWakeOutcomes.Failed, WorkflowSuspensionWakeOutcomes.Held]);

        world.Store.FailTakes = false;
        var next = NextTriggerWait(run, "wait-2", "window-opened");
        world.Store.Candidates = [next];
        world.Store.Clock = Now.AddSeconds(10);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Select(take => take.Id).Should().NotContain(next.SuspensionId,
            "the hold is for the wait the failed take was for, and that is not the wait the run is on now");
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wake_for_a_kind_this_waker_leaves_alone_is_counted_as_not_mine()
    {
        var row = Template("1 == 1");
        var world = new World(row)
        {
            Options = new WorkflowSuspensionWakeOptions { Kinds = [WorkflowSuspensionKind.Sleep] }
        };

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.NotMine]);
    }

    [Fact]
    public async Task A_wake_into_a_store_with_no_wake_path_is_counted_as_not_mine()
    {
        var row = Template("1 == 1");
        List<string> outcomes = [];
        var waker = new WorkflowSuspensionWaker(
            new StoreWithNoWakePath(new World(row).Store), new RecordingResumer(), NullLogger.Instance,
            ParkedTemplates.Compiler(),
            metrics: new CountingMetrics(outcomes));

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                ExecutionLogId = row.ExecutionLogId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        outcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.NotMine]);
    }

    [Fact]
    public async Task A_held_wake_is_counted_again_when_it_is_finally_answered()
    {
        var row = Template("1 == 1");
        var world = new World();
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Held]);

        world.Store.Candidates = [row];
        (await waker.WakeOnceAsync(default)).Should().Be(1);

        world.WakeOutcomes.Should().Equal(
            [WorkflowSuspensionWakeOutcomes.Held, WorkflowSuspensionWakeOutcomes.Taken],
            "the terminal outcome is the second one; the hold was the wake being kept, not answered");
    }

    [Fact]
    public async Task A_wake_that_replaces_one_already_held_counts_the_one_it_replaced_as_dropped()
    {
        var suspensionId = Guid.NewGuid();
        var world = new World();
        var waker = world.Waker();

        for (var i = 0; i < 2; i++)
            await waker.WakeNowAsync(
                new WorkflowSuspensionWake
                {
                    SuspensionId = suspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
                },
                default);

        world.WakeOutcomes.Should().Equal(
            [
                WorkflowSuspensionWakeOutcomes.Held,
                WorkflowSuspensionWakeOutcomes.Held,
                WorkflowSuspensionWakeOutcomes.Dropped
            ],
            "two wakes were held and one of those holds was displaced by the other");
    }

    [Fact]
    public async Task A_wake_that_arrives_inside_an_attempt_does_not_end_that_attempts_wake_a_second_time()
    {
        var row = Template("1 == 1");
        var world = new World();
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        world.Store.Candidates = [row];
        Task<bool>? fired = null;
        world.Store.OnRead = () =>
        {
            world.Store.OnRead = null;
            world.Store.Candidates = [];
            world.Store.TakenByAPeer = true;
            fired = waker.WakeNowAsync(
                new WorkflowSuspensionWake
                {
                    SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
                },
                default);
        };

        await waker.WakeOnceAsync(default);
        (await fired!).Should().BeFalse();

        world.WakeOutcomes.Should().Equal(
            [
                WorkflowSuspensionWakeOutcomes.Held,
                WorkflowSuspensionWakeOutcomes.Held,
                WorkflowSuspensionWakeOutcomes.Lost
            ],
            "two wakes were held; the first was finished with by the take that lost its race, and the second "
            + "is still held — so exactly one of them has ended");
    }

    [Fact]
    public async Task A_wake_replaced_while_it_was_being_read_for_is_counted_as_dropped()
    {
        var row = Template("1 == 1") with { SuspendedAtUtc = Now.AddSeconds(30) };
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(50), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Poll
            },
            default);

        world.Store.Candidates = [row];
        Task<bool>? fired = null;
        world.Store.OnRead = () =>
        {
            world.Store.OnRead = null;
            world.Store.Candidates = [];
            fired = waker.WakeNowAsync(
                new WorkflowSuspensionWake
                {
                    SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
                },
                default);
        };

        await waker.WakeOnceAsync(default);
        (await fired!).Should().BeFalse();

        world.WakeOutcomes.Should().Equal(
            [
                WorkflowSuspensionWakeOutcomes.Held,
                WorkflowSuspensionWakeOutcomes.Held,
                WorkflowSuspensionWakeOutcomes.Dropped
            ],
            "the newer wake took the hold, so the older one is a wake nobody will ever finish with");
    }

    [Fact]
    public async Task A_trigger_wake_for_a_wait_a_peer_already_took_is_counted_as_lost_and_not_held()
    {
        var suspensionId = Guid.NewGuid();
        var world = new World { Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) } };
        world.Store.Answered.Add(suspensionId);
        var waker = world.Waker();

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake { SuspensionId = suspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger },
            default)).Should().BeFalse();
        world.Store.Clock += TimeSpan.FromMinutes(1);
        await waker.WakeOnceAsync(default);

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Lost]);
        world.Store.Reads.Should().Be(1, "a wait already answered is not read for again");
        world.Log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_held_trigger_wake_whose_wait_a_peer_then_took_is_forgotten_without_the_dropped_warning()
    {
        var suspensionId = Guid.NewGuid();
        var world = new World { Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) } };
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake { SuspensionId = suspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger },
            default);
        world.Store.Answered.Add(suspensionId);
        await waker.WakeOnceAsync(default);
        world.Store.Clock += TimeSpan.FromMinutes(1);
        await waker.WakeOnceAsync(default);

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Held, WorkflowSuspensionWakeOutcomes.Lost]);
        world.Store.Reads.Should().Be(2);
        world.Log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_helper_change_wake_for_a_wait_already_taken_is_still_held()
    {
        var suspensionId = Guid.NewGuid();
        var world = new World();
        world.Store.Answered.Add(suspensionId);

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake { SuspensionId = suspensionId, Reason = WorkflowSuspensionWakeReasons.HelperChange },
            default);

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Held]);
    }

    [Fact]
    public async Task A_trigger_wake_whose_wait_never_landed_is_still_dropped_with_the_warning()
    {
        var world = new World { Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) } };
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake { SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger },
            default);
        await waker.WakeOnceAsync(default);
        world.Store.Clock += TimeSpan.FromMinutes(1);
        await waker.WakeOnceAsync(default);

        world.WakeOutcomes.Should().Equal(
            [WorkflowSuspensionWakeOutcomes.Held, WorkflowSuspensionWakeOutcomes.Dropped]);
        world.Log.Warnings.Should().ContainSingle().Which.Should().Contain("the event is lost");
    }

    [Fact]
    public async Task A_dropped_trigger_wake_says_what_it_did_rather_than_what_became_of_the_run()
    {
        var suspensionId = Guid.NewGuid();
        var world = new World { Options = new WorkflowSuspensionWakeOptions { HoldWakesFor = TimeSpan.FromSeconds(10) } };
        var waker = world.Waker();

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake { SuspensionId = suspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger },
            default);
        await waker.WakeOnceAsync(default);
        world.Store.Answered.Add(suspensionId);
        world.Store.Clock += TimeSpan.FromMinutes(1);
        await waker.WakeOnceAsync(default);

        world.WakeOutcomes.Should().Equal(
            [WorkflowSuspensionWakeOutcomes.Held, WorkflowSuspensionWakeOutcomes.Dropped]);
        world.Store.Reads.Should().Be(2, "the expiry is not a read, which is why the warning cannot know");
        var warning = world.Log.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("dropped without having taken its wait");
        warning.Should().Contain("If nothing took that wait, the event is lost");
        warning.Should().NotContain("deadline");
    }

    [Fact]
    public async Task A_wake_whose_first_read_ever_fails_is_counted_as_dropped()
    {
        var world = new World();
        world.Store.FailReads = true;

        (await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeFalse();

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Dropped],
            "held, it would be held against an instant no read ever answered with");
    }

    [Fact]
    public async Task A_wake_for_a_child_wait_a_waker_cannot_read_is_counted_as_not_mine()
    {
        var row = Parked(kind: WorkflowSuspensionKind.SubWorkflow);
        var world = new World(row);

        await world.Waker().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        world.WakeOutcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.NotMine]);
    }

    [Fact]
    public async Task A_wake_for_a_child_wait_that_names_no_child_is_counted_as_dropped()
    {
        var row = Parked(kind: WorkflowSuspensionKind.SubWorkflow);
        List<string> outcomes = [];
        var waker = new WorkflowSuspensionWaker(
            new World(row).Store, new RecordingResumer(), NullLogger.Instance,
            ParkedTemplates.Compiler(),
            scopes: new ScopesWhoseJournalThrows(), metrics: new CountingMetrics(outcomes));

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        outcomes.Should().Equal([WorkflowSuspensionWakeOutcomes.Dropped]);
    }

    [Fact]
    public async Task A_wake_whose_child_could_not_be_read_is_counted_as_failed()
    {
        var row = Parked(kind: WorkflowSuspensionKind.SubWorkflow) with
        {
            ConfigJson = JsonSerializer.Serialize(new { childExecutionLogId = Guid.NewGuid() })
        };
        List<string> outcomes = [];
        var waker = new WorkflowSuspensionWaker(
            new World(row).Store, new RecordingResumer(), NullLogger.Instance,
            ParkedTemplates.Compiler(),
            scopes: new ScopesWhoseJournalThrows(), metrics: new CountingMetrics(outcomes));

        await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default);

        outcomes.Should().Equal(
            [WorkflowSuspensionWakeOutcomes.Failed, WorkflowSuspensionWakeOutcomes.Held],
            "a take the store could not make is held for a later pass, not dropped");
    }

    [Fact]
    public async Task A_wake_whose_meter_throws_still_takes_its_row()
    {
        var row = Template("1 == 1");
        var world = new World(row);
        var resumer = new RecordingResumer();
        var waker = new WorkflowSuspensionWaker(
            world.Store, resumer, NullLogger.Instance, ParkedTemplates.Compiler(), metrics: new ThrowingMetrics(),
            scopes: ParkedTemplates.Scopes());

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            default)).Should().BeTrue();

        resumer.Resumed.Should().ContainSingle().Which.SuspensionId.Should().Be(row.SuspensionId);
    }

    [Theory]
    [InlineData("wait-1", "door-opened")]
    [InlineData("wait-2", "window-opened")]
    public async Task A_trigger_wake_naming_only_its_run_does_not_answer_a_wait_parked_after_it_found_none(
        string nextBlock, string nextTrigger)
    {
        var run = Guid.NewGuid();
        var world = new World();
        var waker = world.Waker();

        (await waker.WakeNowAsync(DoorOpened(run), default)).Should().BeFalse();

        var next = NextTriggerWait(run, nextBlock, nextTrigger);
        world.Store.Candidates = [next];
        world.Store.Clock = Now.AddSeconds(5);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Should().BeEmpty("the wait the event was for was answered before this one was parked");
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_held_on_a_settling_wait_does_not_answer_the_next_one()
    {
        var run = Guid.NewGuid();
        var settling = Parked() with { ExecutionLogId = run, SuspendedAtUtc = Now.AddSeconds(30) };
        var world = new World(settling)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(50), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();

        (await waker.WakeNowAsync(DoorOpened(run), default)).Should().BeFalse();

        var next = NextTriggerWait(run, "wait-1", "door-opened") with { SuspendedAtUtc = Now.AddSeconds(35) };
        world.Store.Candidates = [next];
        world.Store.Clock = Now.AddSeconds(40);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Should().BeEmpty("the wake was held for the wait its read found, which a peer has answered");
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_held_for_a_worker_does_not_answer_the_next_wait()
    {
        var run = Guid.NewGuid();
        var busy = Template("1 == 1");
        var world = new World(busy, Parked() with { ExecutionLogId = run })
        {
            Options = new WorkflowSuspensionWakeOptions { MaxConcurrentWakes = 1 }
        };
        world.Resumer.HoldsUp = busy.ExecutionLogId;
        var waker = world.Waker();
        var waking = waker.WakeNowAsync(
            new WorkflowSuspensionWake { SuspensionId = busy.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Poll },
            default);
        await world.Resumer.Entered.WaitAsync(TimeSpan.FromSeconds(10));

        (await waker.WakeNowAsync(DoorOpened(run), default)).Should().BeFalse();

        world.Resumer.Release();
        (await waking).Should().BeTrue();
        var next = NextTriggerWait(run, "wait-1", "door-opened");
        world.Store.Candidates = [next];
        world.Store.Clock = Now.AddSeconds(5);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Select(take => take.Id).Should().NotContain(next.SuspensionId,
            "the wake was held for the wait its read found, which a peer has answered");
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_stopped_in_its_window_does_not_answer_the_next_wait()
    {
        var run = Guid.NewGuid();
        var world = new World(Parked() with { ExecutionLogId = run, SuspendedAtUtc = Now })
        {
            Clock = new AdvancingClock { FiresTimers = false },
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromSeconds(5), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();
        using var stopping = new CancellationTokenSource();

        var waking = waker.WakeNowAsync(DoorOpened(run), stopping.Token);
        world.Clock.Waits.Should().Equal(new[] { TimeSpan.FromSeconds(5) });
        world.Store.Reads.Should().Be(1);
        await stopping.CancelAsync();
        (await waking).Should().BeFalse();
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Held);

        var next = NextTriggerWait(run, "wait-1", "door-opened");
        world.Store.Candidates = [next];
        world.Store.Clock = Now.AddSeconds(10);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Should().BeEmpty("the wake was held for the wait its read found, which a peer has answered");
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_whose_read_failed_does_not_answer_the_next_wait()
    {
        var run = Guid.NewGuid();
        var world = new World(Parked() with { ExecutionLogId = run });
        var waker = world.Waker();
        await waker.WakeOnceAsync(default);
        world.Store.FailReads = true;

        (await waker.WakeNowAsync(DoorOpened(run), default)).Should().BeFalse();

        world.Store.FailReads = false;
        world.Store.Candidates = [NextTriggerWait(run, "wait-1", "door-opened")];
        world.Store.Clock = Now.AddSeconds(5);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Should().BeEmpty("nothing says the wait this read finds is the one the event was for");
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_reads_again_for_the_wait_it_found_after_waiting_out_its_window()
    {
        var run = Guid.NewGuid();
        var settling = Parked() with { ExecutionLogId = run, SuspendedAtUtc = Now.AddMilliseconds(-10) };
        var next = NextTriggerWait(run, "wait-1", "door-opened") with { SuspendedAtUtc = Now.AddMinutes(-1) };
        var world = new World(settling)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(50), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        world.Store.OnRead = () =>
        {
            world.Store.OnRead = null;
            world.Store.Candidates = [next];
        };

        (await world.Waker().WakeNowAsync(DoorOpened(run), default)).Should().BeFalse();

        world.Store.Taken.Should().BeEmpty("the wait the read after the window is for is the one the first read found");
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_held_after_waiting_is_held_for_the_wait_it_found()
    {
        var run = Guid.NewGuid();
        var settling = Parked() with { ExecutionLogId = run, SuspendedAtUtc = Now.AddMilliseconds(-20) };
        var next = NextTriggerWait(run, "wait-1", "door-opened") with { SuspendedAtUtc = Now.AddMinutes(-1) };
        var world = new World(settling)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(50), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();

        (await waker.WakeNowAsync(DoorOpened(run), default)).Should().BeFalse();
        world.WakeOutcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Held);

        world.Store.Candidates = [next];
        world.Store.Clock = Now.AddSeconds(10);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Should().BeEmpty("the hold is for the wait the wake's read found, not the run's next one");
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_whose_read_after_the_wait_failed_is_held_for_the_wait_it_found()
    {
        var run = Guid.NewGuid();
        var settling = Parked() with { ExecutionLogId = run, SuspendedAtUtc = Now.AddMilliseconds(-20) };
        var next = NextTriggerWait(run, "wait-1", "door-opened") with { SuspendedAtUtc = Now.AddMinutes(-1) };
        var world = new World(settling)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromMilliseconds(50), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        world.Store.OnRead = () =>
        {
            world.Store.OnRead = null;
            world.Store.FailReads = true;
        };
        var waker = world.Waker();

        (await waker.WakeNowAsync(DoorOpened(run), default)).Should().BeFalse();

        world.Store.FailReads = false;
        world.Store.Candidates = [next];
        world.Store.Clock = Now.AddSeconds(10);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Should().BeEmpty("the hold is for the wait the wake's first read found, not the run's next one");
    }

    [Fact]
    public async Task A_trigger_wake_naming_only_its_run_stopped_in_a_later_wait_is_held_for_the_wait_it_found()
    {
        var run = Guid.NewGuid();
        var settling = Parked() with { ExecutionLogId = run, SuspendedAtUtc = Now };
        var next = NextTriggerWait(run, "wait-1", "door-opened") with { SuspendedAtUtc = Now.AddMinutes(-1) };
        var world = new World(settling)
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromSeconds(5), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        world.Store.ClocksOnRead.Enqueue(Now.AddMilliseconds(4950));
        world.Store.ClocksOnRead.Enqueue(Now.AddMilliseconds(4955));
        using var stopping = new CancellationTokenSource();
        world.Store.OnRead = () =>
        {
            if (world.Store.Reads == 2)
                stopping.Cancel();
        };
        var waker = world.Waker();

        (await waker.WakeNowAsync(DoorOpened(run), stopping.Token)).Should().BeFalse();
        world.Store.Reads.Should().Be(2);

        world.Store.OnRead = null;
        world.Store.Candidates = [next];
        world.Store.Clock = Now.AddSeconds(10);
        await waker.WakeOnceAsync(default);

        world.Store.Taken.Should().BeEmpty("the hold is for the wait the wake's first read found, not the run's next one");
    }

    [Fact]
    public async Task A_pass_leaves_a_held_wake_whose_row_is_inside_its_window_held_rather_than_waiting_for_it()
    {
        var row = Parked() with { SuspendedAtUtc = Now };
        var world = new World
        {
            Options = new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.FromSeconds(5), HoldWakesFor = TimeSpan.FromMinutes(5)
            }
        };
        var waker = world.Waker();
        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake { SuspensionId = row.SuspensionId, Reason = WorkflowSuspensionWakeReasons.Trigger },
            default)).Should().BeFalse();

        world.Store.Candidates = [row];
        world.Store.Clock = Now.AddSeconds(1);

        (await waker.WakeOnceAsync(default).WaitAsync(TimeSpan.FromSeconds(3))).Should().Be(0);
        world.Store.Taken.Should().BeEmpty();
    }

    [Fact]
    public async Task A_poll_wake_naming_only_its_run_is_held_until_its_run_parks()
    {
        var run = Guid.NewGuid();
        var world = new World();
        var waker = world.Waker();

        (await waker.WakeNowAsync(
            new WorkflowSuspensionWake { ExecutionLogId = run, Reason = WorkflowSuspensionWakeReasons.Poll },
            default)).Should().BeFalse();

        world.Store.Candidates = [ParkedTemplates.Pin(Parked(kind: WorkflowSuspensionKind.WaitTemplate) with { ExecutionLogId = run }, "1 == 1")];
        (await waker.WakeOnceAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task A_trigger_wake_that_names_its_wait_and_its_run_is_held_until_that_wait_lands()
    {
        var run = Guid.NewGuid();
        var landing = NextTriggerWait(run, "wait-1", "door-opened");
        var world = new World();
        var waker = world.Waker();

        (await waker.WakeNowAsync(DoorOpened(run) with { SuspensionId = landing.SuspensionId }, default))
            .Should().BeFalse();

        world.Store.Candidates = [landing];
        world.Store.Clock = Now.AddSeconds(5);
        (await waker.WakeOnceAsync(default)).Should().Be(1);

        world.Resumer.Resumed.Should().ContainSingle().Which.SuspensionId.Should().Be(landing.SuspensionId);
    }

    private static WorkflowSuspensionWake DoorOpened(Guid run) => new()
    {
        ExecutionLogId = run,
        Reason = WorkflowSuspensionWakeReasons.Trigger,
        Data = new Dictionary<string, object?> { ["event"] = "door-opened #1" }
    };

    private static WorkflowSuspensionCandidate NextTriggerWait(Guid run, string blockId, string triggerId) =>
        Parked() with
        {
            ExecutionLogId = run,
            BlockId = blockId,
            ConfigJson = JsonSerializer.Serialize(new WorkflowWaitForTriggerConfig([triggerId])),
            SuspendedAtUtc = Now.AddSeconds(1)
        };

    private static WorkflowSuspensionCandidate Template(string expression) =>
        ParkedTemplates.Pin(Parked(kind: WorkflowSuspensionKind.WaitTemplate), expression);

    private static WorkflowSuspensionCandidate Parked(
        DateTimeOffset? timeoutAtUtc = null, WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitForTrigger) =>
        new(
            SuspensionId: Guid.NewGuid(),
            ExecutionLogId: Guid.NewGuid(),
            WorkflowId: Guid.NewGuid(),
            BlockId: "wait-1",
            Kind: kind,
            ConfigJson: "{}",
            StatePayloadJson: Payload,
            SuspendedAtUtc: Now.AddMinutes(-5),
            TimeoutAtUtc: timeoutAtUtc ?? Now.AddMinutes(5));

    private static readonly string Payload = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
    {
        VariablesJson = WorkflowVariablesEncoder.Encode(new Dictionary<string, object?> { ["threshold"] = 9 }),
        PreviousOutputsJson = "{}",
        TriggerEnvelopeJson = "{}",
        ExecutionStateId = null
    });

    private sealed class AdvancingClock : TimeProvider
    {
        private long ticks;
        public ConcurrentQueue<TimeSpan> Waits { get; } = new();
        public bool FiresTimers { get; init; } = true;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref ticks, TimeSpan.TicksPerMillisecond);
        public override DateTimeOffset GetUtcNow() => Now + TimeSpan.FromTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new AdvancingTimer(this, callback, state, dueTime);

        private void Schedule(TimeSpan dueTime, TimerCallback callback, object? state)
        {
            if (dueTime == Timeout.InfiniteTimeSpan) return;
            Waits.Enqueue(dueTime);
            if (!FiresTimers) return;
            Interlocked.Add(ref ticks, dueTime.Ticks);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        private sealed class AdvancingTimer : ITimer
        {
            private int disposed;
            public AdvancingTimer(AdvancingClock clock, TimerCallback callback, object? state, TimeSpan dueTime)
            {
                this.clock = clock; this.callback = callback; this.state = state;
                clock.Schedule(dueTime, callback, state);
            }
            private readonly AdvancingClock clock;
            private readonly TimerCallback callback;
            private readonly object? state;
            public bool Change(TimeSpan nextDue, TimeSpan period)
            {
                if (disposed != 0) return false;
                clock.Schedule(nextDue, callback, state);
                return true;
            }
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class World(params WorkflowSuspensionCandidate[] parked)
    {
        public AdvancingClock Clock { get; init; } = new();
        public WakeableStore Store { get; } = new() { Candidates = parked };
        public RecordingResumer Resumer { get; init; } = new();
        public bool TakenByAPeer { get; init; }
        public WorkflowSuspensionWakeOptions? Options { get; init; }

        public List<string> WakeOutcomes { get; } = [];

        public WarningLogger Log { get; } = new();

        public RecordingScopes? Scopes { get; init; }

        public WorkflowSuspensionWaker Waker()
        {
            Store.TakenByAPeer = TakenByAPeer;
            IWorkflowRunScopeFactory? scopes = Scopes is null && Store.Candidates.Any(c => c.Kind == WorkflowSuspensionKind.SubWorkflow)
                ? null
                : ParkedTemplates.Scopes(Scopes);
            return new WorkflowSuspensionWaker(Store, Resumer, Log, ParkedTemplates.Compiler(Options?.ExpressionNamespaces), Options, scopes: scopes, metrics: new CountingMetrics(WakeOutcomes),
                timeProvider: Clock);
        }
    }

    private sealed class WarningLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class WakeableStore : IWorkflowSuspensionResumeStore
    {
        public IReadOnlyList<WorkflowSuspensionCandidate> Candidates { get; set; } = [];

        public IReadOnlyList<WorkflowSuspensionCandidate> PageCandidates { get; set; } = [];

        public DateTimeOffset Clock { get; set; } = Now;

        public TimeSpan AdvanceOnRead { get; set; } = TimeSpan.Zero;

        public Queue<DateTimeOffset> ClocksOnRead { get; } = new();

        public bool FailReads { get; set; }

        public List<(Guid Id, WorkflowSuspensionStatus Status)> Taken { get; } = [];

        public List<Guid> PutBack { get; } = [];

        public bool TakenByAPeer { get; set; }

        public int Reads { get; private set; }

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(Clock, [.. PageCandidates], 0));

        public HashSet<Guid> Answered { get; } = [];

        public Action? OnRead { get; set; }

        public Task<WorkflowParkedRowRead?> GetParkedAsync(
            Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken)
        {
            Reads++;
            if (FailReads)
                throw new InvalidOperationException("This store cannot be reached just now.");

            if (ClocksOnRead.TryDequeue(out var scripted))
                Clock = scripted;
            Clock += AdvanceOnRead;
            var read = suspensionId is { } answeredId && Answered.Contains(answeredId)
                ? new WorkflowParkedRowRead(Clock, null) { NoLongerWaiting = true }
                : new WorkflowParkedRowRead(
                    Clock,
                    Candidates.FirstOrDefault(c =>
                        (suspensionId is null || c.SuspensionId == suspensionId)
                        && (executionLogId is null || c.ExecutionLogId == executionLogId)));

            OnRead?.Invoke();
            return Task.FromResult<WorkflowParkedRowRead?>(read);
        }

        public Task<bool> ReleaseAsync(
            Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
        {
            PutBack.Add(suspensionId);
            return Task.FromResult(true);
        }

        public bool FailTakes { get; set; }

        public string FailTakesWith { get; set; } = "the store could not take the row";

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken)
        {
            Taken.Add((suspensionId, status));
            if (FailTakes)
                throw new InvalidOperationException(FailTakesWith);
            if (TakenByAPeer)
                return Task.FromResult<ResumeContext?>(null);

            var candidate = Candidates.Concat(PageCandidates).First(c => c.SuspensionId == suspensionId);
            return Task.FromResult<ResumeContext?>(new ResumeContext
            {
                SuspensionId = candidate.SuspensionId,
                ExecutionLogId = candidate.ExecutionLogId,
                WorkflowId = candidate.WorkflowId,
                BlockId = candidate.BlockId,
                Kind = candidate.Kind,
                OutcomeJson = outcomeJson,
                StatePayloadJson = candidate.StatePayloadJson,
                SuspendedAtUtc = candidate.SuspendedAtUtc
            });
        }
    }

    private sealed class RecordingScopes : IWorkflowRunScopeFactory, IWorkflowRunScope
    {
        private readonly RecordingJournal journal = new();

        public List<WorkflowExecutionLogEntry> Entries => journal.Entries;

        public IWorkflowRunScope CreateScope() => this;

        public IWorkflowRunJournal Journal => journal;

        public IServiceProvider Services => throw new NotSupportedException();
        public IWorkflowStore Store => throw new NotSupportedException();
        public IWorkflowAuditSink Audit => throw new NotSupportedException();
        public IWorkflowAuthorization Authorization => throw new NotSupportedException();

        public void Dispose() { }

        private sealed class RecordingJournal : IWorkflowRunJournal
        {
            public List<WorkflowExecutionLogEntry> Entries { get; } = [];

            public Task SaveRunDetailsAsync(
                Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
                IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken)
            {
                Entries.AddRange(logEntries);
                return Task.CompletedTask;
            }

            public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task SaveRunAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }

    private sealed class ScopesWhoseJournalThrows : IWorkflowRunScopeFactory, IWorkflowRunScope
    {
        public IWorkflowRunScope CreateScope() => this;

        public IWorkflowRunJournal Journal =>
            throw new InvalidOperationException("the journal cannot be reached just now");

        public IServiceProvider Services => throw new NotSupportedException();
        public IWorkflowStore Store => throw new NotSupportedException();
        public IWorkflowAuditSink Audit => throw new NotSupportedException();
        public IWorkflowAuthorization Authorization => throw new NotSupportedException();

        public void Dispose() { }
    }

    private sealed class ThrowingMetrics : CountingMetrics
    {
        public ThrowingMetrics() : base([]) { }

        public override void RecordSuspensionWake(string outcome) =>
            throw new InvalidOperationException("this meter is not answering just now");
    }

    private class CountingMetrics(List<string> outcomes) : IWorkflowMetrics
    {
        public virtual void RecordSuspensionWake(string outcome) => outcomes.Add(outcome);

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class StoreWithoutTheRead(IWorkflowSuspensionResumeStore inner) : IWorkflowSuspensionResumeStore
    {
        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            inner.ScanAsync(afterPosition, limit, cancellationToken);

        public Task<bool> ReleaseAsync(
            Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(suspensionId, takenAs, cancellationToken);

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken) =>
            inner.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);
    }

    private sealed class StoreWithNoWakePath(IWorkflowSuspensionResumeStore inner) : IWorkflowSuspensionResumeStore
    {
        public int Reads { get; private set; }

        public Task<WorkflowParkedRowRead?> GetParkedAsync(
            Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<WorkflowParkedRowRead?>(null);
        }

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            inner.ScanAsync(afterPosition, limit, cancellationToken);

        public Task<bool> ReleaseAsync(
            Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(suspensionId, takenAs, cancellationToken);

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken) =>
            inner.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);
    }

    private sealed class RecordingResumer : IWorkflowRunResumer
    {
        private readonly List<ResumeContext> resumed = [];
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ResumeContext> Resumed
        {
            get { lock (resumed) return [.. resumed]; }
        }

        public Guid? HoldsUp { get; set; }

        public bool Cancels { get; init; }

        public bool Defers { get; init; }

        public Task Entered => entered.Task;

        public void Release() => released.TrySetResult();

        public async Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(
            ResumeContext ctx, CancellationToken cancellationToken)
        {
            if (Cancels)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            lock (resumed)
                resumed.Add(ctx);

            if (Defers)
                return WorkflowResumeOutcome.Deferred;

            if (HoldsUp == ctx.ExecutionLogId)
            {
                entered.TrySetResult();
                await released.Task.ConfigureAwait(false);
            }

            return WorkflowResumeOutcome.Resumed;
        }
    }
}
