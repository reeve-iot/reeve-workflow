using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class SuspensionTakeOnPostgresTests(ClusterBackends backends)
{
    [Fact]
    public async Task Taking_a_parked_wait_marks_it_and_hands_back_the_row_it_took()
    {
        await using var world = await World.StartAsync(backends, "dispatch_take_marks");
        var resolvedAt = new DateTimeOffset(2031, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var taken = await world.Store.TryResolveAsync(
            world.Parked.Id, WorkflowSuspensionStatus.TimedOut, """{"reason":"deadline"}""", resolvedAt, default);

        taken.Should().NotBeNull();
        taken!.SuspensionId.Should().Be(world.Parked.Id);
        taken.ExecutionLogId.Should().Be(world.Parked.ExecutionLogId);
        taken.WorkflowId.Should().Be(world.WorkflowId);
        taken.BlockId.Should().Be(FreightDesk.WorkNodeId);
        taken.Kind.Should().Be(WorkflowSuspensionKind.WaitTemplate);
        taken.OutcomeJson.Should().Be("""{"reason":"deadline"}""");
        taken.StatePayloadJson.Should().Be(world.Parked.StatePayloadJson);
        taken.SuspendedAtUtc.Should().Be(world.Parked.SuspendedAtUtc);

        var row = await world.RowAsync();
        row.Status.Should().Be("TimedOut");
        row.OutcomeJson.Should().Be("""{"reason":"deadline"}""");
        row.ResolvedAtUtc.Should().Be(resolvedAt);
        row.TakeCount.Should().Be(world.Parked.TakeCount + 1);
    }

    [Fact]
    public async Task A_wait_somebody_took_first_is_not_taken_again()
    {
        await using var world = await World.StartAsync(backends, "dispatch_take_once");
        (await world.Store.TryResolveAsync(world.Parked.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        var afterFirst = await world.RowAsync();

        (await world.Store.TryResolveAsync(world.Parked.Id, WorkflowSuspensionStatus.TimedOut, """{"late":true}""", DateTimeOffset.UtcNow, default))
            .Should().BeNull();

        (await world.RowAsync()).Should().Be(afterFirst);
    }

    [Fact]
    public async Task Taking_a_wait_on_postgres_is_not_built_by_entity_framework()
    {
        await using var world = await World.StartAsync(backends, "dispatch_take_raw");
        var sent = world.Commands.All.Count;

        (await world.Store.TryResolveAsync(world.Parked.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();

        world.Commands.All.Skip(sent).Should().BeEmpty("the take and the row it hands back are one statement, written by the store");
    }

    [Fact]
    public async Task A_read_after_a_peer_took_the_wait_says_it_was_answered_and_one_for_no_wait_does_not()
    {
        await using var world = await World.StartAsync(backends, "dispatch_read_answered");
        (await world.Store.GetParkedAsync(world.Parked.Id, null, default))!.Row.Should().NotBeNull();
        (await world.Store.TryResolveAsync(world.Parked.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();

        var taken = await world.Store.GetParkedAsync(world.Parked.Id, world.Parked.ExecutionLogId, default);
        var nothing = await world.Store.GetParkedAsync(Guid.NewGuid(), null, default);

        taken!.Row.Should().BeNull();
        taken.NoLongerWaiting.Should().BeTrue();
        nothing!.Row.Should().BeNull();
        nothing.NoLongerWaiting.Should().BeFalse();
    }

    private sealed record Row(string Status, string? OutcomeJson, DateTimeOffset? ResolvedAtUtc, int TakeCount);

    private sealed record ParkedRow(
        Guid Id, Guid ExecutionLogId, string StatePayloadJson, DateTimeOffset SuspendedAtUtc, int TakeCount);

    private sealed class World : IAsyncDisposable
    {
        private readonly string postgres;
        private readonly ServiceProvider container;

        private World(string postgres, Guid workflowId, ParkedRow parked)
        {
            this.postgres = postgres;
            WorkflowId = workflowId;
            Parked = parked;
            container = QueryPlans.Container(postgres, Commands);
            Store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);
        }

        public Guid WorkflowId { get; }

        public ParkedRow Parked { get; }

        public QueryPlans.RecordingCommands Commands { get; } = new();

        public IWorkflowSuspensionResumeStore Store { get; }

        public static async Task<World> StartAsync(ClusterBackends backends, string database)
        {
            var postgres = await backends.CreateDatabaseAsync(database);
            Guid workflowId;
            await using (var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
                         {
                             ReplicaId = "desk-take",
                             PostgresConnectionString = postgres,
                             RedisConnectionString = backends.RedisConnectionString
                         }))
            {
                await FreightDesk.WarmAsync(postgres, desk);
                workflowId = await FreightDesk.SeedHoldUntilAsync(
                    postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
                    shipment: "SHP-7301", DateTimeOffset.UtcNow.AddHours(6));
                await desk.ExecuteAsync(workflowId);
                _ = await FreightDesk.EventuallyAsync(
                    () => FreightDesk.SuspensionsAsync(postgres, workflowId),
                    rows => rows.Count == 1,
                    TimeSpan.FromSeconds(30),
                    "the hold to be parked",
                    desk);
            }

            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT id, execution_log_id, state_payload_json::text, suspended_at_utc, take_count
                FROM automation.workflow_suspensions
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            var parked = new ParkedRow(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4));

            return new World(postgres, workflowId, parked);
        }

        public async Task<Row> RowAsync()
        {
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT status, outcome_json, resolved_at_utc, take_count
                FROM automation.workflow_suspensions WHERE id = '{Parked.Id}'
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            return new Row(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetInt32(3));
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
