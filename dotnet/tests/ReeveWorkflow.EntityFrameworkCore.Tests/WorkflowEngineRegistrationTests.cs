using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class WorkflowEngineRegistrationTests
{
    [Fact]
    public void An_engine_resolves_from_the_one_call_and_it_is_the_librarys_own()
    {
        using var provider = Build();

        provider.GetRequiredService<IWorkflowEngine>().Should().BeOfType<WorkflowEngine>();
    }

    [Fact]
    public void The_engine_is_one_object_for_the_process_because_its_coordinators_are()
    {
        using var provider = Build();

        provider.GetRequiredService<IWorkflowEngine>().Should().BeSameAs(
            provider.GetRequiredService<IWorkflowEngine>(),
            "a second engine would keep a second coordinator per workflow, each holding the whole budget");
    }

    [Fact]
    public void A_host_that_wrote_its_own_engine_keeps_it()
    {
        using var provider = Build(after: services => services.AddSingleton<IWorkflowEngine, HostEngine>());

        provider.GetRequiredService<IWorkflowEngine>().Should().BeOfType<HostEngine>();
    }

    [Fact]
    public async Task The_sweep_claims_in_the_name_the_deployment_stated_once()
    {
        var recovery = new RecordingRecoveryStore();
        using var provider = Build(
            configure: options => options.Defaults.ReplicaId = "desk-7",
            after: services => services.AddSingleton<IWorkflowRunRecoveryStore>(recovery));

        await provider.GetRequiredService<WorkflowRunRecoverySweep>().SweepAsync(CancellationToken.None);

        recovery.Asked.Should().ContainSingle().Which.ClaimedByReplicaId.Should().Be("desk-7");
    }

    [Fact]
    public void The_sweep_is_not_started_unless_a_host_asks_for_it()
    {
        using var provider = Build();

        provider.GetServices<IHostedService>().OfType<WorkflowRunRecoveryService>().Should().BeEmpty(
            "a recovered run is run again from the start, and what that costs depends on what the blocks do");
        provider.GetService<WorkflowRunRecoverySweep>().Should().NotBeNull(
            "a host that runs the sweep on its own schedule still needs the sweep itself");
    }

    [Fact]
    public void A_host_that_asks_for_the_sweep_gets_it_on_a_timer()
    {
        using var provider = Build(
            configure: options => options.Defaults.ReplicaId = "replica-1",
            engine: options => options.Recovery.Enabled = true);

        provider.GetServices<IHostedService>().OfType<WorkflowRunRecoveryService>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_cutoff_that_would_reclaim_every_running_row_is_refused(bool sweepEnabled)
    {
        var build = () => Build(engine: options =>
        {
            options.Recovery.Enabled = sweepEnabled;
            options.Recovery.AbandonedAfter = TimeSpan.Zero;
        });

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*AbandonedAfter*",
            "at zero a row is abandoned the instant it is written, and the sweep reclaims runs that are executing");
    }

    [Fact]
    public async Task A_host_that_says_nothing_presumes_a_run_dead_after_a_minute_not_five()
    {
        var recovery = new RecordingRecoveryStore();
        using var provider = Build(
            configure: options => options.Defaults.ReplicaId = "desk-7",
            after: services => services.AddSingleton<IWorkflowRunRecoveryStore>(recovery));

        await provider.GetRequiredService<WorkflowRunRecoverySweep>().SweepAsync(CancellationToken.None);

        recovery.Asked.Should().ContainSingle().Which.AbandonedAfter.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData(null, null, 60)]
    [InlineData(120, null, 120)]
    [InlineData(120, 300, 300)]
    public async Task The_registered_waker_takes_over_a_dead_takers_wait_after_the_cutoff_a_dead_runs_row_is_given(
        int? abandonedAfterSeconds, int? retakeAfterSeconds, int expectedSeconds)
    {
        var resumes = new RecordingResumeStore();
        using var provider = Build(
            engine: options =>
            {
                if (abandonedAfterSeconds is { } abandoned)
                    options.Recovery.AbandonedAfter = TimeSpan.FromSeconds(abandoned);
                if (retakeAfterSeconds is { } retake)
                    options.Waker = options.Waker with { RetakeAbandonedTakesAfter = TimeSpan.FromSeconds(retake) };
            },
            after: services => services.AddSingleton<IWorkflowSuspensionResumeStore>(resumes));

        await provider.GetRequiredService<WorkflowSuspensionWaker>().WakeOnceAsync(CancellationToken.None);

        resumes.AskedForAbandonedTakesOlderThan.Should().ContainSingle()
            .Which.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(29, 10)]
    [InlineData(2, 1)]
    public void A_cutoff_two_missed_beats_could_reach_is_refused(int abandonedAfterSeconds, int heartbeatSeconds)
    {
        var build = () => Build(engine: options =>
        {
            options.Recovery.AbandonedAfter = TimeSpan.FromSeconds(abandonedAfterSeconds);
            options.RunHeartbeatInterval = TimeSpan.FromSeconds(heartbeatSeconds);
        });

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*AbandonedAfter*RunHeartbeatInterval*",
            "a replica whose beat was late twice would have its executing run reclaimed and run a second time");
    }

    [Theory]
    [InlineData(30, 10)]
    [InlineData(1, null)]
    public void A_cutoff_three_beats_long_or_one_with_no_beat_at_all_is_accepted(int abandonedAfterSeconds, int? heartbeatSeconds)
    {
        var build = () => Build(engine: options =>
        {
            options.Recovery.AbandonedAfter = TimeSpan.FromSeconds(abandonedAfterSeconds);
            options.RunHeartbeatInterval = heartbeatSeconds is { } beat ? TimeSpan.FromSeconds(beat) : null;
        });

        build.Should().NotThrow();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_heartbeat_that_never_beats_is_refused_because_off_is_null(int milliseconds)
    {
        var build = () => Build(engine: options => options.RunHeartbeatInterval = TimeSpan.FromMilliseconds(milliseconds));

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*RunHeartbeatInterval*");
    }

    [Theory]
    [InlineData(172_800)]
    [InlineData(0)]
    public void A_container_ceiling_the_engine_could_never_apply_is_refused_at_the_call_that_states_it(int seconds)
    {
        var build = () => Build(engine: options => options.DefaultContainerTimeoutSeconds = seconds);

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*86400*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_step_cap_that_records_nothing_is_refused(int steps)
    {
        var build = () => Build(engine: options => options.MaxRecordedSteps = steps);

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("MaxRecordedSteps must be positive*");
    }

    [Fact]
    public void A_sweep_over_a_table_the_run_state_never_lands_in_is_refused()
    {
        using var provider = Build(
            engine: options => options.Recovery.Enabled = true,
            after: services => services.AddSingleton<IWorkflowRunStateStore, HostRunStateStore>());

        var resolve = () => provider.GetRequiredService<WorkflowRunRecoverySweep>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*Seams.RunRecoveryStore*",
            "the sweep would claim nothing for ever and log nothing, which reads exactly like a quiet fleet");
    }

    [Fact]
    public void A_resumer_and_a_sleep_runner_are_built_from_the_same_seams()
    {
        using var provider = Build();

        provider.GetRequiredService<WorkflowRunResumer>().Should().NotBeNull();
        provider.GetRequiredService<WorkflowSleepRunner>().Should().NotBeNull(
            "a graph host that parks a wait in the run's row should not assemble one out of the seams again");
    }

    [Fact]
    public void Parked_runs_are_moved_on_a_timer_without_a_host_asking()
    {
        using var provider = Build();

        provider.GetServices<IHostedService>().OfType<WorkflowSuspensionWakeService>().Should().ContainSingle();
    }

    [Fact]
    public void A_host_that_wakes_parked_runs_its_own_way_can_turn_the_pass_off()
    {
        using var provider = Build(engine: options => options.Waker = options.Waker with { Enabled = false });

        provider.GetServices<IHostedService>().OfType<WorkflowSuspensionWakeService>().Should().BeEmpty();
        provider.GetService<WorkflowSuspensionWaker>().Should().NotBeNull(
            "a host running the pass on its own schedule still needs the pass itself");
    }

    [Fact]
    public void No_block_sleeps_durably_until_a_host_names_one()
    {
        using var provider = Build();

        provider.GetRequiredService<WorkflowHostDefaultsOptions>().DurableSleepBlockTypeIds.Should().BeEmpty(
            "the library ships no blocks, so which of the host's blocks waits is the host's to say");
    }

    [Fact]
    public void Calling_it_twice_does_not_start_two_sweeps()
    {
        using var provider = Build(
            configure: options => options.Defaults.ReplicaId = "replica-1",
            engine: options => options.Recovery.Enabled = true,
            twice: true);

        provider.GetServices<IHostedService>().OfType<WorkflowRunRecoveryService>().Should().ContainSingle();
    }

    [Fact]
    public void A_sweep_on_a_host_that_never_named_this_replica_is_refused()
    {
        using var provider = Build(engine: options => options.Recovery.Enabled = true);

        var start = () => provider.GetServices<IHostedService>().ToArray();

        start.Should().Throw<InvalidOperationException>(
                "every claim would write a null owner, and the renewal that fences on it matches every peer")
            .Which.Message.Should()
            .Contain(nameof(WorkflowHostDefaultsOptions.ReplicaId)).And
            .Contain("Recovery.Enabled");
    }

    [Fact]
    public void The_same_host_without_the_sweep_needs_no_name()
    {
        using var provider = Build();

        provider.GetServices<IHostedService>().OfType<WorkflowRunRecoveryService>().Should().BeEmpty(
            "the fence only matters to a claim, and nothing claims anything here");
    }

    [Fact]
    public void The_walk_is_not_a_container_contract_so_nothing_can_be_put_in_its_place()
    {
        using var provider = Build();

        provider.GetService<WorkflowGraphExecutorOptions>().Should().BeNull(
            "a host that has one for its own executor must not be answering for the engine's walk");
    }

    [Fact]
    public void The_resumers_walk_reads_that_call_too_not_an_options_object_the_container_happens_to_hold()
    {
        var categories = new CollectingLoggerProvider();
        using var provider = Build(
            engine: options => options.LoggerCategory = "deployment.category",
            after: services =>
            {
                services.AddSingleton(new WorkflowEngineOptions { LoggerCategory = "zz.stray.category" });
                services.AddLogging(builder => builder.AddProvider(categories));
            });

        provider.GetRequiredService<WorkflowRunResumer>();

        categories.Categories.Should().Contain("deployment.category")
            .And.NotContain("zz.stray.category", "the deployment named its channel in the call it composed");
    }

    [Fact]
    public void A_settings_object_a_host_registered_for_its_own_reasons_is_not_the_engines()
    {
        var categories = new CollectingLoggerProvider();
        using var provider = Build(
            configure: options => options.Defaults.ReplicaId = "desk-7",
            after: services =>
            {
                services.AddSingleton(new WorkflowEngineSettings { LoggerCategory = "zz.host.category" });
                services.AddLogging(builder => builder.AddProvider(categories));
            });

        provider.GetRequiredService<IWorkflowEngine>();

        categories.Categories.Should().Contain("ReeveWorkflow.Engine")
            .And.NotContain("zz.host.category", "a host's object is not one of the engine's seams");
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<string> Categories { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return NullLogger.Instance;
        }

        public void Dispose() { }
    }

    private sealed class RecordingResumeStore : IWorkflowSuspensionResumeStore
    {
        public List<TimeSpan> AskedForAbandonedTakesOlderThan { get; } = [];

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(DateTimeOffset.UtcNow, [], 0));

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken) => Task.FromResult<ResumeContext?>(null);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(
            WorkflowAbandonedTakeCursor after, TimeSpan abandonedAfter, int limit, CancellationToken cancellationToken)
        {
            AskedForAbandonedTakesOlderThan.Add(abandonedAfter);
            return Task.FromResult(new WorkflowAbandonedTakeScan([], after));
        }
    }

    private sealed class RecordingRecoveryStore : IWorkflowRunRecoveryStore
    {
        public List<WorkflowRunRecoveryClaim> Asked { get; } = [];

        public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
            WorkflowRunRecoveryClaim request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            return Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>([]);
        }

        public Task<bool> RenewClaimAsync(
            Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class HostRunStateStore : IWorkflowRunStateStore
    {
        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CompleteAsync(
            Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void An_audit_sink_filled_as_scoped_does_not_stop_the_engine_resolving()
    {
        using var provider = Build(configure: options =>
            options.Seams.AuditSink.Use<ScopedAuditSink>(ServiceLifetime.Scoped));

        var resolve = () => provider.GetRequiredService<IWorkflowEngine>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public async Task Each_write_to_a_scoped_sink_gets_a_unit_of_work_of_its_own()
    {
        var written = new AuditLog();
        using var provider = Build(
            configure: options => options.Seams.AuditSink.Use<ScopedAuditSink>(ServiceLifetime.Scoped),
            after: services => services.AddSingleton(written));
        var sink = new ScopeOpeningWorkflowAuditSink(provider.GetRequiredService<IServiceScopeFactory>());

        await sink.WriteAsync(new WorkflowAuditEvent("first", null, Guid.NewGuid(), null));
        await sink.WriteAsync(new WorkflowAuditEvent("second", null, Guid.NewGuid(), null));

        written.Kinds.Should().Equal("first", "second");
        written.Sinks.Should().Be(2);
    }

    private sealed class AuditLog
    {
        public List<string> Kinds { get; } = [];
        public int Sinks { get; set; }
    }

    private sealed class ScopedAuditSink : IWorkflowAuditSink
    {
        private readonly AuditLog? log;

        public ScopedAuditSink(AuditLog? log = null)
        {
            this.log = log;
            if (log is not null) log.Sinks++;
        }

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            log?.Kinds.Add(auditEvent.Kind);
            return Task.CompletedTask;
        }
    }

    private sealed class HostEngine : IWorkflowEngine
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            Guid workflowId, WorkflowFire? fire, IReadOnlyDictionary<string, object?>? inputParameters,
            Guid? parentExecutionId = null, int? iterationIndex = null, string? callStackJson = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsBlockIdempotentAsync(
            Guid workflowId, string blockPath, Guid? versionId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task A_host_that_turned_the_pass_off_is_warned_at_startup_of_an_overdue_parked_sleep(bool overdueSleep, int warnings)
    {
        var logs = new WarningProvider();
        using var provider = Build(
            engine: options => options.Waker = options.Waker with { Enabled = false },
            after: services =>
            {
                services.AddSingleton<IWorkflowSuspensionResumeStore>(new OneRowResumeStore(overdueSleep));
                services.AddLogging(logging => logging.AddProvider(logs));
            });

        foreach (var hosted in provider.GetServices<IHostedService>().Where(h => h.GetType().Name == "WorkflowUnwokenSleepCheck"))
            await hosted.StartAsync(CancellationToken.None);

        logs.Warnings.Count(w => w.Contains("Sleep")).Should().Be(warnings);
    }

    private sealed class OneRowResumeStore(bool overdueSleep) : IWorkflowSuspensionResumeStore
    {
        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(DateTimeOffset.UtcNow, overdueSleep
                ? [new WorkflowSuspensionCandidate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "nap", ReeveWorkflow.Core.Model.WorkflowSuspensionKind.Sleep,
                    "{}", "{}", DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1))]
                : [], 0));

        public Task<ResumeContext?> TryResolveAsync(Guid suspensionId, ReeveWorkflow.Core.Model.WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) => Task.FromResult<ResumeContext?>(null);

        public Task<bool> ReleaseAsync(Guid suspensionId, ReeveWorkflow.Core.Model.WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class WarningProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Warnings);

        public void Dispose() { }

        private sealed class Logger(System.Collections.Concurrent.ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task The_wake_source_in_its_slot_is_subscribed_to_by_the_wakers_service()
    {
        var source = new CountingWakeSource();
        using var provider = Build(configure: options => options.Seams.SuspensionWakeSource.Use(_ => source));

        var service = provider.GetServices<IHostedService>()
            .OfType<WorkflowSuspensionWakeService>().Single();

        await service.StartAsync(CancellationToken.None);
        source.Subscribers.Should().Be(1);

        await service.StopAsync(CancellationToken.None);
        source.Subscribers.Should().Be(0);
    }

    [Fact]
    public async Task The_recovery_sweep_the_host_composes_counts_its_claims()
    {
        var metrics = new RecordingMetrics();
        using var provider = Build(after: services =>
        {
            services.AddSingleton<IWorkflowMetrics>(metrics);
            services.AddSingleton<IWorkflowRunRecoveryStore>(new NothingToClaim());
        });

        await provider.GetRequiredService<WorkflowRunRecoverySweep>().SweepAsync(CancellationToken.None);

        metrics.Claims.Should().Equal([0],
            "a sweep that claims nothing still says so, and a host that never wired the metric could not");
    }

    [Fact]
    public async Task The_waker_the_host_composes_counts_its_wakes()
    {
        var metrics = new RecordingMetrics();
        using var provider = Build(after: services =>
        {
            services.AddSingleton<IWorkflowMetrics>(metrics);
            services.AddSingleton<IWorkflowSuspensionResumeStore>(new NothingParked());
        });

        await provider.GetRequiredService<WorkflowSuspensionWaker>().WakeNowAsync(
            new WorkflowSuspensionWake
            {
                SuspensionId = Guid.NewGuid(), Reason = WorkflowSuspensionWakeReasons.Trigger
            },
            CancellationToken.None);

        metrics.Wakes.Should().Equal([WorkflowSuspensionWakeOutcomes.Held]);
    }

    [Fact]
    public async Task The_recovery_sweep_the_host_composes_writes_to_the_hosts_audit_sink()
    {
        var audit = new RecordingAudit();
        var row = new WorkflowRunStateRow
        {
            ExecutionId = Guid.NewGuid(),
            WorkflowId = Guid.NewGuid(),
            Status = WorkflowRunStateStatus.Running
        };
        using var provider = Build(after: services =>
        {
            services.AddScoped<IWorkflowAuditSink>(_ => audit);
            services.AddSingleton<IWorkflowRunRecoveryStore>(new ClaimsOneRow(row));
        });

        await provider.GetRequiredService<WorkflowRunRecoverySweep>().SweepAsync(CancellationToken.None);

        audit.Kinds.Should().Contain(WorkflowAuditEventKinds.ClaimedByRecovery);
        audit.Subjects.Should().Contain(row.ExecutionId,
            "the row has no checkpoint here, so the subject is its run-state row");
    }

    private sealed class ClaimsOneRow(WorkflowRunStateRow row) : IWorkflowRunRecoveryStore
    {
        public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
            WorkflowRunRecoveryClaim request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>([row]);

        public Task<bool> RenewClaimAsync(
            Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class RecordingAudit : IWorkflowAuditSink
    {
        private readonly List<WorkflowAuditEvent> events = [];

        public IReadOnlyList<string> Kinds
        {
            get { lock (events) return events.Select(written => written.Kind).ToList(); }
        }

        public IReadOnlyList<Guid> Subjects
        {
            get { lock (events) return events.Select(written => written.SubjectId).ToList(); }
        }

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (events) events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class NothingToClaim : IWorkflowRunRecoveryStore
    {
        public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
            WorkflowRunRecoveryClaim request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>([]);

        public Task<bool> RenewClaimAsync(
            Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class NothingParked : IWorkflowSuspensionResumeStore
    {
        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(DateTimeOffset.UtcNow, [], 0));

        public Task<WorkflowParkedRowRead?> GetParkedAsync(
            Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowParkedRowRead?>(new WorkflowParkedRowRead(DateTimeOffset.UtcNow, null));

        public Task<bool> ReleaseAsync(
            Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<ResumeContext?>(null);
    }

    private sealed class RecordingMetrics : IWorkflowMetrics
    {
        public List<int> Claims { get; } = [];
        public List<string> Wakes { get; } = [];

        public void RecordRecoveryClaim(int rows) => Claims.Add(rows);
        public void RecordSuspensionWake(string outcome) => Wakes.Add(outcome);

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class CountingWakeSource : IWorkflowSuspensionWakeSource
    {
        private int subscribers;

        public int Subscribers => Volatile.Read(ref subscribers);

        public IDisposable Subscribe(Func<WorkflowSuspensionWake, CancellationToken, Task> onWake)
        {
            Interlocked.Increment(ref subscribers);
            return new Subscription(() => Interlocked.Decrement(ref subscribers));
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    [Fact]
    public void The_engine_keeps_its_idle_sweep_on_the_registered_clock_and_the_period_the_call_stated()
    {
        var clock = new RecordingClock();
        using var provider = Build(
            engine: options => options.CoordinatorIdleTimeout = TimeSpan.FromSeconds(5),
            after: services => services.AddSingleton<TimeProvider>(clock));

        provider.GetRequiredService<IWorkflowEngine>();

        clock.TimerPeriods.Should().Equal(
            [TimeSpan.FromSeconds(5)], "the engine reads the host's clock and the idle period the call stated");
    }

    [Fact]
    public void The_engine_listens_for_a_restart_cancel_once_on_the_channel_the_host_named()
    {
        var signal = new RecordingSignal();
        using var provider = Build(
            configure: options => options.Defaults.SignalChannels.RestartCancel = "desk_restart_cancel",
            after: services => services.AddSingleton<IWorkflowClusterSignal>(signal));

        provider.GetRequiredService<IWorkflowEngine>();

        signal.Subscribed.Should().Equal(["desk_restart_cancel"],
            "one subscription for the whole engine, on the channel peers publish to");
    }

    private static ServiceProvider Build(
        Action<WorkflowPersistenceOptions>? configure = null,
        Action<WorkflowEngineOptions>? engine = null,
        Action<IServiceCollection>? after = null,
        bool twice = false)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        var builder = services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(context => context.UseSqlite(new SqliteConnection("Filename=:memory:")));
            configure?.Invoke(options);
        });

        builder.AddWorkflowEngine(engine);
        if (twice)
            builder.AddWorkflowEngine(engine);

        after?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class RecordingClock : TimeProvider
    {
        public List<TimeSpan> TimerPeriods { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerPeriods.Add(period);
            return new NeverFires();
        }

        private sealed class NeverFires : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSignal : IWorkflowClusterSignal
    {
        public List<string> Subscribed { get; } = [];

        public Task PublishAsync(
            string channel, Guid id, string? payload = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
        {
            Subscribed.Add(channel);
            return new Nothing();
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
