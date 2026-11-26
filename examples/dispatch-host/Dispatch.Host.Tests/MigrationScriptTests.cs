using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class MigrationScriptTests(ClusterBackends backends)
{
    [Fact]
    public async Task The_plain_script_builds_the_schema_that_migrating_builds()
    {
        var expected = await MigratedSchemaAsync("dispatch_script_plain_reference");
        var connectionString = await backends.CreateDatabaseAsync("dispatch_script_plain");

        await ExecuteAsync(connectionString, Script(MigrationsSqlGenerationOptions.Default));

        (await SchemaAsync(connectionString)).Should().Be(expected);
        (await AppliedAsync(connectionString)).Should().Equal(AllMigrations());
    }

    [Fact]
    public async Task The_idempotent_script_builds_the_schema_and_a_second_run_changes_nothing()
    {
        var expected = await MigratedSchemaAsync("dispatch_script_idempotent_reference");
        var connectionString = await backends.CreateDatabaseAsync("dispatch_script_idempotent");
        var script = Script(MigrationsSqlGenerationOptions.Idempotent);

        await ExecuteAsync(connectionString, script);
        await ExecuteAsync(connectionString, script);

        (await SchemaAsync(connectionString)).Should().Be(expected);
        (await AppliedAsync(connectionString)).Should().Equal(AllMigrations());
    }

    private async Task<string> MigratedSchemaAsync(string databaseName)
    {
        var connectionString = await backends.CreateDatabaseAsync(databaseName);
        await using var context = new WorkflowDbContext(new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(connectionString).UseWorkflowPostgreSqlSchema().Options);
        await context.Database.MigrateAsync();
        return await SchemaAsync(connectionString);
    }

    private static string Script(MigrationsSqlGenerationOptions options)
    {
        using var context = new WorkflowDesignTimeDbContextFactory().CreateDbContext([]);
        return context.GetService<IMigrator>().GenerateScript(null, null, options);
    }

    private static List<string> AllMigrations()
    {
        using var context = new WorkflowDesignTimeDbContextFactory().CreateDbContext([]);
        return context.Database.GetMigrations().Order(StringComparer.Ordinal).ToList();
    }

    private static async Task ExecuteAsync(string connectionString, string script)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(script, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> SchemaAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT string_agg(line, E'\\n' ORDER BY line) FROM ("
            + "SELECT format('column %s.%s %s %s %s', table_name, column_name, data_type, is_nullable, coalesce(column_default, '')) AS line "
            + "FROM information_schema.columns WHERE table_schema = 'automation' "
            + "UNION ALL SELECT format('index %s', indexdef) FROM pg_indexes WHERE schemaname = 'automation' "
            + "UNION ALL SELECT format('constraint %s %s %s', conrelid::regclass, conname, pg_get_constraintdef(oid)) "
            + "FROM pg_constraint WHERE connamespace = 'automation'::regnamespace "
            + "UNION ALL SELECT format('storage %s %s', relname, reloptions) FROM pg_class "
            + "WHERE relnamespace = 'automation'::regnamespace AND reloptions IS NOT NULL "
            + "UNION ALL SELECT format('hypertable %s.%s', hypertable_schema, hypertable_name) "
            + "FROM timescaledb_information.hypertables WHERE hypertable_schema = 'automation') AS schema", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> AppliedAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"MigrationId\" FROM automation.\"__WorkflowMigrationsHistory\" ORDER BY \"MigrationId\"", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var applied = new List<string>();
        while (await reader.ReadAsync())
            applied.Add(reader.GetString(0));
        return applied;
    }
}
