using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReeveWorkflow.Npgsql;

namespace ReeveWorkflow.Npgsql.Tests;

public class NpgsqlWorkflowRunLockProviderTests
{
    private const string Unreachable = "Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d;Timeout=2";

    [Fact]
    public async Task A_database_it_cannot_reach_is_reported_as_a_fault_and_never_as_a_peer_holding_the_key()
    {
        await using var provider = Build();

        var acquiring = async () => await provider.TryAcquireAsync("WorkflowRunBudget_x");

        await acquiring.Should().ThrowAsync<NpgsqlException>();
    }

    [Fact]
    public async Task The_prefix_reaches_the_derivation_so_two_deployments_on_one_database_can_be_told_apart()
    {
        var derived = new List<string>();
        await using var provider = Build(options =>
        {
            options.KeyPrefix = "staging:";
            options.LockId = key => { derived.Add(key); return 1; };
        });

        await Assert.ThrowsAnyAsync<Exception>(() => provider.TryAcquireAsync("WorkflowVersionRetention_Leader"));

        derived.Should().ContainSingle().Which.Should().Be("staging:WorkflowVersionRetention_Leader");
    }

    [Fact]
    public void A_provider_with_no_connection_string_says_which_call_is_missing_one()
    {
        var building = () => new NpgsqlWorkflowRunLockProvider(
            new NpgsqlWorkflowLockOptions(), NullLogger<NpgsqlWorkflowRunLockProvider>.Instance);

        building.Should().Throw<InvalidOperationException>().WithMessage("*UseNpgsqlLocks*");
    }

    [Fact]
    public async Task Disposing_it_twice_is_not_an_error_a_host_shutdown_has_to_avoid()
    {
        var provider = Build();

        await provider.DisposeAsync();
        var disposingAgain = async () => await provider.DisposeAsync();

        await disposingAgain.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_disposed_provider_refuses_rather_than_reporting_the_key_taken()
    {
        var provider = Build();
        await provider.DisposeAsync();

        var acquiring = async () => await provider.TryAcquireAsync("WorkflowRunBudget_x");

        await acquiring.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task A_host_string_that_asks_for_multiplexing_still_reaches_the_server()
    {
        var multiplexed = Unreachable + ";Multiplexing=true";
        if (!new NpgsqlConnectionStringBuilder().ContainsKey("Multiplexing"))
        {
            var building = () => Build(options => options.ConnectionString = multiplexed);
            building.Should().Throw<ArgumentException>();
            return;
        }

        await using var provider = Build(options => options.ConnectionString = multiplexed);

        var acquiring = async () => await provider.TryAcquireAsync("WorkflowRunBudget_x");

        await acquiring.Should().ThrowAsync<NpgsqlException>();
    }

    [Fact]
    public void The_session_is_unpooled_even_when_the_host_string_pools()
    {
        var session = NpgsqlWorkflowRunLockProvider.SessionConnectionString(Unreachable + ";Pooling=true");

        new NpgsqlConnectionStringBuilder(session).Pooling.Should().BeFalse();
    }

    [Theory]
    [InlineData("", 30)]
    [InlineData(";Keepalive=0", 30)]
    [InlineData(";Keepalive=7", 7)]
    public void The_session_probes_an_idle_connection_unless_the_host_chose_how_often(string configured, int expected)
    {
        var session = NpgsqlWorkflowRunLockProvider.SessionConnectionString(Unreachable + configured);

        new NpgsqlConnectionStringBuilder(session).KeepAlive.Should().Be(expected);
    }

    private static NpgsqlWorkflowRunLockProvider Build(Action<NpgsqlWorkflowLockOptions>? configure = null)
    {
        var options = new NpgsqlWorkflowLockOptions { ConnectionString = Unreachable };
        configure?.Invoke(options);
        return new NpgsqlWorkflowRunLockProvider(options, NullLogger<NpgsqlWorkflowRunLockProvider>.Instance);
    }
}
