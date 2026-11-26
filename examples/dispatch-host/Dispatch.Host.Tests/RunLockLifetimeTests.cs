using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReeveWorkflow.Npgsql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public class RunLockLifetimeTests(ClusterBackends backends)
{
    private const string Key = "WorkflowRunBudget_lifetime";

    [Fact]
    public async Task A_key_this_replica_already_holds_is_refused_rather_than_granted_a_second_time()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_reentrant");
        await using var provider = Build(connectionString);

        await using var first = await provider.TryAcquireAsync(Key);
        var second = await provider.TryAcquireAsync(Key);

        first.Should().NotBeNull();
        second.Should().BeNull();
    }

    [Fact]
    public async Task A_peer_replica_takes_the_key_only_once_it_is_given_back()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_handover");
        await using var provider = Build(connectionString);
        await using var peer = Build(connectionString);

        var held = await provider.TryAcquireAsync(Key);
        var refused = await peer.TryAcquireAsync(Key);
        await held!.DisposeAsync();
        await using var granted = await peer.TryAcquireAsync(Key);

        refused.Should().BeNull();
        granted.Should().NotBeNull();
    }

    [Fact]
    public async Task A_session_that_dies_takes_its_locks_with_it_and_the_lease_says_so()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_session_death");
        await using var provider = Build(connectionString, "lock-session-death");
        await using var peer = Build(connectionString);

        await using var lease = await provider.TryAcquireAsync(Key);
        (await lease!.IsValidAsync()).Should().BeTrue();

        await KillSessionAsync(connectionString, "lock-session-death");

        (await lease.IsValidAsync()).Should().BeFalse();
        (await peer.TryAcquireAsync(Key)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_lease_from_a_dead_session_does_not_release_the_key_the_next_one_took()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_generation");
        await using var provider = Build(connectionString, "lock-generation");
        await using var peer = Build(connectionString);

        var stale = await provider.TryAcquireAsync(Key);
        await KillSessionAsync(connectionString, "lock-generation");
        await using var current = await provider.TryAcquireAsync(Key);

        await stale!.DisposeAsync();

        current.Should().NotBeNull();
        (await peer.TryAcquireAsync(Key)).Should().BeNull("the reopened session still holds it");
    }

    [Fact]
    public async Task The_session_it_locks_on_is_its_own_and_closes_with_it()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_own_session");
        var provider = Build(connectionString, "lock-own-session");
        var lease = await provider.TryAcquireAsync(Key);
        (await SessionCountAsync(connectionString, "lock-own-session")).Should().Be(1);

        await provider.DisposeAsync();

        await TakeOnceGivenBackAsync(connectionString, Key);
        await lease!.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_the_provider_with_a_lease_still_out_gives_the_key_back_and_the_lease_still_closes()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_shutdown");
        var provider = Build(connectionString);
        var lease = await provider.TryAcquireAsync(Key);

        await provider.DisposeAsync();
        var closing = async () => await lease!.DisposeAsync();

        await closing.Should().NotThrowAsync();
        await TakeOnceGivenBackAsync(connectionString, Key);
    }

    [Fact]
    public async Task Two_deployments_on_one_database_stop_excluding_each_other_once_they_are_prefixed()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_prefix");
        await using var production = Build(connectionString, prefix: "prod:");
        await using var staging = Build(connectionString, prefix: "staging:");

        await using var held = await production.TryAcquireAsync("WorkflowVersionRetention_Leader");
        await using var alsoHeld = await staging.TryAcquireAsync("WorkflowVersionRetention_Leader");

        held.Should().NotBeNull();
        alsoHeld.Should().NotBeNull();
    }

    [Fact]
    public async Task One_unlock_that_fails_on_a_session_still_answering_is_retried_and_the_other_leases_keep_their_locks()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_unlock_retry");
        await InjectUnlockFaultsAsync(connectionString, faults: 1);
        await using var provider = Build(connectionString, shadowUnlock: true);
        await using var peer = Build(connectionString);

        var released = await provider.TryAcquireAsync(Key);
        await using var kept = await provider.TryAcquireAsync(Key + "_other");
        await released!.DisposeAsync();

        (await kept!.IsValidAsync()).Should().BeTrue("the session that holds it was never dropped");
        (await peer.TryAcquireAsync(Key + "_other")).Should().BeNull();
        (await peer.TryAcquireAsync(Key)).Should().NotBeNull("the retry gave back the lock the first attempt could not");
    }

    [Fact]
    public async Task An_unlock_that_keeps_failing_still_drops_the_session_so_no_key_is_left_that_nothing_alive_releases()
    {
        var connectionString = await backends.CreateDatabaseAsync("locks_unlock_gives_up");
        await InjectUnlockFaultsAsync(connectionString, faults: int.MaxValue);
        await using var provider = Build(connectionString, shadowUnlock: true);
        await using var peer = Build(connectionString);

        var released = await provider.TryAcquireAsync(Key);
        var lostWithIt = await provider.TryAcquireAsync(Key + "_other");
        await released!.DisposeAsync();

        (await lostWithIt!.IsValidAsync()).Should().BeFalse("a dropped session held nothing, and its leases say so");
        (await peer.TryAcquireAsync(Key)).Should().NotBeNull();
    }

    private static NpgsqlWorkflowRunLockProvider Build(
        string connectionString, string? applicationName = null, string prefix = "", bool shadowUnlock = false)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (applicationName is not null)
            builder.ApplicationName = applicationName;
        if (shadowUnlock)
            builder.SearchPath = "public,pg_catalog";
        var configured = builder.ConnectionString;

        return new NpgsqlWorkflowRunLockProvider(
            new NpgsqlWorkflowLockOptions { ConnectionString = configured, KeyPrefix = prefix },
            NullLogger<NpgsqlWorkflowRunLockProvider>.Instance);
    }

    private static async Task InjectUnlockFaultsAsync(string connectionString, int faults)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "CREATE SEQUENCE public.unlock_calls; "
            + "CREATE FUNCTION public.pg_advisory_unlock(key bigint) RETURNS boolean LANGUAGE plpgsql AS $$ "
            + $"BEGIN IF nextval('public.unlock_calls') <= {faults} THEN RAISE EXCEPTION 'injected unlock fault'; END IF; "
            + "RETURN pg_catalog.pg_advisory_unlock(key); END $$;",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task KillSessionAsync(string connectionString, string applicationName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT bool_and(pg_terminate_backend(pid, 20000)) FROM pg_stat_activity WHERE application_name = @name",
            connection);
        command.Parameters.AddWithValue("name", applicationName);
        (await command.ExecuteScalarAsync()).Should().Be(true, "the session was there to kill, and it exited");
    }

    private static async Task TakeOnceGivenBackAsync(string connectionString, string key)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var bound = new NpgsqlCommand("SET lock_timeout = '20s'", connection))
            await bound.ExecuteNonQueryAsync();
        await using var take = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection);
        take.Parameters.AddWithValue("key", NpgsqlWorkflowLockOptions.DeriveLockId(key));
        await take.ExecuteNonQueryAsync();
        await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
        release.Parameters.AddWithValue("key", NpgsqlWorkflowLockOptions.DeriveLockId(key));
        await release.ExecuteNonQueryAsync();
    }

    private static async Task<long> SessionCountAsync(string connectionString, string applicationName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name", connection);
        command.Parameters.AddWithValue("name", applicationName);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
