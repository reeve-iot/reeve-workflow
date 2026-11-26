using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class LoopRecoveryTests(ClusterBackends backends)
{
    private static readonly TimeSpan AbandonedRunAfter = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(2);

    private const int Iterations = 4;
    private const string Carrier = "Ring";

    [Fact]
    public async Task A_run_killed_in_the_third_iteration_is_finished_by_the_peer_with_the_first_two_run_once()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_loop_recovery");
        await using var dying = await DispatchReplica.StartAsync(Settings("desk-dying", postgres));
        await using var surviving = await DispatchReplica.StartAsync(Settings("desk-surviving", postgres));
        await FreightDesk.WarmAsync(postgres, dying, surviving);
        var workflowId = await FreightDesk.SeedDefinitionAsync(postgres, "loop-recovery", Loop("SHP-5601"));

        _ = AbandonedRunRecoveryTests.Forget(dying.ExecuteAsync(workflowId));
        await FreightDesk.EventuallyAsync(
            () => Task.FromResult(Ran(dying)),
            ran => ran >= 3,
            TimeSpan.FromSeconds(30), "the dying replica to enter the third iteration", dying, surviving);
        dying.Kill();

        var finished = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                rows => rows.All(row => row.Status == nameof(WorkflowRunStateStatus.Completed)),
                TimeSpan.FromSeconds(90), "the surviving replica to finish the run", surviving))
            .Should().ContainSingle().Subject;
        finished.Status.Should().Be(nameof(WorkflowRunStateStatus.Completed));
        (await FreightDesk.EventuallyAsync(
                () => FreightDesk.JournalAsync(postgres, workflowId),
                entries => entries.All(entry => entry.Status != WorkflowExecutionStatus.Running),
                TimeSpan.FromSeconds(30), "the entry the killed replica left open to be closed with the run", surviving))
            .Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);

        Ran(dying).Should().Be(3, "the two iterations it finished and the one it died in");
        Ran(surviving).Should().Be(2,
            "the iteration the run died in, re-entered from the position the checkpoint carried, and the one "
            + "after it — a recovery that re-entered at the root would run all four again");
        (Ran(dying) + Ran(surviving)).Should().Be(Iterations + 1,
            "each completed iteration's blocks ran exactly once, and the iteration in flight ran twice");
    }

    private static int Ran(DispatchReplica replica) =>
        Regex.Matches(replica.Console, $"on carrier {Carrier} for").Count;

    private static string Loop(string shipment)
    {
        var dwell = Dwell.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        return $$"""
            {
              "nodes": [
                { "id": "start", "blockTypeId": "WorkflowInput","position":{"x":0,"y":0},"configuration":{} },
                { "id": "loop", "blockTypeId": "ForLoop",
                  "configuration": { "From": 0, "To": {{Iterations}}, "Step": 1 },
                  "subCanvases": {
                    "Body": {
                      "nodes": [
                        { "id": "in", "blockTypeId": "WorkflowInput","position":{"x":0,"y":0},"configuration":{} },
                        { "id": "haul", "blockTypeId": "dispatch.assign-carrier",
                          "configuration": { "shipment": "{{shipment}}", "carrier": "{{Carrier}}", "dwellSeconds": {{dwell}} },"position":{"x":0,"y":0} }
                      ],
                      "edges": [ { "id": "b1", "sourceNodeId": "in", "targetNodeId": "haul","mappings":{} } ]
                    }
                  },"position":{"x":0,"y":0}
                }
              ],
              "edges": [ { "id": "e1", "sourceNodeId": "start", "targetNodeId": "loop","mappings":{} } ]
            }
            """;
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString,
        AbandonedRunAfter = AbandonedRunAfter,
        RecoverySweepInterval = SweepInterval
    };
}
