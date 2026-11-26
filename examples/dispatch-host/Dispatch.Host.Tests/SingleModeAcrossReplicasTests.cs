using System.Net;
using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class SingleModeAcrossReplicasTests(ClusterBackends backends)
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(18);

    [Fact]
    public async Task A_single_mode_workflow_dispatched_on_both_replicas_runs_once_and_can_run_again_after()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_single_mode");

        await using var first = await DispatchReplica.StartAsync(Settings("desk-1", postgres));
        await using var second = await DispatchReplica.StartAsync(Settings("desk-2", postgres));

        await FreightDesk.WarmAsync(postgres, first, second);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Single, maxConcurrentRuns: 1,
            shipment: "SHP-4471", carrier: "Nordfracht", Dwell.TotalSeconds);

        var firstRun = first.ExecuteAsync(workflowId);

        await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            states => states.Any(state => state.OwnedByReplicaId == first.Id
                                          && state.Status == nameof(WorkflowRunStateStatus.Running)),
            TimeSpan.FromSeconds(30),
            "the first replica to be holding the bay",
            first, second);

        var shadowed = await second.ExecuteAsync(workflowId);
        var ran = await firstRun;

        shadowed.Outputs.Should().BeEmpty(
            "the second replica must find the workflow already running on a peer and not start it again");
        ran.Outputs.Should().ContainKey("waybill")
            .WhoseValue!.ToString().Should().Be("WB-NORDFRACHT-SHP4471");

        (await FreightDesk.JournalAsync(postgres, workflowId))
            .Should().ContainSingle("only one of the two simultaneous dispatches may become a run")
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);

        var afterwards = await second.ExecuteAsync(workflowId);
        afterwards.Status.Should().Be(HttpStatusCode.OK);
        afterwards.Outputs.Should().ContainKey("waybill",
            "the lock is released when the run ends, so the other replica may take it next");

        (await FreightDesk.JournalAsync(postgres, workflowId)).Should().HaveCount(2);
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString
    };
}
