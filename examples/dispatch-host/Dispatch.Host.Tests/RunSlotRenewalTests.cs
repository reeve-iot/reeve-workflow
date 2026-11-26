using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Redis;
using StackExchange.Redis;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class RunSlotRenewalTests(ClusterBackends backends)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_slot_renewed_inside_its_TTL_keeps_its_place_in_the_budget_long_past_it()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(backends.RedisConnectionString);
        var gate = Gate(redis);
        var workflowId = Guid.NewGuid();
        (await gate.TryAcquireAsync(workflowId, "run-a", 1, CancellationToken.None)).Should().BeTrue();

        var until = DateTimeOffset.UtcNow + Ttl * 3;
        while (DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(Ttl / 4);
            (await gate.RenewAsync(workflowId, "run-a", CancellationToken.None)).Should().BeTrue();
        }

        (await gate.TryAcquireAsync(workflowId, "run-b", 1, CancellationToken.None)).Should().BeFalse(
            "the first run is still running and still renewing, so a budget of one has no room for a second");
    }

    [Fact]
    public async Task A_slot_left_past_its_TTL_goes_to_the_next_asker_and_cannot_be_renewed_afterwards()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(backends.RedisConnectionString);
        var gate = Gate(redis);
        var workflowId = Guid.NewGuid();
        (await gate.TryAcquireAsync(workflowId, "run-a", 1, CancellationToken.None)).Should().BeTrue();

        await Task.Delay(Ttl * 2);

        (await gate.TryAcquireAsync(workflowId, "run-b", 1, CancellationToken.None)).Should().BeTrue();
        (await gate.RenewAsync(workflowId, "run-a", CancellationToken.None)).Should().BeFalse(
            "its place was pruned and given away; renewing it would put two runs into a budget of one");
        (await gate.TryAcquireAsync(workflowId, "run-a", 1, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_slot_that_expired_but_nobody_asked_for_is_still_its_runs_to_renew()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(backends.RedisConnectionString);
        var gate = Gate(redis);
        var workflowId = Guid.NewGuid();
        (await gate.TryAcquireAsync(workflowId, "run-a", 1, CancellationToken.None)).Should().BeTrue();

        await Task.Delay(Ttl * 1.4);

        (await gate.RenewAsync(workflowId, "run-a", CancellationToken.None)).Should().BeTrue();
        (await gate.TryAcquireAsync(workflowId, "run-b", 1, CancellationToken.None)).Should().BeFalse(
            "the renewal re-scored the slot to now, so the next prune keeps it");
    }

    [Fact]
    public async Task A_token_that_never_held_a_slot_is_not_given_one_by_renewing()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(backends.RedisConnectionString);
        var gate = Gate(redis);
        var workflowId = Guid.NewGuid();

        (await gate.RenewAsync(workflowId, "run-a", CancellationToken.None)).Should().BeFalse();
        (await gate.TryAcquireAsync(workflowId, "run-b", 1, CancellationToken.None)).Should().BeTrue();
    }

    private static RedisWorkflowRunSlotGate Gate(IConnectionMultiplexer redis) =>
        new(redis, NullLogger<RedisWorkflowRunSlotGate>.Instance, new RedisWorkflowOptions
        {
            RunSlotKeyPrefix = "renewal-test:",
            RunSlotTtl = Ttl
        });
}
