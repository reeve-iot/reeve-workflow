using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class RunRowWriteTests(ClusterBackends backends)
{
    private const int Blocks = 12;

    [Fact]
    public async Task A_run_block_after_block_rewrites_its_row_in_place()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_row_hot");
        await using var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
        {
            ReplicaId = "desk-hot",
            PostgresConnectionString = postgres,
            RedisConnectionString = backends.RedisConnectionString
        });
        await FreightDesk.WarmAsync(postgres, desk);
        var workflowId = await FreightDesk.SeedAssignCarrierChainAsync(postgres, "SHP-7101", Blocks);
        var before = await UpdatesAsync(postgres);

        await desk.ExecuteAsync(workflowId);
        _ = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == "Completed",
            TimeSpan.FromSeconds(60),
            "the chain to run to its end",
            desk);

        var after = await FreightDesk.EventuallyAsync(
            () => UpdatesAsync(postgres),
            counts => counts.All - before.All >= Blocks + 1,
            TimeSpan.FromSeconds(30),
            "the desk's sessions to report the run's writes",
            desk);

        (after.HeapOnly - before.HeapOnly).Should().BeGreaterThanOrEqualTo(Blocks,
            $"none of the {Blocks} writes before a block changes an indexed column ({after.All - before.All} updates in all)");
    }

    [Fact]
    public async Task The_run_table_leaves_room_on_each_page_for_the_next_version_of_a_row()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_row_fillfactor");
        await using var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
        {
            ReplicaId = "desk-fill",
            PostgresConnectionString = postgres,
            RedisConnectionString = backends.RedisConnectionString
        });
        await FreightDesk.WarmAsync(postgres, desk);

        await using var connection = new NpgsqlConnection(postgres);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT reloptions FROM pg_class WHERE oid = 'automation.workflow_executions_state'::regclass", connection);

        (await command.ExecuteScalarAsync() as string[]).Should().NotBeNull()
            .And.Contain("fillfactor=70", "an update is heap-only only while the row's page has room for it");
    }

    [Fact]
    public async Task The_abandoned_run_sweep_reads_by_status_not_the_whole_run_table()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_sweep_plan");
        await using (var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
                     {
                         ReplicaId = "desk-sweep",
                         PostgresConnectionString = postgres,
                         RedisConnectionString = backends.RedisConnectionString
                     }))
        {
            await FreightDesk.WarmAsync(postgres, desk);
            var workflowId = await FreightDesk.SeedAssignCarrierChainAsync(postgres, "SHP-7102", 1);
            await desk.ExecuteAsync(workflowId);
            _ = await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                rows => rows.Count == 1 && rows[0].Status == "Completed",
                TimeSpan.FromSeconds(60),
                "the run to end",
                desk);
        }

        await FreightDesk.ExecuteSqlAsync(postgres, """
            INSERT INTO automation.workflow_executions_state
                (execution_id, workflow_id, workflow_version, status, current_block_path, variables_json,
                 last_transition_utc, started_at_utc, completed_at_utc, version)
            SELECT gen_random_uuid(), s.workflow_id, s.workflow_version,
                   CASE WHEN g % 20000 = 0 THEN 'Running' ELSE 'Completed' END, '', s.variables_json,
                   now() - interval '3 days' + g * interval '1 second', now() - interval '3 days', now(), 1
            FROM (SELECT * FROM automation.workflow_executions_state LIMIT 1) s CROSS JOIN generate_series(1, 60000) g;
            ANALYZE automation.workflow_executions_state;
            """);

        await using var container = QueryPlans.Container(postgres, new QueryPlans.RecordingCommands());
        var store = new EfWorkflowRunRecoveryStore(container.GetRequiredService<IServiceScopeFactory>());

        (await IndexExistsAsync(postgres)).Should().BeTrue(
            "ix_workflow_executions_state_status_wait_until is the index the abandoned-run sweep exists to ride; "
            + "if it is gone, that is what this test has found and the counters below mean nothing");

        var before = await SettledScansAsync(postgres);

        var claimed = await store.ClaimAbandonedRunsAsync(new WorkflowRunRecoveryClaim
        {
            AbandonedAfter = TimeSpan.FromMinutes(5),
            BatchSize = 50,
            ClaimedByReplicaId = "sweeper"
        }, default);

        claimed.Should().HaveCount(3);

        var after = await FreightDesk.EventuallyAsync(
            () => ScansAsync(postgres),
            scans => scans.StatusIndex > before.StatusIndex,
            TimeSpan.FromSeconds(30),
            "the claim's read of the status index to be counted");
        after.Sequential.Should().Be(before.Sequential,
            "a sweep that read every run the deployment has ever finished would be one sequential scan of sixty "
            + "thousand rows, every interval, on every replica");
    }

    private static async Task<(long Sequential, long StatusIndex)> ScansAsync(string postgres)
    {
        await using var connection = new NpgsqlConnection(postgres);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT t.seq_scan, COALESCE(i.idx_scan, 0)
            FROM pg_stat_user_tables t
            LEFT JOIN pg_stat_user_indexes i
              ON i.relid = t.relid AND i.indexrelname = 'ix_workflow_executions_state_status_wait_until'
            WHERE t.schemaname = 'automation' AND t.relname = 'workflow_executions_state'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<bool> IndexExistsAsync(string postgres)
    {
        await using var connection = new NpgsqlConnection(postgres);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_user_indexes
            WHERE schemaname = 'automation' AND relname = 'workflow_executions_state'
              AND indexrelname = 'ix_workflow_executions_state_status_wait_until'
            """, connection);
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private static async Task<(long Sequential, long StatusIndex)> SettledScansAsync(string postgres)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var last = await ScansAsync(postgres);
        for (var still = 0; still < 3;)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The run table's scan counters never settled: still moving after 30 seconds, last read "
                    + $"{last.Sequential} sequential and {last.StatusIndex} on the status index.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(700));
            var now = await ScansAsync(postgres);
            still = now == last ? still + 1 : 0;
            last = now;
        }

        return last;
    }

    private static async Task<(long All, long HeapOnly)> UpdatesAsync(string postgres)
    {
        await using var connection = new NpgsqlConnection(postgres);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT n_tup_upd, n_tup_hot_upd FROM pg_stat_user_tables
            WHERE schemaname = 'automation' AND relname = 'workflow_executions_state'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
