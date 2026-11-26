using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowSuspensionRetakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Cutoff = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_wait_whose_taker_died_is_taken_again_and_its_run_re_entered_with_the_answer_it_was_taken_with()
    {
        var abandoned = Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""");
        var world = new World(abandoned);

        (await world.Waker(Cutoff).WakeOnceAsync(default)).Should().Be(1);

        world.Store.AskedForAbandonedTakesOlderThan.Should().ContainSingle().Which.Should().Be(Cutoff);
        world.Store.Retaken.Should().ContainSingle().Which.Should().BeSameAs(abandoned);
        var resumed = world.Resumer.Resumed.Should().ContainSingle().Subject;
        resumed.SuspensionId.Should().Be(abandoned.SuspensionId);
        resumed.OutcomeJson.Should().Be("""{"outcome":"satisfied"}""",
            "the run is owed the answer that was decided, not a fresh look at a template that may read otherwise now");
        world.Store.Resolved.Should().BeEmpty("the wait is not taken a second time as if it were still parked");
    }

    [Fact]
    public async Task A_waker_given_no_cutoff_never_looks_for_abandoned_takes()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.TimedOut, """{"outcome":"timed_out"}"""));

        (await world.Waker(retakeAfter: null).WakeOnceAsync(default)).Should().Be(0);

        world.Store.AskedForAbandonedTakesOlderThan.Should().BeEmpty();
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wait_whose_run_has_no_state_row_is_never_retaken_because_nothing_could_fence_the_resume()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""", stateRow: false));

        (await world.Waker(Cutoff).WakeOnceAsync(default)).Should().Be(0);

        world.Store.Retaken.Should().BeEmpty();
        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retake_somebody_else_won_is_not_resumed_here()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""))
        {
            RetakeWonElsewhere = true
        };

        (await world.Waker(Cutoff).WakeOnceAsync(default)).Should().Be(0);

        world.Resumer.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retaken_wait_the_resume_defers_is_left_taken_for_a_later_look_rather_than_put_back()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"event","data":{"k":1}}"""));
        world.Resumer.Answer = WorkflowResumeOutcome.Deferred;

        (await world.Waker(Cutoff).WakeOnceAsync(default)).Should().Be(0);

        world.Store.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retaken_wait_whose_resume_is_stopped_before_it_enters_the_run_is_left_taken_too()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"event","data":{"k":1}}"""));
        world.Resumer.Throws = new OperationCanceledException();

        await world.Waker(Cutoff).WakeOnceAsync(default);

        world.Store.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retake_that_failed_is_read_again_by_the_next_look()
    {
        var first = Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""");
        var second = Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""");
        var world = new World(first, second);
        world.Store.RetakeThrowsFor = second.SuspensionId;
        world.Store.NextCursor = new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = Now.AddMinutes(-1) };
        var waker = world.Waker(Cutoff);

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        world.Store.LookedFrom[1].Should().Be(new WorkflowAbandonedTakeCursor
        {
            AfterTakenAtUtc = first.TakenAtUtc, AfterSuspensionId = first.SuspensionId
        });
    }

    [Fact]
    public async Task A_retaken_run_somebody_is_still_resuming_is_neither_put_back_nor_counted()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""));
        world.Resumer.Answer = WorkflowResumeOutcome.HeldElsewhere;

        (await world.Waker(Cutoff).WakeOnceAsync(default)).Should().Be(0);

        world.Store.Released.Should().BeEmpty();
    }

    [Fact]
    public async Task A_look_for_abandoned_takes_that_fails_does_not_undo_the_waits_the_pass_did_move()
    {
        var due = new WorkflowSuspensionCandidate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wait-1", WorkflowSuspensionKind.WaitForTrigger, "{}",
            Payload(stateRow: true), Now.AddMinutes(-5), Now.AddSeconds(-1));
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""))
        {
            Parked = [due],
            AbandonedScanThrows = true
        };

        (await world.Waker(Cutoff).WakeOnceAsync(default)).Should().Be(1);

        world.Resumer.Resumed.Should().ContainSingle().Which.SuspensionId.Should().Be(due.SuspensionId);
    }

    [Fact]
    public async Task Each_look_for_abandoned_takes_reads_on_from_where_the_last_one_got()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""));
        var got = new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = Now.AddMinutes(-1) };
        world.Store.NextCursor = got;
        var waker = world.Waker(Cutoff);

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        world.Store.LookedFrom.Should().Equal(WorkflowAbandonedTakeCursor.Start, got);
    }

    [Fact]
    public async Task A_look_that_ran_out_of_workers_reads_its_takes_again_rather_than_stepping_past_them()
    {
        var world = new World(
            Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""),
            Abandoned(WorkflowSuspensionStatus.TimedOut, """{"outcome":"timed_out"}"""));
        world.Store.NextCursor = new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = Now.AddMinutes(-1) };
        world.Resumer.Hold = new TaskCompletionSource<WorkflowResumeOutcome>();
        var waker = world.Waker(Cutoff, workers: 1);

        (await waker.TakeOnceAsync(default)).Should().Be(1);
        world.Resumer.Hold.SetResult(WorkflowResumeOutcome.Resumed);
        await waker.WaitForResumesAsync();
        await waker.TakeOnceAsync(default);

        world.Store.LookedFrom.Should().Equal(WorkflowAbandonedTakeCursor.Start, WorkflowAbandonedTakeCursor.Start);
    }

    private static WorkflowSuspensionCandidate Abandoned(
        WorkflowSuspensionStatus takenAs, string outcomeJson, bool stateRow = true) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wait-1", WorkflowSuspensionKind.WaitTemplate, "{}",
            Payload(stateRow), Now.AddMinutes(-10), Now.AddMinutes(-3))
        {
            Status = takenAs,
            OutcomeJson = outcomeJson,
            TakeCount = 1,
            TakenAtUtc = Now.AddMinutes(-3)
        };

    private static string Payload(bool stateRow) => JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
    {
        VariablesJson = "{}",
        PreviousOutputsJson = "{}",
        TriggerEnvelopeJson = "{}",
        ExecutionStateId = stateRow ? Guid.NewGuid() : null
    }, WorkflowGraphHelpers.JsonOptions);

    [Fact]
    public async Task A_takeover_is_written_down_where_a_host_can_read_it()
    {
        var abandoned = Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""");
        var world = new World(abandoned);

        await world.Waker(Cutoff).WakeOnceAsync(default);

        var written = world.Audit.Written.Should().ContainSingle().Subject;
        written.Kind.Should().Be(WorkflowAuditEventKinds.RetakenByWaker);
        written.SubjectId.Should().Be(abandoned.ExecutionLogId,
            "every other suspension kind's subject is the run, not the row it is parked on");
        world.Audit.Field("suspensionId").Should().Be(abandoned.SuspensionId);
        world.Audit.Field("takenAs").Should().Be(nameof(WorkflowSuspensionStatus.Resumed),
            "which answer the dead taker had already decided is what a reader is asking");
    }

    [Fact]
    public async Task A_takeover_another_replica_won_is_not_written_down_here()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, "{}")) { RetakeWonElsewhere = true };

        await world.Waker(Cutoff).WakeOnceAsync(default);

        world.Audit.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retake_of_a_run_that_was_re_entered_after_all_is_not_written_down()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""));
        world.Resumer.Answer = WorkflowResumeOutcome.HeldElsewhere;

        await world.Waker(Cutoff).WakeOnceAsync(default);

        world.Audit.Written.Should().BeEmpty();
        world.Log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retake_the_resume_defers_is_not_written_down_yet()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""));
        world.Resumer.Answer = WorkflowResumeOutcome.Deferred;

        await world.Waker(Cutoff).WakeOnceAsync(default);

        world.Audit.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retake_whose_resume_answers_abandoned_is_not_written_down()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""));
        world.Resumer.Answer = WorkflowResumeOutcome.Abandoned;

        await world.Waker(Cutoff).WakeOnceAsync(default);

        world.Audit.Written.Should().BeEmpty();
        world.Log.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wait_a_pass_takes_and_resumes_is_not_written_down_as_a_takeover()
    {
        var due = new WorkflowSuspensionCandidate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wait-1", WorkflowSuspensionKind.WaitForTrigger, "{}",
            Payload(stateRow: true), Now.AddMinutes(-5), Now.AddSeconds(-1));
        var world = new World { Parked = [due] };

        (await world.Waker(Cutoff).WakeOnceAsync(default)).Should().Be(1);

        world.Resumer.Resumed.Should().ContainSingle();
        world.Audit.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retake_whose_resume_threw_is_not_written_down()
    {
        var world = new World(Abandoned(WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}"""));
        world.Resumer.Throws = new InvalidOperationException("the walk failed");

        await world.Waker(Cutoff).WakeOnceAsync(default);

        world.Store.Retaken.Should().ContainSingle("the retake has to have reached the resume");
        world.Audit.Written.Should().BeEmpty();
        world.Log.Warnings.Should().NotContain(line => line.Contains("took it over"));
    }

    private sealed class World(params WorkflowSuspensionCandidate[] abandoned)
    {
        public Store Store { get; } = new() { Abandoned = abandoned };
        public Resumer Resumer { get; } = new();
        public RecordingAudit Audit { get; } = new();
        public WarningLogger Log { get; } = new();
        public WorkflowSuspensionCandidate[] Parked { get; init; } = [];
        public bool RetakeWonElsewhere { get; init; }
        public bool AbandonedScanThrows { get; init; }

        public WorkflowSuspensionWaker Waker(TimeSpan? retakeAfter, int workers = 8)
        {
            Store.Parked = Parked;
            Store.RetakeWonElsewhere = RetakeWonElsewhere;
            Store.AbandonedScanThrows = AbandonedScanThrows;
            return new WorkflowSuspensionWaker(Store, Resumer, Log, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions
            {
                RetakeAbandonedTakesAfter = retakeAfter,
                MaxConcurrentResumes = workers
            }, audit: Audit);
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

    private sealed class RecordingAudit : IWorkflowAuditSink
    {
        public List<WorkflowAuditEvent> Written { get; } = [];

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Written.Add(auditEvent);
            return Task.CompletedTask;
        }

        public object? Field(string name) =>
            Written.Single().Data is IReadOnlyDictionary<string, object?> data && data.TryGetValue(name, out var v)
                ? v
                : null;
    }

    private sealed class Store : IWorkflowSuspensionResumeStore
    {
        public IReadOnlyList<WorkflowSuspensionCandidate> Parked { get; set; } = [];
        public IReadOnlyList<WorkflowSuspensionCandidate> Abandoned { get; init; } = [];
        public bool RetakeWonElsewhere { get; set; }
        public bool AbandonedScanThrows { get; set; }
        public List<TimeSpan> AskedForAbandonedTakesOlderThan { get; } = [];
        public List<WorkflowSuspensionCandidate> Retaken { get; } = [];
        public List<Guid> Resolved { get; } = [];
        public List<(Guid Id, WorkflowSuspensionStatus TakenAs)> Released { get; } = [];

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(Now, Parked, 0));

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken)
        {
            Resolved.Add(suspensionId);
            var row = Parked.Single(c => c.SuspensionId == suspensionId);
            return Task.FromResult<ResumeContext?>(ContextOf(row, outcomeJson));
        }

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
        {
            Released.Add((suspensionId, takenAs));
            return Task.FromResult(true);
        }

        public Task<WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(
            WorkflowAbandonedTakeCursor after, TimeSpan abandonedAfter, int limit, CancellationToken cancellationToken)
        {
            AskedForAbandonedTakesOlderThan.Add(abandonedAfter);
            LookedFrom.Add(after);
            return AbandonedScanThrows
                ? Task.FromException<WorkflowAbandonedTakeScan>(new InvalidOperationException("the database is away"))
                : Task.FromResult(new WorkflowAbandonedTakeScan(Abandoned, NextCursor));
        }

        public WorkflowAbandonedTakeCursor NextCursor { get; set; } = WorkflowAbandonedTakeCursor.Start;

        public List<WorkflowAbandonedTakeCursor> LookedFrom { get; } = [];

        public Guid? RetakeThrowsFor { get; set; }

        public Task<ResumeContext?> TryRetakeAsync(WorkflowSuspensionCandidate abandoned, CancellationToken cancellationToken)
        {
            if (abandoned.SuspensionId == RetakeThrowsFor)
                return Task.FromException<ResumeContext?>(new InvalidOperationException("the database is away"));
            Retaken.Add(abandoned);
            return Task.FromResult(RetakeWonElsewhere ? null : ContextOf(abandoned, abandoned.OutcomeJson!));
        }

        private static ResumeContext ContextOf(WorkflowSuspensionCandidate row, string outcomeJson) => new()
        {
            SuspensionId = row.SuspensionId,
            ExecutionLogId = row.ExecutionLogId,
            WorkflowId = row.WorkflowId,
            BlockId = row.BlockId,
            Kind = row.Kind,
            OutcomeJson = outcomeJson,
            StatePayloadJson = row.StatePayloadJson,
            SuspendedAtUtc = row.SuspendedAtUtc
        };
    }

    private sealed class Resumer : IWorkflowRunResumer
    {
        public List<ResumeContext> Resumed { get; } = [];
        public WorkflowResumeOutcome Answer { get; set; } = WorkflowResumeOutcome.Resumed;

        public TaskCompletionSource<WorkflowResumeOutcome>? Hold { get; set; }

        public Exception? Throws { get; set; }

        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken)
        {
            if (Throws is not null)
                return Task.FromException<WorkflowResumeOutcome>(Throws);
            Resumed.Add(ctx);
            return Hold?.Task ?? Task.FromResult(Answer);
        }
    }
}
