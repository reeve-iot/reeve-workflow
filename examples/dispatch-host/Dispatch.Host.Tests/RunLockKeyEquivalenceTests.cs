using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReeveWorkflow.Npgsql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public class RunLockKeyEquivalenceTests(ClusterBackends backends)
{
    public static TheoryData<string, string> Keys => new()
    {
        { "", "WorkflowRunBudget_8d6c2f6e0b7c4f0e9d1a3b5c7e9f1a2b" },
        { "", "WorkflowVersionRetention_Leader" },
        { "staging:", "WorkflowVersionRetention_Leader" },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task A_holder_on_the_old_statement_refuses_the_provider_until_it_lets_go(string prefix, string key)
    {
        var connectionString = await backends.CreateDatabaseAsync($"lock_keys_old_holds_{Guid.NewGuid():N}");
        var oldKey = OldKey(prefix, key);
        await using var holder = await OpenAsync(connectionString);
        (await OldTryAsync(holder, oldKey)).Should().BeTrue();
        await using var provider = Build(connectionString, prefix);

        var refused = await provider.TryAcquireAsync(key);
        (await OldUnlockAsync(holder, oldKey)).Should().BeTrue();
        await using var granted = await provider.TryAcquireAsync(key);

        refused.Should().BeNull("the provider's key is the one the old statement holds");
        granted.Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task A_lease_refuses_the_old_statement_until_it_is_given_back(string prefix, string key)
    {
        var connectionString = await backends.CreateDatabaseAsync($"lock_keys_lease_holds_{Guid.NewGuid():N}");
        var oldKey = OldKey(prefix, key);
        await using var peer = await OpenAsync(connectionString);
        await using var provider = Build(connectionString, prefix);

        var lease = await provider.TryAcquireAsync(key);
        var whileHeld = await OldTryAsync(peer, oldKey);
        await lease!.DisposeAsync();
        var afterwards = await OldTryAsync(peer, oldKey);

        whileHeld.Should().BeFalse("the old statement asks for the key the lease holds");
        afterwards.Should().BeTrue();
    }

    [Fact]
    public async Task A_custom_lock_id_reaches_the_server_as_it_is_whatever_the_prefix()
    {
        const long chosen = -4_611_686_018_427_387_905L;
        var connectionString = await backends.CreateDatabaseAsync($"lock_keys_custom_{Guid.NewGuid():N}");
        await using var peer = await OpenAsync(connectionString);
        await using var provider = new NpgsqlWorkflowRunLockProvider(
            new NpgsqlWorkflowLockOptions { ConnectionString = connectionString, KeyPrefix = "ignored:", LockId = _ => chosen },
            NullLogger<NpgsqlWorkflowRunLockProvider>.Instance);

        await using var lease = await provider.TryAcquireAsync("anything");

        lease.Should().NotBeNull();
        (await OldTryAsync(peer, chosen)).Should().BeFalse();
    }

    private static long OldKey(string prefix, string key) =>
        BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + key)));

    private static NpgsqlWorkflowRunLockProvider Build(string connectionString, string prefix) =>
        new(new NpgsqlWorkflowLockOptions { ConnectionString = connectionString, KeyPrefix = prefix },
            NullLogger<NpgsqlWorkflowRunLockProvider>.Instance);

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> OldTryAsync(NpgsqlConnection connection, long key)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> OldUnlockAsync(NpgsqlConnection connection, long key)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        command.Parameters.AddWithValue("key", key);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
