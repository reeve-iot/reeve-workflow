using System.Collections.Concurrent;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Dispatch.Host.Tests;

public sealed class ClusterBackends : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres =
        new PostgreSqlBuilder("timescale/timescaledb:latest-pg18").WithTmpfsMount("/var/lib/postgresql").Build();

    private readonly RedisContainer redis = new RedisBuilder("redis:7-alpine").WithTmpfsMount("/data").Build();

    private static readonly ConcurrentQueue<string> Created = new();

    private static string? adminConnectionString;

    public string RedisConnectionString => redis.GetConnectionString();

    public static void DropCreatedDatabases()
    {
        if (adminConnectionString is null || Created.IsEmpty)
            return;

        NpgsqlConnection.ClearAllPools();
        using var connection = new NpgsqlConnection(adminConnectionString);
        connection.Open();
        while (Created.TryDequeue(out var name))
        {
            using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            drop.ExecuteNonQuery();
        }
    }

    public async Task<string> CreateDatabaseAsync(string name, bool dropAfterTest = true)
    {
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        if (dropAfterTest)
            Created.Enqueue(name);

        return new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        {
            Database = name,
            MaxPoolSize = 20,
            ConnectionIdleLifetime = 30
        }.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await redis.StartAsync();
        adminConnectionString = postgres.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        adminConnectionString = null;
        await redis.DisposeAsync();
        await postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TwoInstanceCollection : ICollectionFixture<ClusterBackends>
{
    public const string Name = "two-instance";
}
