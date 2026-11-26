using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class ThreeReplicaRecoveryTests(ClusterBackends backends)
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AbandonedRunAfter = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Two_live_sweepers_over_one_abandoned_run_carry_it_on_once_between_them()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_three_replicas");

        await using var dying = await DispatchReplica.StartAsync(Settings("desk-dying", postgres));
        await using var first = await DispatchReplica.StartAsync(Settings("desk-first", postgres));
        await using var second = await DispatchReplica.StartAsync(Settings("desk-second", postgres));

        await FreightDesk.WarmAsync(postgres, dying, first, second);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-9003", carrier: "Ostbahn", Dwell.TotalSeconds);

        _ = AbandonedRunRecoveryTests.Forget(dying.ExecuteAsync(workflowId));

        var abandoned = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                states => states.Any(state => state.OwnedByReplicaId == dying.Id
                                              && state.Status == nameof(WorkflowRunStateStatus.Running)),
                TimeSpan.FromSeconds(30),
                "the dying replica to be holding the bay",
                dying, first, second))
            .Single();

        dying.Kill();

        var settled = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.All(row => row.Status == nameof(WorkflowRunStateStatus.Completed)),
            TimeSpan.FromSeconds(60),
            "the abandoned run to be claimed by one of the two survivors and finished",
            first, second);

        settled.Should().ContainSingle(
            "one of the two sweepers took the row and re-entered it in place, and the other found it already "
            + "renewed; a second row would mean a run was started beside it")
            .Which.ExecutionId.Should().Be(abandoned.ExecutionId);

        var journal = await FreightDesk.EventuallyAsync(
            () => FreightDesk.JournalAsync(postgres, workflowId),
            entries => entries.All(entry => entry.Status != WorkflowExecutionStatus.Running),
            TimeSpan.FromSeconds(30), "the entry the run was writing to be closed with it", first, second);
        journal.Should().ContainSingle("a run dispatched twice would leave a second entry behind the first one")
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded, "the run is finished under the entry it was writing");

        static int Holds(DispatchReplica replica) =>
            System.Text.RegularExpressions.Regex.Matches(replica.Console, "on carrier Ostbahn for").Count;
        (Holds(first) + Holds(second)).Should().Be(1,
            "exactly one of the two survivors re-entered the run; two would have held the bay twice");
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString,
        AbandonedRunAfter = AbandonedRunAfter,
        RecoverySweepInterval = TimeSpan.FromMilliseconds(500)
    };
}
