using FluentAssertions;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepDrainWarningTests
{
    private const int Long = 600_000;

    [Fact]
    public async Task An_idle_pass_uses_the_probe_once_without_reading_candidate_pages()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var counting = new CountingAbandonedLooks(world.Suspensions)
        {
            Probe = new ReeveWorkflow.Core.Hosting.WorkflowSuspensionWakeProbe(
                new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 10, 3, 13, 0, 0, TimeSpan.Zero), false, false)
        };
        var waker = new WorkflowSuspensionWaker(counting, world.Resumer(engine),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { RetakeAbandonedTakesAfter = TimeSpan.FromMinutes(1) });

        await waker.WakeOnceAsync(default);

        counting.Probes.Should().Be(1);
        counting.Scans.Should().Be(0);
        counting.Looks.Should().Be(0);
        waker.NextPassDelay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_due_time_inside_the_poll_interval_sets_the_same_next_pass_delay()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var counting = new CountingAbandonedLooks(world.Suspensions)
        {
            Probe = new ReeveWorkflow.Core.Hosting.WorkflowSuspensionWakeProbe(
                now, now.AddSeconds(2), false, false)
        };
        var waker = new WorkflowSuspensionWaker(counting, world.Resumer(engine),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions());

        await waker.WakeOnceAsync(default);

        waker.NextPassDelay.Should().Be(TimeSpan.FromMilliseconds(2_001));
    }

    [Fact]
    public async Task A_due_suspension_is_scanned_and_woken_at_the_fake_store_instant()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromDays(365));
        var now = world.Suspensions.Now;
        var counting = new CountingAbandonedLooks(world.Suspensions)
        {
            Probe = new ReeveWorkflow.Core.Hosting.WorkflowSuspensionWakeProbe(
                now, world.Suspensions.SingleRow.TimeoutAtUtc, false, false)
        };
        var waker = new WorkflowSuspensionWaker(counting, world.Resumer(engine),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero });

        await waker.WakeOnceAsync(default);

        counting.Probes.Should().Be(1);
        counting.Scans.Should().Be(1);
        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Due_template_and_abandoned_probe_results_each_reach_their_page(
        bool due, bool template, bool abandoned)
    {
        var world = new DurableSleepWorld();
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var counting = new CountingAbandonedLooks(world.Suspensions)
        {
            Probe = new ReeveWorkflow.Core.Hosting.WorkflowSuspensionWakeProbe(
                now, due ? now : now.AddHours(1), template, abandoned)
        };
        var waker = new WorkflowSuspensionWaker(counting, world.Resumer(null),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { RetakeAbandonedTakesAfter = TimeSpan.FromMinutes(1) });

        await waker.WakeOnceAsync(default);

        counting.Scans.Should().Be(1);
        counting.Looks.Should().Be(1);
    }

    [Fact]
    public async Task A_waker_that_leaves_out_sleep_warns_of_overdue_parked_sleeps_once_a_minute()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromHours(1));
        var logger = new WarningLogger();
        var waker = new WorkflowSuspensionWaker(world.Suspensions, world.Resumer(engine), logger,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, Kinds = [WorkflowSuspensionKind.WaitTemplate] });

        await waker.WakeOnceAsync(default);
        await waker.WakeOnceAsync(default);

        logger.Warnings.Should().ContainSingle(w => w.Contains("Sleep"));
        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended, "this waker still leaves the sleep alone");
    }

    [Fact]
    public async Task A_waker_that_wakes_sleeps_or_finds_none_overdue_says_nothing()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var logger = new WarningLogger();
        var waker = new WorkflowSuspensionWaker(world.Suspensions, world.Resumer(engine), logger,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, Kinds = [WorkflowSuspensionKind.WaitTemplate] });

        await waker.WakeOnceAsync(default);

        logger.Warnings.Should().BeEmpty("a sleep that is not due yet is not stranded");
    }

    [Fact]
    public async Task A_waker_left_at_its_defaults_does_not_look_for_abandoned_takes()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        using var engine = world.Engine();
        var counting = new CountingAbandonedLooks(world.Suspensions);
        var waker = new WorkflowSuspensionWaker(counting, world.Resumer(engine),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions());

        await waker.WakeOnceAsync(default);

        counting.Looks.Should().Be(0, "a waker told nothing about abandoned takes does not pay for the query");
    }

    private sealed class CountingAbandonedLooks(MemorySuspensions inner) : ReeveWorkflow.Core.Hosting.IWorkflowSuspensionResumeStore
    {
        public int Looks;
        public int Probes;
        public int Scans;
        public ReeveWorkflow.Core.Hosting.WorkflowSuspensionWakeProbe? Probe { get; init; }

        public Task<ReeveWorkflow.Core.Hosting.WorkflowSuspensionWakeProbe?> ProbeWakeAsync(
            TimeSpan? abandonedAfter, ReeveWorkflow.Core.Hosting.WorkflowAbandonedTakeCursor abandonedAfterCursor, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Probes);
            return Task.FromResult(Probe);
        }

        public Task<ReeveWorkflow.Core.Hosting.WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            CountScan(afterPosition, limit, cancellationToken);

        private async Task<ReeveWorkflow.Core.Hosting.WorkflowSuspensionScan> CountScan(
            int afterPosition, int limit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Scans);
            return await inner.ScanAsync(afterPosition, limit, cancellationToken);
        }

        public Task<ReeveWorkflow.Core.Hosting.WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(
            ReeveWorkflow.Core.Hosting.WorkflowAbandonedTakeCursor after, TimeSpan abandonedAfter, int limit,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Looks);
            return inner.ScanAbandonedTakesAsync(after, abandonedAfter, limit, cancellationToken);
        }

        public Task<ResumeContext?> TryRetakeAsync(
            ReeveWorkflow.Core.Hosting.WorkflowSuspensionCandidate abandoned, CancellationToken cancellationToken) =>
            inner.TryRetakeAsync(abandoned, cancellationToken);

        public Task<ResumeContext?> TryResolveAsync(Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) =>
            inner.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(suspensionId, takenAs, cancellationToken);
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
}
