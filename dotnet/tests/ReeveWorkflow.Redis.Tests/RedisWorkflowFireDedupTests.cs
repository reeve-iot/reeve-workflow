using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis.Tests;

public class RedisWorkflowFireDedupTests
{
    private static (RedisWorkflowFireDedup Dedup, IDatabase Db) Build(
        bool claimed = true, Exception? throws = null, RedisFireDedupOptions? options = null)
    {
        var db = Substitute.For<IDatabase>();
        var call = db.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
            Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
        if (throws is not null)
            call.Returns<Task<bool>>(_ => throw throws);
        else
            call.Returns(Task.FromResult(claimed));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        return (new RedisWorkflowFireDedup(
            multiplexer, NullLogger<RedisWorkflowFireDedup>.Instance, options), db);
    }

    [Fact]
    public async Task The_claim_key_is_the_prefix_and_the_callers_fire_key()
    {
        var (dedup, db) = Build();

        await dedup.TryClaimFireAsync("state:sensor-4|obs:2026-09-08T10:00:00Z", TimeSpan.FromSeconds(5), CancellationToken.None);

        await db.Received(1).StringSetAsync(
            (RedisKey)"reeveworkflow:fire-dedup:state:sensor-4|obs:2026-09-08T10:00:00Z",
            Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_host_that_already_has_claims_under_another_prefix_can_keep_it()
    {
        var (dedup, db) = Build(options: new RedisFireDedupOptions { KeyPrefix = "dispatch:fire-dedup:" });

        await dedup.TryClaimFireAsync("event:9", TimeSpan.FromSeconds(5), CancellationToken.None);

        await db.Received(1).StringSetAsync(
            (RedisKey)"dispatch:fire-dedup:event:9",
            Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task The_claim_and_its_expiry_are_one_call_so_no_claim_can_outlive_its_window()
    {
        var (dedup, db) = Build();

        await dedup.TryClaimFireAsync("event:9", TimeSpan.FromSeconds(30), CancellationToken.None);

        await db.Received(1).StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(),
            TimeSpan.FromSeconds(30), Arg.Any<bool>(), When.NotExists, Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_key_a_sibling_replica_already_holds_is_refused()
    {
        var (dedup, _) = Build(claimed: false);

        var claimed = await dedup.TryClaimFireAsync("event:9", TimeSpan.FromSeconds(5), CancellationToken.None);

        claimed.Should().BeFalse();
    }

    [Fact]
    public async Task A_redis_fault_admits_the_fire_rather_than_losing_it()
    {
        var (dedup, _) = Build(throws: new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));

        var claimed = await dedup.TryClaimFireAsync("event:9", TimeSpan.FromSeconds(5), CancellationToken.None);

        claimed.Should().BeTrue();
    }

    [Fact]
    public async Task A_caller_that_could_not_derive_a_key_still_gets_its_fire()
    {
        var (dedup, db) = Build();

        var claimed = await dedup.TryClaimFireAsync(string.Empty, TimeSpan.FromSeconds(5), CancellationToken.None);

        claimed.Should().BeTrue();
        await db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_cancelled_claim_is_a_cancellation_and_not_a_degraded_admission()
    {
        var (dedup, _) = Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var claiming = async () => await dedup.TryClaimFireAsync("event:9", TimeSpan.FromSeconds(5), cancelled.Token);

        await claiming.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_fingerprint_of_a_key_carries_nothing_of_the_key()
    {
        var (dedup, _) = Build();
        const string key = "geo:sha:user-7|52.2297|21.0122";

        var fingerprint = dedup.Fingerprint(key);

        fingerprint.Should().NotContain("user-7").And.NotContain("52.2297");
        fingerprint.Should().Be(dedup.Fingerprint(key));
        fingerprint.Should().NotBe(dedup.Fingerprint(key + "0"));
    }

    [Fact]
    public void A_claim_cannot_be_built_around_a_connection_that_is_not_there()
    {
        var construct = () => new RedisWorkflowFireDedup(
            null!, NullLogger<RedisWorkflowFireDedup>.Instance, null);

        construct.Should().Throw<ArgumentNullException>();
    }
}
