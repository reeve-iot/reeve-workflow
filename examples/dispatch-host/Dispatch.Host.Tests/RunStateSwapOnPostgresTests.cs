using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class RunStateSwapOnPostgresTests(ClusterBackends backends)
{
    [Fact]
    public async Task A_swap_writes_every_column_it_names_and_one_more_version()
    {
        await using var world = await World.StartAsync(backends, "dispatch_swap_writes");
        var run = await world.BeginAsync(owner: "desk-1");
        await world.SqlAsync($"""
            UPDATE automation.workflow_executions_state
            SET wait_until_utc = now(), wait_for_event_key = 'depot:1', last_transition_utc = now() - interval '1 hour'
            WHERE execution_id = '{run}'
            """);
        var before = await world.RowAsync(run);

        var moved = await world.Store.TransitionAsync(new WorkflowRunStateTransition
        {
            ExecutionId = run,
            FromStatus = WorkflowRunStateStatus.Running,
            ToStatus = WorkflowRunStateStatus.Sleeping,
            CurrentBlockPath = "assign-3",
            VariablesJson = """{"shipment":"SHP-7201","attempt":2}""",
            WaitUntilUtc = null,
            WaitForEventKey = null,
            OwnedByReplicaId = "desk-2",
            ExpectedOwnedByReplicaId = "desk-1"
        }, default);

        moved.Should().BeTrue();
        var after = await world.RowAsync(run);
        after.Status.Should().Be("Sleeping");
        after.CurrentBlockPath.Should().Be("assign-3");
        JsonDocument.Parse(after.VariablesJson).RootElement.GetProperty("attempt").GetInt32().Should().Be(2);
        after.VariablesType.Should().Be("object", "the variables are stored as JSON, not as a string of it");
        after.WaitUntilUtc.Should().BeNull();
        after.WaitForEventKey.Should().BeNull();
        after.Owner.Should().Be("desk-2");
        after.Version.Should().Be(before.Version + 1);
        after.LastTransitionUtc.Should().BeAfter(before.LastTransitionUtc);
    }

    [Fact]
    public async Task A_swap_writes_a_wait_it_names()
    {
        await using var world = await World.StartAsync(backends, "dispatch_swap_wait");
        var run = await world.BeginAsync(owner: "desk-1");
        var until = new DateTimeOffset(2031, 5, 4, 3, 2, 1, TimeSpan.Zero);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Running, "desk-1") with
        {
            ToStatus = WorkflowRunStateStatus.Waiting,
            WaitUntilUtc = until,
            WaitForEventKey = "depot:7"
        }, default)).Should().BeTrue();

        var row = await world.RowAsync(run);
        row.WaitUntilUtc.Should().Be(until);
        row.WaitForEventKey.Should().Be("depot:7");
    }

    [Fact]
    public async Task A_swap_from_a_status_the_row_is_not_in_changes_nothing()
    {
        await using var world = await World.StartAsync(backends, "dispatch_swap_status");
        var run = await world.BeginAsync(owner: "desk-1");
        var before = await world.RowAsync(run);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Sleeping, "desk-1"), default))
            .Should().BeFalse();

        (await world.RowAsync(run)).Should().Be(before);
    }

    [Fact]
    public async Task A_swap_expecting_an_owner_the_row_does_not_have_changes_nothing()
    {
        await using var world = await World.StartAsync(backends, "dispatch_swap_owner");
        var run = await world.BeginAsync(owner: "desk-1");
        var unowned = await world.BeginAsync(owner: null);
        var before = await world.RowAsync(run);
        var beforeUnowned = await world.RowAsync(unowned);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Running, "desk-2"), default))
            .Should().BeFalse();
        (await world.Store.TransitionAsync(Swap(unowned, WorkflowRunStateStatus.Running, "desk-2"), default))
            .Should().BeFalse("a row nobody owns is not the row of the owner this caller expected");

        (await world.RowAsync(run)).Should().Be(before);
        (await world.RowAsync(unowned)).Should().Be(beforeUnowned);
    }

    [Fact]
    public async Task A_swap_expecting_no_particular_owner_takes_the_row_whoever_holds_it()
    {
        await using var world = await World.StartAsync(backends, "dispatch_swap_any_owner");
        var run = await world.BeginAsync(owner: "desk-1");

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Running, expectedOwner: null), default))
            .Should().BeTrue();

        (await world.RowAsync(run)).Owner.Should().Be("desk-9");
    }

    [Theory]
    [InlineData("desk-1")]
    [InlineData(null)]
    public async Task A_swap_expecting_the_row_at_another_node_changes_nothing_and_at_its_own_moves_it(string? expectedOwner)
    {
        await using var world = await World.StartAsync(backends, $"dispatch_swap_node_{expectedOwner ?? "any"}".Replace('-', '_'));
        var run = await world.BeginAsync(owner: "desk-1");
        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Running, "desk-1") with
        {
            ToStatus = WorkflowRunStateStatus.Suspended, CurrentBlockPath = "wait-2", OwnedByReplicaId = "desk-1"
        }, default)).Should().BeTrue();
        var parked = await world.RowAsync(run);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Suspended, expectedOwner) with
        {
            CurrentBlockPath = "wait-1", ExpectedCurrentBlockPath = "wait-1"
        }, default)).Should().BeFalse("the row is parked at a later wait than the one this resume took");
        (await world.RowAsync(run)).Should().Be(parked);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Suspended, expectedOwner) with
        {
            CurrentBlockPath = "wait-2", ExpectedCurrentBlockPath = "wait-2"
        }, default)).Should().BeTrue();
        (await world.RowAsync(run)).Owner.Should().Be("desk-9");
    }

    [Theory]
    [InlineData("desk-1")]
    [InlineData(null)]
    public async Task A_swap_expecting_the_token_the_row_was_parked_with_takes_only_that_park(string? expectedOwner)
    {
        await using var world = await World.StartAsync(backends, $"dispatch_swap_token_{expectedOwner ?? "any"}".Replace('-', '_'));
        var run = await world.BeginAsync(owner: "desk-1");
        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Running, "desk-1") with
        {
            ToStatus = WorkflowRunStateStatus.Suspended, CurrentBlockPath = "wait-1", OwnedByReplicaId = "desk-1",
            WaitForEventKey = "parked:second"
        }, default)).Should().BeTrue();
        var parked = await world.RowAsync(run);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Suspended, expectedOwner) with
        {
            CurrentBlockPath = "wait-1", ExpectedCurrentBlockPath = "wait-1", ExpectedWaitForEventKey = "parked:first",
            WaitForEventKey = "resumed:first"
        }, default)).Should().BeFalse("the row is parked on a later park than the one this resume woke");
        (await world.RowAsync(run)).Should().Be(parked);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Suspended, expectedOwner) with
        {
            CurrentBlockPath = "wait-1", ExpectedCurrentBlockPath = "wait-1", ExpectedWaitForEventKey = "parked:second",
            WaitForEventKey = "resumed:second"
        }, default)).Should().BeTrue();
        var taken = await world.RowAsync(run);
        taken.WaitForEventKey.Should().Be("resumed:second");
        taken.Owner.Should().Be("desk-9");
    }

    [Theory]
    [InlineData("desk-1")]
    [InlineData(null)]
    public async Task A_swap_expecting_a_version_lands_only_on_the_row_it_read(string? expectedOwner)
    {
        await using var world = await World.StartAsync(backends, $"dispatch_swap_version_{expectedOwner ?? "any"}".Replace('-', '_'));
        var run = await world.BeginAsync(owner: "desk-1");
        var read = await world.RowAsync(run);
        await world.SqlAsync($"""
            UPDATE automation.workflow_executions_state SET version = version + 1 WHERE execution_id = '{run}'
            """);
        var moved = await world.RowAsync(run);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Running, expectedOwner) with
        {
            ToStatus = WorkflowRunStateStatus.Suspended, ExpectedVersion = (int)read.Version
        }, default)).Should().BeFalse("an unrelated writer moved the row after the read this swap was decided on");
        (await world.RowAsync(run)).Should().Be(moved);

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Running, expectedOwner) with
        {
            ToStatus = WorkflowRunStateStatus.Suspended, ExpectedVersion = (int)moved.Version
        }, default)).Should().BeTrue();
        (await world.RowAsync(run)).Version.Should().Be(moved.Version + 1);
    }

    [Fact]
    public async Task A_swap_on_postgres_is_not_built_by_entity_framework()
    {
        await using var world = await World.StartAsync(backends, "dispatch_swap_raw");
        var run = await world.BeginAsync(owner: "desk-1");
        var sent = world.Commands.All.Count;

        (await world.Store.TransitionAsync(Swap(run, WorkflowRunStateStatus.Running, "desk-1"), default))
            .Should().BeTrue();

        world.Commands.All.Skip(sent).Should().BeEmpty();
    }

    private static WorkflowRunStateTransition Swap(Guid run, WorkflowRunStateStatus from, string? expectedOwner) => new()
    {
        ExecutionId = run,
        FromStatus = from,
        ToStatus = WorkflowRunStateStatus.Running,
        CurrentBlockPath = "assign-2",
        VariablesJson = "{}",
        OwnedByReplicaId = "desk-9",
        ExpectedOwnedByReplicaId = expectedOwner
    };

    private sealed record Row(
        string Status, string CurrentBlockPath, string VariablesJson, string VariablesType, DateTimeOffset? WaitUntilUtc,
        string? WaitForEventKey, string? Owner, long Version, DateTimeOffset LastTransitionUtc);

    private sealed class World : IAsyncDisposable
    {
        private readonly string postgres;
        private readonly ServiceProvider container;
        private readonly Guid workflowId;

        private World(string postgres, Guid workflowId)
        {
            this.postgres = postgres;
            this.workflowId = workflowId;
            container = QueryPlans.Container(postgres, Commands);
            Store = new EfWorkflowRunStateStore(container.GetRequiredService<IServiceScopeFactory>());
        }

        public QueryPlans.RecordingCommands Commands { get; } = new();

        public IWorkflowRunStateStore Store { get; }

        public static async Task<World> StartAsync(ClusterBackends backends, string database)
        {
            var postgres = await backends.CreateDatabaseAsync(database);
            await using (var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
                         {
                             ReplicaId = "desk-schema",
                             PostgresConnectionString = postgres,
                             RedisConnectionString = backends.RedisConnectionString
                         }))
            {
                await FreightDesk.WarmAsync(postgres, desk);
            }

            return new World(postgres, await FreightDesk.SeedAssignCarrierChainAsync(postgres, "SHP-7200", 1));
        }

        public Task<Guid> BeginAsync(string? owner) => Store.BeginAsync(new WorkflowRunStateBegin
        {
            WorkflowId = workflowId,
            WorkflowVersion = 1,
            VariablesJson = "{}",
            TriggerSource = "test",
            OwnedByReplicaId = owner
        }, default);

        public Task SqlAsync(string sql) => FreightDesk.ExecuteSqlAsync(postgres, sql);

        public async Task<Row> RowAsync(Guid run)
        {
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT status, current_block_path, variables_json::text, jsonb_typeof(variables_json), wait_until_utc,
                       wait_for_event_key, owned_by_replica_id, version, last_transition_utc
                FROM automation.workflow_executions_state WHERE execution_id = '{run}'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return new Row(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Convert.ToInt64(reader.GetValue(7)),
                reader.GetFieldValue<DateTimeOffset>(8));
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
