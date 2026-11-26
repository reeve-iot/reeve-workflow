using Dispatch.Host.Engine;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class MigrationRaceTests(ClusterBackends backends)
{
    [Fact]
    public async Task Two_replicas_migrating_one_empty_database_at_once_both_finish()
    {
        var connectionString = await backends.CreateDatabaseAsync("dispatch_migration_race");

        var outcomes = await RaceAsync(connectionString);

        outcomes.Should().AllSatisfy(failure => failure.Should().BeNull(
            "Entity Framework's own lock is let go between migrations while each replica works from the list it "
            + "read at the start, so the loser applies a migration the winner has already applied"));
    }

    [Fact]
    public async Task A_replica_waiting_for_a_peer_s_migration_holds_no_statement_open()
    {
        var connectionString = await backends.CreateDatabaseAsync("dispatch_migration_wait");
        await using (var seed = Replica(connectionString))
            (await MigrateAsync(seed)).Should().BeNull();

        var shortTimeout = new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = 2 }.ConnectionString;
        await using var peer = new NpgsqlConnection(connectionString);
        await peer.OpenAsync();
        await using (var take = new NpgsqlCommand(
            $"SELECT pg_advisory_lock({WorkflowSchemaMigrationService.SchemaLockKey})", peer))
            await take.ExecuteNonQueryAsync();

        await using var replica = Replica(shortTimeout);
        var migrating = Task.Run(() => MigrateAsync(replica));
        await Task.Delay(TimeSpan.FromSeconds(1));

        await using (var look = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() "
            + "AND wait_event_type = 'Lock' AND wait_event = 'advisory'", peer))
            ((long)(await look.ExecuteScalarAsync())!).Should().Be(0,
                "a replica blocked in pg_advisory_lock is a transaction, and a concurrent index build waits for it");

        await Task.Delay(TimeSpan.FromSeconds(3));
        migrating.IsCompleted.Should().BeFalse("the peer still holds the schema");
        await using (var give = new NpgsqlCommand(
            $"SELECT pg_advisory_unlock({WorkflowSchemaMigrationService.SchemaLockKey})", peer))
            await give.ExecuteNonQueryAsync();

        (await migrating.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeNull(
            "waiting longer than one command may take is a peer migrating, not a failure");
    }

    [Fact]
    public async Task The_lock_s_session_is_closed_once_the_schema_is_up_to_date()
    {
        var connectionString = await backends.CreateDatabaseAsync("dispatch_migration_gate");

        await using (var replica = Replica(connectionString))
            (await MigrateAsync(replica)).Should().BeNull();

        await using var look = new NpgsqlConnection(connectionString);
        await look.OpenAsync();
        await using var sessions = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = @name", look);
        sessions.Parameters.AddWithValue("name", WorkflowSchemaMigrationService.GateApplicationName);

        var count = await CountAsync(sessions);
        for (var deadline = DateTime.UtcNow.AddSeconds(10); count != 0 && DateTime.UtcNow < deadline;)
        {
            await Task.Delay(25);
            count = await CountAsync(sessions);
        }

        count.Should().Be(0,
            "a pooled session outlives its disposal: it holds a slot of the server's limit and keeps a lock whose release failed");
    }

    private static async Task<long> CountAsync(NpgsqlCommand sessions) => (long)(await sessions.ExecuteScalarAsync())!;

    private static async Task<Exception?[]> RaceAsync(string connectionString)
    {
        await using var first = Replica(connectionString);
        await using var second = Replica(connectionString);

        return await Task.WhenAll(
            Task.Run(() => MigrateAsync(first)),
            Task.Run(() => MigrateAsync(second)));
    }

    private static async Task<Exception?> MigrateAsync(ServiceProvider replica)
    {
        var service = new WorkflowSchemaMigrationService(
            replica.GetRequiredService<IServiceScopeFactory>(),
            replica.GetRequiredService<DispatchHostOptions>(),
            NullLogger<WorkflowSchemaMigrationService>.Instance);
        try
        {
            await service.StartAsync(CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static ServiceProvider Replica(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new DispatchHostOptions { PostgresConnectionString = connectionString });
        services.AddWorkflows(workflows => workflows.ConfigureDbContext(options => options.UseNpgsql(connectionString)))
            .AddPostgreSqlSchema();
        return services.BuildServiceProvider();
    }
}
