using System.Collections.Concurrent;
using System.Data.Common;
using System.Transactions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Compilation;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class CompilationStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    private readonly SettableClock clock = new(new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly SaveInterceptor interceptor = new();
    private readonly AmbientTransactions ambient = new();
    private readonly SqliteWorkflowDatabase database;

    public CompilationStoreTests()
    {
        database = new SqliteWorkflowDatabase(
            services => services.AddSingleton<TimeProvider>(clock), onFile: true,
            context: options => options.AddInterceptors(interceptor, ambient));
    }

    public void Dispose() => database.Dispose();

    [Fact]
    public async Task A_verdict_is_read_back_under_the_triple_that_produced_it()
    {
        var versionId = Guid.NewGuid();

        await Store().SaveAsync(new CompilationVerdict(
            versionId, "digest-a", "hash-a", false,
            [new CompilationDiagnostic(CompilationRules.BlockTypeUnknown, "gone", "n1")],
            ["plugin@1"], DependencyFingerprint.CompilerVersion, T0), default);

        (await Store().FindAsync(versionId, "digest-a", "hash-a", default))!.Diagnostics.Should()
            .ContainSingle().Which.RuleId.Should().Be(CompilationRules.BlockTypeUnknown);
        (await Store().FindAsync(versionId, "digest-a", "hash-b", default)).Should().BeNull(
            "a different catalogue shape is a different question, not a stale answer");
        (await Store().FindAsync(versionId, "digest-b", "hash-a", default)).Should().BeNull(
            "a draft edited in place keeps its version id, so the digest half of the key must matter too");
        (await Store().FindAsync(Guid.NewGuid(), "digest-a", "hash-a", default)).Should().BeNull(
            "another version with the same content and catalogue is still another version");
    }

    [Fact]
    public async Task Every_field_a_verdict_carries_round_trips()
    {
        var versionId = Guid.NewGuid();
        var diagnostic = new CompilationDiagnostic(CompilationRules.BlockTypeUnknown, "gone \"quoted\" ünï", "n1", "p1",
            [new CanvasPathSegment("loop", "body"), new CanvasPathSegment("try", "catch")], "plugin@1");
        var verdict = new CompilationVerdict(
            versionId, "digest-a", "hash-a", true, [diagnostic],
            ["plugin@1", "plugin@2"], DependencyFingerprint.CompilerVersion, T0);

        await Store().SaveAsync(verdict, default);

        var found = await Store().FindAsync(versionId, "digest-a", "hash-a", default);
        found.Should().NotBeNull();
        found!.WorkflowVersionId.Should().Be(versionId);
        found.DefinitionDigest.Should().Be("digest-a");
        found.DependencyHash.Should().Be("hash-a");
        found.IsValid.Should().BeTrue();
        found.Diagnostics.Should().ContainSingle().Which.Should().BeEquivalentTo(diagnostic);
        found.Diagnostics[0].CanvasPath.Should().Equal(diagnostic.CanvasPath);
        found.BlockSources.Should().Equal("plugin@1", "plugin@2");
        found.CompilerVersion.Should().Be(DependencyFingerprint.CompilerVersion);
        found.CompiledAtUtc.Should().Be(T0);
    }

    [Fact]
    public async Task Saving_under_the_same_triple_again_replaces_every_field_of_the_one_row()
    {
        var versionId = Guid.NewGuid();
        await Store().SaveAsync(new CompilationVerdict(
            versionId, "digest-a", "hash-a", false,
            [new CompilationDiagnostic(CompilationRules.BlockTypeUnknown, "gone", "n1")],
            ["plugin@1"], "compiler-1", T0), default);

        await Store().SaveAsync(new CompilationVerdict(
            versionId, "digest-a", "hash-a", true,
            [new CompilationDiagnostic(CompilationRules.PortTypeMismatch, "later", "n2")],
            ["plugin@2"], "compiler-2", T0.AddMinutes(1)), default);

        var found = await Store().FindAsync(versionId, "digest-a", "hash-a", default);
        found!.IsValid.Should().BeTrue();
        found.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.PortTypeMismatch);
        found.BlockSources.Should().Equal("plugin@2");
        found.CompilerVersion.Should().Be("compiler-2");
        found.CompiledAtUtc.Should().Be(T0.AddMinutes(1));
        (await RowCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Each_part_of_the_key_keeps_a_row_of_its_own()
    {
        var versionId = Guid.NewGuid();
        var otherVersionId = Guid.NewGuid();
        await Store().SaveAsync(Sourced(versionId, "digest-a", "hash-a", "base"), default);
        await Store().SaveAsync(Sourced(versionId, "digest-b", "hash-a", "edited-draft"), default);
        await Store().SaveAsync(Sourced(versionId, "digest-a", "hash-b", "other-catalogue"), default);
        await Store().SaveAsync(Sourced(otherVersionId, "digest-a", "hash-a", "other-version"), default);

        (await RowCountAsync()).Should().Be(4);
        (await SourceAsync(versionId, "digest-a", "hash-a")).Should().Be("base");
        (await SourceAsync(versionId, "digest-b", "hash-a")).Should().Be("edited-draft");
        (await SourceAsync(versionId, "digest-a", "hash-b")).Should().Be("other-catalogue");
        (await SourceAsync(otherVersionId, "digest-a", "hash-a")).Should().Be("other-version");
    }

    [Fact]
    public async Task A_second_writer_that_loses_the_insert_race_finishes_as_an_update_not_an_error()
    {
        var versionId = Guid.NewGuid();
        interceptor.BeforeFirstInsert = context => PeerInsert(context, versionId);

        var saving = () => Store().SaveAsync(new CompilationVerdict(
            versionId, "digest-a", "hash-a", true, [], ["plugin@1"], DependencyFingerprint.CompilerVersion, T0.AddMinutes(1)),
            default);

        await saving.Should().NotThrowAsync("a unique-key race between two writers of the same triple is expected");
        var found = await Store().FindAsync(versionId, "digest-a", "hash-a", default);
        found!.IsValid.Should().BeTrue("the losing writer's own content still reaches the row, as an update");
        (await RowCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_save_that_fails_for_a_reason_other_than_the_key_still_throws()
    {
        interceptor.BeforeFirstInsert = _ => throw new DbUpdateException("The disk is full.");

        var saving = () => Store().SaveAsync(new CompilationVerdict(
            Guid.NewGuid(), "digest-a", "hash-a", true, [], [], DependencyFingerprint.CompilerVersion, T0), default);

        (await saving.Should().ThrowAsync<DbUpdateException>()).WithMessage("The disk is full.");
    }

    [Fact]
    public async Task A_hash_that_differs_only_in_case_is_a_different_key()
    {
        var versionId = Guid.NewGuid();
        await Store().SaveAsync(new CompilationVerdict(
            versionId, "digest-a", "deadbeef", true, [], [], DependencyFingerprint.CompilerVersion, T0), default);

        (await Store().FindAsync(versionId, "digest-a", "DEADBEEF", default)).Should().BeNull();
        (await Store().FindAsync(versionId, "DIGEST-A", "deadbeef", default)).Should().BeNull();
    }

    [Fact]
    public void The_store_resolves_from_the_root_provider_under_scope_validation()
    {
        using var withConsumer = new SqliteWorkflowDatabase(
            services => services.AddSingleton(sp => new StoreHolder(sp.GetRequiredService<IWorkflowCompilationStore>())));

        var holder = () => withConsumer.Services.GetRequiredService<StoreHolder>();

        holder.Should().NotThrow().Which.Store.Should().BeOfType<EfWorkflowCompilationStore>();
        withConsumer.Services.GetRequiredService<IWorkflowCompilationStore>().Should().BeSameAs(
            holder().Store, "one store answers every caller");
    }

    [Fact]
    public async Task A_save_never_commits_what_the_caller_left_pending_on_its_own_context()
    {
        var workflowId = database.SeedWorkflow();
        using var caller = database.Services.CreateScope();
        var db = caller.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        (await db.Workflows.SingleAsync(w => w.Id == workflowId)).SetWaitPollIntervalSeconds(7, T0);

        await caller.ServiceProvider.GetRequiredService<IWorkflowCompilationStore>()
            .SaveAsync(Sourced(Guid.NewGuid(), "digest-a", "hash-a", "plugin@1"), default);

        (await WaitPollIntervalAsync(workflowId)).Should().BeNull("the caller never saved its own change");
    }

    [Fact]
    public async Task A_lost_race_leaves_what_the_caller_left_pending_for_the_caller_to_save()
    {
        var workflowId = database.SeedWorkflow();
        using var caller = database.Services.CreateScope();
        var db = caller.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = await db.Workflows.SingleAsync(w => w.Id == workflowId);
        workflow.SetWaitPollIntervalSeconds(7, T0);
        var versionId = Guid.NewGuid();
        interceptor.BeforeFirstInsert = context => PeerInsert(context, versionId);

        await caller.ServiceProvider.GetRequiredService<IWorkflowCompilationStore>()
            .SaveAsync(Sourced(versionId, "digest-a", "hash-a", "plugin@1"), default);
        interceptor.Raced.Should().BeTrue("this test is about the race path");

        db.Entry(workflow).State.Should().Be(EntityState.Modified);
        await db.SaveChangesAsync();
        (await WaitPollIntervalAsync(workflowId)).Should().Be(7, "the caller's own save still writes its own change");
    }

    [Fact]
    public async Task The_last_save_wins_whichever_scope_resolved_the_store()
    {
        var versionId = Guid.NewGuid();
        using var a = database.Services.CreateScope();
        using var b = database.Services.CreateScope();
        var fromA = a.ServiceProvider.GetRequiredService<IWorkflowCompilationStore>();
        var fromB = b.ServiceProvider.GetRequiredService<IWorkflowCompilationStore>();

        await fromA.SaveAsync(Sourced(versionId, "digest-a", "hash-a", "plugin@A", T0), default);
        await fromB.SaveAsync(Sourced(versionId, "digest-a", "hash-a", "plugin@B", T0.AddMinutes(1)), default);
        await fromA.SaveAsync(Sourced(versionId, "digest-a", "hash-a", "plugin@A", T0.AddMinutes(2)), default);

        var found = await Store().FindAsync(versionId, "digest-a", "hash-a", default);
        found!.BlockSources.Should().Equal(["plugin@A"], "the last save reported success with this content");
        found.CompiledAtUtc.Should().Be(T0.AddMinutes(2));
    }

    [Fact]
    public async Task Concurrent_calls_on_one_store_each_land()
    {
        var store = Store();
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();

        await Task.WhenAll(ids.Select(id => Task.Run(() => store.SaveAsync(Sourced(id, "digest-a", "hash-a", "p"), default))));

        (await RowCountAsync()).Should().Be(ids.Length);
    }

    [Fact]
    public async Task No_call_joins_an_ambient_transaction_the_caller_opened()
    {
        var versionId = Guid.NewGuid();
        ambient.Reset();

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await Store().SaveAsync(Sourced(versionId, "digest-a", "hash-a", "plugin@1"), default);
            (await Store().FindAsync(versionId, "digest-a", "hash-a", default)).Should().NotBeNull();
            await Store().DeleteStaleAsync([], TimeSpan.FromDays(3650), default);
        }

        ambient.Commands.Should().BeGreaterThanOrEqualTo(3, "one command at least for each of the three calls");
        ambient.InsideAnAmbientTransaction.Should().Be(0, "a verdict is a cache write, not part of the caller's work");
        (await RowCountAsync()).Should().Be(1, "the caller rolling back its own work does not take the verdict with it");
    }

    [Fact]
    public async Task On_a_shared_connection_a_save_runs_no_command_and_says_the_verdict_was_not_saved()
    {
        using var shared = SharedConnection();
        using var caller = shared.Database.Services.CreateScope();
        var db = caller.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE workflow_compilations SET is_valid = 1 WHERE 0");
        shared.Commands.Reset();

        var saving = () => shared.Store.SaveAsync(Sourced(Guid.NewGuid(), "digest-a", "hash-a", "plugin@1"), default);

        (await saving.Should().ThrowAsync<CompilationStoreUnavailableException>()).WithMessage("*already open*not saved*");
        shared.Commands.Commands.Should().Be(0, "a command on this connection runs inside the caller's transaction");
        (await db.WorkflowCompilations.CountAsync()).Should().Be(0, "nothing was written inside the caller's transaction");
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task On_a_shared_connection_a_find_answers_a_miss_without_a_command()
    {
        using var shared = SharedConnection();
        var versionId = Guid.NewGuid();
        using var caller = shared.Database.Services.CreateScope();
        var db = caller.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        db.WorkflowCompilations.Add(new WorkflowCompilation
        {
            WorkflowVersionId = versionId, DefinitionDigest = "digest-a", DependencyHash = "hash-a",
            CompilerVersion = "c", CompiledAtUtc = T0,
        });
        await db.SaveChangesAsync();
        shared.Commands.Reset();

        (await shared.Store.FindAsync(versionId, "digest-a", "hash-a", default)).Should().BeNull(
            "a miss costs a recompile; a read inside the caller's transaction can abort it");
        shared.Commands.Commands.Should().Be(0);
    }

    [Fact]
    public async Task On_a_shared_connection_the_delete_refuses_rather_than_answer_nothing()
    {
        using var shared = SharedConnection();

        var deleting = () => shared.Store.DeleteStaleAsync([], TimeSpan.FromDays(1), default);

        (await deleting.Should().ThrowAsync<CompilationStoreUnavailableException>()).WithMessage("*already open*");
        shared.Commands.Commands.Should().Be(0);
    }

    [Fact]
    public async Task On_a_shared_connection_the_callers_pending_change_is_left_for_the_caller_to_save()
    {
        using var shared = SharedConnection();
        var workflowId = shared.Database.SeedWorkflow();
        using var caller = shared.Database.Services.CreateScope();
        var db = caller.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = await db.Workflows.SingleAsync(w => w.Id == workflowId);
        workflow.SetWaitPollIntervalSeconds(7, T0);

        await FluentActions.Awaiting(() => shared.Store.SaveAsync(Sourced(Guid.NewGuid(), "digest-a", "hash-a", "p"), default))
            .Should().ThrowAsync<CompilationStoreUnavailableException>();

        db.Entry(workflow).State.Should().Be(EntityState.Modified);
        await db.SaveChangesAsync();
        (await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == workflowId)).WaitPollIntervalSeconds.Should().Be(7);
    }

    [Fact]
    public async Task On_a_shared_connection_the_shape_is_logged_once_per_store()
    {
        using var shared = SharedConnection();

        for (var i = 0; i < 3; i++)
        {
            await FluentActions.Awaiting(() => shared.Store.SaveAsync(Sourced(Guid.NewGuid(), "digest-a", "hash-a", "p"), default))
                .Should().ThrowAsync<CompilationStoreUnavailableException>();
            (await shared.Store.FindAsync(Guid.NewGuid(), "digest-a", "hash-a", default)).Should().BeNull();
        }

        shared.Logged.Where(entry => entry.Category == typeof(EfWorkflowCompilationStore).FullName)
            .Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task Over_a_shared_connection_the_service_logs_the_refusal_once_and_its_retries_send_no_command()
    {
        using var shared = SharedConnection();
        var store = new CountingStore(shared.Store);
        var service = CompilationScopes.Service(new NoBlocks(), new NoWorkflows(), store,
            logger: shared.Database.Services.GetRequiredService<ILoggerFactory>().CreateLogger<WorkflowCompilationService>());
        var version = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, "{\"nodes\":[],\"edges\":[]}", null, null, Guid.NewGuid(), T0);
        shared.Commands.Reset();

        for (var i = 0; i < 5; i++)
            await service.EnsureAsync(version, null, default);

        store.Saves.Should().Be(5, "the first compile and four unsaved hits each try the save");
        store.Finds.Should().Be(1);
        shared.Commands.Commands.Should().Be(0);
        shared.Logged.Where(entry => entry.Level >= LogLevel.Warning).GroupBy(entry => entry.Category)
            .ToDictionary(group => group.Key, group => group.Count()).Should().Equal(new Dictionary<string, int>
            {
                [typeof(EfWorkflowCompilationStore).FullName!] = 1,
                [typeof(WorkflowCompilationService).FullName!] = 1,
            });
        shared.Logged.Should().OnlyContain(entry => entry.Error == null, "a declined call is not a fault to trace");
    }

    [Theory]
    [InlineData(@"digest\0a", "hash-a")]
    [InlineData(@"digest\udc00a", "hash-a")]
    [InlineData(@"digest-a\ud800", "hash-a")]
    [InlineData("digest-a", @"hash\0a")]
    [InlineData("digest-a", @"hash\udc00a")]
    [InlineData("digest-a", @"hash-a\ud800")]
    public async Task DeleteStaleAsync_refuses_a_current_key_no_save_accepts_and_deletes_nothing(string digest, string hash)
    {
        var versionId = Guid.NewGuid();
        await Store().SaveAsync(Sourced(versionId, "digest-a", "hash-old", "old", at: clock.Now.AddDays(-90)), default);
        ambient.Reset();

        var deleting = () => Store().DeleteStaleAsync(
            [new CompilationKey(versionId, Unescape(digest), Unescape(hash))], TimeSpan.FromDays(1), default);

        (await deleting.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("current");
        ambient.Commands.Should().Be(0);
        (await SourcesAsync()).Should().Equal("old");
    }

    [Fact]
    public async Task A_time_with_an_offset_is_stored_as_the_same_instant_in_UTC()
    {
        var versionId = Guid.NewGuid();
        var local = new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.FromHours(2));

        await Store().SaveAsync(new CompilationVerdict(versionId, "digest-a", "hash-a", true, [], [], "c", local), default);

        var found = await Store().FindAsync(versionId, "digest-a", "hash-a", default);
        found!.CompiledAtUtc.Offset.Should().Be(TimeSpan.Zero);
        found.CompiledAtUtc.Should().Be(local);
    }

    [Theory]
    [MemberData(nameof(EveryStringAVerdictCarries))]
    public async Task A_verdict_with_a_NUL_or_an_unpaired_surrogate_in_any_string_is_refused_and_nothing_is_written(
        string site, string bad)
    {
        var verdict = WithAt(site, bad);
        ambient.Reset();

        var saving = () => Store().SaveAsync(verdict, default);

        (await saving.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("verdict");
        ambient.Commands.Should().Be(0, "the refusal comes before any command");
        (await RowCountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(@"digest\0a", "hash-a")]
    [InlineData("digest-a", @"hash\ud800a")]
    [InlineData("digest-a", @"hash-a\ud800")]
    public async Task A_find_under_a_key_no_save_accepts_answers_a_miss_without_a_command(string digest, string hash)
    {
        ambient.Reset();

        (await Store().FindAsync(Guid.NewGuid(), Unescape(digest), Unescape(hash), default)).Should().BeNull();
        ambient.Commands.Should().Be(0);
    }

    [Fact]
    public async Task A_surrogate_pair_is_kept_as_written()
    {
        var versionId = Guid.NewGuid();
        var diagnostic = new CompilationDiagnostic(CompilationRules.BlockTypeUnknown, "node 'a\ud83d\ude00' is unknown", "a\ud83d\ude00");

        await Store().SaveAsync(new CompilationVerdict(
            versionId, "digest-a", "hash-a", false, [diagnostic], ["plugin\ud83d\ude00"], "c", T0), default);

        var found = await Store().FindAsync(versionId, "digest-a", "hash-a", default);
        found!.Diagnostics.Should().ContainSingle().Which.NodeId.Should().Be("a\ud83d\ude00");
        found.BlockSources.Should().Equal("plugin\ud83d\ude00");
    }

    public static TheoryData<string, string> EveryStringAVerdictCarries()
    {
        var sites = new List<string>
        {
            nameof(CompilationVerdict.DefinitionDigest), nameof(CompilationVerdict.DependencyHash),
            nameof(CompilationVerdict.CompilerVersion), "BlockSources[1]",
        };
        sites.AddRange(StringProperties(typeof(CompilationDiagnostic)).Select(p => $"Diagnostics[1].{p.Name}"));
        sites.AddRange(StringProperties(typeof(CanvasPathSegment)).Select(p => $"Diagnostics[1].CanvasPath[1].{p.Name}"));

        var data = new TheoryData<string, string>();
        foreach (var site in sites)
        {
            data.Add(site, @"a\0b");
            data.Add(site, @"a\udc00b");
            data.Add(site, @"ab\ud800");
        }

        return data;
    }

    private static IEnumerable<System.Reflection.PropertyInfo> StringProperties(Type type) =>
        type.GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite);

    private static string Unescape(string text) => System.Text.RegularExpressions.Regex.Unescape(text);

    private static CompilationVerdict WithAt(string site, string bad)
    {
        bad = Unescape(bad);
        var segments = new[] { new CanvasPathSegment("loop", "body"), new CanvasPathSegment("try", "catch") };
        var diagnostics = new[]
        {
            new CompilationDiagnostic("rule", "message", "n0", "p0", segments, "plugin@0"),
            new CompilationDiagnostic("rule", "message", "n1", "p1", segments, "plugin@1"),
        };
        var verdict = new CompilationVerdict(
            Guid.NewGuid(), "digest-a", "hash-a", false, diagnostics, ["plugin@0", "plugin@1"], "c", T0);

        switch (site)
        {
            case nameof(CompilationVerdict.DefinitionDigest): return verdict with { DefinitionDigest = bad };
            case nameof(CompilationVerdict.DependencyHash): return verdict with { DependencyHash = bad };
            case nameof(CompilationVerdict.CompilerVersion): return verdict with { CompilerVersion = bad };
            case "BlockSources[1]": return verdict with { BlockSources = ["plugin@0", bad] };
        }

        var name = site[(site.LastIndexOf('.') + 1)..];
        if (site.Contains("CanvasPath"))
        {
            object segment = segments[1];
            typeof(CanvasPathSegment).GetProperty(name)!.SetValue(segment, bad);
            diagnostics[1] = diagnostics[1] with { CanvasPath = [segments[0], (CanvasPathSegment)segment] };
        }
        else
        {
            var diagnostic = diagnostics[1] with { };
            typeof(CompilationDiagnostic).GetProperty(name)!.SetValue(diagnostic, bad);
            diagnostics[1] = diagnostic;
        }

        return verdict with { Diagnostics = diagnostics };
    }

    [Fact]
    public async Task A_backslash_u_sequence_written_as_text_is_not_mistaken_for_a_NUL()
    {
        var versionId = Guid.NewGuid();

        await Store().SaveAsync(new CompilationVerdict(
            versionId, "digest-a", "hash-a", false, [], [@"plugin\u0000@1"], "c", T0), default);

        (await Store().FindAsync(versionId, "digest-a", "hash-a", default))!.BlockSources.Should().Equal(@"plugin\u0000@1");
    }

    [Fact]
    public async Task DeleteStaleAsync_refuses_a_retention_shorter_than_an_hour()
    {
        foreach (var olderThan in new[] { TimeSpan.Zero, -TimeSpan.FromTicks(1), WorkflowCompilationSweeper.MinimumRetention - TimeSpan.FromTicks(1) })
        {
            var deleting = () => Store().DeleteStaleAsync([], olderThan, default);

            (await deleting.Should().ThrowAsync<ArgumentOutOfRangeException>()).Which.ParamName.Should().Be("olderThan");
        }
    }

    [Fact]
    public async Task DeleteStaleAsync_deletes_a_row_one_tick_older_than_the_retention_on_its_own_clock()
    {
        var retention = WorkflowCompilationSweeper.MinimumRetention;
        var cutoff = clock.Now - retention;
        var named = (await ExistingVersionsAsync(1)).Single();
        var gone = Guid.NewGuid();
        await Store().SaveAsync(Sourced(named, "digest-a", "hash-current", "current", cutoff.AddDays(-9)), default);
        foreach (var (at, name) in new[] { (cutoff.AddTicks(-1), "older"), (cutoff, "at"), (cutoff.AddTicks(1), "younger") })
        {
            await Store().SaveAsync(Sourced(named, "digest-a", "hash-" + name, "named-" + name, at), default);
            await Store().SaveAsync(Sourced(gone, "digest-a", "hash-" + name, "gone-" + name, at), default);
        }

        var deleted = await Store().DeleteStaleAsync([new CompilationKey(named, "digest-a", "hash-current")], retention, default);

        deleted.Should().Be(2);
        (await SourcesAsync()).Should().BeEquivalentTo("current", "named-at", "named-younger", "gone-at", "gone-younger");
    }

    [Theory]
    [InlineData(null, "hash-a")]
    [InlineData("", "hash-a")]
    [InlineData(" ", "hash-a")]
    [InlineData("digest-a", null)]
    [InlineData("digest-a", "")]
    [InlineData("digest-a", " ")]
    public async Task DeleteStaleAsync_refuses_a_current_key_missing_its_digest_or_hash(string? digest, string? hash)
    {
        var deleting = () => Store().DeleteStaleAsync([new CompilationKey(Guid.NewGuid(), digest!, hash!)], TimeSpan.FromDays(1), default);

        (await deleting.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("current");
    }

    [Fact]
    public async Task DeleteStaleAsync_refuses_a_null_key_and_a_null_collection()
    {
        var nullKey = () => Store().DeleteStaleAsync([null!], TimeSpan.FromDays(1), default);
        var nullCurrent = () => Store().DeleteStaleAsync(null!, TimeSpan.FromDays(1), default);

        (await nullKey.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("current");
        (await nullCurrent.Should().ThrowAsync<ArgumentNullException>()).Which.ParamName.Should().Be("current");
    }

    [Fact]
    public async Task DeleteStaleAsync_refuses_a_version_given_two_current_keys()
    {
        var versionId = (await ExistingVersionsAsync(1)).Single();
        await Store().SaveAsync(Sourced(versionId, "digest-a", "hash-old", "old", at: clock.Now.AddDays(-90)), default);
        ambient.Reset();

        var twice = () => Store().DeleteStaleAsync(
            [new CompilationKey(versionId, "digest-a", "hash-a"), new CompilationKey(versionId, "digest-a", "hash-b")],
            TimeSpan.FromDays(1), default);

        (await twice.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("current");
        ambient.Commands.Should().Be(0);
        (await SourcesAsync()).Should().Equal("old");
    }

    [Fact]
    public async Task DeleteStaleAsync_keeps_each_named_versions_current_row_however_old_when_their_hashes_differ()
    {
        var old = clock.Now.AddDays(-90);
        var ids = await ExistingVersionsAsync(2);
        await Store().SaveAsync(Sourced(ids[0], "digest-a", "hash-a", "a-current", old), default);
        await Store().SaveAsync(Sourced(ids[1], "digest-b", "hash-b", "b-current", old), default);

        var deleted = await Store().DeleteStaleAsync(
            [new CompilationKey(ids[0], "digest-a", "hash-a"), new CompilationKey(ids[1], "digest-b", "hash-b")],
            TimeSpan.FromDays(1), default);

        deleted.Should().Be(0);
        (await SourcesAsync()).Should().BeEquivalentTo("a-current", "b-current");
    }

    [Fact]
    public async Task DeleteStaleAsync_drops_a_named_versions_old_rows_under_another_digest_or_hash_and_keeps_its_recent_ones()
    {
        var versionId = (await ExistingVersionsAsync(1)).Single();
        await Store().SaveAsync(Sourced(versionId, "digest-b", "hash-b", "current", clock.Now.AddDays(-90)), default);
        await Store().SaveAsync(Sourced(versionId, "digest-a", "hash-b", "earlier-digest", clock.Now.AddDays(-90)), default);
        await Store().SaveAsync(Sourced(versionId, "digest-b", "hash-a", "earlier-hash", clock.Now.AddDays(-90)), default);
        await Store().SaveAsync(Sourced(versionId, "digest-a", "hash-a", "recent-other", clock.Now.AddHours(-2)), default);

        var deleted = await Store().DeleteStaleAsync([new CompilationKey(versionId, "digest-b", "hash-b")], TimeSpan.FromDays(1), default);

        deleted.Should().Be(2);
        (await SourcesAsync()).Should().BeEquivalentTo("current", "recent-other");
    }

    [Fact]
    public async Task DeleteStaleAsync_drops_the_old_rows_of_a_version_that_no_longer_exists_and_keeps_its_recent_ones()
    {
        var gone = Guid.NewGuid();
        await Store().SaveAsync(Sourced(gone, "digest-a", "hash-a", "gone-old", clock.Now.AddDays(-90)), default);
        await Store().SaveAsync(Sourced(gone, "digest-b", "hash-a", "gone-recent", clock.Now.AddHours(-2)), default);

        var deleted = await Store().DeleteStaleAsync([], TimeSpan.FromDays(1), default);

        deleted.Should().Be(1);
        (await SourcesAsync()).Should().Equal("gone-recent");
    }

    [Fact]
    public async Task DeleteStaleAsync_keeps_every_row_of_an_existing_version_it_is_not_given_whatever_its_age()
    {
        var old = clock.Now.AddDays(-90);
        var ids = await ExistingVersionsAsync(3);
        await Store().SaveAsync(Sourced(ids[0], "digest-a", "hash-a", "named-current", old), default);
        await Store().SaveAsync(Sourced(ids[1], "digest-a", "hash-a", "unnamed-first", old), default);
        await Store().SaveAsync(Sourced(ids[1], "digest-b", "hash-b", "unnamed-second", old), default);
        await Store().SaveAsync(Sourced(ids[2], "digest-a", "hash-a", "draft", old), default);

        var partial = await Store().DeleteStaleAsync([new CompilationKey(ids[0], "digest-a", "hash-a")], TimeSpan.FromDays(1), default);
        var empty = await Store().DeleteStaleAsync([], TimeSpan.FromDays(1), default);

        (partial, empty).Should().Be((0, 0));
        (await SourcesAsync()).Should().BeEquivalentTo("named-current", "unnamed-first", "unnamed-second", "draft");
    }

    [Fact]
    public async Task DeleteStaleAsync_keeps_a_row_whose_digest_and_hash_are_another_named_versions_key()
    {
        var old = clock.Now.AddDays(-90);
        var ids = await ExistingVersionsAsync(2);
        await Store().SaveAsync(Sourced(ids[0], "digest-a", "hash-a", "first-current", old), default);
        await Store().SaveAsync(Sourced(ids[0], "digest-b", "hash-b", "first-under-the-seconds-key", old), default);
        await Store().SaveAsync(Sourced(ids[0], "digest-c", "hash-c", "first-superseded", old), default);
        await Store().SaveAsync(Sourced(ids[1], "digest-b", "hash-b", "second-current", old), default);

        var deleted = await Store().DeleteStaleAsync(
            [new CompilationKey(ids[0], "digest-a", "hash-a"), new CompilationKey(ids[1], "digest-b", "hash-b")],
            TimeSpan.FromDays(1), default);

        deleted.Should().Be(1);
        (await SourcesAsync()).Should().BeEquivalentTo("first-current", "first-under-the-seconds-key", "second-current");
    }

    [Fact]
    public async Task DeleteStaleAsync_compares_instants_whatever_offset_they_were_saved_with()
    {
        await Store().SaveAsync(Sourced(Guid.NewGuid(), "digest-a", "hash-old", "east",
            clock.Now.AddHours(-36).ToOffset(TimeSpan.FromHours(14))), default);
        await Store().SaveAsync(Sourced(Guid.NewGuid(), "digest-a", "hash-old", "west",
            clock.Now.AddHours(-6).ToOffset(TimeSpan.FromHours(-12))), default);

        await Store().DeleteStaleAsync([], TimeSpan.FromDays(1), default);

        (await SourcesAsync()).Should().Equal("west");
    }

    [Fact]
    public async Task DeleteStaleAsync_with_a_retention_longer_than_the_calendar_deletes_nothing()
    {
        await Store().SaveAsync(Sourced(Guid.NewGuid(), "digest-a", "hash-old", "old", clock.Now.AddYears(-50)), default);

        ambient.Reset();

        (await Store().DeleteStaleAsync([], TimeSpan.MaxValue, default)).Should().Be(0);

        ambient.Commands.Should().Be(0);
        (await SourcesAsync()).Should().Equal("old");
    }

    [Fact]
    public async Task DeleteStaleAsync_takes_forty_thousand_named_versions_in_a_statement_per_thousand()
    {
        var ids = await ExistingVersionsAsync(40_000);
        using (var scope = database.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            foreach (var hash in new[] { "hash-b", "hash-a" })
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO workflow_compilations SELECT id, 'digest-a', {0}, 1, '[]', '[]', 'c', {1} FROM workflow_versions",
                    hash, clock.Now.AddDays(-90).UtcDateTime);
        }

        ambient.Reset();
        var deleted = await Store().DeleteStaleAsync(
            [.. ids.Select(id => new CompilationKey(id, "digest-a", "hash-b"))], TimeSpan.FromDays(1), default);

        deleted.Should().Be(40_000);
        ambient.Deletes.Should().Be(41, "forty statements of a thousand named versions, and one for the versions gone");
        (await RowCountAsync()).Should().Be(40_000);
        foreach (var id in new[] { ids[0], ids[999], ids[1_000], ids[^1] })
            (await Store().FindAsync(id, "digest-a", "hash-b", default)).Should().NotBeNull();
    }

    private static CompilationVerdict Verdict(
        Guid versionId, string dependencyHash, bool isValid, DateTimeOffset? at = null, string digest = "digest-a") =>
        new(versionId, digest, dependencyHash, isValid,
            isValid ? [] : [new CompilationDiagnostic(CompilationRules.BlockTypeUnknown, "gone", "n1")],
            [], DependencyFingerprint.CompilerVersion, at ?? T0);

    private static CompilationVerdict Sourced(
        Guid versionId, string digest, string dependencyHash, string source, DateTimeOffset? at = null) =>
        new(versionId, digest, dependencyHash, true, [], [source], DependencyFingerprint.CompilerVersion, at ?? T0);

    private static void PeerInsert(DbContext context, Guid versionId) => context.Database.ExecuteSqlRaw(
        "INSERT INTO workflow_compilations "
        + "(workflow_version_id, definition_digest, dependency_hash, is_valid, diagnostics_json, "
        + "block_sources_json, compiler_version, compiled_at_utc) VALUES ({0}, {1}, {2}, 0, '[]', '[]', {3}, {4})",
        versionId, "digest-a", "hash-a", "peer-version", T0);

    private IWorkflowCompilationStore Store() => database.Services.GetRequiredService<IWorkflowCompilationStore>();

    private async Task<Guid[]> ExistingVersionsAsync(int count)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(Guid.NewGuid(), "retention", null, Guid.NewGuid(), T0);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {0}) "
            + "INSERT INTO workflow_versions (id, workflow_id, version_number, definition_json, created_at_utc, "
            + "created_by_user_id, is_published) "
            + "SELECT printf('%08X-%04X-4000-8000-%012X', abs(random()) % 4294967296, i % 65536, i), {1}, i, '{{}}', {2}, {3}, 0 FROM n",
            count, workflow.Id, T0, Guid.NewGuid());
        return [.. await db.WorkflowVersions.Where(v => v.WorkflowId == workflow.Id)
            .OrderBy(v => v.VersionNumber).Select(v => v.Id).ToListAsync()];
    }

    private async Task<int> RowCountAsync()
    {
        using var scope = database.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowCompilations.CountAsync();
    }

    private async Task<IReadOnlyList<string>> SourcesAsync()
    {
        using var scope = database.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowCompilations
            .Select(r => r.BlockSourcesJson).ToListAsync();
        return rows.Select(json => System.Text.Json.JsonSerializer.Deserialize<string[]>(json)!.Single()).ToArray();
    }

    private async Task<string> SourceAsync(Guid versionId, string digest, string dependencyHash) =>
        (await Store().FindAsync(versionId, digest, dependencyHash, default))!.BlockSources.Single();

    private async Task<int?> WaitPollIntervalAsync(Guid workflowId)
    {
        using var scope = database.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows
            .Where(w => w.Id == workflowId).Select(w => w.WaitPollIntervalSeconds).SingleAsync();
    }

    private static SharedDesk SharedConnection()
    {
        var commands = new AmbientTransactions();
        var logged = new ConcurrentQueue<(LogLevel Level, string Category, string Message, Exception? Error)>();
        var database = new SqliteWorkflowDatabase(
            services => services.AddSingleton<ILoggerProvider>(new Collecting(logged)),
            context: options => options.AddInterceptors(commands));
        return new SharedDesk(database, commands, logged);
    }

    private sealed record SharedDesk(
        SqliteWorkflowDatabase Database,
        AmbientTransactions Commands,
        ConcurrentQueue<(LogLevel Level, string Category, string Message, Exception? Error)> Logged) : IDisposable
    {
        public IWorkflowCompilationStore Store => Database.Services.GetRequiredService<IWorkflowCompilationStore>();

        public void Dispose() => Database.Dispose();
    }

    private sealed class Collecting(ConcurrentQueue<(LogLevel, string, string, Exception?)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<(LogLevel, string, string, Exception?)> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue((logLevel, category, formatter(state, exception), exception));
        }
    }

    private sealed class CountingStore(IWorkflowCompilationStore inner) : IWorkflowCompilationStore
    {
        public int Finds;
        public int Saves;

        public Task<CompilationVerdict?> FindAsync(
            Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Finds);
            return inner.FindAsync(workflowVersionId, definitionDigest, dependencyHash, cancellationToken);
        }

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Saves);
            return inner.SaveAsync(verdict, cancellationToken);
        }

        public Task<int> DeleteStaleAsync(
            IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            inner.DeleteStaleAsync(current, olderThan, cancellationToken);
    }

    private sealed class NoBlocks : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class NoWorkflows : IWorkflowStore
    {
        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken) => Task.FromResult<Workflow?>(null);

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowVersion?>(null);

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<(Guid, string)?>(null);
    }

    private sealed record StoreHolder(IWorkflowCompilationStore Store);

    private sealed class SaveInterceptor : SaveChangesInterceptor
    {
        public Action<DbContext>? BeforeFirstInsert;
        public bool Raced;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context
                && context.ChangeTracker.Entries<WorkflowCompilation>().Any(entry => entry.State == EntityState.Added)
                && Interlocked.Exchange(ref BeforeFirstInsert, null) is { } arrange)
            {
                arrange(context);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Raced = true;
            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }
    }

    private sealed class AmbientTransactions : DbCommandInterceptor
    {
        public int Commands;
        public int InsideAnAmbientTransaction;
        public int Deletes;

        public void Reset()
        {
            Interlocked.Exchange(ref Commands, 0);
            Interlocked.Exchange(ref InsideAnAmbientTransaction, 0);
            Interlocked.Exchange(ref Deletes, 0);
        }

        private void Record(DbCommand command)
        {
            Interlocked.Increment(ref Commands);
            if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.Ordinal))
                Interlocked.Increment(ref Deletes);
            if (Transaction.Current is not null)
                Interlocked.Increment(ref InsideAnAmbientTransaction);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
