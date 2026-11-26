using System.Collections.Concurrent;
using System.Transactions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Compilation;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class CompilationStoreOnPostgresTests(ClusterBackends backends)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(1);

    [Fact]
    public async Task DeleteStaleAsync_drops_a_named_versions_old_row_under_a_hash_that_is_no_longer_its_own()
    {
        await using var desk = await StoreAsync("dispatch_compilation_stale");
        var versionId = (await ExistingVersionsAsync(desk, 1)).Single();
        await InsertRowAsync(desk.ConnectionString, versionId, "hash-old", DateTimeOffset.UtcNow.AddDays(-90));
        await InsertRowAsync(desk.ConnectionString, versionId, "hash-current", DateTimeOffset.UtcNow.AddDays(-90));

        var deleted = await desk.Store.DeleteStaleAsync([new CompilationKey(versionId, "digest-a", "hash-current")], Retention, default);

        deleted.Should().Be(1);
        (await HashesAsync(desk.ConnectionString)).Should().Equal("hash-current");
    }

    [Fact]
    public async Task DeleteStaleAsync_keeps_each_named_versions_current_row_however_old_when_their_hashes_differ()
    {
        await using var desk = await StoreAsync("dispatch_compilation_current");
        var ids = await ExistingVersionsAsync(desk, 2);
        await InsertRowAsync(desk.ConnectionString, ids[0], "hash-a", DateTimeOffset.UtcNow.AddDays(-90));
        await InsertRowAsync(desk.ConnectionString, ids[1], "hash-b", DateTimeOffset.UtcNow.AddDays(-90));

        await desk.Store.DeleteStaleAsync(
            [new CompilationKey(ids[0], "digest-a", "hash-a"), new CompilationKey(ids[1], "digest-a", "hash-b")], Retention, default);

        (await RowCountAsync(desk.ConnectionString)).Should().Be(2);
    }

    [Fact]
    public async Task DeleteStaleAsync_drops_the_old_row_of_a_version_that_no_longer_exists_and_leaves_a_recent_one()
    {
        await using var desk = await StoreAsync("dispatch_compilation_recent");
        await InsertRowAsync(desk.ConnectionString, Guid.NewGuid(), "hash-old", DateTimeOffset.UtcNow.AddDays(-90));
        await InsertRowAsync(desk.ConnectionString, Guid.NewGuid(), "hash-recent", DateTimeOffset.UtcNow);

        await desk.Store.DeleteStaleAsync([], Retention, default);

        (await HashesAsync(desk.ConnectionString)).Should().Equal(["hash-recent"], "younger than the retention threshold");
    }

    [Fact]
    public async Task DeleteStaleAsync_keeps_every_row_of_an_existing_version_it_is_not_given()
    {
        await using var desk = await StoreAsync("dispatch_compilation_unnamed");
        var ids = await ExistingVersionsAsync(desk, 2);
        await InsertRowAsync(desk.ConnectionString, ids[0], "hash-a", DateTimeOffset.UtcNow.AddDays(-90));
        await InsertRowAsync(desk.ConnectionString, ids[1], "hash-a", DateTimeOffset.UtcNow.AddDays(-90));
        await InsertRowAsync(desk.ConnectionString, ids[1], "hash-b", DateTimeOffset.UtcNow.AddDays(-90));

        var partial = await desk.Store.DeleteStaleAsync([new CompilationKey(ids[0], "digest-a", "hash-a")], Retention, default);
        var empty = await desk.Store.DeleteStaleAsync([], Retention, default);

        (partial, empty).Should().Be((0, 0));
        (await RowCountAsync(desk.ConnectionString)).Should().Be(3);
    }

    [Fact]
    public async Task DeleteStaleAsync_takes_a_hundred_thousand_named_versions_in_a_statement_per_thousand()
    {
        var commands = new QueryPlans.RecordingCommands();
        await using var desk = await StoreAsync("dispatch_compilation_many", commands);
        var ids = await ExistingVersionsAsync(desk, 100_000);
        await ExecuteAsync(desk.ConnectionString, """
            INSERT INTO automation.workflow_compilations
            SELECT v.id, 'digest-a', h, true, '[]', '[]', 'c', now() - interval '90 days'
            FROM automation.workflow_versions v, (VALUES ('hash-a'), ('hash-b')) hashes(h)
            WHERE v.version_number > 1;
            INSERT INTO automation.workflow_compilations
            SELECT gen_random_uuid(), 'digest-a', 'hash-a', true, '[]', '[]', 'c', now() - interval '90 days'
            FROM generate_series(1, 1000);
            ANALYZE automation.workflow_compilations;
            """);
        var before = commands.All.Count;

        var deleted = await desk.Store.DeleteStaleAsync(
            [.. ids.Select(id => new CompilationKey(id, "digest-a", "hash-b"))], Retention, default);

        deleted.Should().Be(101_000);
        commands.All.Skip(before).Should().HaveCount(101).And.OnlyContain(text => text.StartsWith("DELETE", StringComparison.Ordinal));
        (await HashesAsync(desk.ConnectionString)).Should().HaveCount(100_000).And.OnlyContain(hash => hash == "hash-b");
    }

    [Fact]
    public async Task The_verdict_table_has_no_index_but_its_primary_key()
    {
        await using var desk = await StoreAsync("dispatch_compilation_indexes");

        (await IndexesAsync(desk.ConnectionString)).Should().Equal("PK_workflow_compilations");
    }

    [Fact]
    public async Task Two_writers_held_until_both_insert_finish_with_one_row_holding_the_losers_content()
    {
        var barrier = new InsertBarrier(parties: 2);
        await using var desk = await StoreAsync("dispatch_compilation_race", barrier);
        var versionId = Guid.NewGuid();

        var a = Task.Run(() => desk.Store.SaveAsync(Verdict(versionId, "plugin@a"), default));
        var b = Task.Run(() => desk.Store.SaveAsync(Verdict(versionId, "plugin@b"), default));
        var racing = () => Task.WhenAll(a, b);

        await racing.Should().NotThrowAsync("Npgsql's unique violation is caught and finished as an update");
        barrier.Losers.Should().ContainSingle("both writers reached their insert before either ran it");
        (await RowCountAsync(desk.ConnectionString)).Should().Be(1);
        (await desk.Store.FindAsync(versionId, "digest-a", "hash-a", default))!.BlockSources.Should().Equal(
            [barrier.Losers.Single()], "the loser's update lands after the winner's insert");
    }

    [Fact]
    public async Task An_insert_refused_for_a_reason_other_than_the_key_still_throws()
    {
        await using var desk = await StoreAsync("dispatch_compilation_overlong");
        var overlong = Verdict(Guid.NewGuid(), "plugin@1") with { DefinitionDigest = new string('a', 65) };

        var saving = () => desk.Store.SaveAsync(overlong, default);

        (await saving.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.StringDataRightTruncation);
        (await RowCountAsync(desk.ConnectionString)).Should().Be(0);
    }

    [Fact]
    public async Task A_race_lost_inside_the_callers_ambient_transaction_neither_throws_nor_dooms_that_transaction()
    {
        var peer = new BeforeFirstInsert();
        await using var desk = await StoreAsync("dispatch_compilation_ambient_race", peer);
        var versionId = Guid.NewGuid();
        peer.Arrange = () => PeerInsert(desk.ConnectionString, versionId);

        var working = async () =>
        {
            using var callers = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            await desk.Store.SaveAsync(Verdict(versionId, "plugin@mine"), default);

            await using var connection = new NpgsqlConnection(desk.ConnectionString);
            await connection.OpenAsync();
            await using var read = new NpgsqlCommand("SELECT count(*) FROM automation.workflow_compilations", connection);
            (await read.ExecuteScalarAsync()).Should().Be(1L);
            callers.Complete();
        };

        await working.Should().NotThrowAsync();
        peer.Arranged.Should().BeTrue("this test is about the race path");
        (await desk.Store.FindAsync(versionId, "digest-a", "hash-a", default))!.BlockSources.Should().Equal("plugin@mine");
    }

    [Fact]
    public async Task A_verdict_saved_inside_an_ambient_transaction_the_caller_rolls_back_is_kept()
    {
        await using var desk = await StoreAsync("dispatch_compilation_ambient_save");

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            await desk.Store.SaveAsync(Verdict(Guid.NewGuid(), "plugin@1"), default);

        (await RowCountAsync(desk.ConnectionString)).Should().Be(1);
    }

    [Fact]
    public async Task A_caller_supplied_open_connection_gets_no_verdict_written_in_its_transaction()
    {
        var commands = new QueryPlans.RecordingCommands();
        await using var desk = await SharedConnectionAsync("dispatch_compilation_shared_commit", commands);
        using var caller = desk.Provider.CreateScope();
        var context = caller.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var before = commands.All.Count;

        var saving = () => desk.Store.SaveAsync(Verdict(Guid.NewGuid(), "plugin@1"), default);

        (await saving.Should().ThrowAsync<CompilationStoreUnavailableException>()).WithMessage("*already open*not saved*");
        (await desk.Store.FindAsync(Guid.NewGuid(), "digest-a", "hash-a", default)).Should().BeNull();
        commands.All.Count.Should().Be(before, "neither call ran a command on the caller's connection");
        await transaction.CommitAsync();
        (await RowCountAsync(desk.ConnectionString)).Should().Be(0);
    }

    [Fact]
    public async Task A_caller_supplied_open_connection_is_refused_before_an_insert_a_peer_could_make_lose()
    {
        var peer = new BeforeFirstInsert();
        await using var desk = await SharedConnectionAsync("dispatch_compilation_shared_race", peer);
        var versionId = Guid.NewGuid();
        peer.Arrange = () => PeerInsert(desk.ConnectionString, versionId);
        using var caller = desk.Provider.CreateScope();
        var context = caller.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();

        await FluentActions.Awaiting(() => desk.Store.SaveAsync(Verdict(versionId, "plugin@mine"), default))
            .Should().ThrowAsync<CompilationStoreUnavailableException>();
        var after = () => context.Database.ExecuteSqlRawAsync(
            "UPDATE automation.workflow_compilations SET is_valid = true WHERE false");

        await after.Should().NotThrowAsync("the caller's transaction is still usable");
        await transaction.CommitAsync();
        peer.Arranged.Should().BeFalse("the store never reached an insert on the caller's connection");
    }

    [Fact]
    public async Task A_caller_supplied_open_connection_answers_a_find_as_a_miss()
    {
        await using var desk = await SharedConnectionAsync("dispatch_compilation_shared_find");
        var versionId = Guid.NewGuid();
        await InsertRowAsync(desk.ConnectionString, versionId, "hash-a", DateTimeOffset.UtcNow);
        using var caller = desk.Provider.CreateScope();
        await using var transaction = await caller.ServiceProvider.GetRequiredService<WorkflowDbContext>()
            .Database.BeginTransactionAsync();

        (await desk.Store.FindAsync(versionId, "digest-a", "hash-a", default)).Should().BeNull(
            "a read inside the caller's transaction can abort it; a miss costs a recompile");
    }

    [Fact]
    public async Task With_a_pool_of_one_the_caller_holds_a_find_fails_after_the_pool_timeout()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(
            await backends.CreateDatabaseAsync("dispatch_compilation_pool_one")) { MaxPoolSize = 1, Timeout = 1 }.ConnectionString;
        await using var desk = await DeskOverAsync(connectionString, options => options.UseNpgsql(connectionString).UseWorkflowPostgreSqlSchema());
        using var caller = desk.Provider.CreateScope();
        var context = caller.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var finding = () => desk.Store.FindAsync(Guid.NewGuid(), "digest-a", "hash-a", default);

        var thrown = await finding.Should().ThrowAsync<Exception>();
        thrown.Which.ToString().Should().Contain("The connection pool has been exhausted");
        await FluentActions.Awaiting(() => context.Database.ExecuteSqlRawAsync("SELECT 1"))
            .Should().NotThrowAsync("the store's wait did not touch the caller's transaction");
    }

    [Fact]
    public async Task A_retention_delete_inside_an_ambient_transaction_the_caller_rolls_back_still_deletes()
    {
        await using var desk = await StoreAsync("dispatch_compilation_ambient_delete");
        await InsertRowAsync(desk.ConnectionString, Guid.NewGuid(), "hash-old", DateTimeOffset.UtcNow.AddDays(-90));

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            await desk.Store.DeleteStaleAsync([], Retention, default);

        (await RowCountAsync(desk.ConnectionString)).Should().Be(0);
    }

    [Fact]
    public async Task A_time_with_an_offset_is_saved_and_read_back_as_the_same_instant()
    {
        await using var desk = await StoreAsync("dispatch_compilation_offset");
        var versionId = Guid.NewGuid();
        var local = new DateTimeOffset(2026, 9, 23, 2, 0, 0, TimeSpan.FromHours(2));

        await desk.Store.SaveAsync(Verdict(versionId, "plugin@1") with { CompiledAtUtc = local }, default);

        var found = await desk.Store.FindAsync(versionId, "digest-a", "hash-a", default);
        found!.CompiledAtUtc.Should().Be(local);
        found.CompiledAtUtc.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_NUL_in_the_compiler_version_is_refused_before_it_reaches_the_database()
    {
        await using var desk = await StoreAsync("dispatch_compilation_nul_text");

        var saving = () => desk.Store.SaveAsync(Verdict(Guid.NewGuid(), "plugin@1") with { CompilerVersion = "c\0x" }, default);

        (await saving.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("CompilerVersion");
        (await RowCountAsync(desk.ConnectionString)).Should().Be(0);
    }

    [Fact]
    public async Task A_NUL_in_a_node_id_is_refused_rather_than_rewritten()
    {
        await using var desk = await StoreAsync("dispatch_compilation_nul_json");
        var diagnostic = new CompilationDiagnostic(CompilationRules.BlockTypeUnknown, "gone", "a\0b");

        var saving = () => desk.Store.SaveAsync(Verdict(Guid.NewGuid(), "plugin@1") with { Diagnostics = [diagnostic] }, default);

        (await saving.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("Diagnostics[0].NodeId");
        (await RowCountAsync(desk.ConnectionString)).Should().Be(0);
    }

    [Fact]
    public async Task A_find_under_a_key_with_a_NUL_answers_a_miss()
    {
        await using var desk = await StoreAsync("dispatch_compilation_nul_find");

        (await desk.Store.FindAsync(Guid.NewGuid(), "digest\0a", "hash-a", default)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteStaleAsync_refuses_a_current_key_with_a_NUL_and_deletes_nothing()
    {
        var commands = new QueryPlans.RecordingCommands();
        await using var desk = await StoreAsync("dispatch_compilation_nul_delete", commands);
        var versionId = Guid.NewGuid();
        await InsertRowAsync(desk.ConnectionString, versionId, "hash-old", DateTimeOffset.UtcNow.AddDays(-90));
        var before = commands.All.Count;

        var deleting = () => desk.Store.DeleteStaleAsync([new CompilationKey(versionId, "digest-a", "hash\0a")], Retention, default);

        (await deleting.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("current");
        commands.All.Count.Should().Be(before);
        (await RowCountAsync(desk.ConnectionString)).Should().Be(1);
    }

    [Fact]
    public async Task A_version_read_back_has_another_definition_digest_than_the_text_it_was_saved_with()
    {
        await using var desk = await StoreAsync("dispatch_compilation_jsonb");
        const string definition = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
        var saved = await SaveVersionAsync(desk, definition, """[{"name":"count","type":"Int"}]""", "[]");

        var first = await ReadVersionAsync(desk, saved.Id);
        var second = await ReadVersionAsync(desk, saved.Id);

        first.DefinitionJson.Should().Be(
            """{"edges": [], "nodes": [{"id": "start", "position": {"x": 0, "y": 0}, "blockTypeId": "WorkflowInput", "configuration": {}}]}""");
        first.InputParametersJson.Should().Be("""[{"name": "count", "type": "Int"}]""");
        first.OutputParametersJson.Should().Be("[]");
        WorkflowCompilationService.ComputeDefinitionDigest(first).Should().NotBe(WorkflowCompilationService.ComputeDefinitionDigest(saved));
        WorkflowCompilationService.ComputeDefinitionDigest(second).Should().Be(WorkflowCompilationService.ComputeDefinitionDigest(first));
        (await ScalarAsync(desk.ConnectionString, """SELECT '{"a":1,"a":2}'::jsonb::text""")).Should().Be(
            """{"a": 2}""", "a digest over the normalised text would give two verdicts one key: the reader refuses the "
            + "text this came from (L3), as the compiler refuses 1e0 where it takes the 1 jsonb reads it back as");
    }

    [Fact]
    public async Task A_number_jsonb_respells_can_turn_an_invalid_verdict_valid()
    {
        await using var desk = await StoreAsync("dispatch_compilation_jsonb_verdict");
        const string definition = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"noop","configuration":{"retryCount":1e0},"position":{"x":0,"y":0}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}
            """;
        var saved = await SaveVersionAsync(desk, definition, null, null);
        var read = await ReadVersionAsync(desk, saved.Id);
        var service = CompilationScopes.Service(new NoopOnly(), new NoWorkflows(), desk.Store);

        var asSaved = await service.EnsureAsync(saved, null, default);
        var asStored = await service.EnsureAsync(read, null, default);

        read.DefinitionJson.Should().Contain("""{"retryCount": 1}""");
        asSaved.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ResilienceConfigInvalid);
        asStored.IsValid.Should().BeTrue();
        asStored.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task The_listing_does_not_read_rows_its_callers_transaction_scope_has_not_committed()
    {
        await using var desk = await StoreAsync("dispatch_compilation_listing_ambient");
        var listing = new EfCurrentWorkflowVersions(desk.Provider.GetRequiredService<IServiceScopeFactory>());

        int inside;
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            await SaveVersionAsync(desk, """{"nodes":[],"edges":[]}""", null, null, makeCurrent: true);
            inside = 0;
            await foreach (var _ in listing.ReadAsync(default))
                inside++;
        }

        inside.Should().Be(0);
        (await RowCountAsync(desk.ConnectionString, "workflows")).Should().Be(0, "the caller's scope was never completed");
    }

    [Fact]
    public async Task A_version_compiled_as_saved_and_again_as_read_back_leaves_two_rows()
    {
        await using var desk = await StoreAsync("dispatch_compilation_jsonb_rows");
        var saved = await SaveVersionAsync(desk, """{"nodes":[],"edges":[]}""", null, null);
        var service = CompilationScopes.Service(new NoBlocks(), new NoWorkflows(), desk.Store);

        await service.EnsureAsync(saved, null, default);
        await service.EnsureAsync(await ReadVersionAsync(desk, saved.Id), null, default);
        await service.EnsureAsync(await ReadVersionAsync(desk, saved.Id), null, default);

        (await RowCountAsync(desk.ConnectionString)).Should().Be(2, "the saved text and the text read back are two digests");
    }

    [Fact]
    public async Task The_current_versions_are_read_in_pages_each_once()
    {
        await using var desk = await StoreAsync("dispatch_compilation_current_versions");
        var published = new List<Guid>();
        for (var i = 0; i < 5; i++)
            published.Add((await SaveVersionAsync(desk, """{"nodes":[],"edges":[]}""", null, null, makeCurrent: true)).Id);
        await SaveVersionAsync(desk, """{"nodes":[],"edges":[]}""", null, null);

        var listed = new List<Guid>();
        await foreach (var current in new EfCurrentWorkflowVersions(desk.Provider.GetRequiredService<IServiceScopeFactory>(), pageSize: 2).ReadAsync(default))
            listed.Add(current.Version.Id);

        listed.Should().BeEquivalentTo(published).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_sweep_and_admission_read_one_digest_for_a_current_version()
    {
        await using var desk = await StoreAsync("dispatch_compilation_one_digest");
        var saved = await SaveVersionAsync(desk,
            """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", """[{"name":"a","type":"Int"}]""", null,
            makeCurrent: true);

        WorkflowVersion? swept = null;
        await foreach (var current in new EfCurrentWorkflowVersions(desk.Provider.GetRequiredService<IServiceScopeFactory>()).ReadAsync(default))
            swept = current.Version;
        using var scope = desk.Provider.CreateScope();
        var admitted = (await new EfWorkflowStore(scope.ServiceProvider.GetRequiredService<WorkflowDbContext>())
            .LoadAsync(saved.WorkflowId, default))!.CurrentVersion!;

        WorkflowCompilationService.ComputeDefinitionDigest(swept!).Should().Be(WorkflowCompilationService.ComputeDefinitionDigest(admitted));
        WorkflowCompilationService.ComputeDefinitionDigest(swept!).Should().NotBe(WorkflowCompilationService.ComputeDefinitionDigest(saved));
    }

    [Fact]
    public async Task A_verdict_of_a_version_whose_insert_has_not_committed_goes_only_once_older_than_the_retention()
    {
        await using var desk = await StoreAsync("dispatch_compilation_uncommitted_version");
        var workflowId = (await SaveVersionAsync(desk, "{}", null, null)).WorkflowId;
        var pending = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(desk.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand($$"""
            INSERT INTO automation.workflow_versions (id, workflow_id, version_number, definition_json, created_at_utc, created_by_user_id, is_published)
            VALUES ('{{pending}}', '{{workflowId}}', 2, '{}', now(), gen_random_uuid(), false)
            """, connection, transaction))
            await insert.ExecuteNonQueryAsync();
        await InsertRowAsync(desk.ConnectionString, pending, "hash-old", DateTimeOffset.UtcNow.AddDays(-90));
        await InsertRowAsync(desk.ConnectionString, pending, "hash-fresh", DateTimeOffset.UtcNow);

        var deleted = await desk.Store.DeleteStaleAsync([], Retention, default).WaitAsync(TimeSpan.FromSeconds(30));
        await transaction.CommitAsync();

        deleted.Should().Be(1, "the delete waited for nothing and the old row cost a recompile");
        (await HashesAsync(desk.ConnectionString)).Should().Equal("hash-fresh");
        (await desk.Store.DeleteStaleAsync([], Retention, default)).Should().Be(0);
    }

    [Fact]
    public async Task A_draft_published_between_the_listing_and_the_retention_keeps_its_verdict()
    {
        const string Start = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
        await using var desk = await StoreAsync("dispatch_compilation_draft_published");
        var current = await SaveVersionAsync(desk, Start, null, null, makeCurrent: true);
        var draftId = Guid.NewGuid();
        await ExecuteAsync(desk.ConnectionString, $$"""
            INSERT INTO automation.workflow_versions (id, workflow_id, version_number, definition_json, created_at_utc, created_by_user_id, is_published)
            VALUES ('{{draftId}}', '{{current.WorkflowId}}', 2, '{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{} },{"id":"n","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{} }],"edges":[]}', now(), gen_random_uuid(), false);
            UPDATE automation.workflows SET draft_version_id = '{{draftId}}' WHERE id = '{{current.WorkflowId}}';
            """);
        var service = CompilationScopes.Service(new NoopOnly(), new NoWorkflows(), desk.Store);
        var draft = CompilationKey.Of(await service.EnsureAsync(await ReadVersionAsync(desk, draftId), null, default));
        await ExecuteAsync(desk.ConnectionString, "UPDATE automation.workflow_compilations SET compiled_at_utc = now() - interval '90 days'");
        var sweeper = new WorkflowCompilationSweeper(service, desk.Store) { Retention = TimeSpan.FromDays(30) };
        var report = await sweeper.SweepAsync(
            new EfCurrentWorkflowVersions(desk.Provider.GetRequiredService<IServiceScopeFactory>()).ReadAsync(default), default);

        await ExecuteAsync(desk.ConnectionString, $"""
            UPDATE automation.workflow_versions SET is_published = true, published_at_utc = now() WHERE id = '{draftId}';
            UPDATE automation.workflows SET current_version_id = '{draftId}', draft_version_id = NULL WHERE id = '{current.WorkflowId}';
            """);
        var deleted = await sweeper.DeleteStaleAsync(report, default);

        report.Current.Should().ContainSingle().Which.WorkflowVersionId.Should().Be(current.Id);
        deleted.Should().Be(0);
        (await desk.Store.FindAsync(draft.WorkflowVersionId, draft.DefinitionDigest, draft.DependencyHash, default)).Should().NotBeNull();
    }

    private static async Task<IReadOnlyList<string>> IndexesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'automation' AND tablename = 'workflow_compilations'",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<Guid>> ExistingVersionsAsync(Desk desk, int count)
    {
        var workflowId = (await SaveVersionAsync(desk, "{}", null, null)).WorkflowId;
        await ExecuteAsync(desk.ConnectionString, $$"""
            INSERT INTO automation.workflow_versions
                (id, workflow_id, version_number, definition_json, created_at_utc, created_by_user_id, is_published)
            SELECT gen_random_uuid(), '{{workflowId}}', g + 1, '{}', now(), gen_random_uuid(), false
            FROM generate_series(1, {{count}}) g;
            """);
        await using var connection = new NpgsqlConnection(desk.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id FROM automation.workflow_versions WHERE workflow_id = @workflow AND version_number > 1 ORDER BY version_number",
            connection);
        command.Parameters.AddWithValue("workflow", workflowId);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private static CompilationVerdict Verdict(Guid versionId, string source) =>
        new(versionId, "digest-a", "hash-a", true, [], [source], DependencyFingerprint.CompilerVersion, DateTimeOffset.UtcNow);

    private static async Task<WorkflowVersion> SaveVersionAsync(
        Desk desk, string definition, string? inputs, string? outputs, bool makeCurrent = false)
    {
        using var scope = desk.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(Guid.NewGuid(), "jsonb", null, Guid.NewGuid(), now);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var version = new WorkflowVersion(Guid.NewGuid(), workflow.Id, 1, definition, inputs, outputs, Guid.NewGuid(), now, isPublished: true);
        db.WorkflowVersions.Add(version);
        if (makeCurrent)
            workflow.SetCurrentVersion(version, now);
        await db.SaveChangesAsync();
        return version;
    }

    private static async Task<WorkflowVersion> ReadVersionAsync(Desk desk, Guid versionId)
    {
        using var scope = desk.Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowVersions
            .AsNoTracking().SingleAsync(v => v.Id == versionId);
    }

    private async Task<Desk> StoreAsync(string database, params IInterceptor[] interceptors)
    {
        var connectionString = await backends.CreateDatabaseAsync(database);
        return await DeskOverAsync(connectionString, options =>
        {
            options.UseNpgsql(connectionString).UseWorkflowPostgreSqlSchema();
            if (interceptors.Length > 0)
                options.AddInterceptors(interceptors);
        });
    }

    private async Task<Desk> SharedConnectionAsync(string database, params IInterceptor[] interceptors)
    {
        var connectionString = await backends.CreateDatabaseAsync(database);
        var connection = new NpgsqlConnection(connectionString);
        return await DeskOverAsync(connectionString, options =>
        {
            options.UseNpgsql(connection).UseWorkflowPostgreSqlSchema();
            if (interceptors.Length > 0)
                options.AddInterceptors(interceptors);
        }, connection);
    }

    private static async Task<Desk> DeskOverAsync(
        string connectionString, Action<DbContextOptionsBuilder> configure, NpgsqlConnection? owned = null)
    {
        var provider = new ServiceCollection().AddDbContext<WorkflowDbContext>(configure).BuildServiceProvider();

        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();

        return new Desk(connectionString, provider,
            new EfWorkflowCompilationStore(provider.GetRequiredService<IServiceScopeFactory>()), owned);
    }


    private static void PeerInsert(string connectionString, Guid versionId)
    {
        using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false }.ConnectionString);
        connection.Open();
        using var insert = new NpgsqlCommand("""
            INSERT INTO automation.workflow_compilations
                (workflow_version_id, definition_digest, dependency_hash, is_valid,
                 diagnostics_json, block_sources_json, compiler_version, compiled_at_utc)
            VALUES (@versionId, 'digest-a', 'hash-a', false, '[]', '["plugin@peer"]', 'peer', now())
            """, connection);
        insert.Parameters.AddWithValue("versionId", versionId);
        insert.ExecuteNonQuery();
    }

    private static async Task InsertRowAsync(string connectionString, Guid versionId, string dependencyHash, DateTimeOffset compiledAtUtc)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO automation.workflow_compilations
                (workflow_version_id, definition_digest, dependency_hash, is_valid,
                 diagnostics_json, block_sources_json, compiler_version, compiled_at_utc)
            VALUES (@versionId, 'digest-a', @dependencyHash, true, '[]', '[]', 'test', @compiledAtUtc)
            """, connection);
        insert.Parameters.AddWithValue("versionId", versionId);
        insert.Parameters.AddWithValue("dependencyHash", dependencyHash);
        insert.Parameters.AddWithValue("compiledAtUtc", compiledAtUtc);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> HashesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT dependency_hash FROM automation.workflow_compilations", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var hashes = new List<string>();
        while (await reader.ReadAsync())
            hashes.Add(reader.GetString(0));
        return hashes;
    }

    private static async Task<long> RowCountAsync(string connectionString, string table = "workflow_compilations")
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM automation.{table}", connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private sealed record Desk(
        string ConnectionString, ServiceProvider Provider, EfWorkflowCompilationStore Store, NpgsqlConnection? Owned)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            if (Owned is not null)
                await Owned.DisposeAsync();
        }
    }

    private sealed class NoopOnly : ReeveWorkflow.Core.Execution.IWorkflowBlocks
    {
        private readonly Dictionary<string, ReeveWorkflow.Abstractions.IWorkflowBlockFactory> byId = new(StringComparer.OrdinalIgnoreCase)
        {
            ["noop"] = new Block("noop"),
        };

        public ReeveWorkflow.Abstractions.IWorkflowBlockFactory? Find(string blockTypeId) => byId.GetValueOrDefault(blockTypeId);

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<ReeveWorkflow.Abstractions.IWorkflowBlockFactory> GetAll() => byId.Values;

        private sealed class Block(string id) : ReeveWorkflow.Abstractions.IWorkflowBlockFactory
        {
            public string BlockTypeId => id;
            public string DisplayName => id;
            public string Description => id;
            public string Category => "test";
            public IReadOnlyCollection<ReeveWorkflow.Abstractions.BlockPortDefinition> Inputs => [];
            public IReadOnlyCollection<ReeveWorkflow.Abstractions.BlockPortDefinition> Outputs => [];

            public ReeveWorkflow.Abstractions.IWorkflowBlockPlugin Create() => throw new NotSupportedException();
        }
    }

    private sealed class NoBlocks : ReeveWorkflow.Core.Execution.IWorkflowBlocks
    {
        public ReeveWorkflow.Abstractions.IWorkflowBlockFactory? Find(string blockTypeId) => null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<ReeveWorkflow.Abstractions.IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class NoWorkflows : ReeveWorkflow.Core.Execution.IWorkflowStore
    {
        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken) => Task.FromResult<Workflow?>(null);

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowVersion?>(null);

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<(Guid, string)?>(null);
    }

    private sealed class InsertBarrier(int parties) : SaveChangesInterceptor
    {
        private readonly Barrier barrier = new(parties);
        private int arrived;

        public ConcurrentBag<string> Losers { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Inserting(eventData.Context) is not null
                && Interlocked.Increment(ref arrived) <= parties
                && !barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken))
            {
                throw new TimeoutException("The other writer never reached its insert.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }
                && Inserting(eventData.Context) is { } row)
            {
                var sources = System.Text.Json.JsonSerializer.Deserialize<string[]>(row.BlockSourcesJson)!;
                Losers.Add(sources.Single());
            }

            return base.SaveChangesFailedAsync(eventData, cancellationToken);
        }

        private static WorkflowCompilation? Inserting(DbContext? context) => context?.ChangeTracker
            .Entries<WorkflowCompilation>().SingleOrDefault(entry => entry.State == EntityState.Added)?.Entity;
    }

    private sealed class BeforeFirstInsert : SaveChangesInterceptor
    {
        public Action? Arrange;
        public bool Arranged;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<WorkflowCompilation>().Any(entry => entry.State == EntityState.Added)
                && Interlocked.Exchange(ref Arrange, null) is { } arrange)
            {
                arrange();
                Arranged = true;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
