using System.Globalization;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore.Tests;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class LoopWaitParkOnPostgresTests(ClusterBackends backends)
{
    [Fact]
    public async Task Every_loop_parks_a_hold_once_per_pass_and_finishes_on_the_replica_that_started_it()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_loop_wait_same");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-one", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var runs = await StartEveryCaseAsync(postgres, desk, DateTimeOffset.UtcNow.AddSeconds(5));

        await GradeAsync(postgres, runs, "desk-one", desk);
    }

    [Fact]
    public async Task Every_loop_parks_a_hold_once_per_pass_and_finishes_on_another_replica()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_loop_wait_another");
        var runs = new List<(Guid WorkflowId, int Passes)>();
        await using (var parking = await DispatchReplica.StartAsync(Settings("desk-one", postgres)))
        {
            await FreightDesk.WarmAsync(postgres, parking);
            runs = await StartEveryCaseAsync(postgres, parking, DateTimeOffset.UtcNow.AddSeconds(45));
            foreach (var (workflowId, _) in runs)
                await FreightDesk.EventuallyAsync(() => FreightDesk.SuspensionsAsync(postgres, workflowId),
                    rows => rows.Count == 1, TimeSpan.FromSeconds(30), "the first pass to park", parking);
            (await parking.StopAsync(TimeSpan.FromSeconds(30))).Should().Be(0);
        }

        await using var waking = await DispatchReplica.StartAsync(Settings("desk-two", postgres));
        await GradeAsync(postgres, runs, "desk-two", waking);
    }

    private static async Task<List<(Guid WorkflowId, int Passes)>> StartEveryCaseAsync(
        string postgres, DispatchReplica desk, DateTimeOffset until)
    {
        var hold = new JsonObject
        {
            ["blockTypeId"] = "dispatch.hold-until",
            ["configuration"] = new JsonObject
            {
                ["shipment"] = "SHP-9105", ["untilUtc"] = until.ToString("O", CultureInfo.InvariantCulture),
                ["released"] = FreightDesk.ReleasedWhen(until)
            }, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }
        };
        var runs = new List<(Guid WorkflowId, int Passes)>();
        foreach (var loop in LoopWaitGraphs.Loops)
            foreach (var depth in new[] { 0, 1, 2 })
                foreach (var passes in new[] { 1, 2, 3 })
                {
                    var workflowId = await FreightDesk.SeedDefinitionAsync(postgres, $"loop-{loop}-{depth}-{passes}",
                        LoopWaitGraphs.Definition(loop, depth, passes, hold));
                    await desk.ExecuteAsync(workflowId);
                    runs.Add((workflowId, passes));
                }
        return runs;
    }

    private static async Task GradeAsync(
        string postgres, List<(Guid WorkflowId, int Passes)> runs, string finisher, DispatchReplica desk)
    {
        foreach (var (workflowId, passes) in runs)
        {
            var state = (await FreightDesk.EventuallyAsync(() => FreightDesk.RunStatesAsync(postgres, workflowId),
                rows => rows.Count == 1 && rows[0].Status is "Completed" or "Failed" or "Cancelled",
                TimeSpan.FromSeconds(120), "the run to end", desk)).Single();
            state.Status.Should().Be(nameof(WorkflowRunStateStatus.Completed), state.FailureReason);
            state.OwnedByReplicaId.Should().Be(finisher);

            var journal = await FreightDesk.EventuallyAsync(() => FreightDesk.JournalAsync(postgres, workflowId),
                entries => entries.Count == 1 && entries[0].Status != WorkflowExecutionStatus.Running,
                TimeSpan.FromSeconds(30), "the entry to close", desk);
            journal.Single().Status.Should().Be(WorkflowExecutionStatus.Succeeded);

            var parks = await FreightDesk.SuspensionsAsync(postgres, workflowId);
            parks.Should().HaveCount(passes, "one park per pass, and no other");
            parks.Select(park => park.Id).Should().OnlyHaveUniqueItems();
            parks.Should().OnlyContain(park => park.BlockId == LoopWaitGraphs.WaitNodeId
                && (park.Status == WorkflowSuspensionStatus.Resumed || park.Status == WorkflowSuspensionStatus.TimedOut));
        }
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString,
        WakerPollInterval = TimeSpan.FromSeconds(1)
    };
}
