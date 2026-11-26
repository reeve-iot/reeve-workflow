using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class ParkedSleepQueryTests(ClusterBackends backends)
{
    [Fact]
    public async Task The_single_wake_probe_reports_the_next_timeout_on_postgres()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_wake_probe");
        await using var container = FreightDesk.Container(postgres);
        using (var scope = container.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        var scopes = container.GetRequiredService<IServiceScopeFactory>();
        var store = new EfWorkflowSuspensionResumeStore(scopes, []);
        var suspensions = new EfWorkflowSuspensionStore(scopes, []);
        var workflow = await SeedAsync(scopes);
        var expected = new DateTimeOffset(
            DateTimeOffset.UtcNow.AddHours(2).Ticks / 10 * 10, TimeSpan.Zero);
        await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.Sleep, expected.AddHours(1));
        await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.Sleep, expected);

        var probe = await store.ProbeWakeAsync(null, WorkflowAbandonedTakeCursor.Start, default);

        probe.Should().NotBeNull();
        probe!.NextTimeoutAtUtc.Should().Be(expected);
        probe.HasTemplateOrSubWorkflow.Should().BeFalse();
        probe.HasAbandonedTake.Should().BeFalse();
    }

    [Fact]
    public async Task The_probe_flags_template_and_sub_workflow_waits_and_reads_the_next_timeout_from_parked_rows_only()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_wake_probe_kinds");
        await using var container = FreightDesk.Container(postgres);
        using (var scope = container.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        var scopes = container.GetRequiredService<IServiceScopeFactory>();
        var store = new EfWorkflowSuspensionResumeStore(scopes, []);
        var suspensions = new EfWorkflowSuspensionStore(scopes, []);
        var workflow = await SeedAsync(scopes);
        var now = DateTimeOffset.UtcNow;
        async Task<WorkflowSuspensionWakeProbe> ProbeAsync() =>
            (await store.ProbeWakeAsync(null, WorkflowAbandonedTakeCursor.Start, default))!;
        async Task ResolveAsync(WorkflowSuspension row) =>
            (await store.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed, "{}", now, default)).Should().NotBeNull();

        var resolvedEarly = await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.Sleep, now.AddHours(1));
        await ResolveAsync(resolvedEarly);
        (await ProbeAsync()).NextTimeoutAtUtc.Should().BeNull("a resolved row is not waiting for anything");

        var expected = Micro(now.AddHours(2));
        await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.Sleep, expected);
        await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.WaitForTrigger, now.AddHours(3));
        var probe = await ProbeAsync();
        probe.NextTimeoutAtUtc.Should().Be(expected, "the earliest timeout among rows still parked, not the resolved one's");
        probe.HasTemplateOrSubWorkflow.Should().BeFalse("a sleep and a trigger wait are read by the due query alone");

        var subWorkflow = await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.SubWorkflow, now.AddHours(4));
        (await ProbeAsync()).HasTemplateOrSubWorkflow.Should().BeTrue();
        await ResolveAsync(subWorkflow);
        (await ProbeAsync()).HasTemplateOrSubWorkflow.Should().BeFalse("it is resolved, so no page has to read it");

        var template = await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.WaitTemplate, now.AddHours(5));
        (await ProbeAsync()).HasTemplateOrSubWorkflow.Should().BeTrue();
        await ResolveAsync(template);
        (await ProbeAsync()).HasTemplateOrSubWorkflow.Should().BeFalse();
    }

    [Fact]
    public async Task The_probe_reports_an_abandoned_take_only_past_its_cursor_and_only_when_retakes_are_on()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_wake_probe_abandoned");
        await using var container = FreightDesk.Container(postgres);
        using (var scope = container.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        var scopes = container.GetRequiredService<IServiceScopeFactory>();
        var store = new EfWorkflowSuspensionResumeStore(scopes, []);
        var suspensions = new EfWorkflowSuspensionStore(scopes, []);
        var workflow = await SeedAsync(scopes);
        var cutoff = TimeSpan.FromMinutes(15);
        async Task<bool> AbandonedAsync(TimeSpan? after, WorkflowAbandonedTakeCursor cursor) =>
            (await store.ProbeWakeAsync(after, cursor, default))!.HasAbandonedTake;

        var sleeper = await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.Sleep, DateTimeOffset.UtcNow.AddSeconds(-5));
        var takenAt = Micro(DateTimeOffset.UtcNow.AddHours(-1));
        (await store.TryResolveAsync(sleeper.Id, WorkflowSuspensionStatus.Resumed, "{}", takenAt, default)).Should().NotBeNull();

        (await AbandonedAsync(cutoff, WorkflowAbandonedTakeCursor.Start)).Should().BeTrue("taken an hour ago on a run still running");
        (await AbandonedAsync(TimeSpan.FromHours(2), WorkflowAbandonedTakeCursor.Start)).Should().BeFalse("taken an hour ago, not two");
        (await AbandonedAsync(null, WorkflowAbandonedTakeCursor.Start)).Should().BeFalse("a waker with retakes off has nothing to retake");

        (await AbandonedAsync(cutoff, new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = takenAt.AddSeconds(-1) }))
            .Should().BeTrue("the take is after the cursor");
        (await AbandonedAsync(cutoff, new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = takenAt }))
            .Should().BeFalse("a cursor at the take's instant is past it");
        (await AbandonedAsync(cutoff, new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = takenAt.AddSeconds(1) }))
            .Should().BeFalse("the probe does not look below its cursor");
        (await AbandonedAsync(cutoff, new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = takenAt, AfterSuspensionId = sleeper.Id }))
            .Should().BeFalse("at the same instant, after this wait's own id");
        (await AbandonedAsync(cutoff, new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = takenAt, AfterSuspensionId = Guid.Empty }))
            .Should().BeTrue("at the same instant, before this wait's id");

        await suspensions.SuspendAsync(new SuspendRequest
        {
            ExecutionLogId = sleeper.ExecutionLogId, WorkflowId = workflow.WorkflowId, BlockId = "nap2",
            Kind = WorkflowSuspensionKind.Sleep, ConfigJson = sleeper.ConfigJson, StatePayloadJson = "{}",
            SuspendedAtUtc = DateTimeOffset.UtcNow, TimeoutAtUtc = DateTimeOffset.UtcNow.AddHours(2)
        }, default);
        (await AbandonedAsync(cutoff, WorkflowAbandonedTakeCursor.Start))
            .Should().BeFalse("the run has parked again since, so the old take is not its park");

        var ended = await ParkAsync(scopes, suspensions, workflow, WorkflowSuspensionKind.Sleep, DateTimeOffset.UtcNow.AddSeconds(-5));
        (await store.TryResolveAsync(ended.Id, WorkflowSuspensionStatus.Resumed, "{}", takenAt, default)).Should().NotBeNull();
        (await AbandonedAsync(cutoff, WorkflowAbandonedTakeCursor.Start)).Should().BeTrue();
        await EndRunAsync(scopes, ended.ExecutionLogId);
        (await AbandonedAsync(cutoff, WorkflowAbandonedTakeCursor.Start)).Should().BeFalse("its run has ended");
    }

    [Fact]
    public async Task The_parked_sleep_queries_and_a_sleep_only_scan_run_on_postgres()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_parked_sleep_queries");
        await using var container = FreightDesk.Container(postgres);
        using (var scope = container.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        var scopes = container.GetRequiredService<IServiceScopeFactory>();
        var store = new EfWorkflowSuspensionStore(scopes, []);
        var resume = new EfWorkflowSuspensionResumeStore(scopes, []);
        var workflow = await SeedAsync(scopes);
        var elsewhere = await SeedAsync(scopes);
        var now = DateTimeOffset.UtcNow;

        var due = await ParkAsync(scopes, store, workflow, WorkflowSuspensionKind.Sleep, now.AddSeconds(-5));
        var notDue = await ParkAsync(scopes, store, workflow, WorkflowSuspensionKind.Sleep, now.AddHours(1));
        var dueTemplate = await ParkAsync(scopes, store, workflow, WorkflowSuspensionKind.WaitTemplate, now.AddSeconds(-5));
        var otherSleep = await ParkAsync(scopes, store, elsewhere, WorkflowSuspensionKind.Sleep, now.AddHours(1));

        (await store.HasParkedSleepAsync(workflow.WorkflowId, default)).Should().BeTrue();
        var sleepsOnly = await resume.ScanAsync(WorkflowSuspensionScanCursor.Start, 50, [WorkflowSuspensionKind.Sleep], default);
        sleepsOnly.Candidates.Select(c => c.SuspensionId).Should().Equal(due.Id);

        var waker = new WorkflowSuspensionWaker(resume, new TakesNothingFurther(), NullLogger.Instance,
            new WorkflowCompilationService(new RegisteredWorkflowBlocks([]), new NoRunScopes()),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, Kinds = [WorkflowSuspensionKind.Sleep] });
        (await waker.WakeOnceAsync(default)).Should().Be(1);

        var listed = await store.ListParkedSleepsAsync(workflow.WorkflowId, default);
        listed.Select(s => (s.SuspensionId, s.Status)).Should().BeEquivalentTo(new[]
        {
            (due.Id, WorkflowSuspensionStatus.Resumed), (notDue.Id, WorkflowSuspensionStatus.Suspended)
        }, "a sleep just taken still counts until its run ends");

        await EndRunAsync(scopes, due.ExecutionLogId);
        (await store.ListParkedSleepsAsync(workflow.WorkflowId, default)).Select(s => s.SuspensionId)
            .Should().Equal(notDue.Id);
        (await store.CancelParkedSleepAsync(notDue.Id, default)).Should().BeTrue();
        (await store.HasParkedSleepAsync(workflow.WorkflowId, default)).Should().BeFalse();

        using var read = container.CreateScope();
        var rows = await read.ServiceProvider.GetRequiredService<WorkflowDbContext>()
            .WorkflowSuspensions.AsNoTracking().ToDictionaryAsync(row => row.Id);
        rows[due.Id].OutcomeJson.Should().Contain("\"_resumeReason\":\"sleep\"");
        rows[notDue.Id].Status.Should().Be(WorkflowSuspensionStatus.Cancelled);
        rows[dueTemplate.Id].Status.Should().Be(WorkflowSuspensionStatus.Suspended, "the pass was limited to sleeps");
        rows[otherSleep.Id].Status.Should().Be(WorkflowSuspensionStatus.Suspended);
    }

    [Fact]
    public async Task The_newer_run_and_abandoned_take_queries_run_on_postgres()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_parked_sleep_restart_and_abandoned");
        await using var container = FreightDesk.Container(postgres);
        using (var scope = container.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        var scopes = container.GetRequiredService<IServiceScopeFactory>();
        var store = new EfWorkflowSuspensionStore(scopes, []);
        var resume = new EfWorkflowSuspensionResumeStore(scopes, []);
        var workflow = await SeedAsync(scopes);
        var elsewhere = await SeedAsync(scopes);

        var sleeper = await ParkAsync(scopes, store, workflow, WorkflowSuspensionKind.Sleep, DateTimeOffset.UtcNow.AddSeconds(-5));
        var parkedAt = DateTimeOffset.UtcNow;
        (await store.HasRunStartedSinceAsync(workflow.WorkflowId, sleeper.ExecutionLogId, parkedAt.AddMinutes(-1), default))
            .Should().BeFalse("the sleeper's own run is not a newer one");
        await ParkAsync(scopes, store, elsewhere, WorkflowSuspensionKind.WaitTemplate, DateTimeOffset.UtcNow.AddHours(1));
        (await store.HasRunStartedSinceAsync(workflow.WorkflowId, sleeper.ExecutionLogId, parkedAt.AddMinutes(-1), default))
            .Should().BeFalse("a run of another workflow does not supersede it");
        await ParkAsync(scopes, store, workflow, WorkflowSuspensionKind.WaitTemplate, DateTimeOffset.UtcNow.AddHours(1));
        (await store.HasRunStartedSinceAsync(workflow.WorkflowId, sleeper.ExecutionLogId, parkedAt.AddMinutes(-1), default))
            .Should().BeTrue();
        (await store.HasRunStartedSinceAsync(workflow.WorkflowId, sleeper.ExecutionLogId, DateTimeOffset.UtcNow.AddMinutes(1), default))
            .Should().BeFalse("a run that started before the park does not supersede it");

        var outcome = WorkflowDurableSleep.OutcomeJson(sleeper.ConfigJson, sleeper.SuspendedAtUtc, sleeper.TimeoutAtUtc);
        (await resume.TryResolveAsync(sleeper.Id, WorkflowSuspensionStatus.Resumed, outcome, DateTimeOffset.UtcNow.AddHours(-1), default))
            .Should().NotBeNull();

        var abandoned = await resume.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, TimeSpan.FromMinutes(15), 10, default);
        abandoned.Candidates.Select(t => (t.SuspensionId, t.Status)).Should().Equal((sleeper.Id, WorkflowSuspensionStatus.Resumed));
        (await resume.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, TimeSpan.FromHours(2), 10, default))
            .Candidates.Should().BeEmpty("taken an hour ago, not two");

        var firstLook = abandoned.Candidates.Single();
        (await resume.TryRetakeAsync(firstLook, default)).Should().NotBeNull();
        (await resume.TryRetakeAsync(firstLook, default)).Should().BeNull("the row is a later take's now");

        (await resume.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, TimeSpan.Zero, 10, default))
            .Candidates.Select(t => t.SuspensionId).Should().Equal([sleeper.Id], "a retake leaves the wait taken, not resolved");

        await store.SuspendAsync(new SuspendRequest
        {
            ExecutionLogId = sleeper.ExecutionLogId, WorkflowId = workflow.WorkflowId, BlockId = "nap2",
            Kind = WorkflowSuspensionKind.Sleep, ConfigJson = sleeper.ConfigJson, StatePayloadJson = "{}",
            SuspendedAtUtc = DateTimeOffset.UtcNow, TimeoutAtUtc = DateTimeOffset.UtcNow.AddHours(2)
        }, default);
        (await resume.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, TimeSpan.Zero, 10, default))
            .Candidates.Should().BeEmpty("the run has parked again since, so the old take is not its park");
        await EndRunAsync(scopes, sleeper.ExecutionLogId);
        (await resume.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, TimeSpan.Zero, 10, default))
            .Candidates.Should().BeEmpty("its run has ended");
    }

    private static DateTimeOffset Micro(DateTimeOffset instant) => new(instant.Ticks / 10 * 10, TimeSpan.Zero);

    private static async Task<(Guid WorkflowId, Guid VersionId)> SeedAsync(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(Guid.NewGuid(), "sleeps", null, FreightDesk.Operator, now);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var version = new WorkflowVersion(
            Guid.NewGuid(), workflow.Id, 1, "{\"nodes\":[],\"edges\":[]}", null, null, FreightDesk.Operator, now, isPublished: true);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();
        return (workflow.Id, version.Id);
    }

    private static async Task<WorkflowSuspension> ParkAsync(
        IServiceScopeFactory scopes, EfWorkflowSuspensionStore store, (Guid WorkflowId, Guid VersionId) workflow,
        WorkflowSuspensionKind kind, DateTimeOffset timeoutAtUtc)
    {
        var log = new WorkflowExecutionLog(
            Guid.NewGuid(), null, workflow.WorkflowId, workflow.VersionId, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            db.WorkflowExecutionLogs.Add(log);
            await db.SaveChangesAsync();
        }

        return await store.SuspendAsync(new SuspendRequest
        {
            ExecutionLogId = log.Id,
            WorkflowId = workflow.WorkflowId,
            BlockId = "nap",
            Kind = kind,
            ConfigJson = kind == WorkflowSuspensionKind.Sleep
                ? JsonSerializer.Serialize(new WorkflowSleepConfig(1000, timeoutAtUtc))
                : "{}",
            StatePayloadJson = "{}",
            SuspendedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            TimeoutAtUtc = timeoutAtUtc
        }, default);
    }

    private static async Task EndRunAsync(IServiceScopeFactory scopes, Guid executionLogId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var log = await db.WorkflowExecutionLogs.SingleAsync(row => row.Id == executionLogId);
        log.Complete(WorkflowExecutionStatus.Cancelled, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private sealed class TakesNothingFurther : IWorkflowRunResumer
    {
        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken) =>
            Task.FromResult(WorkflowResumeOutcome.Resumed);
    }

    private sealed class NoRunScopes : IWorkflowRunScopeFactory, IWorkflowRunScope
    {
        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();
        public IWorkflowStore Store => throw new NotSupportedException();
        public IWorkflowRunJournal Journal => throw new NotSupportedException();
        public IWorkflowAuditSink Audit => throw new NotSupportedException();
        public IWorkflowAuthorization Authorization => throw new NotSupportedException();
        public IWorkflowRunScope CreateScope() => this;
        public void Dispose() { }
    }
}
