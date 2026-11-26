using System.Net;
using FluentAssertions;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class DepotEventDeduplicationTests(ClusterBackends backends)
{
    [Fact]
    public async Task One_depot_event_heard_by_both_replicas_becomes_one_run_and_a_second_event_still_runs()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_dedup");

        await using var first = await DispatchReplica.StartAsync(Settings("desk-1", postgres));
        await using var second = await DispatchReplica.StartAsync(Settings("desk-2", postgres));

        await FreightDesk.WarmAsync(postgres, first, second);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-2280", carrier: "Rheinlast", dwellSeconds: 0);

        var heard = await first.ObserveDepotEventAsync(workflowId, "scan-88431");
        var heardAgain = await second.ObserveDepotEventAsync(workflowId, "scan-88431");

        heard.Status.Should().Be(HttpStatusCode.OK);
        heard.Outputs.Should().ContainKey("waybill")
            .WhoseValue!.ToString().Should().Be("WB-RHEINLAST-SHP2280");
        heardAgain.Outputs.Should().BeEmpty(
            "the second replica heard the same physical scan, and one scan is one consignment");

        (await FreightDesk.JournalAsync(postgres, workflowId))
            .Should().ContainSingle("one scan, one run")
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);

        var next = await second.ObserveDepotEventAsync(workflowId, "scan-88432");
        next.Outputs.Should().ContainKey("waybill");
        (await FreightDesk.JournalAsync(postgres, workflowId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task One_depot_event_answered_by_two_workflows_runs_each_of_them_once()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_dedup_two_workflows");

        await using var first = await DispatchReplica.StartAsync(Settings("desk-1", postgres));
        await using var second = await DispatchReplica.StartAsync(Settings("desk-2", postgres));

        await FreightDesk.WarmAsync(postgres, first, second);

        var intake = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-2281", carrier: "Rheinlast", dwellSeconds: 0);
        var customs = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-2282", carrier: "Nordfracht", dwellSeconds: 0);

        foreach (var replica in new[] { first, second })
        foreach (var workflowId in new[] { intake, customs })
            (await replica.ObserveDepotEventAsync(workflowId, "scan-88433")).Status.Should().Be(HttpStatusCode.OK);

        (await FreightDesk.JournalAsync(postgres, intake)).Should().ContainSingle("the fan-out still collapses");
        (await FreightDesk.JournalAsync(postgres, customs)).Should().ContainSingle(
            "a second workflow the same scan fires holds a claim of its own, not the first one's");
    }

    [Theory]
    [InlineData(WorkflowMode.Single)]
    [InlineData(WorkflowMode.Parallel)]
    public async Task One_depot_event_on_both_replicas_of_a_workflow_that_does_not_compile_leaves_one_refused_row(
        WorkflowMode mode)
    {
        const string retired = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"gone","blockTypeId":"dispatch.retired","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"gone","mappings":{}}]}
            """;
        var postgres = await backends.CreateDatabaseAsync("dispatch_dedup_refused_" + mode.ToString().ToLowerInvariant());

        await using var first = await DispatchReplica.StartAsync(Settings("desk-1", postgres));
        await using var second = await DispatchReplica.StartAsync(Settings("desk-2", postgres));

        await FreightDesk.WarmAsync(postgres, first, second);

        var workflowId = await FreightDesk.SeedDefinitionAsync(postgres, mode, "Retired " + mode, retired);
        await first.ObserveDepotEventAsync(workflowId, "scan-88434");
        await second.ObserveDepotEventAsync(workflowId, "scan-88434");

        (await FreightDesk.JournalAsync(postgres, workflowId)).Should().ContainSingle("one scan, one refusal on record")
            .Which.Status.Should().Be(WorkflowExecutionStatus.Failed);
    }

    [Fact]
    public async Task Two_single_workflows_one_scan_fires_under_an_event_only_key_leave_a_refused_row_each()
    {
        const string retired = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"gone","blockTypeId":"dispatch.retired","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"gone","mappings":{}}]}
            """;
        var postgres = await backends.CreateDatabaseAsync("dispatch_dedup_refused_event_only");

        await using var first = await DispatchReplica.StartAsync(Settings("desk-1", postgres) with { EventOnlyFireKeys = true });
        await using var second = await DispatchReplica.StartAsync(Settings("desk-2", postgres) with { EventOnlyFireKeys = true });

        await FreightDesk.WarmAsync(postgres, first, second);

        var workflows = new[]
        {
            await FreightDesk.SeedDefinitionAsync(postgres, WorkflowMode.Single, "Retired one", retired),
            await FreightDesk.SeedDefinitionAsync(postgres, WorkflowMode.Single, "Retired two", retired),
        };
        foreach (var replica in new[] { first, second })
            foreach (var workflowId in workflows)
                await replica.ObserveDepotEventAsync(workflowId, "scan-88435");

        foreach (var workflowId in workflows)
            (await FreightDesk.JournalAsync(postgres, workflowId)).Should().ContainSingle("each workflow's refusal claims its own row")
                .Which.Status.Should().Be(WorkflowExecutionStatus.Failed);
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString
    };
}
