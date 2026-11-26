using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class CompilationSweepServiceTests : IDisposable
{
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string Invalid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"gone.block","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly ConcurrentQueue<Logged> logged = new();
    private readonly List<IDisposable> owned = [];

    public void Dispose()
    {
        foreach (var disposable in owned)
            disposable.Dispose();
    }

    [Fact]
    public async Task A_host_that_does_not_opt_in_gets_no_timer_and_can_still_sweep_on_demand()
    {
        var database = Database();
        var seeded = Seed(database, Valid);

        database.Services.GetServices<IHostedService>().OfType<WorkflowCompilationSweepService>().Should().BeEmpty();
        var run = await Runner(database).RunOnceAsync(default);

        run!.Report.Current.Should().ContainSingle().Which.WorkflowVersionId.Should().Be(seeded);
    }

    [Fact]
    public async Task On_demand_the_runner_sweeps_then_deletes_and_returns_the_report()
    {
        var database = Database();
        var valid = Seed(database, Valid);
        var invalid = Seed(database, Invalid);
        var store = database.Services.GetRequiredService<IWorkflowCompilationStore>();
        var gone = Guid.NewGuid();
        await store.SaveAsync(Verdict(gone, DateTimeOffset.UtcNow.AddDays(-90)), default);

        var run = await Runner(database).RunOnceAsync(default);

        run!.Report.Current.Select(k => k.WorkflowVersionId).Should().BeEquivalentTo([valid, invalid]);
        run.Report.Invalid.Should().ContainSingle().Which.WorkflowVersionId.Should().Be(invalid);
        run.Deleted.Should().Be(1, "the row of a version that no longer exists is older than the retention");
        (await store.FindAsync(gone, "digest", "hash", default)).Should().BeNull();
        foreach (var key in run.Report.Current)
            (await store.FindAsync(key.WorkflowVersionId, key.DefinitionDigest, key.DependencyHash, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task The_configured_retention_is_the_one_the_run_deletes_by()
    {
        var database = Database(workflows: options => options.CompilationSweep.Retention = TimeSpan.FromHours(2));
        Seed(database, Valid);
        var store = database.Services.GetRequiredService<IWorkflowCompilationStore>();
        await store.SaveAsync(Verdict(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-3)), default);
        await store.SaveAsync(Verdict(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1)), default);

        var run = await Runner(database).RunOnceAsync(default);

        run!.Deleted.Should().Be(1, "only the gone version's row older than two hours goes");
    }

    [Fact]
    public async Task A_change_to_the_sweep_options_after_AddWorkflows_does_not_reach_the_runs_retention()
    {
        WorkflowCompilationSweepOptions? given = null;
        var database = Database(workflows: options => given = options.CompilationSweep);
        given!.Retention = WorkflowCompilationSweeper.MinimumRetention;
        Seed(database, Valid);
        var store = database.Services.GetRequiredService<IWorkflowCompilationStore>();
        await store.SaveAsync(Verdict(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-3)), default);

        var run = await Runner(database).RunOnceAsync(default);

        run!.Deleted.Should().Be(0, "the retention is the 30 days AddWorkflows was given, not the hour set after it");
    }

    [Fact]
    public async Task A_change_to_the_sweep_options_after_AddWorkflows_does_not_reach_the_timer()
    {
        WorkflowCompilationSweepOptions? given = null;
        var host = Hosted(workflows: options => given = options.CompilationSweep);
        given!.Interval = TimeSpan.FromMinutes(2);
        await host.Service.StartAsync(default);
        host.Lifetime.Start();
        await host.Gate.Entered(1).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        host.Clock.NextWait.Should().Be(Interval);
        await host.Service.StopAsync(default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_fault_after_the_report_is_logged_with_the_counts_and_then_each_version_that_does_not_compile()
    {
        var failing = new FailingRetention();
        var database = Database(workflows: options => options.Seams.CompilationStore.Use(sp => new FailingDeleteStore(
            new EfWorkflowCompilationStore(sp.GetRequiredService<IServiceScopeFactory>()), failing)));
        var invalid = Seed(database, Invalid);

        var running = () => Runner(database).RunOnceAsync(default);

        await running.Should().ThrowAsync<InvalidOperationException>();
        var runner = Of<WorkflowCompilationSweepRunner>();
        runner.Select(l => l.Level).Should().Equal(LogLevel.Error, LogLevel.Warning);
        runner[0].Message.Should().Contain("1 invalid").And.Contain("the retention did not finish");
        runner[1].Message.Should().Contain(invalid.ToString()).And.Contain(CompilationRules.BlockTypeUnknown);
    }

    [Fact]
    public async Task An_on_demand_caller_hears_a_cancellation_it_did_not_ask_for_and_the_runner_logs_it()
    {
        var timeout = new OperationCanceledException("the lock provider's own timeout");
        var database = Database(services: services => services.AddSingleton<IWorkflowLeaderGate>(new RecordingGate { FailFirst = timeout }));

        var running = () => Runner(database).RunOnceAsync(default);

        (await running.Should().ThrowAsync<OperationCanceledException>()).Which.Should().BeSameAs(timeout);
        Of<WorkflowCompilationSweepRunner>().Should().ContainSingle(l => l.Level == LogLevel.Error).Which.Error.Should().BeSameAs(timeout);
    }

    [Fact]
    public void A_compiler_runner_or_sweeper_the_host_registered_before_AddWorkflows_is_the_one_that_answers()
    {
        WorkflowCompilationService? compiler = null;
        WorkflowCompilationSweepRunner? runner = null;
        var database = Database(services: services =>
        {
            services.AddSingleton(sp => compiler = new WorkflowCompilationService(
                sp.GetRequiredService<IWorkflowBlocks>(), sp.GetRequiredService<IWorkflowRunScopeFactory>()));
            services.AddSingleton(sp => runner = new WorkflowCompilationSweepRunner(
                sp.GetRequiredService<IServiceScopeFactory>(), new WorkflowCompilationSweepOptions(), new RecordingGate()));
            services.AddScoped(sp => new WorkflowCompilationSweeper(sp.GetRequiredService<WorkflowCompilationService>(), null)
            {
                Retention = TimeSpan.FromDays(7),
            });
        });
        using var scope = database.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<WorkflowCompilationService>().Should().BeSameAs(compiler);
        scope.ServiceProvider.GetRequiredService<WorkflowCompilationSweepRunner>().Should().BeSameAs(runner);
        scope.ServiceProvider.GetRequiredService<WorkflowCompilationSweeper>().Retention.Should().Be(TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task An_on_demand_caller_hears_the_fault_the_run_logged()
    {
        var failing = new FailingRetention();
        var database = Database(workflows: options => options.Seams.CompilationStore.Use(sp => new FailingDeleteStore(
            new EfWorkflowCompilationStore(sp.GetRequiredService<IServiceScopeFactory>()), failing)));
        Seed(database, Valid);

        var running = () => Runner(database).RunOnceAsync(default);

        (await running.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failing.Thrown);
        Of<WorkflowCompilationSweepRunner>().Should().ContainSingle(l => l.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_run_logs_its_counts_and_then_each_version_that_does_not_compile()
    {
        var database = Database();
        var invalid = Seed(database, Invalid);

        await Runner(database).RunOnceAsync(default);

        var runner = Of<WorkflowCompilationSweepRunner>();
        runner.Select(l => l.Level).Should().Equal(LogLevel.Information, LogLevel.Warning);
        runner[0].Message.Should().Contain("1 current").And.Contain("1 invalid").And.Contain("0 rows deleted");
        runner[1].Message.Should().Contain(invalid.ToString()).And.Contain(CompilationRules.BlockTypeUnknown);
    }

    [Fact]
    public async Task A_run_logs_each_version_it_could_not_answer_and_each_verdict_it_did_not_save()
    {
        var fault = new InvalidOperationException("the resolver is down");
        var database = Database(
            workflows: options => options.Seams.CompilationStore.Use(sp => new FailingSaveStore(
                new EfWorkflowCompilationStore(sp.GetRequiredService<IServiceScopeFactory>()))),
            services: services =>
            {
                services.AddSingleton<IWorkflowBlockFactory>(new CallFactory());
                services.AddSingleton<INodePortResolver>(new ThrowingResolver(fault));
            });
        var unsaved = Seed(database, Valid);
        var failed = Seed(database, """{"nodes":[{"id":"call","blockTypeId":"Host.Call","configuration":{"Callee":"x"},"position":{"x":0,"y":0}}],"edges":[]}""");

        await Runner(database).RunOnceAsync(default);

        var runner = Of<WorkflowCompilationSweepRunner>();
        runner.Select(l => l.Level).Should().Equal(LogLevel.Information, LogLevel.Warning, LogLevel.Warning);
        runner[0].Message.Should().Contain("1 current").And.Contain("1 failed").And.Contain("1 unsaved");
        runner[1].Message.Should().Contain(failed.ToString());
        runner[1].Error.Should().BeSameAs(fault);
        runner[2].Message.Should().Contain(unsaved.ToString());
    }

    [Fact]
    public async Task The_run_goes_through_the_leader_gate_under_its_own_key()
    {
        var gate = new RecordingGate();
        var database = Database(services: services => services.AddSingleton<IWorkflowLeaderGate>(gate));
        Seed(database, Valid);

        var run = await Runner(database).RunOnceAsync(default);

        gate.Keys.Should().Equal(WorkflowCompilationSweepRunner.LeaderLockKey);
        WorkflowCompilationSweepRunner.LeaderLockKey.Should().Be("WorkflowCompilationSweep_Leader");
        run.Should().NotBeNull();
    }

    [Fact]
    public async Task A_replica_that_does_not_lead_neither_sweeps_nor_deletes()
    {
        var gate = new RecordingGate { Leads = false };
        var database = Database(services: services => services.AddSingleton<IWorkflowLeaderGate>(gate));
        Seed(database, Valid);
        var store = database.Services.GetRequiredService<IWorkflowCompilationStore>();
        var gone = Guid.NewGuid();
        await store.SaveAsync(Verdict(gone, DateTimeOffset.UtcNow.AddDays(-90)), default);

        var run = await Runner(database).RunOnceAsync(default);

        run.Should().BeNull();
        (await store.FindAsync(gone, "digest", "hash", default)).Should().NotBeNull("nothing was deleted");
        (await CountVerdictsAsync(database)).Should().Be(1, "nothing was swept");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task With_no_gate_registered_the_runner_takes_the_gate_derived_from_the_run_lock_provider(bool lockProvider)
    {
        var locks = new DenyingLocks();
        var database = Database(workflows: lockProvider ? options => options.Seams.RunLockProvider.UseInstance(locks) : null);
        Seed(database, Valid);

        var run = await Runner(database).RunOnceAsync(default);

        if (lockProvider)
        {
            run.Should().BeNull("a peer holds the key");
            locks.Asked.Should().Equal(WorkflowCompilationSweepRunner.LeaderLockKey);
        }
        else
        {
            run.Should().NotBeNull("a host with no lock provider sweeps on every replica");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_leader_gate_the_host_adds_with_TryAdd_on_either_side_of_AddWorkflows_is_the_one_the_run_goes_through(bool after)
    {
        var gate = new RecordingGate();
        Action<IServiceCollection> register = services => services.TryAddSingleton<IWorkflowLeaderGate>(gate);
        var database = after ? Database(after: register) : Database(services: register);
        Seed(database, Valid);

        var run = await Runner(database).RunOnceAsync(default);

        gate.Keys.Should().Equal(WorkflowCompilationSweepRunner.LeaderLockKey);
        run.Should().NotBeNull();
    }

    [Fact]
    public async Task Two_runs_on_one_replica_never_overlap()
    {
        var gate = new RecordingGate { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var database = Database(services: services => services.AddSingleton<IWorkflowLeaderGate>(gate));
        Seed(database, Valid);
        var runner = Runner(database);

        var first = runner.RunOnceAsync(default);
        await gate.Entered(1).WaitAsync(Deadline);
        var second = runner.RunOnceAsync(default);
        var enteredWhileTheFirstHeld = gate.Keys.Count;
        gate.Hold.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Deadline);

        enteredWhileTheFirstHeld.Should().Be(1);
        gate.MostAtOnce.Should().Be(1);
        gate.Keys.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_run_cancelled_while_it_waits_for_the_one_before_it_does_not_let_a_third_overlap_the_first()
    {
        var gate = new RecordingGate { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var database = Database(services: services => services.AddSingleton<IWorkflowLeaderGate>(gate));
        Seed(database, Valid);
        var runner = Runner(database);
        var first = runner.RunOnceAsync(default);
        await gate.Entered(1).WaitAsync(Deadline);
        using var cancel = new CancellationTokenSource();
        var second = runner.RunOnceAsync(cancel.Token);
        await cancel.CancelAsync();
        var cancelled = () => second.WaitAsync(Deadline);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        var third = runner.RunOnceAsync(default);
        var enteredWhileTheFirstHeld = gate.Keys.Count;
        gate.Hold.SetResult();
        await Task.WhenAll(first, third).WaitAsync(Deadline);

        enteredWhileTheFirstHeld.Should().Be(1, "the gate is entered before RunOnceAsync returns");
        gate.MostAtOnce.Should().Be(1);
    }

    [Fact]
    public async Task A_database_whose_model_has_a_change_no_migration_covers_is_not_swept()
    {
        var database = Database(context: builder => builder.ReplaceService<IMigrationsAssembly, OneMigrationAndNoSnapshot>());
        using (var scope = database.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var history = db.GetService<IHistoryRepository>();
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(OneMigrationAndNoSnapshot.Id, "10.0.11")));
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        }

        Seed(database, Valid);

        var run = await Runner(database).RunOnceAsync(default);

        run.Should().BeNull();
        (await CountVerdictsAsync(database)).Should().Be(0);
        Of<WorkflowCompilationSweepRunner>().Should().ContainSingle().Which.Should().Match<Logged>(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("no migration covers") && l.Error == null);
    }

    [Fact]
    public async Task An_EnsureCreated_database_is_swept_although_VerifyOnly_refuses_it()
    {
        var database = Database();
        Seed(database, Valid);

        var verifying = () => database.Services.EnsureWorkflowsDatabaseAsync(WorkflowDatabaseMode.VerifyOnly);
        var run = await Runner(database).RunOnceAsync(default);

        await verifying.Should().ThrowAsync<WorkflowDatabaseNotReadyException>().WithMessage("*no migration covers*");
        run.Should().NotBeNull();
    }

    [Fact]
    public async Task The_listing_is_read_in_pages_of_the_configured_size_and_no_two_answers_overlap()
    {
        var pages = new PageReads();
        var inFlight = new InFlight();
        var database = Database(
            workflows: options =>
            {
                options.CompilationSweep.PageSize = 2;
                options.Seams.CompilationStore.Use(sp => new OverlapRecordingStore(
                    new EfWorkflowCompilationStore(sp.GetRequiredService<IServiceScopeFactory>()), inFlight));
            },
            context: builder => builder.AddInterceptors(pages));
        for (var i = 0; i < 5; i++)
            Seed(database, Valid);
        pages.Reset();

        var run = await Runner(database).RunOnceAsync(default);

        run!.Report.Current.Should().HaveCount(5);
        pages.Reads.Should().Be(3, "five workflows in pages of two");
        inFlight.Calls.Should().BeGreaterThanOrEqualTo(5);
        inFlight.MostAtOnce.Should().Be(1);
    }

    [Theory]
    [InlineData("Interval", 59)]
    [InlineData("PageSize", 0)]
    [InlineData("PageSize", 10_001)]
    [InlineData("Retention", 3_599)]
    [InlineData("ReadinessRetry", 0)]
    [InlineData("ReadinessRetry", 21_601)]
    public void An_interval_page_size_retention_or_readiness_retry_out_of_bounds_is_refused_at_AddWorkflows(string option, int value)
    {
        var building = () => Database(workflows: options =>
        {
            var sweep = options.CompilationSweep;
            switch (option)
            {
                case "Interval": sweep.Interval = TimeSpan.FromSeconds(value); break;
                case "PageSize": sweep.PageSize = value; break;
                case "Retention": sweep.Retention = TimeSpan.FromSeconds(value); break;
                default: sweep.ReadinessRetry = TimeSpan.FromSeconds(value); break;
            }
        });

        building.Should().Throw<InvalidOperationException>().WithMessage($"*CompilationSweep.{option}*");
    }

    [Fact]
    public void An_interval_or_readiness_retry_longer_than_the_longest_delay_is_refused_at_AddWorkflows()
    {
        var oneTickOver = WorkflowCompilationSweepOptions.MaximumInterval + TimeSpan.FromTicks(1);

        foreach (var interval in new[] { oneTickOver, TimeSpan.MaxValue })
        {
            var building = () => Database(workflows: options => options.CompilationSweep.Interval = interval);
            building.Should().Throw<InvalidOperationException>().WithMessage("*CompilationSweep.Interval*");
        }

        var retrying = () => Database(workflows: options => options.CompilationSweep.ReadinessRetry = oneTickOver);
        retrying.Should().Throw<InvalidOperationException>().WithMessage("*CompilationSweep.ReadinessRetry*");
    }

    [Fact]
    public void The_bounds_themselves_are_accepted()
    {
        var building = () => Database(workflows: options =>
        {
            options.CompilationSweep.Interval = TimeSpan.FromMinutes(1);
            options.CompilationSweep.ReadinessRetry = TimeSpan.FromMinutes(1);
            options.CompilationSweep.PageSize = 10_000;
            options.CompilationSweep.Retention = WorkflowCompilationSweeper.MinimumRetention;
        });

        building.Should().NotThrow();
        Database(workflows: options => options.CompilationSweep.PageSize = 1).Should().NotBeNull();
        Database(workflows: options =>
        {
            options.CompilationSweep.Interval = WorkflowCompilationSweepOptions.MaximumInterval;
            options.CompilationSweep.ReadinessRetry = WorkflowCompilationSweepOptions.MaximumInterval;
        }).Should().NotBeNull();
    }

    [Fact]
    public async Task The_first_run_follows_the_host_start_and_the_next_one_each_interval()
    {
        var host = Hosted();
        await host.Service.StartAsync(default);
        await host.Lifetime.Asked.WaitAsync(Deadline);

        host.Gate.Keys.Should().BeEmpty("nothing runs before the host has started");
        host.Clock.Armed.Should().Be(0);

        host.Lifetime.Start();
        await host.Gate.Entered(1).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);
        host.Clock.NextWait.Should().Be(Interval);
        host.Clock.Advance(Interval);
        await host.Gate.Entered(2).WaitAsync(Deadline);

        await host.Service.StopAsync(default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_replica_that_found_the_database_not_ready_asks_again_after_the_readiness_retry()
    {
        TogglingMigrations.Present = true;
        var host = Hosted(context: builder => builder.ReplaceService<IMigrationsAssembly, TogglingMigrations>());
        await host.Service.StartAsync(default);
        host.Lifetime.Start();
        await host.Gate.Entered(1).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        host.Clock.NextWait.Should().Be(ReadinessRetry);
        TogglingMigrations.Present = false;
        host.Clock.Advance(ReadinessRetry);
        await host.Gate.Entered(2).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        (await CountVerdictsAsync(host.Database)).Should().Be(1, "the second run found it ready and swept");
        await host.Service.StopAsync(default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_faulting_retention_is_logged_with_the_counts_and_the_timer_runs_again_after_the_interval()
    {
        var failing = new FailingRetention();
        var host = Hosted(workflows: options => options.Seams.CompilationStore.Use(sp => new FailingDeleteStore(
            new EfWorkflowCompilationStore(sp.GetRequiredService<IServiceScopeFactory>()), failing)));
        await host.Service.StartAsync(default);
        host.Lifetime.Start();
        await host.Gate.Entered(1).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        var error = Of<WorkflowCompilationSweepRunner>().Should().ContainSingle(l => l.Level == LogLevel.Error).Which;
        error.Error.Should().BeSameAs(failing.Thrown);
        error.Message.Should().Contain("1 current");
        host.Clock.NextWait.Should().Be(Interval);
        host.Clock.Advance(Interval);
        await host.Gate.Entered(2).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        failing.Calls.Should().Be(2);
        await host.Service.StopAsync(default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_fault_before_the_report_is_logged_and_the_timer_runs_again_after_the_interval()
    {
        var fault = new InvalidOperationException("the lock provider is down");
        var host = Hosted(gate: new RecordingGate { FailFirst = fault });
        await host.Service.StartAsync(default);
        host.Lifetime.Start();
        await host.Gate.Entered(1).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        Of<WorkflowCompilationSweepRunner>().Should().ContainSingle(l => l.Level == LogLevel.Error)
            .Which.Error.Should().BeSameAs(fault);
        host.Clock.NextWait.Should().Be(Interval);
        host.Clock.Advance(Interval);
        await host.Gate.Entered(2).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        (await CountVerdictsAsync(host.Database)).Should().Be(1, "the next interval swept");
        await host.Service.StopAsync(default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_replica_that_did_not_lead_waits_the_interval_not_the_readiness_retry()
    {
        var host = Hosted(gate: new RecordingGate { Leads = false });
        await host.Service.StartAsync(default);
        host.Lifetime.Start();
        await host.Gate.Entered(1).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        host.Clock.NextWait.Should().Be(Interval);
        await host.Service.StopAsync(default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_stop_is_a_fault_and_the_timer_runs_again_after_the_interval()
    {
        var host = Hosted(gate: new RecordingGate { FailFirst = new OperationCanceledException("the lock provider's own timeout") });
        await host.Service.StartAsync(default);
        host.Lifetime.Start();
        await host.Gate.Entered(1).WaitAsync(Deadline);
        await host.Clock.WaitForArmedAsync(1, Deadline);

        host.Clock.NextWait.Should().Be(Interval);
        host.Clock.Advance(Interval);
        await host.Gate.Entered(2).WaitAsync(Deadline);
        await host.Service.StopAsync(default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_stop_before_the_host_has_started_ends_the_service_without_a_run()
    {
        var host = Hosted();
        await host.Service.StartAsync(default);
        await host.Lifetime.Asked.WaitAsync(Deadline);

        await host.Service.StopAsync(default).WaitAsync(Deadline);

        host.Service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        host.Gate.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Stopping_the_host_during_a_query_of_the_run_logs_no_error()
    {
        var blocking = new BlockingVerdictReads();
        var host = Hosted(context: builder => builder.AddInterceptors(blocking));
        blocking.Armed = true;
        await host.Service.StartAsync(default);
        host.Lifetime.Start();
        await blocking.Entered.Task.WaitAsync(Deadline);

        await host.Service.StopAsync(default).WaitAsync(Deadline);

        logged.Where(l => l.Level >= LogLevel.Error
                || (l.Level >= LogLevel.Warning && l.Category.StartsWith("ReeveWorkflow", StringComparison.Ordinal)))
            .Should().BeEmpty();
        host.Service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Stopping_the_host_during_a_run_logs_no_error()
    {
        var gate = new RecordingGate { WaitForCancellation = true };
        var host = Hosted(gate: gate);
        await host.Service.StartAsync(default);
        host.Lifetime.Start();
        await gate.Entered(1).WaitAsync(Deadline);

        await host.Service.StopAsync(default).WaitAsync(Deadline);

        logged.Where(l => l.Level >= LogLevel.Warning && l.Category.StartsWith("ReeveWorkflow", StringComparison.Ordinal))
            .Should().BeEmpty();
        host.Service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ReadinessRetry = TimeSpan.FromMinutes(1);

    private (SqliteWorkflowDatabase Database, WorkflowCompilationSweepService Service, RecordingGate Gate, TimerClock Clock, Lifetime Lifetime) Hosted(
        RecordingGate? gate = null, Action<WorkflowPersistenceOptions>? workflows = null,
        Action<DbContextOptionsBuilder>? context = null)
    {
        gate ??= new RecordingGate();
        var clock = new TimerClock(DateTimeOffset.UtcNow);
        var lifetime = new Lifetime();
        var database = Database(
            workflows: options =>
            {
                options.CompilationSweep.Enabled = true;
                workflows?.Invoke(options);
            },
            services: services =>
            {
                services.AddSingleton<IWorkflowLeaderGate>(gate);
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IHostApplicationLifetime>(lifetime);
            },
            context: context);
        Seed(database, Valid);
        var service = database.Services.GetServices<IHostedService>().OfType<WorkflowCompilationSweepService>().Single();
        return (database, service, gate, clock, lifetime);
    }

    private SqliteWorkflowDatabase Database(
        Action<WorkflowPersistenceOptions>? workflows = null, Action<IServiceCollection>? services = null,
        Action<DbContextOptionsBuilder>? context = null, Action<IServiceCollection>? after = null)
    {
        var database = new SqliteWorkflowDatabase(
            collection =>
            {
                collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
                collection.AddSingleton<ILoggerProvider>(new Collecting(logged));
                services?.Invoke(collection);
            },
            onFile: true, context: context, workflows: workflows, after: after);
        owned.Add(database);
        return database;
    }

    private static WorkflowCompilationSweepRunner Runner(SqliteWorkflowDatabase database) =>
        database.Services.GetRequiredService<WorkflowCompilationSweepRunner>();

    private List<Logged> Of<T>() => [.. logged.Where(l => l.Category == typeof(T).FullName)];

    private static Guid Seed(SqliteWorkflowDatabase database, string definition)
    {
        var now = DateTimeOffset.UtcNow;
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(Guid.NewGuid(), "wf", null, Guid.NewGuid(), now);
        db.Workflows.Add(workflow);
        db.SaveChanges();
        var version = new WorkflowVersion(Guid.NewGuid(), workflow.Id, 1, definition, null, null, Guid.NewGuid(), now, isPublished: true);
        db.WorkflowVersions.Add(version);
        db.SaveChanges();
        workflow.SetCurrentVersion(version, now);
        db.SaveChanges();
        return version.Id;
    }

    private static async Task<int> CountVerdictsAsync(SqliteWorkflowDatabase database)
    {
        using var scope = database.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowCompilations.CountAsync();
    }

    private static CompilationVerdict Verdict(Guid versionId, DateTimeOffset compiledAtUtc) =>
        new(versionId, "digest", "hash", true, [], [], "2", compiledAtUtc);

    private sealed record Logged(LogLevel Level, string Category, string Message, Exception? Error);

    private sealed class Collecting(ConcurrentQueue<Logged> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<Logged> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(new Logged(logLevel, category, formatter(state, exception), exception));
        }
    }

    private sealed class RecordingGate : IWorkflowLeaderGate
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> entered = new();
        private int atOnce;
        private int mostAtOnce;

        public List<string> Keys { get; } = [];

        public bool Leads { get; init; } = true;

        public TaskCompletionSource? Hold { get; init; }

        public Exception? FailFirst { get; init; }

        public bool WaitForCancellation { get; init; }

        public int MostAtOnce => Volatile.Read(ref mostAtOnce);

        public Task Entered(int count) => entered.GetOrAdd(count, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public async Task RunUnderLeaderAsync(string key, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            int count;
            lock (Keys)
            {
                Keys.Add(key);
                count = Keys.Count;
            }

            var now = Interlocked.Increment(ref atOnce);
            InterlockedMax(ref mostAtOnce, now);
            try
            {
                entered.GetOrAdd(count, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                if (count == 1 && FailFirst is { } fault)
                    throw fault;
                if (WaitForCancellation)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Hold is { } hold)
                    await hold.Task;
                if (Leads)
                    await work(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref atOnce);
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private sealed class DenyingLocks : IWorkflowRunLockProvider
    {
        public List<string> Asked { get; } = [];

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            lock (Asked)
                Asked.Add(lockKey);
            return Task.FromResult<IWorkflowRunLock?>(null);
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Asked => asked.Task;

        public CancellationToken ApplicationStarted
        {
            get
            {
                asked.TrySetResult();
                return started.Token;
            }
        }

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Start() => started.Cancel();

        public void StopApplication()
        {
        }
    }

    private sealed class PageReads : DbCommandInterceptor
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public void Reset() => Interlocked.Exchange(ref reads, 0);

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"workflows\"", StringComparison.Ordinal)
                && command.CommandText.Contains("LIMIT", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref reads);
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingVerdictReads : DbCommandInterceptor
    {
        public volatile bool Armed;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("workflow_compilations", StringComparison.Ordinal))
            {
                Armed = false;
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return result;
        }
    }

    private sealed class InFlight
    {
        private int now;
        private int most;
        private int calls;

        public int MostAtOnce => Volatile.Read(ref most);

        public int Calls => Volatile.Read(ref calls);

        public async Task<T> Around<T>(Func<Task<T>> call)
        {
            Interlocked.Increment(ref calls);
            InterlockedMax(ref most, Interlocked.Increment(ref now));
            try
            {
                await Task.Delay(20);
                return await call();
            }
            finally
            {
                Interlocked.Decrement(ref now);
            }
        }
    }

    private sealed class OverlapRecordingStore(IWorkflowCompilationStore inner, InFlight inFlight) : IWorkflowCompilationStore
    {
        public Task<CompilationVerdict?> FindAsync(Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken) =>
            inFlight.Around(() => inner.FindAsync(workflowVersionId, definitionDigest, dependencyHash, cancellationToken));

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken) =>
            inFlight.Around(async () =>
            {
                await inner.SaveAsync(verdict, cancellationToken);
                return 0;
            });

        public Task<int> DeleteStaleAsync(IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            inner.DeleteStaleAsync(current, olderThan, cancellationToken);
    }

    private sealed class OneMigrationAndNoSnapshot : IMigrationsAssembly
    {
        public const string Id = "20260101000000_Only";

        public IReadOnlyDictionary<string, System.Reflection.TypeInfo> Migrations { get; } =
            new Dictionary<string, System.Reflection.TypeInfo> { [Id] = System.Reflection.IntrospectionExtensions.GetTypeInfo(typeof(OnlyMigration)) };

        public ModelSnapshot? ModelSnapshot => null;

        public System.Reflection.Assembly Assembly => typeof(OneMigrationAndNoSnapshot).Assembly;

        public string? FindMigrationId(string nameOrId) => nameOrId == Id ? Id : null;

        public Migration CreateMigration(System.Reflection.TypeInfo migrationClass, string activeProvider) => new OnlyMigration();

        public void AddMigrations(System.Reflection.Assembly migrationsAssembly) => throw new NotSupportedException();

        private sealed class OnlyMigration : Migration
        {
            protected override void Up(MigrationBuilder migrationBuilder)
            {
            }
        }
    }

    private sealed class TogglingMigrations : IMigrationsAssembly
    {
        public static volatile bool Present;

        public IReadOnlyDictionary<string, System.Reflection.TypeInfo> Migrations =>
            Present
                ? new Dictionary<string, System.Reflection.TypeInfo>
                {
                    [OneMigrationAndNoSnapshot.Id] = System.Reflection.IntrospectionExtensions.GetTypeInfo(typeof(NoOpMigration)),
                }
                : new Dictionary<string, System.Reflection.TypeInfo>();

        public ModelSnapshot? ModelSnapshot => null;

        public System.Reflection.Assembly Assembly => typeof(TogglingMigrations).Assembly;

        public string? FindMigrationId(string nameOrId) => null;

        public Migration CreateMigration(System.Reflection.TypeInfo migrationClass, string activeProvider) => new NoOpMigration();

        public void AddMigrations(System.Reflection.Assembly migrationsAssembly) => throw new NotSupportedException();

        private sealed class NoOpMigration : Migration
        {
            protected override void Up(MigrationBuilder migrationBuilder)
            {
            }
        }
    }

    private sealed class FailingRetention
    {
        public InvalidOperationException Thrown { get; } = new("40P01: deadlock detected");

        public int Calls;
    }

    private sealed class FailingDeleteStore(IWorkflowCompilationStore inner, FailingRetention failing) : IWorkflowCompilationStore
    {
        public Task<CompilationVerdict?> FindAsync(Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken) =>
            inner.FindAsync(workflowVersionId, definitionDigest, dependencyHash, cancellationToken);

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken) => inner.SaveAsync(verdict, cancellationToken);

        public Task<int> DeleteStaleAsync(IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref failing.Calls) == 1
                ? Task.FromException<int>(failing.Thrown)
                : inner.DeleteStaleAsync(current, olderThan, cancellationToken);
    }

    private sealed class FailingSaveStore(IWorkflowCompilationStore inner) : IWorkflowCompilationStore
    {
        public Task<CompilationVerdict?> FindAsync(Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken) =>
            inner.FindAsync(workflowVersionId, definitionDigest, dependencyHash, cancellationToken);

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("the verdict table is read-only"));

        public Task<int> DeleteStaleAsync(IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            inner.DeleteStaleAsync(current, olderThan, cancellationToken);
    }

    private sealed class ThrowingResolver(Exception fault) : INodePortResolver
    {
        public string BlockTypeId => "Host.Call";

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken) =>
            Task.FromException<NodePortResolution>(fault);
    }

    private sealed class CallFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => "Host.Call";
        public string DisplayName => "Call";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [new BlockPortDefinition("Callee", "Callee", PortValueType.String, true)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool PortsAreResolvedElsewhere => true;
        public string? RunsWorkflowFrom => "Callee";

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
