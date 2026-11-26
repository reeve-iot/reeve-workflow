using System.Text.RegularExpressions;
using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class CheckpointRecoveryTests(ClusterBackends backends)
{
    private static readonly TimeSpan AbandonedRunAfter = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Held = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(0.2);

    [Fact]
    public async Task A_run_killed_inside_the_label_block_is_failed_naming_it_with_none_of_the_carriers_run_again()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_checkpoint_label");
        await using var dying = await DispatchReplica.StartAsync(Settings("desk-dying", postgres));
        await using var surviving = await DispatchReplica.StartAsync(Settings("desk-surviving", postgres));
        await FreightDesk.WarmAsync(postgres, dying, surviving);
        var workflowId = await FreightDesk.SeedDefinitionAsync(postgres, "checkpoint-label", Chain("SHP-5401", Quick, labelDwell: Held));

        var abandoned = await KillInside(postgres, workflowId, "label", dying, surviving);

        var decided = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                rows => rows.All(row => row.Status != nameof(WorkflowRunStateStatus.Running)),
                TimeSpan.FromSeconds(60), "the surviving replica to decide about the run", surviving))
            .Should().ContainSingle().Subject;
        decided.ExecutionId.Should().Be(abandoned);
        decided.Status.Should().Be(nameof(WorkflowRunStateStatus.Failed));
        decided.FailureReason.Should().Be("block_not_idempotent_during_recovery: label");
        (await FreightDesk.EventuallyAsync(
                () => FreightDesk.JournalAsync(postgres, workflowId),
                entries => entries.All(entry => entry.Status != WorkflowExecutionStatus.Running),
                TimeSpan.FromSeconds(30), "the entry the killed replica left open to be closed with the run", surviving))
            .Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowExecutionStatus.Failed);

        foreach (var carrier in Carriers)
            Runs(dying, carrier).Should().Be(1, $"the dying replica ran {carrier} before it reached the label");
        foreach (var carrier in Carriers)
            Runs(surviving, carrier).Should().Be(0, $"{carrier} is in the checkpoint and is not run again");
        surviving.Console.Should().NotContain("Printing the label", "a second label is a pallet shipped twice");
    }

    [Fact]
    public async Task A_run_killed_inside_the_third_carrier_is_finished_by_the_peer_with_the_first_two_run_once()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_checkpoint_third");
        await using var dying = await DispatchReplica.StartAsync(Settings("desk-dying", postgres));
        await using var surviving = await DispatchReplica.StartAsync(Settings("desk-surviving", postgres));
        await FreightDesk.WarmAsync(postgres, dying, surviving);
        var workflowId = await FreightDesk.SeedDefinitionAsync(
            postgres, "checkpoint-third", Chain("SHP-5402", Quick, labelDwell: Quick, thirdDwell: Held));

        var abandoned = await KillInside(postgres, workflowId, "c3", dying, surviving);

        var finished = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                rows => rows.All(row => row.Status == nameof(WorkflowRunStateStatus.Completed)),
                TimeSpan.FromSeconds(60), "the surviving replica to finish the run", surviving))
            .Should().ContainSingle().Subject;
        finished.ExecutionId.Should().Be(abandoned);
        (await FreightDesk.EventuallyAsync(
                () => FreightDesk.JournalAsync(postgres, workflowId),
                entries => entries.All(entry => entry.Status != WorkflowExecutionStatus.Running),
                TimeSpan.FromSeconds(30), "the entry the run was writing to be closed with it", surviving))
            .Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);

        Runs(dying, "First").Should().Be(1);
        Runs(dying, "Second").Should().Be(1);
        Runs(surviving, "First").Should().Be(0, "each of the first two runs exactly once across both replicas");
        Runs(surviving, "Second").Should().Be(0, "each of the first two runs exactly once across both replicas");
        Runs(surviving, "Third").Should().Be(1, "the block the replica died in is the one block run again");
        Regex.Matches(surviving.Console, "Printing the label").Count.Should().Be(1);
    }

    private static readonly string[] Carriers = ["First", "Second", "Third"];

    private static async Task<Guid> KillInside(
        string postgres, Guid workflowId, string node, DispatchReplica dying, DispatchReplica surviving)
    {
        _ = AbandonedRunRecoveryTests.Forget(dying.ExecuteAsync(workflowId));
        var inside = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                rows => rows.Any(row => row.CurrentBlockPath == node && row.OwnedByReplicaId == dying.Id),
                TimeSpan.FromSeconds(30), $"the dying replica to enter {node}", dying, surviving))
            .Single();
        dying.Kill();
        return inside.ExecutionId;
    }

    private static int Runs(DispatchReplica replica, string carrier) =>
        Regex.Matches(replica.Console, $"on carrier {carrier} for").Count;

    private static string Chain(string shipment, TimeSpan dwell, TimeSpan labelDwell, TimeSpan? thirdDwell = null)
    {
        string Carrier(string id, string carrier, TimeSpan held) => $$"""
            { "id": "{{id}}", "blockTypeId": "dispatch.assign-carrier",
              "configuration": { "shipment": "{{shipment}}", "carrier": "{{carrier}}", "dwellSeconds": {{held.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}} },"position":{"x":0,"y":0} }
            """;
        return $$"""
            {
              "nodes": [
                { "id": "start", "blockTypeId": "WorkflowInput","position":{"x":0,"y":0},"configuration":{} },
                {{Carrier("c1", "First", dwell)}},
                {{Carrier("c2", "Second", dwell)}},
                {{Carrier("c3", "Third", thirdDwell ?? dwell)}},
                { "id": "label", "blockTypeId": "dispatch.print-label",
                  "configuration": { "shipment": "{{shipment}}", "dwellSeconds": {{labelDwell.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}} },"position":{"x":0,"y":0} }
              ],
              "edges": [
                { "id": "e1", "sourceNodeId": "start", "targetNodeId": "c1","mappings":{} },
                { "id": "e2", "sourceNodeId": "c1", "targetNodeId": "c2","mappings":{} },
                { "id": "e3", "sourceNodeId": "c2", "targetNodeId": "c3","mappings":{} },
                { "id": "e4", "sourceNodeId": "c3", "targetNodeId": "label","mappings":{} }
              ]
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
