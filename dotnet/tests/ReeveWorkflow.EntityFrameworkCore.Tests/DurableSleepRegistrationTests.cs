using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class DurableSleepRegistrationTests : IDisposable
{
    private const string Sleep = "test.sleep";
    private const string After = "test.after";

    private readonly SqliteConnection connection = new("Filename=:memory:");
    private readonly Blocks blocks = new();

    public DurableSleepRegistrationTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task Without_a_threshold_a_sleep_waits_in_the_process_as_it_always_has()
    {
        using var provider = Build();
        var workflowId = Seed(provider, sleepMs: 50);

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        (await SuspensionsAsync(provider)).Should().BeEmpty();
        blocks.Afters.Should().Be(1);
    }

    [Fact]
    public async Task With_a_threshold_a_long_sleep_parks_its_run_in_the_librarys_table()
    {
        using var provider = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1));
        var workflowId = Seed(provider, sleepMs: 600_000);

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
            .WaitAsync(TimeSpan.FromSeconds(30));

        var parked = (await SuspensionsAsync(provider)).Should().ContainSingle().Subject;
        parked.Kind.Should().Be(WorkflowSuspensionKind.Sleep);
        parked.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        blocks.Afters.Should().Be(0);
    }

    [Fact]
    public async Task A_run_too_big_for_the_stores_cap_sleeps_in_the_process_instead_of_failing()
    {
        using var provider = Build(
            engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMilliseconds(1),
            services => services.AddSingleton(new WorkflowSuspensionOptions { MaxStatePayloadBytes = 64 }));
        var workflowId = Seed(provider, sleepMs: 50);

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null);

        (await SuspensionsAsync(provider)).Should().BeEmpty();
        blocks.Afters.Should().Be(1);
    }

    [Fact]
    public async Task A_sleep_parked_by_one_container_is_finished_by_the_next()
    {
        Guid workflowId;
        using (var dying = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1)))
        {
            workflowId = Seed(dying, sleepMs: 600_000);
            await dying.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }

        using var surviving = Build(
            engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1), LaterClock);

        (await surviving.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync()).Should().Be(1);

        (await JournalAsync(surviving, workflowId)).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        blocks.Afters.Should().Be(1);
        (await SuspensionsAsync(surviving)).Single().Status.Should().Be(WorkflowSuspensionStatus.Resumed);
    }

    [Fact]
    public async Task A_sleep_parked_before_the_threshold_was_unset_is_finished_by_a_host_draining_it()
    {
        Guid workflowId;
        using (var parking = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1)))
        {
            workflowId = Seed(parking, sleepMs: 600_000);
            await parking.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }

        using var draining = Build(after: LaterClock);

        (await draining.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync()).Should().Be(1);
        (await JournalAsync(draining, workflowId)).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task The_composed_wake_service_cancels_and_saves_a_resumed_walk_when_it_stops()
    {
        Guid workflowId;
        using (var parking = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1)))
        {
            workflowId = Seed(parking, sleepMs: 600_000);
            await parking.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        using var stopping = Build(engine =>
        {
            engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
            engine.Waker = engine.Waker with { PollInterval = TimeSpan.FromMilliseconds(50) };
        }, LaterClock);
        blocks.HoldAfter = true;
        var service = stopping.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WorkflowSuspensionWakeService>().Single();
        await service.StartAsync(default);
        await blocks.AfterReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        blocks.AfterToken.IsCancellationRequested.Should().BeTrue("the walk took the tick's stopping token with it");
        (await JournalAsync(stopping, workflowId)).Status.Should().Be(WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_pass_stuck_in_the_store_does_not_hold_the_stop_off_the_resumed_walks()
    {
        Guid workflowId;
        using (var parking = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1)))
        {
            workflowId = Seed(parking, sleepMs: 600_000);
            await parking.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        using var stopping = Build(engine =>
        {
            engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
            engine.Waker = engine.Waker with { PollInterval = TimeSpan.FromMilliseconds(50) };
        }, LaterClock);
        blocks.HoldAfter = true;
        var service = stopping.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WorkflowSuspensionWakeService>().Single();
        await service.StartAsync(default);
        await blocks.AfterReached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var scan = (LaterScan)stopping.GetRequiredService<IWorkflowSuspensionResumeStore>();
        scan.HoldScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Delay(300);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = blocks.AfterToken.Register(() => cancelled.TrySetResult());

        var stop = service.StopAsync(CancellationToken.None);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        stop.IsCompleted.Should().BeFalse("the stuck pass has not let the service go yet");
        scan.HoldScan.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));
        (await JournalAsync(stopping, workflowId)).Status.Should().Be(WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_takeover_the_composed_waker_makes_is_written_to_the_hosts_audit_sink()
    {
        Guid workflowId;
        using (var dying = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1)))
        {
            workflowId = Seed(dying, sleepMs: 600_000);
            await dying.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }

        var audit = new RecordingAudit();
        using var surviving = Build(
            engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1),
            services =>
            {
                LaterClock(services);
                services.AddScoped<IWorkflowAuditSink>(_ => audit);
            });
        var parked = (await SuspensionsAsync(surviving)).Single();
        (await new EfWorkflowSuspensionResumeStore(surviving.GetRequiredService<IServiceScopeFactory>(), [])
            .TryResolveAsync(parked.Id, WorkflowSuspensionStatus.Resumed,
                WorkflowDurableSleep.OutcomeJson(parked.ConfigJson, parked.SuspendedAtUtc, parked.TimeoutAtUtc),
                DateTimeOffset.UtcNow, default))
            .Should().NotBeNull("this is the taker that dies before it re-enters the run");
        var taken = (await SuspensionsAsync(surviving)).Single();
        ((LaterScan)surviving.GetRequiredService<IWorkflowSuspensionResumeStore>()).Abandoned.Add(
            new WorkflowSuspensionCandidate(taken.Id, taken.ExecutionLogId, taken.WorkflowId, taken.BlockId, taken.Kind,
                taken.ConfigJson, taken.StatePayloadJson, taken.SuspendedAtUtc, taken.TimeoutAtUtc)
            {
                Status = taken.Status, OutcomeJson = taken.OutcomeJson, TakeCount = taken.TakeCount,
                TakenAtUtc = taken.ResolvedAtUtc
            });

        await surviving.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync();

        (await JournalAsync(surviving, workflowId)).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        blocks.Afters.Should().Be(1);
        audit.Written.Should().ContainSingle(written => written.Kind == WorkflowAuditEventKinds.RetakenByWaker)
            .Which.SubjectId.Should().Be(taken.ExecutionLogId,
                "a suspension event's subject is the run, as every other suspension kind's is");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_composed_waker_looks_for_abandoned_takes_whether_or_not_sleeps_park(bool threshold)
    {
        using var provider = Build(engine =>
        {
            if (threshold)
                engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
        }, LaterClock);

        await provider.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync();

        ((LaterScan)provider.GetRequiredService<IWorkflowSuspensionResumeStore>()).AbandonedLooks.Should().Be(1,
            "the registration defaults the cutoff from Recovery.AbandonedAfter, for every kind a waker takes");
    }

    [Fact]
    public async Task A_park_wakes_the_composed_wake_service_before_its_next_poll()
    {
        using var provider = Build(engine =>
        {
            engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
            engine.Waker = engine.Waker with { PollInterval = TimeSpan.FromHours(1) };
        }, LaterClock);
        var workflowId = Seed(provider, sleepMs: 600_000);
        var service = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WorkflowSuspensionWakeService>().Single();
        await service.StartAsync(default);

        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
            .WaitAsync(TimeSpan.FromSeconds(30));

        await ((LaterScan)provider.GetRequiredService<IWorkflowSuspensionResumeStore>()).Scanned
            .WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_park_signalled_the_moment_the_composed_wake_service_has_started_still_wakes_it()
    {
        using var provider = Build(engine =>
        {
            engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
            engine.Waker = engine.Waker with { PollInterval = TimeSpan.FromHours(1) };
        }, LaterClock);
        var service = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<WorkflowSuspensionWakeService>().Single();
        await service.StartAsync(default);

        provider.GetRequiredService<WorkflowSuspensionParkSignal>().Publish();

        await ((LaterScan)provider.GetRequiredService<IWorkflowSuspensionResumeStore>()).Scanned
            .WaitAsync(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_park_on_one_engine_is_not_published_to_the_park_signal_of_another()
    {
        using var parking = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1));
        using var bystander = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1));
        int heardByParking = 0, heardByBystander = 0;
        using var first = parking.GetRequiredService<WorkflowSuspensionParkSignal>().Subscribe(() => heardByParking++);
        using var second = bystander.GetRequiredService<WorkflowSuspensionParkSignal>().Subscribe(() => heardByBystander++);
        var workflowId = Seed(parking, sleepMs: 600_000);

        await parking.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
            .WaitAsync(TimeSpan.FromSeconds(30));

        (heardByParking, heardByBystander).Should().Be((1, 0));
    }

    [Fact]
    public async Task A_resumed_segment_that_parks_again_publishes_to_its_engines_signal()
    {
        Guid workflowId;
        using (var dying = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1)))
        {
            workflowId = Seed(dying, sleepMs: 600_000, secondSleepMs: 600_000);
            await dying.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null)
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        using var surviving = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1), LaterClock);
        var heard = 0;
        using var subscription = surviving.GetRequiredService<WorkflowSuspensionParkSignal>().Subscribe(() => heard++);

        (await surviving.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync()).Should().Be(1);

        (await SuspensionsAsync(surviving)).Should().HaveCount(2, "the second sleep parked its own row");
        heard.Should().Be(1);
    }

    [Fact]
    public async Task The_probe_answers_nothing_on_a_provider_it_has_no_query_for_so_the_waker_reads_its_pages()
    {
        using var provider = Build();

        var probe = await new EfWorkflowSuspensionResumeStore(provider.GetRequiredService<IServiceScopeFactory>(), [])
            .ProbeWakeAsync(TimeSpan.FromMinutes(1), WorkflowAbandonedTakeCursor.Start, default);

        probe.Should().BeNull();
    }

    [Fact]
    public void Without_a_threshold_resolving_the_resumer_does_not_resolve_the_engine()
    {
        var engineResolved = 0;
        using var provider = Build(after: services => services.AddSingleton<IWorkflowEngine>(_ =>
        {
            Interlocked.Increment(ref engineResolved);
            return new DecoratedEngine();
        }));

        provider.GetRequiredService<WorkflowRunResumer>().Should().NotBeNull();

        engineResolved.Should().Be(0, "a host whose engine depends on the resumer would otherwise build a cycle");
    }

    [Fact]
    public async Task The_composed_resumer_takes_its_lease_from_the_engines_own_coordinators()
    {
        using var provider = Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1), LaterClock);
        var workflowId = Seed(provider, sleepMs: 600_000);
        var engine = (WorkflowEngine)provider.GetRequiredService<IWorkflowEngine>();
        await engine.ExecuteAsync(workflowId, null, null).WaitAsync(TimeSpan.FromSeconds(30));
        var workflow = await WorkflowAsync(provider, workflowId);

        await using (var held = (await engine.Coordinators.For(workflow).TryAcquireForResumeAsync(default))!)
        {
            (await provider.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync()).Should().Be(0);
            (await SuspensionsAsync(provider)).Single().Status.Should().Be(WorkflowSuspensionStatus.Suspended,
                "a resume that could not take the lease puts the row back for the next pass");
        }

        (await provider.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_threshold_of_zero_or_less_is_refused_when_the_engine_is_composed(int seconds)
    {
        var build = () => Build(engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromSeconds(seconds));

        build.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*DurableSleep.ParkWhenAtLeast*");
    }

    [Fact]
    public void A_waker_limited_to_no_kind_at_all_is_refused_when_the_engine_is_composed()
    {
        var build = () => Build(engine => engine.Waker = engine.Waker with { Kinds = [] });

        build.Should().Throw<ArgumentException>().WithMessage("*Waker.Kinds*");
    }

    public enum Blind { WakerOff, KindsWithoutSleep }

    [Theory]
    [InlineData(Blind.WakerOff)]
    [InlineData(Blind.KindsWithoutSleep)]
    public void A_threshold_nothing_would_wake_is_refused_when_the_engine_is_composed(Blind blind)
    {
        var build = () => Build(engine =>
        {
            engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
            engine.Waker = blind == Blind.WakerOff
                ? engine.Waker with { Enabled = false }
                : engine.Waker with { Kinds = [WorkflowSuspensionKind.WaitTemplate] };
        });

        build.Should().Throw<InvalidOperationException>().WithMessage("*DurableSleep.ParkWhenAtLeast*");
    }

    [Fact]
    public void A_threshold_over_an_engine_whose_coordinators_the_resumer_cannot_reach_is_refused_when_it_is_built()
    {
        using var provider = Build(
            engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1),
            services => services.AddSingleton<IWorkflowEngine, DecoratedEngine>());

        var resolve = () => provider.GetRequiredService<WorkflowRunResumer>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*IWorkflowRunCoordinatorPool*");
    }

    [Fact]
    public void A_decorated_engine_whose_host_registers_its_coordinators_composes_a_resumer()
    {
        using var provider = Build(
            engine => engine.DurableSleep.ParkWhenAtLeast = TimeSpan.FromMinutes(1),
            services =>
            {
                services.AddSingleton<IWorkflowEngine, DecoratedEngine>();
                services.AddSingleton<IWorkflowRunCoordinatorPool>(new NoPool());
            });

        provider.GetRequiredService<WorkflowRunResumer>().Should().NotBeNull();
    }

    private sealed class DecoratedEngine : IWorkflowEngine
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(Guid workflowId, WorkflowFire? fire,
            IReadOnlyDictionary<string, object?>? inputParameters, Guid? parentExecutionId = null, int? iterationIndex = null,
            string? callStackJson = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsBlockIdempotentAsync(Guid workflowId, string blockPath, Guid? versionId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoPool : IWorkflowRunCoordinatorPool
    {
        public WorkflowRunCoordinator For(Workflow workflow) => throw new NotSupportedException();
    }

    private static void LaterClock(IServiceCollection services) =>
        services.AddSingleton<IWorkflowSuspensionResumeStore>(sp => new LaterScan(sp.GetRequiredService<IServiceScopeFactory>()));

    private ServiceProvider Build(Action<WorkflowEngineOptions>? engine = null, Action<IServiceCollection>? after = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IWorkflowBlocks>(blocks);

        services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(context => context.UseSqlite(connection));
                options.Defaults.DurableSleepBlockTypeIds.Add(Sleep);
            })
            .AddWorkflowEngine(options =>
            {
                options.SleepPollInterval = TimeSpan.FromMilliseconds(20);
                engine?.Invoke(options);
            });
        after?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        return provider;
    }

    private static Guid Seed(IServiceProvider provider, int sleepMs, int? secondSleepMs = null)
    {
        var second = secondSleepMs is { } ms
            ? $$$"""{"id":"nap2","blockTypeId":"{{{Sleep}}}","configuration":{"DurationMs":{{{ms}}}},"position":{"x":0,"y":0}},"""
            : "";
        var afterSource = secondSleepMs is null ? "nap" : "nap2";
        var secondEdge = secondSleepMs is null
            ? ""
            : """{"id":"e3","sourceNodeId":"nap","targetNodeId":"nap2","mappings":{}},""";
        var definitionJson = $$$"""
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
                      {"id":"nap","blockTypeId":"{{{Sleep}}}","configuration":{"DurationMs":{{{sleepMs}}}},"position":{"x":0,"y":0}},
                      {{{second}}}
                      {"id":"after","blockTypeId":"{{{After}}}","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"nap","mappings":{}},
                      {{{secondEdge}}}
                      {"id":"e2","sourceNodeId":"{{{afterSource}}}","targetNodeId":"after","mappings":{}}]}
            """;

        var workflowId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(workflowId, "wf", null, ownerId, now);
        db.Workflows.Add(workflow);
        db.SaveChanges();

        var version = new WorkflowVersion(
            Guid.NewGuid(), workflowId, 1, definitionJson, null, null, ownerId, now, isPublished: true);
        db.WorkflowVersions.Add(version);
        db.SaveChanges();

        workflow.SetCurrentVersion(version, now);
        db.SaveChanges();
        return workflowId;
    }

    private static async Task<List<WorkflowSuspension>> SuspensionsAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>()
            .WorkflowSuspensions.AsNoTracking().ToListAsync();
    }

    private static async Task<WorkflowExecutionLog> JournalAsync(IServiceProvider provider, Guid workflowId)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>()
            .WorkflowExecutionLogs.AsNoTracking().SingleAsync(log => log.WorkflowId == workflowId);
    }

    private static async Task<Workflow> WorkflowAsync(IServiceProvider provider, Guid workflowId)
    {
        using var scope = provider.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWorkflowStore>().LoadAsync(workflowId, default))!;
    }

    private sealed class LaterScan(IServiceScopeFactory scopes) : IWorkflowSuspensionResumeStore
    {
        private readonly EfWorkflowSuspensionResumeStore store = new(scopes, []);
        private int abandonedLooks;

        public int AbandonedLooks => abandonedLooks;

        public List<WorkflowSuspensionCandidate> Abandoned { get; } = [];

        public Task<WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor after,
            TimeSpan abandonedAfter, int limit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref abandonedLooks);
            List<WorkflowSuspensionCandidate> handedOut = [.. Abandoned];
            Abandoned.Clear();
            return Task.FromResult(new WorkflowAbandonedTakeScan(handedOut, after));
        }

        public Task<ResumeContext?> TryRetakeAsync(WorkflowSuspensionCandidate abandoned, CancellationToken cancellationToken) =>
            store.TryRetakeAsync(abandoned, cancellationToken);

        public TaskCompletionSource? HoldScan { get; set; }

        public Task Scanned => scanned.Task;

        private readonly TaskCompletionSource scanned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken)
        {
            scanned.TrySetResult();
            if (HoldScan is { } hold)
                await hold.Task;
            var now = DateTimeOffset.UtcNow.AddHours(1);
            using var scope = scopes.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowSuspensions
                .AsNoTracking().Where(row => row.Status == WorkflowSuspensionStatus.Suspended)
                .ToListAsync(cancellationToken);

            return new WorkflowSuspensionScan(now, rows
                .Where(row => row.TimeoutAtUtc <= now)
                .Select(row => new WorkflowSuspensionCandidate(row.Id, row.ExecutionLogId, row.WorkflowId, row.BlockId,
                    row.Kind, row.ConfigJson, row.StatePayloadJson, row.SuspendedAtUtc, row.TimeoutAtUtc))
                .ToList(), 0);
        }

        public Task<ResumeContext?> TryResolveAsync(Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) =>
            store.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            store.ReleaseAsync(suspensionId, takenAs, cancellationToken);
    }

    private sealed class RecordingAudit : IWorkflowAuditSink
    {
        private readonly List<WorkflowAuditEvent> events = [];

        public IReadOnlyList<WorkflowAuditEvent> Written
        {
            get { lock (events) return [.. events]; }
        }

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (events) events.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class Blocks : IWorkflowBlocks
    {
        private int afters;

        public int Afters => afters;

        public bool HoldAfter { get; set; }

        public TaskCompletionSource AfterReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken AfterToken { get; set; }

        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId switch
        {
            Sleep => new Factory(Sleep, () => new SleepBlock()),
            After => new Factory(After, () => new AfterBlock(
                () => Interlocked.Increment(ref afters), HoldAfter ? AfterReached : null, token => AfterToken = token)),
            _ => null
        };

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [Find(Sleep)!, Find(After)!];
    }

    private sealed class Factory(string id, Func<IWorkflowBlockPlugin> create) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => create();
    }

    private sealed class SleepBlock : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var ms = WorkflowExecutionHelpers.ResolveSleepDurationMs(inputs);
            await Task.Delay(TimeSpan.FromMilliseconds(ms), cancellationToken);
            return new Dictionary<string, object?> { ["Completed"] = true, ["DurationMs"] = ms };
        }
    }

    private sealed class AfterBlock(Action count, TaskCompletionSource? reached = null, Action<CancellationToken>? token = null)
        : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            count();
            token?.Invoke(cancellationToken);
            if (reached is not null)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return new Dictionary<string, object?>();
        }
    }
}

internal static class WakerWalks
{
    public static async Task<int> WakeAndWalkAsync(this WorkflowSuspensionWaker waker)
    {
        var moved = await waker.WakeOnceAsync(default);
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return moved;
    }
}
