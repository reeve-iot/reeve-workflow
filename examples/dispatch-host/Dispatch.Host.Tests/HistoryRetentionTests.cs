using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.History;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class HistoryRetentionTests(ClusterBackends backends)
{
    [Fact]
    public async Task A_sweep_deletes_a_backlog_past_fifty_thousand_runs_when_its_time_allows()
    {
        var postgres = await SchemaAsync("dispatch_retention_backlog");
        await ExpiredRunsAsync(postgres, 60_000);
        await using var container = FreightDesk.Container(postgres);

        await Sweeper(container, new WorkflowExecutionHistoryRetentionOptions(), TimeProvider.System)
            .SweepUnderLeaderAsync(default);

        (await ExpiredLeftAsync(postgres)).Should().Be(0);
    }

    [Fact]
    public async Task A_sweep_stops_deleting_once_its_time_is_spent()
    {
        var postgres = await SchemaAsync("dispatch_retention_budget");
        await ExpiredRunsAsync(postgres, 1_000);
        await using var container = FreightDesk.Container(postgres);
        var clock = new FastStopwatch(TimeSpan.FromMilliseconds(400));

        await Sweeper(container, new WorkflowExecutionHistoryRetentionOptions { MaxSweepSeconds = 1, DeleteBatchSize = 50 }, clock)
            .SweepUnderLeaderAsync(default);

        (await ExpiredLeftAsync(postgres)).Should().BeInRange(1_000 - 4 * 50, 1_000 - 50,
            "each batch reads the stopwatch as 400 ms on, so a one-second budget lets two or three through");
    }

    [Fact]
    public async Task A_sweep_that_had_to_stop_comes_back_after_a_pause_rather_than_the_interval()
    {
        var postgres = await SchemaAsync("dispatch_retention_pause");
        await ExpiredRunsAsync(postgres, 1_000);
        await using var container = FreightDesk.Container(postgres);
        var sweeper = Sweeper(container, new WorkflowExecutionHistoryRetentionOptions
        {
            MaxSweepSeconds = 1,
            DeleteBatchSize = 100,
            SweepIntervalHours = 168
        }, new FastStopwatch(TimeSpan.FromMilliseconds(600)));

        await sweeper.StartAsync(default);
        try
        {
            _ = await FreightDesk.EventuallyAsync(
                () => ExpiredLeftAsync(postgres),
                left => left == 0,
                TimeSpan.FromSeconds(60),
                "the sweep to come back every second until the backlog is gone");
        }
        finally
        {
            await sweeper.StopAsync(default);
        }
    }

    [Fact]
    public async Task A_sweep_stopped_by_its_row_ceiling_comes_back_after_a_pause_too()
    {
        var postgres = await SchemaAsync("dispatch_retention_ceiling");
        await ExpiredRunsAsync(postgres, 1_000);
        await using var container = FreightDesk.Container(postgres);
        var sweeper = Sweeper(container, new WorkflowExecutionHistoryRetentionOptions
        {
            MaxSweepSeconds = 1,
            DeleteBatchSize = 50,
            MaxDeletesPerSweep = 100,
            SweepIntervalHours = 168
        }, TimeProvider.System);

        await sweeper.StartAsync(default);
        try
        {
            _ = await FreightDesk.EventuallyAsync(
                () => ExpiredLeftAsync(postgres),
                left => left == 0,
                TimeSpan.FromSeconds(60),
                "a hundred runs a sweep, a second apart, until the backlog is gone");
        }
        finally
        {
            await sweeper.StopAsync(default);
        }
    }

    private async Task<string> SchemaAsync(string database)
    {
        var postgres = await backends.CreateDatabaseAsync(database);
        await using var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
        {
            ReplicaId = "desk-retention",
            PostgresConnectionString = postgres,
            RedisConnectionString = backends.RedisConnectionString
        });
        await FreightDesk.WarmAsync(postgres, desk);
        return postgres;
    }

    private static async Task ExpiredRunsAsync(string postgres, int count)
    {
        var workflowId = await FreightDesk.SeedAssignCarrierChainAsync(postgres, "SHP-7400", 1);
        await FreightDesk.ExecuteSqlAsync(postgres, $"""
            INSERT INTO automation.workflow_execution_logs
                (id, attempt_number, workflow_id, workflow_version_id, status, started_at_utc, completed_at_utc)
            SELECT gen_random_uuid(), 1, w.id, w.current_version_id, 1,
                   now() - interval '200 days' - g * interval '1 second', now() - interval '200 days' - g * interval '1 second'
            FROM automation.workflows w CROSS JOIN generate_series(1, {count}) g
            WHERE w.id = '{workflowId}';
            ANALYZE automation.workflow_execution_logs;
            """);
    }

    private static async Task<long> ExpiredLeftAsync(string postgres)
    {
        await using var connection = new NpgsqlConnection(postgres);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM automation.workflow_execution_logs WHERE completed_at_utc < now() - interval '100 days'",
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static WorkflowExecutionHistoryRetentionService Sweeper(
        ServiceProvider container, WorkflowExecutionHistoryRetentionOptions history, TimeProvider clock) =>
        new(
            container.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkflowRetentionOptions { ExecutionHistory = history }),
            clock,
            new SingleReplicaLeaderGate(),
            NullLogger<WorkflowExecutionHistoryRetentionService>.Instance);

    private sealed class FastStopwatch(TimeSpan step) : TimeProvider
    {
        private long reads;

        public override long GetTimestamp() =>
            base.GetTimestamp() + Interlocked.Increment(ref reads) * (long)(step.TotalSeconds * TimestampFrequency);
    }
}
