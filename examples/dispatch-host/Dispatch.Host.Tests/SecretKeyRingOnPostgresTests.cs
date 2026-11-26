using FluentAssertions;
using Dispatch.Host.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using ReeveWorkflow.Core.Hosting;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class SecretKeyRingOnPostgresTests(ClusterBackends backends)
{
    [Fact]
    public async Task A_header_one_desk_protected_is_revealed_by_a_desk_built_after_it()
    {
        var connection = await backends.CreateDatabaseAsync("dispatch_key_ring");
        var site = new WorkflowSecretSite(Guid.NewGuid(), "", "http", "Headers", "Authorization");

        string stored;
        using (var first = Desk(connection))
        {
            await StartMigrationServiceAsync(first);
            stored = await first.Services.GetRequiredService<IWorkflowSecretCodec>().ProtectAsync(site, "Bearer s3cr3t", default);
        }

        using var second = Desk(connection);

        (await second.Services.GetRequiredService<IWorkflowSecretCodec>().RevealAsync(site, stored, default)).Should().Be("Bearer s3cr3t");
    }

    [Fact]
    public async Task A_desk_that_does_not_migrate_still_gets_its_key_ring_table_and_leaves_the_workflow_schema_alone()
    {
        var connection = await backends.CreateDatabaseAsync("dispatch_key_ring_no_migrate");
        using var desk = Desk(connection);

        await StartMigrationServiceAsync(desk);

        (await RegclassAsync(connection, "dispatch_data_protection_keys")).Should().BeTrue();
        (await RegclassAsync(connection, "automation.workflows")).Should().BeFalse("MigrateOnStartup is off");
    }

    private static Task StartMigrationServiceAsync(WebApplication desk) =>
        desk.Services.GetServices<IHostedService>().OfType<WorkflowSchemaMigrationService>().Single().StartAsync(default);

    private static async Task<bool> RegclassAsync(string connection, string name)
    {
        await using var open = new NpgsqlConnection(connection);
        await open.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", open);
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static WebApplication Desk(string connection) => DispatchHost.Build(
        new DispatchHostOptions { PostgresConnectionString = connection, MigrateOnStartup = false });
}
