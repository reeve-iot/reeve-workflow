using Dispatch.Host.Freight;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class ParkedWaitKeyDependencyTests(ClusterBackends backends)
{
    [Fact]
    public async Task A_fire_reads_exactly_the_parked_rows_with_its_dependency_key()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_dependency");
        var first = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":["door-open"]}""");
        var second = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":["door-open","sunset"]}""");
        await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":["sunset"]}""");

        var scan = await world.Resume().ScanParkedWaitsByDependencyKeyAsync(
            "trigger-id:door-open", WorkflowSuspensionScanCursor.Start, 20, default);

        scan.Waits.Select(wait => wait.SuspensionId).Should().BeEquivalentTo([first, second]);
        scan.Waits.Should().HaveCount(2, "the SQL result contains only matching rows");
    }

    [Fact]
    public async Task A_predicate_wait_is_found_by_a_keyed_scan_of_one_of_its_keys()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_predicate");
        var both = await world.ParkAsync(WorkflowSuspensionKind.Predicate,
            """{"input":"Predicate","keys":["ship-1/temp","ship-1/door"],"host":null}""");
        var door = await world.ParkAsync(WorkflowSuspensionKind.Predicate, """{"input":"Predicate","keys":["ship-1/door"],"host":null}""");
        await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":["ship-1/temp"]}""");

        (await world.ScanAsync(WorkflowDependencyKeys.PredicatePrefix + "ship-1/temp")).Should().BeEquivalentTo([both]);
        (await world.ScanAsync(WorkflowDependencyKeys.PredicatePrefix + "ship-1/door")).Should().BeEquivalentTo([both, door]);
    }

    [Fact]
    public async Task Taking_or_abandoning_a_wait_removes_its_dependency_keys()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_cleanup");
        var taken = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":["taken"]}""");
        await world.Resume().TryResolveAsync(taken, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default);
        var abandoned = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":["abandoned"]}""");

        var executionId = await world.ExecutionIdAsync(abandoned);
        await new EfWorkflowSuspensionStore(world.Container.GetRequiredService<IServiceScopeFactory>(), World.Registrations)
            .CancelByExecutionAsync(executionId, default);

        (await world.KeyCountAsync(taken)).Should().Be(0);
        (await world.KeyCountAsync(abandoned)).Should().Be(0);
    }

    [Fact]
    public async Task The_keyed_scan_is_answered_from_the_key_table()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_keyed_reads");
        var match = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["fired"]}""");
        await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["other"]}""");
        var sent = world.Commands.Readers.Count;

        (await world.ScanAsync("trigger-id:fired")).Should().Equal(match);

        world.Commands.Readers.Skip(sent).Should().NotBeEmpty().And.OnlyContain(
            command => command.Text.Contains("workflow_suspension_dependency_keys"),
            "every read of a keyed scan goes through the keys, and none of them is the unkeyed scan of every parked row");
    }

    [Fact]
    public async Task A_wait_without_key_rows_is_not_offered_by_a_keyed_scan()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_unkeyed");
        await world.InsertParkedAsync(
            """{"Expression":"${device.kitchen.temperature} > 22"}""", kind: WorkflowSuspensionKind.WaitTemplate);
        await world.InsertParkedAsync("""{"TriggerIds":["rolling"]}""");
        var keyed = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["rolling"]}""");

        (await world.ScanAsync("device:kitchen:temperature")).Should().BeEmpty("every writer keys a wait as it parks");
        var sent = world.Commands.Readers.Count;
        (await world.ScanAsync("trigger-id:rolling")).Should().Equal(keyed);
        world.Commands.Readers.Skip(sent).Should().HaveCount(2,
            "one page through the key index and one read of that page's keys, with no look for rows that have none");
    }

    [Fact]
    public async Task A_keyed_wait_comes_back_with_all_its_keys()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_all_keys");
        await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["a","b"]}""");

        var scan = await world.Resume().ScanParkedWaitsByDependencyKeyAsync(
            "trigger-id:a", WorkflowSuspensionScanCursor.Start, 20, default);

        scan.Waits.Should().ContainSingle().Which.DependencyKeys.Should().BeEquivalentTo(["trigger-id:a", "trigger-id:b"]);
    }

    [Fact]
    public async Task A_put_back_wait_has_its_keys_again_and_a_second_put_back_does_no_harm()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_put_back");
        var id = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["again"]}""");
        (await world.Resume().TryResolveAsync(id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await world.KeysAsync(id)).Should().BeEmpty();
        (await world.ScanAsync("trigger-id:again")).Should().BeEmpty("a taken wait is not offered");

        (await world.Resume().ReleaseAsync(id, WorkflowSuspensionStatus.Resumed, default)).Should().BeTrue();

        (await world.KeysAsync(id)).Should().Equal("trigger-id:again");
        (await world.ScanAsync("trigger-id:again")).Should().Equal(id);
        (await world.Resume().ReleaseAsync(id, WorkflowSuspensionStatus.Resumed, default))
            .Should().BeFalse("it is parked again, not taken, so there is nothing to put back");
        (await world.KeysAsync(id)).Should().Equal("trigger-id:again");
    }

    [Fact]
    public async Task A_put_back_works_under_a_retrying_execution_strategy()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_put_back_retry");
        var id = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["retry"]}""");
        (await world.Resume().TryResolveAsync(id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        await using var retrying = new ServiceCollection()
            .AddDbContext<WorkflowDbContext>(db => db.UseNpgsql(world.Postgres, npgsql => npgsql.EnableRetryOnFailure()))
            .BuildServiceProvider();
        var store = new EfWorkflowSuspensionResumeStore(retrying.GetRequiredService<IServiceScopeFactory>(), World.Registrations);

        (await store.ReleaseAsync(id, WorkflowSuspensionStatus.Resumed, default)).Should().BeTrue();

        (await world.KeysAsync(id)).Should().Equal("trigger-id:retry");
    }

    [Fact]
    public async Task An_expression_wait_is_keyed_by_what_it_reads_when_it_parks()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_template_keys");
        var id = await world.ParkAsync(
            WorkflowSuspensionKind.WaitTemplate, """{"Expression":"${device.kitchen.temperature} > 22"}""");

        (await world.KeysAsync(id)).Should().Equal("device:kitchen:temperature");
        (await world.ScanAsync("device:kitchen:temperature")).Should().Equal(id);
        (await world.ScanAsync("device:kitchen:humidity")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_an_old_replica_took_is_not_offered_by_its_left_over_keys()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_left_over");
        var id = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["left-over"]}""");
        await using (var connection = new NpgsqlConnection(world.Postgres))
        {
            await connection.OpenAsync();
            await using var take = new NpgsqlCommand(
                "UPDATE automation.workflow_suspensions SET status = 'Resumed', outcome_json = '{}'::jsonb, resolved_at_utc = now(), take_count = take_count + 1 WHERE id = $1",
                connection);
            take.Parameters.AddWithValue(id);
            (await take.ExecuteNonQueryAsync()).Should().Be(1);
        }

        (await world.KeyCountAsync(id)).Should().Be(1, "an old replica's take does not know the keys exist");
        (await world.ScanAsync("trigger-id:left-over")).Should().BeEmpty("a taken row is not offered");
    }

    [Fact]
    public async Task Keyed_pages_visit_every_match_once_across_a_shared_instant()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_paging");
        var at = DateTimeOffset.UtcNow.AddMinutes(5);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
            ids.Add(await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["paged"]}""", i < 3 ? at : at.AddSeconds(1)));
        for (var i = 0; i < 3; i++)
        {
            await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["not-paged"]}""", at);
            ids.Add(await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["paged"]}""", at));
        }

        var seen = new List<Guid>();
        var cursor = WorkflowSuspensionScanCursor.Start;
        for (var page = 0; page < 30; page++)
        {
            var scan = await world.Resume().ScanParkedWaitsByDependencyKeyAsync("trigger-id:paged", cursor, 2, default);
            seen.AddRange(scan.Waits.Select(wait => wait.SuspensionId));
            cursor = scan.NextCursor;
            if (cursor.AfterSuspensionId is null)
                break;
        }

        seen.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(ids);
    }

    [Fact]
    public async Task A_page_of_one_holds_the_match_behind_older_rows_with_other_keys()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_page_of_one");
        var early = DateTimeOffset.UtcNow.AddMinutes(1);
        await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["older"]}""", early);
        await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["older"]}""", early.AddSeconds(1));
        var match = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["newer"]}""", early.AddSeconds(2));

        var scan = await world.Resume().ScanParkedWaitsByDependencyKeyAsync(
            "trigger-id:newer", WorkflowSuspensionScanCursor.Start, 1, default);

        scan.Waits.Should().ContainSingle().Which.SuspensionId.Should().Be(match);
    }

    [Fact]
    public async Task A_resolved_or_cancelled_wait_is_not_offered()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_status");
        var timedOut = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["gone"]}""");
        await world.Resume().TryResolveAsync(timedOut, WorkflowSuspensionStatus.TimedOut, "{}", DateTimeOffset.UtcNow, default);
        var cancelled = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"TriggerIds":["gone"]}""");
        await new EfWorkflowSuspensionStore(world.Container.GetRequiredService<IServiceScopeFactory>(), World.Registrations)
            .CancelByExecutionAsync(await world.ExecutionIdAsync(cancelled), default);

        (await world.ScanAsync("trigger-id:gone")).Should().BeEmpty();
    }

    [Fact]
    public async Task The_written_key_the_index_key_and_the_keyed_scan_key_of_a_host_namespace_are_one()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_one_key");
        var id = await world.ParkAsync(
            WorkflowSuspensionKind.WaitTemplate, """{"Expression":"${depot.north.gate7.state.open} == true"}""");

        var written = await world.KeysAsync(id);
        var index = (await world.Resume().ScanParkedWaitsAsync(WorkflowSuspensionScanCursor.Start, 50, null, default))
            .Waits.Single(wait => wait.SuspensionId == id).DependencyKeys;

        written.Should().Equal("depot:north:gate7");
        index.Should().BeEquivalentTo(written);
        (await world.ScanAsync("depot:north:gate7")).Should().Equal(id);
    }

    [Fact]
    public async Task A_key_row_written_under_another_rule_is_not_found_by_the_key_the_index_build_hands_out()
    {
        await using var world = await World.StartAsync(backends, "parked_wait_disagreeing_row");
        var id = await world.ParkAsync(
            WorkflowSuspensionKind.WaitTemplate, """{"Expression":"${previous.w.Out.auth} == 1"}""");
        await world.RewriteKeyAsync(id, "previous:w:Out:auth");

        var index = (await world.Resume().ScanParkedWaitsAsync(WorkflowSuspensionScanCursor.Start, 50, null, default))
            .Waits.Single(wait => wait.SuspensionId == id).DependencyKeys;

        index.Should().Equal("previous:w:Out");
        (await world.ScanAsync("previous:w:Out")).Should().BeEmpty("the row is keyed by what it was written with");
        (await world.ScanAsync("previous:w:Out:auth")).Should().Equal(id);
    }

    private sealed class World : IAsyncDisposable
    {
        public static IReadOnlyList<WorkflowExpressionNamespace> Registrations { get; } =
        [
            new WorkflowExpressionNamespace("depot", 2, new ClearanceStaticCheck(),
                (_, _) => throw new InvalidOperationException("these tests resolve no expression")),
        ];

        private readonly string postgres;
        private int parked;
        private int blocks;

        private World(string postgres, Guid workflowId, ParkedRow parkedRow)
        {
            this.postgres = postgres;
            WorkflowId = workflowId;
            Parked = parkedRow;
            Container = QueryPlans.Container(postgres, Commands);
            Store = new EfWorkflowSuspensionResumeStore(Container.GetRequiredService<IServiceScopeFactory>(), Registrations);
        }

        public QueryPlans.RecordingCommands Commands { get; } = new();
        public string Postgres => postgres;
        public Guid WorkflowId { get; }
        public ParkedRow Parked { get; }
        public ServiceProvider Container { get; }
        public IWorkflowSuspensionResumeStore Store { get; }

        public static async Task<World> StartAsync(ClusterBackends backends, string database)
        {
            var postgres = await backends.CreateDatabaseAsync(database);
            Guid workflowId;
            await using (var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
                         {
                             ReplicaId = "desk-waits",
                             PostgresConnectionString = postgres,
                             RedisConnectionString = backends.RedisConnectionString
                         }))
            {
                await FreightDesk.WarmAsync(postgres, desk);
                workflowId = await FreightDesk.SeedHoldUntilAsync(
                    postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
                    shipment: "SHP-7401", DateTimeOffset.UtcNow.AddHours(6));
                await desk.ExecuteAsync(workflowId);
                _ = await FreightDesk.EventuallyAsync(
                    () => FreightDesk.SuspensionsAsync(postgres, workflowId),
                    rows => rows.Count == 1,
                    TimeSpan.FromSeconds(30),
                    "the hold to be parked",
                    desk);
            }

            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT id, execution_log_id FROM automation.workflow_suspensions", connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            return new World(postgres, workflowId, new ParkedRow(reader.GetGuid(0), reader.GetGuid(1)));
        }

        public IWorkflowSuspensionResumeStore Resume() => Store;

        public async Task<Guid> ParkAsync(WorkflowSuspensionKind kind, string configJson, DateTimeOffset? at = null)
        {
            var parkedAt = at ?? DateTimeOffset.UtcNow.AddSeconds(++parked);
            var suspension = await new EfWorkflowSuspensionStore(Container.GetRequiredService<IServiceScopeFactory>(), Registrations)
                .SuspendAsync(new SuspendRequest
                {
                    ExecutionLogId = Parked.ExecutionLogId,
                    WorkflowId = WorkflowId,
                    BlockId = $"extra-{++blocks}",
                    Kind = kind,
                    ConfigJson = configJson,
                    StatePayloadJson = "{}",
                    SuspendedAtUtc = parkedAt,
                    TimeoutAtUtc = DateTimeOffset.UtcNow.AddHours(6)
                }, default);
            return suspension.Id;
        }

        public async Task<Guid> InsertParkedAsync(
            string configJson, string status = "Suspended", WorkflowSuspensionKind kind = WorkflowSuspensionKind.WaitForTrigger,
            DateTimeOffset? suspendedAt = null)
        {
            var id = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO automation.workflow_suspensions
                    (id, execution_log_id, workflow_id, block_id, kind, status, config_json,
                     state_payload_json, suspended_at_utc, timeout_at_utc, take_count)
                VALUES ($1, $2, $3, $4, $5, $9, $6::jsonb, '{}'::jsonb, $7, $8, 0)
                """, connection);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(Parked.ExecutionLogId);
            command.Parameters.AddWithValue(WorkflowId);
            command.Parameters.AddWithValue($"extra-{++blocks}");
            command.Parameters.AddWithValue((int)kind);
            command.Parameters.AddWithValue(configJson);
            command.Parameters.AddWithValue(suspendedAt ?? DateTimeOffset.UtcNow.AddSeconds(++parked));
            command.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddHours(6));
            command.Parameters.AddWithValue(status);
            await command.ExecuteNonQueryAsync();
            return id;
        }

        public async Task<IReadOnlyList<Guid>> ScanAsync(string key) =>
            (await Resume().ScanParkedWaitsByDependencyKeyAsync(key, WorkflowSuspensionScanCursor.Start, 50, default))
            .Waits.Select(wait => wait.SuspensionId).ToArray();

        public async Task<IReadOnlyList<(WorkflowSuspensionKind Kind, string Config, string[] Keys)>> SuspendedRowsAsync()
        {
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT s.kind, s.config_json::text, COALESCE(array_agg(k.dependency_key) FILTER (WHERE k.dependency_key IS NOT NULL), '{}')
                FROM automation.workflow_suspensions s
                LEFT JOIN automation.workflow_suspension_dependency_keys k ON k.suspension_id = s.id
                WHERE s.status = 'Suspended' AND s.kind = 0
                GROUP BY s.id
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(WorkflowSuspensionKind, string, string[])>();
            while (await reader.ReadAsync())
                rows.Add(((WorkflowSuspensionKind)reader.GetInt32(0), reader.GetString(1), reader.GetFieldValue<string[]>(2)));
            return rows;
        }

        public async Task<Guid> ExecutionIdAsync(Guid suspensionId)
        {
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT execution_log_id FROM automation.workflow_suspensions WHERE id = $1", connection);
            command.Parameters.AddWithValue(suspensionId);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return reader.GetGuid(0);
        }

        public async Task<IReadOnlyList<string>> KeysAsync(Guid suspensionId)
        {
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT dependency_key FROM automation.workflow_suspension_dependency_keys WHERE suspension_id = $1 ORDER BY dependency_key",
                connection);
            command.Parameters.AddWithValue(suspensionId);
            await using var reader = await command.ExecuteReaderAsync();
            var keys = new List<string>();
            while (await reader.ReadAsync())
                keys.Add(reader.GetString(0));
            return keys;
        }

        public async Task RewriteKeyAsync(Guid suspensionId, string key)
        {
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE automation.workflow_suspension_dependency_keys SET dependency_key = $2 WHERE suspension_id = $1",
                connection);
            command.Parameters.AddWithValue(suspensionId);
            command.Parameters.AddWithValue(key);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        public async Task<int> KeyCountAsync(Guid suspensionId)
        {
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM automation.workflow_suspension_dependency_keys WHERE suspension_id = $1", connection);
            command.Parameters.AddWithValue(suspensionId);
            return (int)(long)(await command.ExecuteScalarAsync() ?? 0L);
        }

        public async ValueTask DisposeAsync() => await Container.DisposeAsync();
    }

    private sealed record ParkedRow(Guid Id, Guid ExecutionLogId);
}
