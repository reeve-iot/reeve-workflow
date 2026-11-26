using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class ParkedWaitScanTests(ClusterBackends backends)
{
    [Fact]
    public async Task Every_wait_a_host_could_wake_comes_back_once_across_the_pages()
    {
        await using var world = await World.StartAsync(backends, "dispatch_parked_waits_page");
        var trigger = await world.ParkAsync(WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":["door-opened"]}""");
        var second = await world.ParkAsync(WorkflowSuspensionKind.WaitTemplate, """{"expression":"${device.dock.free}"}""");
        var child = await world.ParkAsync(WorkflowSuspensionKind.SubWorkflow, """{"waitingOn":"5f1f1f1f-1f1f-1f1f-1f1f-1f1f1f1f1f1f"}""");
        var sleep = await world.ParkAsync(WorkflowSuspensionKind.Sleep, """{"durationMs":60000,"wakeAtUtc":"2031-01-01T00:00:00+00:00"}""");

        var seen = await world.EveryWaitAsync(limit: 2);

        seen.Select(wait => wait.SuspensionId).Should().BeEquivalentTo(
            [world.Parked.Id, trigger, second, child],
            "each parked wait a host can wake is read once, and the pages cover all of them");
        seen.Should().NotContain(wait => wait.SuspensionId == sleep,
            "a sleep ends at its own deadline, so there is nothing a host could publish for it");
    }

    [Fact]
    public async Task A_trigger_waits_keys_are_read_from_the_row_it_is_parked_on()
    {
        await using var world = await World.StartAsync(backends, "dispatch_parked_waits_trigger");
        var trigger = await world.ParkAsync(
            WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":["door-opened","sun-set"]}""");

        var wait = (await world.EveryWaitAsync(limit: 10)).Single(read => read.SuspensionId == trigger);

        wait.Kind.Should().Be(WorkflowSuspensionKind.WaitForTrigger);
        wait.DependencyKeys.Should().BeEquivalentTo(["trigger-id:door-opened", "trigger-id:sun-set"]);
        wait.ExecutionLogId.Should().Be(world.Parked.ExecutionLogId);
    }

    [Fact]
    public async Task A_template_that_reads_a_helper_says_so_and_names_its_devices()
    {
        await using var world = await World.StartAsync(backends, "dispatch_parked_waits_template");
        var parked = await world.ParkAsync(
            WorkflowSuspensionKind.WaitTemplate,
            """{"expression":"${helper.evening} || ${device.dock.free} > 0"}""");

        var wait = (await world.EveryWaitAsync(limit: 10)).Single(read => read.SuspensionId == parked);

        wait.DependencyKeys.Should().BeEquivalentTo(["helper:evening", "device:dock:free"]);
    }

    [Fact]
    public async Task A_wait_whose_config_cannot_be_read_does_not_end_the_page()
    {
        await using var world = await World.StartAsync(backends, "dispatch_parked_waits_unreadable");
        var unreadable = await world.ParkAsync(
            WorkflowSuspensionKind.WaitForTrigger, """{"triggerIds":"door-opened"}""");
        var after = await world.ParkAsync(WorkflowSuspensionKind.WaitTemplate, """{"expression":"${device.dock.free}"}""");

        var seen = await world.EveryWaitAsync(limit: 10);

        seen.Single(read => read.SuspensionId == unreadable).DependencyKeys.Should().BeEmpty();
        seen.Single(read => read.SuspensionId == after).DependencyKeys.Should().BeEquivalentTo(["device:dock:free"]);
    }

    [Fact]
    public async Task The_keys_of_a_wait_the_engine_itself_parked_are_read_from_what_the_block_wrote()
    {
        await using var world = await World.StartAsync(backends, "dispatch_parked_waits_engine");

        var wait = (await world.EveryWaitAsync(limit: 10)).Single(read => read.SuspensionId == world.Parked.Id);

        wait.DependencyKeys.Should().BeEquivalentTo(["now:epoch"],
            "the block wrote a condition over the clock, and this is that condition's own path");
    }

    [Fact]
    public async Task A_wait_somebody_took_leaves_the_index()
    {
        await using var world = await World.StartAsync(backends, "dispatch_parked_waits_taken");

        (await world.EveryWaitAsync(limit: 10)).Should().ContainSingle()
            .Which.SuspensionId.Should().Be(world.Parked.Id);

        await world.Store.TryResolveAsync(
            world.Parked.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, default);

        (await world.EveryWaitAsync(limit: 10)).Should().BeEmpty();
    }

    private sealed record ParkedRow(Guid Id, Guid ExecutionLogId);

    private sealed class World : IAsyncDisposable
    {
        private readonly string postgres;
        private readonly ServiceProvider container;
        private int parked;

        private World(string postgres, Guid workflowId, ParkedRow parkedRow)
        {
            this.postgres = postgres;
            WorkflowId = workflowId;
            Parked = parkedRow;
            container = QueryPlans.Container(postgres, new QueryPlans.RecordingCommands());
            Store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);
        }

        public Guid WorkflowId { get; }

        public ParkedRow Parked { get; }

        public IWorkflowSuspensionResumeStore Store { get; }

        public static async Task<World> StartAsync(ClusterBackends backends, string database)
        {
            var postgres = await backends.CreateDatabaseAsync(database);
            Guid workflowId;
            await using (var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
                         {
                             ReplicaId = "desk-waits",
                             PostgresConnectionString = postgres,
                             RedisConnectionString = backends.RedisConnectionString
                         }))
            {
                await FreightDesk.WarmAsync(postgres, desk);
                workflowId = await FreightDesk.SeedHoldUntilAsync(
                    postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
                    shipment: "SHP-7401", DateTimeOffset.UtcNow.AddHours(6));
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
            await using var command = new NpgsqlCommand(
                "SELECT id, execution_log_id FROM automation.workflow_suspensions", connection);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();

            return new World(postgres, workflowId, new ParkedRow(reader.GetGuid(0), reader.GetGuid(1)));
        }

        public async Task<Guid> ParkAsync(WorkflowSuspensionKind kind, string configJson)
        {
            var id = Guid.NewGuid();
            await using var connection = new NpgsqlConnection(postgres);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO automation.workflow_suspensions
                    (id, execution_log_id, workflow_id, block_id, kind, status, config_json,
                     state_payload_json, suspended_at_utc, timeout_at_utc, take_count)
                VALUES ($1, $2, $3, $4, $5, 'Suspended', $6::jsonb, '{}'::jsonb, $7, $8, 0)
                """, connection);
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(Parked.ExecutionLogId);
            command.Parameters.AddWithValue(WorkflowId);
            command.Parameters.AddWithValue($"extra-{++parked}");
            command.Parameters.AddWithValue((int)kind);
            command.Parameters.AddWithValue(configJson);
            command.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddSeconds(parked));
            command.Parameters.AddWithValue(DateTimeOffset.UtcNow.AddHours(6));
            await command.ExecuteNonQueryAsync();
            return id;
        }

        public async Task<IReadOnlyList<WorkflowParkedWait>> EveryWaitAsync(int limit)
        {
            var seen = new List<WorkflowParkedWait>();
            var cursor = WorkflowSuspensionScanCursor.Start;
            for (var page = 0; page < 20; page++)
            {
                var read = await Store.ScanParkedWaitsAsync(cursor, limit, null, default);
                seen.AddRange(read.Waits);
                cursor = read.NextCursor;
                if (read.Waits.Count < limit)
                    return seen;
            }

            throw new InvalidOperationException("the pages never reached the end of the parked waits");
        }

        public async ValueTask DisposeAsync() => await container.DisposeAsync();
    }
}
