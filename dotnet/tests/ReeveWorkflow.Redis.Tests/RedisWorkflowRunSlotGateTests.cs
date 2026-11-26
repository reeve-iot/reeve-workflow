using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis.Tests;

public class RedisWorkflowRunSlotGateTests
{
    private static readonly Guid Workflow = Guid.Parse("6f1a2b3c-4d5e-6f70-8192-a3b4c5d6e7f8");

    private static (RedisWorkflowRunSlotGate Gate, IDatabase Db) Build(
        RedisResult? acquireResult = null,
        Exception? throws = null,
        RedisWorkflowOptions? options = null)
    {
        var db = Substitute.For<IDatabase>();
        var call = db.ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
        if (throws is not null)
            call.Returns<Task<RedisResult>>(_ => throw throws);
        else
            call.Returns(Task.FromResult(acquireResult ?? RedisResult.Create((RedisValue)1)));

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        return (new RedisWorkflowRunSlotGate(
            multiplexer, NullLogger<RedisWorkflowRunSlotGate>.Instance, options), db);
    }

    [Fact]
    public async Task The_slot_key_is_the_prefix_and_the_workflow_id()
    {
        var (gate, db) = Build();

        await gate.TryAcquireAsync(Workflow, "token", 4, CancellationToken.None);

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(k => k.Single() == (RedisKey)"wfslots:6f1a2b3c4d5e6f708192a3b4c5d6e7f8"),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_host_that_already_has_slots_under_another_prefix_can_keep_it()
    {
        var (gate, db) = Build(options: new RedisWorkflowOptions { RunSlotKeyPrefix = "legacy:slots:" });

        await gate.TryAcquireAsync(Workflow, "token", 4, CancellationToken.None);

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(k => k.Single() == (RedisKey)"legacy:slots:6f1a2b3c4d5e6f708192a3b4c5d6e7f8"),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task The_budget_the_TTL_and_the_token_reach_the_script_in_that_order()
    {
        var (gate, db) = Build(options: new RedisWorkflowOptions { RunSlotTtl = TimeSpan.FromSeconds(90) });

        await gate.TryAcquireAsync(Workflow, "slot-7", 3, CancellationToken.None);

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Is<RedisValue[]>(v =>
                v.Length == 3 && v[0] == (RedisValue)3 && v[1] == (RedisValue)90_000L && v[2] == (RedisValue)"slot-7"),
            Arg.Any<CommandFlags>());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task The_scripts_answer_is_the_admission(long scriptResult, bool admitted)
    {
        var (gate, _) = Build(RedisResult.Create((RedisValue)scriptResult));

        (await gate.TryAcquireAsync(Workflow, "token", 1, CancellationToken.None)).Should().Be(admitted);
    }

    [Fact]
    public async Task A_Redis_failure_admits_rather_than_wedging_execution()
    {
        var (gate, _) = Build(throws: new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        (await gate.TryAcquireAsync(Workflow, "token", 1, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task A_release_that_fails_is_left_to_the_TTL()
    {
        var db = Substitute.For<IDatabase>();
        db.SortedSetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        var gate = new RedisWorkflowRunSlotGate(multiplexer, NullLogger<RedisWorkflowRunSlotGate>.Instance);

        var release = async () => await gate.ReleaseAsync(Workflow, "token");

        await release.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_release_removes_the_token_from_the_workflows_set()
    {
        var (gate, db) = Build();

        await gate.ReleaseAsync(Workflow, "slot-7");

        await db.Received(1).SortedSetRemoveAsync(
            (RedisKey)"wfslots:6f1a2b3c4d5e6f708192a3b4c5d6e7f8", (RedisValue)"slot-7", Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Without_Redis_the_gate_admits_and_releases_nothing()
    {
        var gate = new RedisWorkflowRunSlotGate(null, NullLogger<RedisWorkflowRunSlotGate>.Instance);

        (await gate.TryAcquireAsync(Workflow, "token", 1, CancellationToken.None)).Should().BeTrue();
        await gate.ReleaseAsync(Workflow, "token");
    }

    [Fact]
    public async Task A_renewal_asks_about_the_same_key_with_the_TTL_and_the_token_and_no_budget()
    {
        var (gate, db) = Build(options: new RedisWorkflowOptions { RunSlotTtl = TimeSpan.FromSeconds(90) });

        await gate.RenewAsync(Workflow, "slot-7", CancellationToken.None);

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(k => k.Single() == (RedisKey)"wfslots:6f1a2b3c4d5e6f708192a3b4c5d6e7f8"),
            Arg.Is<RedisValue[]>(v => v.Length == 2 && v[0] == (RedisValue)90_000L && v[1] == (RedisValue)"slot-7"),
            Arg.Any<CommandFlags>());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task The_renewal_scripts_answer_is_whether_the_slot_is_still_held(long scriptResult, bool held)
    {
        var (gate, _) = Build(RedisResult.Create((RedisValue)scriptResult));

        (await gate.RenewAsync(Workflow, "token", CancellationToken.None)).Should().Be(held);
    }

    [Fact]
    public async Task A_Redis_failure_during_a_renewal_keeps_the_run_rather_than_stopping_it()
    {
        var (gate, _) = Build(throws: new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        (await gate.RenewAsync(Workflow, "token", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Without_Redis_a_renewal_holds_what_nothing_could_expire()
    {
        var gate = new RedisWorkflowRunSlotGate(null, NullLogger<RedisWorkflowRunSlotGate>.Instance);

        (await gate.RenewAsync(Workflow, "token", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task A_cancelled_acquire_throws_rather_than_admitting()
    {
        var (gate, _) = Build();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var acquire = async () => await gate.TryAcquireAsync(Workflow, "token", 1, cancelled.Token);

        await acquire.Should().ThrowAsync<OperationCanceledException>();
    }
}
