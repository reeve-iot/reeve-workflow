using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class AbandonedRunRecoveryTests(ClusterBackends backends)
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AbandonedRunAfter = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task A_run_left_behind_by_a_killed_replica_is_claimed_and_finished_by_its_peer_under_its_own_entry()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_recovery");

        await using var dying = await DispatchReplica.StartAsync(Settings("desk-dying", postgres));
        await using var surviving = await DispatchReplica.StartAsync(Settings("desk-surviving", postgres));

        await FreightDesk.WarmAsync(postgres, dying, surviving);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-9002", carrier: "Ostbahn", Dwell.TotalSeconds);

        _ = Forget(dying.ExecuteAsync(workflowId));

        var abandoned = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                states => states.Any(state => state.OwnedByReplicaId == dying.Id
                                              && state.Status == nameof(WorkflowRunStateStatus.Running)),
                TimeSpan.FromSeconds(30),
                "the dying replica to be holding the bay",
                dying, surviving))
            .Single();

        var killedAt = DateTimeOffset.UtcNow;
        dying.Kill();

        var claimed = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.Any(row => row.ExecutionId == abandoned.ExecutionId
                                    && row.OwnedByReplicaId == surviving.Id),
            TimeSpan.FromSeconds(60),
            $"the surviving replica to put its own name on the abandoned row {AbandonedRunAfter.TotalSeconds:0}s after it went quiet",
            surviving);
        claimed.Should().Contain(row => row.ExecutionId == abandoned.ExecutionId
                                        && row.Status == nameof(WorkflowRunStateStatus.Running),
            "the claim takes the row over where it stands; it does not close it and start again");

        var settled = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.All(row => row.Status == nameof(WorkflowRunStateStatus.Completed)),
            TimeSpan.FromSeconds(60),
            "the reclaimed run to be finished in place",
            surviving);
        settled.Should().ContainSingle("the run goes on in the row it had, not in a new one")
            .Which.Should().Match<RunState>(row => row.ExecutionId == abandoned.ExecutionId && row.FailureReason == null);

        var journal = await FreightDesk.JournalAsync(postgres, workflowId);
        journal.Should().ContainSingle("the run goes on under the entry it was writing")
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        journal[0].CompletedAtUtc.Should().BeAfter(killedAt, "the surviving replica finished it after its peer died");
    }

    internal static async Task Forget(Task<DispatchResponse> dispatch)
    {
        try
        {
            await dispatch;
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
        }
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
