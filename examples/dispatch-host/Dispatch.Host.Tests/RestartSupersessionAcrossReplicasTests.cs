using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class RestartSupersessionAcrossReplicasTests(ClusterBackends backends)
{
    private const int StatusCodeClientClosedRequest = 499;

    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task A_restart_dispatched_on_one_replica_tears_down_the_run_in_flight_on_the_other()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_restart");

        await using var holding = await DispatchReplica.StartAsync(Settings("desk-holding", postgres));
        await using var superseding = await DispatchReplica.StartAsync(Settings("desk-superseding", postgres));

        await FreightDesk.WarmAsync(postgres, holding, superseding);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Restart, maxConcurrentRuns: 1,
            shipment: "SHP-5150", carrier: "Alpentrans", Dwell.TotalSeconds);

        var interrupted = holding.ExecuteAsync(workflowId);

        var opened = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                states => states.Any(state => state.OwnedByReplicaId == holding.Id
                                              && state.Status == nameof(WorkflowRunStateStatus.Running)),
                TimeSpan.FromSeconds(30),
                "the first replica to be holding the bay",
                holding, superseding))
            .Single();

        var takeover = await superseding.ExecuteAsync(workflowId);
        var superseded = await interrupted;

        ((int)superseded.Status).Should().Be(StatusCodeClientClosedRequest,
            "a run torn down by a peer's restart is not a run that failed, and the caller is told which it was");
        superseded.Outputs.Should().BeEmpty();

        takeover.Outputs.Should().ContainKey("waybill")
            .WhoseValue!.ToString().Should().Be("WB-ALPENTRANS-SHP5150",
                "the run that superseded the other is the one that survives");

        (await FreightDesk.JournalAsync(postgres, workflowId))
            .Should().Contain(entry => entry.Status == WorkflowExecutionStatus.Cancelled,
                "the peer's cancel reached the replica that was holding the bay");

        (await FreightDesk.RunStatesAsync(postgres, workflowId))
            .Should().Contain(row => row.ExecutionId == opened.ExecutionId
                                     && row.Status == nameof(WorkflowRunStateStatus.Cancelled));
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString
    };
}
