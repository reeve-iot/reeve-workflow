using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowSuspensionWakerProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retake = TimeSpan.FromMinutes(1);

    private static WorkflowSuspensionWakeProbe Idle(DateTimeOffset asOf, TimeSpan? dueIn = null) =>
        new(asOf, dueIn is { } due ? asOf + due : null, false, false);

    private static WorkflowSuspensionWaker Waker(
        ScriptedStore store, WorkflowSuspensionWakeOptions? options = null, ILogger? logger = null) =>
        new(store, new NoResumes(), logger ?? NullLogger.Instance, ParkedTemplates.Compiler(), options ?? new WorkflowSuspensionWakeOptions());

    [Fact]
    public async Task The_first_probe_reads_every_take_and_an_idle_none_moves_the_cursor_one_cutoff_behind_the_instant()
    {
        var store = new ScriptedStore { Probe = call => Idle(Now.AddSeconds(5 * (call - 1))) };
        var waker = Waker(store, new WorkflowSuspensionWakeOptions { RetakeAbandonedTakesAfter = Retake });

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        store.ProbeCalls.Select(call => call.After).Should().OnlyContain(after => after == Retake);
        store.ProbeCalls[0].Cursor.AfterTakenAtUtc.Should().BeNull("a replica that starts reads every take there is once");
        store.ProbeCalls[1].Cursor.AfterTakenAtUtc.Should().Be(Now - Retake);
        store.ProbeCalls[2].Cursor.AfterTakenAtUtc.Should().Be(Now.AddSeconds(5) - Retake,
            "the range the probe reads stays one cutoff wide while the table is idle");
        store.ProbeCalls.Select(call => call.Cursor.AfterSuspensionId).Should().OnlyContain(id => id == null);
        store.Looks.Should().Be(0);
        store.Scans.Should().Be(0);
    }

    [Fact]
    public async Task A_waker_with_retakes_off_asks_for_no_abandoned_takes_and_keeps_its_cursor_at_the_start()
    {
        var store = new ScriptedStore { Probe = call => Idle(Now.AddSeconds(5 * (call - 1))) };
        var waker = Waker(store);

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        store.ProbeCalls.Select(call => call.After).Should().OnlyContain(after => after == null);
        store.ProbeCalls.Select(call => call.Cursor).Should().OnlyContain(cursor => cursor == WorkflowAbandonedTakeCursor.Start);
        store.Looks.Should().Be(0);
    }

    [Fact]
    public async Task A_probe_that_finds_an_abandoned_take_does_not_move_the_cursor_past_it()
    {
        var store = new ScriptedStore
        {
            Probe = call => call == 1 ? new WorkflowSuspensionWakeProbe(Now, null, false, true) : Idle(Now.AddSeconds(5))
        };
        var waker = Waker(store, new WorkflowSuspensionWakeOptions { RetakeAbandonedTakesAfter = Retake });

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        store.Looks.Should().Be(1, "the first pass found an abandoned take and read the look");
        store.ProbeCalls[1].Cursor.Should().Be(store.LookCursorAnswered,
            "the second probe asks from where the look left the cursor, not from the idle shortcut");
    }

    [Fact]
    public async Task A_pass_after_a_scan_that_found_rows_skips_the_probe_and_probing_resumes_after_an_empty_scan()
    {
        var store = new ScriptedStore
        {
            Probe = _ => new WorkflowSuspensionWakeProbe(Now, null, true, false),
            Scan = call => call == 1
                ? new WorkflowSuspensionScan(Now, [Young()], 0)
                : new WorkflowSuspensionScan(Now, [], 0)
        };
        var waker = Waker(store);

        await waker.WakeOnceAsync(default);
        (store.Probes, store.Scans).Should().Be((1, 1), "a template estate that answers scan costs a probe and the pages");

        await waker.WakeOnceAsync(default);
        (store.Probes, store.Scans).Should().Be((1, 2), "the pages had rows, so this pass goes straight to them");

        await waker.WakeOnceAsync(default);
        (store.Probes, store.Scans).Should().Be((2, 3), "the pages were empty, so this pass probes again");
    }

    [Fact]
    public async Task A_scan_that_has_more_pages_keeps_the_probe_off_for_the_next_pass()
    {
        var store = new ScriptedStore
        {
            Probe = _ => new WorkflowSuspensionWakeProbe(Now, null, true, false),
            Scan = _ => new WorkflowSuspensionScan(Now, [], 0)
            {
                NextCursor = new WorkflowSuspensionScanCursor(0) { AfterSuspendedAtUtc = Now, AfterSuspensionId = Guid.NewGuid() }
            }
        };
        var waker = Waker(store);

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        (store.Probes, store.Scans).Should().Be((1, 2), "the scan stopped mid-estate, so the next pass reads on from it");
    }

    [Fact]
    public async Task A_probe_that_throws_leaves_the_pass_to_the_normal_pages_at_the_poll_interval()
    {
        var store = new ScriptedStore
        {
            Probe = call => call == 1 ? Idle(Now, TimeSpan.FromSeconds(2)) : throw new InvalidOperationException("probe")
        };
        var options = new WorkflowSuspensionWakeOptions { PollInterval = TimeSpan.FromSeconds(7) };
        var waker = Waker(store, options);

        await waker.WakeOnceAsync(default);
        waker.NextPassDelay.Should().Be(TimeSpan.FromMilliseconds(2_001));
        await waker.WakeOnceAsync(default);

        store.Scans.Should().Be(1, "a failing shortcut is not a pass that looks at nothing");
        waker.NextPassDelay.Should().Be(TimeSpan.FromSeconds(7), "a scanning pass drops the short delay an idle one set");
    }

    [Fact]
    public async Task A_probe_that_answers_nothing_leaves_the_pass_to_the_normal_pages()
    {
        var store = new ScriptedStore { Probe = _ => null };
        var waker = Waker(store, new WorkflowSuspensionWakeOptions { PollInterval = TimeSpan.FromSeconds(7) });

        await waker.WakeOnceAsync(default);

        store.Scans.Should().Be(1);
        waker.NextPassDelay.Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_cancelled_probe_ends_the_pass_rather_than_falling_back_to_the_pages()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new ScriptedStore
        {
            Probe = _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        var waker = Waker(store);

        var pass = () => waker.WakeOnceAsync(cancellation.Token);

        await pass.Should().ThrowAsync<OperationCanceledException>();
        store.Scans.Should().Be(0);
    }

    [Fact]
    public async Task An_idle_pass_still_tries_the_wakes_it_holds()
    {
        var store = new ScriptedStore { Probe = _ => Idle(Now), ParkedRead = new WorkflowParkedRowRead(Now, null) };
        var waker = Waker(store);
        (await waker.WakeNowAsync(Wake(), default)).Should().BeFalse("its row has not landed, so the wake is held");
        store.Reads.Should().Be(1);

        await waker.WakeOnceAsync(default);

        store.Reads.Should().Be(2, "the idle pass replays the hold, which is the latency the wake exists for");
        store.Scans.Should().Be(0);
    }

    [Fact]
    public async Task An_idle_pass_dates_a_later_wakes_hold_from_its_probe_instant()
    {
        var store = new ScriptedStore { Probe = _ => Idle(Now) };
        var waker = Waker(store);
        await waker.WakeOnceAsync(default);
        store.ReadThrows = true;

        await waker.WakeNowAsync(Wake(), default);
        await waker.WakeOnceAsync(default);

        store.Reads.Should().Be(2, "a hold needs an instant to be dated from, and the probe is where an idle waker got it");
    }

    [Fact]
    public async Task An_idle_pass_still_warns_of_a_parked_sleep_its_kinds_leave_out()
    {
        var logger = new WarningLogger();
        var store = new ScriptedStore { Probe = _ => Idle(Now), Stranded = Young(WorkflowSuspensionKind.Sleep) };
        var waker = Waker(store, new WorkflowSuspensionWakeOptions { Kinds = [WorkflowSuspensionKind.WaitTemplate] }, logger);

        await waker.WakeOnceAsync(default);

        logger.Warnings.Should().ContainSingle(warning => warning.Contains("Sleep"));
        store.Scans.Should().Be(0);
    }

    [Fact]
    public void A_park_signal_reaches_only_the_waker_subscribed_to_it()
    {
        var one = new WorkflowSuspensionParkSignal();
        var other = new WorkflowSuspensionParkSignal();
        int heardByOne = 0, heardByOther = 0;
        using var first = one.Subscribe(() => heardByOne++);
        using var second = other.Subscribe(() => heardByOther++);

        one.Publish();

        (heardByOne, heardByOther).Should().Be((1, 0), "a park on one engine is not news to another engine's waker");
    }

    [Fact]
    public void A_park_signal_forgets_a_subscriber_that_has_been_disposed_and_survives_a_throwing_one()
    {
        var signal = new WorkflowSuspensionParkSignal();
        var heard = 0;
        using var throwing = signal.Subscribe(() => throw new InvalidOperationException("subscriber"));
        var subscription = signal.Subscribe(() => heard++);

        signal.Publish();
        subscription.Dispose();
        subscription.Dispose();
        signal.Publish();

        heard.Should().Be(1, "a subscriber that threw is not a park that failed, and a disposed one hears nothing");
    }

    private static WorkflowSuspensionWake Wake() => new()
    {
        SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger
    };

    private static WorkflowSuspensionCandidate Young(WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitTemplate) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "wait", kind, "{}", "{}", Now, Now.AddHours(1));

    private sealed class NoResumes : IWorkflowRunResumer
    {
        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No pass in these tests takes a row.");
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

    private sealed class ScriptedStore : IWorkflowSuspensionResumeStore
    {
        private readonly List<(TimeSpan? After, WorkflowAbandonedTakeCursor Cursor)> probeCalls = [];
        private int probes, scans, looks, reads;

        public Func<int, WorkflowSuspensionWakeProbe?> Probe { get; init; } = _ => null;

        public Func<int, WorkflowSuspensionScan> Scan { get; init; } = _ => new WorkflowSuspensionScan(Now, [], 0);

        public WorkflowParkedRowRead? ParkedRead { get; init; }

        public bool ReadThrows { get; set; }

        public WorkflowSuspensionCandidate? Stranded { get; init; }

        public WorkflowAbandonedTakeCursor LookCursorAnswered { get; } = new()
        {
            AfterTakenAtUtc = Now.AddMinutes(-30), AfterSuspensionId = Guid.Parse("00000000-0000-0000-0000-000000000042")
        };

        public int Probes => Volatile.Read(ref probes);
        public int Scans => Volatile.Read(ref scans);
        public int Looks => Volatile.Read(ref looks);
        public int Reads => Volatile.Read(ref reads);

        public IReadOnlyList<(TimeSpan? After, WorkflowAbandonedTakeCursor Cursor)> ProbeCalls => probeCalls;

        public Task<WorkflowSuspensionWakeProbe?> ProbeWakeAsync(
            TimeSpan? abandonedAfter, WorkflowAbandonedTakeCursor abandonedAfterCursor, CancellationToken cancellationToken)
        {
            probeCalls.Add((abandonedAfter, abandonedAfterCursor));
            return Task.FromResult(Probe(Interlocked.Increment(ref probes)));
        }

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(Scan(Interlocked.Increment(ref scans)));

        public Task<WorkflowSuspensionScan> ScanAsync(
            WorkflowSuspensionScanCursor after, int limit, IReadOnlyCollection<WorkflowSuspensionKind>? kinds,
            CancellationToken cancellationToken)
        {
            if (kinds is { Count: 1 } only)
            {
                List<WorkflowSuspensionCandidate> found = Stranded is { } row && row.Kind == only.Single() ? [row] : [];
                return Task.FromResult(new WorkflowSuspensionScan(Now, found, 0));
            }

            return ScanAsync(after.Position, limit, cancellationToken);
        }

        public Task<WorkflowParkedRowRead?> GetParkedAsync(
            Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);
            return ReadThrows
                ? throw new InvalidOperationException("read")
                : Task.FromResult(ParkedRead);
        }

        public Task<WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(
            WorkflowAbandonedTakeCursor after, TimeSpan abandonedAfter, int limit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref looks);
            return Task.FromResult(new WorkflowAbandonedTakeScan([], LookCursorAnswered));
        }

        public Task<ResumeContext?> TryResolveAsync(Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult<ResumeContext?>(null);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
