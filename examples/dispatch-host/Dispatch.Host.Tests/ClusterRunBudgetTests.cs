using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class ClusterRunBudgetTests(ClusterBackends backends)
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Inside_one_replica_the_budget_holds_without_any_cluster_backend()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_budget_one_replica");

        await using var only = await DispatchReplica.StartAsync(
            Settings("desk-1", postgres, redis: null, hasPeerReplicas: false));

        await FreightDesk.WarmAsync(postgres, only);

        var workflowId = await SeedAsync(postgres, "SHP-1000");
        var held = only.ExecuteAsync(workflowId);
        await WaitUntilRunningAsync(postgres, workflowId, only.Id, only);

        var second = await only.ExecuteAsync(workflowId);
        await held;

        second.Outputs.Should().BeEmpty("a budget of one run is a budget of one run inside a replica");
        (await FreightDesk.JournalAsync(postgres, workflowId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Declaring_peers_with_no_cluster_run_budget_does_not_start()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_budget_without_redis");

        var start = async () => await DispatchReplica.StartAsync(Settings("desk-1", postgres, redis: null));

        var refusal = (await start.Should().ThrowAsync<InvalidOperationException>()).Which;

        refusal.Message.Should().Contain(DispatchReplica.RefusedToStart)
            .And.Contain("Seams.RunSlotGate")
            .And.Contain("Seams.FireDedup",
                "the message has to name what is missing, or an operator learns only that something is");
        refusal.Message.Should().NotContain("Seams.RunLockProvider",
            "this desk fills that one from PostgreSQL and needs no Redis for it");
    }

    [Fact]
    public async Task The_same_deployment_with_a_cluster_run_budget_runs_it_once()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_budget_with_redis");

        await using var first = await DispatchReplica.StartAsync(
            Settings("desk-1", postgres, backends.RedisConnectionString));
        await using var second = await DispatchReplica.StartAsync(
            Settings("desk-2", postgres, backends.RedisConnectionString));

        await FreightDesk.WarmAsync(postgres, first, second);

        var workflowId = await SeedAsync(postgres, "SHP-1002");
        var firstRun = first.ExecuteAsync(workflowId);
        await WaitUntilRunningAsync(postgres, workflowId, first.Id, first, second);

        var secondRun = await second.ExecuteAsync(workflowId);
        await firstRun;

        secondRun.Outputs.Should().BeEmpty(
            "the budget is held across replicas, so the second dispatch finds it spent");
        (await FreightDesk.JournalAsync(postgres, workflowId)).Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    private static Task<Guid> SeedAsync(string postgres, string shipment) =>
        FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 1,
            shipment, carrier: "Donaufracht", Dwell.TotalSeconds);

    private static async Task WaitUntilRunningAsync(
        string postgres, Guid workflowId, string replicaId, params DispatchReplica[] replicas) =>
        (await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            states => states.Any(state => state.OwnedByReplicaId == replicaId
                                          && state.Status == nameof(WorkflowRunStateStatus.Running)),
            TimeSpan.FromSeconds(30),
            "the first replica to be holding the bay",
            replicas)).Should().NotBeEmpty();

    private static DispatchReplicaSettings Settings(
        string replicaId, string postgres, string? redis, bool hasPeerReplicas = true) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = redis,
        HasPeerReplicas = hasPeerReplicas
    };
}
