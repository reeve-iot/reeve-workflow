using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Engine;

public sealed class WorkflowSchemaMigrationService(
    IServiceScopeFactory scopeFactory,
    DispatchHostOptions options,
    ILogger<WorkflowSchemaMigrationService> logger) : IHostedService
{
    public const long SchemaLockKey = 5_245_452_026_090_918;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var gate = new NpgsqlConnection(GateConnectionString(options.PostgresConnectionString));
        await gate.OpenAsync(cancellationToken).ConfigureAwait(false);
        await AcquireAsync(gate, cancellationToken).ConfigureAwait(false);

        try
        {
            if (options.MigrateOnStartup)
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IWorkflowSchemaMigrator>().MigrateAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("The workflow schema is up to date.");
            }
            await EnsureKeyRingTableAsync(gate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync(gate).ConfigureAwait(false);
        }
    }

    private static async Task EnsureKeyRingTableAsync(NpgsqlConnection gate, CancellationToken cancellationToken)
    {
        await using var exists = new NpgsqlCommand($"SELECT to_regclass('{KeyRingTable}') IS NOT NULL", gate);
        if ((bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            return;

        await using var create = new NpgsqlCommand(
            $"CREATE TABLE IF NOT EXISTS {KeyRingTable} (id bigserial PRIMARY KEY, friendly_name text NOT NULL, xml text NOT NULL)", gate);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal const string KeyRingTable = "dispatch_data_protection_keys";

    private async Task AcquireAsync(NpgsqlConnection gate, CancellationToken cancellationToken)
    {
        await using var ask = new NpgsqlCommand($"SELECT pg_try_advisory_lock({SchemaLockKey})", gate);
        while (!(bool)(await ask.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
        {
            logger.LogDebug("Another replica is migrating the workflow schema; waiting for it.");
            await Task.Delay(LockPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync(NpgsqlConnection gate)
    {
        try
        {
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({SchemaLockKey})", gate);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not release the workflow schema lock; it goes with the session.");
        }
    }

    public const string GateApplicationName = "dispatch-schema-lock";

    private static string GateConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, ApplicationName = GateApplicationName }
            .ConnectionString;

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(250);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
