using System.Diagnostics;
using Dispatch.Host.Freight;
using FluentAssertions;
using StackExchange.Redis;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class ClearanceWakeTests(ClusterBackends backends)
{
    private static readonly TimeSpan NoPassWillCome = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan OneWakeApart = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SettleWindow = new WorkflowSuspensionWakeOptions().SettleFor;

    [Fact]
    public async Task A_cleared_shipment_walks_on_in_the_time_a_wake_takes_not_the_time_a_pass_takes()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_clearance_wake");
        await ClearTheDeskAsync("SHP-7301");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-customs", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        const string shipment = "SHP-7301";
        var workflowId = await FreightDesk.SeedAwaitClearanceAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5, shipment);

        await desk.ExecuteAsync(workflowId);

        var parked = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == WorkflowSuspensionStatus.Suspended,
            TimeSpan.FromSeconds(30),
            "the run to park on a clearance this desk has not granted",
            desk);

        parked[0].BlockId.Should().Be(FreightDesk.WorkNodeId);

        await Task.Delay(SettleWindow + TimeSpan.FromMilliseconds(500));

        var clock = Stopwatch.StartNew();
        await desk.GrantClearanceAsync(shipment);

        var resolved = await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, workflowId),
            rows => rows[0].Status != WorkflowSuspensionStatus.Suspended,
            TimeSpan.FromMinutes(1),
            "the desk's own word to move the run",
            desk);
        clock.Stop();

        resolved[0].Status.Should().Be(WorkflowSuspensionStatus.Resumed,
            "the condition the row carries became true, which is not the same as running out of time");
        resolved[0].OutcomeJson.Should().Contain("satisfied");
        clock.Elapsed.Should().BeLessThan(OneWakeApart,
            "the waker's pass is five minutes away, so whatever moved this run was not a pass — and the "
            + "roadmap's own done-when is that the wait ends within one wake call rather than one poll "
            + "interval");

        var runs = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == nameof(WorkflowRunStateStatus.Completed),
            TimeSpan.FromSeconds(30),
            "the resumed run to finish",
            desk);

        runs[0].FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task A_clearance_moves_the_run_that_waited_for_it_and_leaves_the_other_parked()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_clearance_one_of_two");
        await ClearTheDeskAsync("SHP-7302", "SHP-7303");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-customs", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var cleared = await FreightDesk.SeedAwaitClearanceAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5, shipment: "SHP-7302");
        var held = await FreightDesk.SeedAwaitClearanceAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5, shipment: "SHP-7303");

        await desk.ExecuteAsync(cleared);
        await desk.ExecuteAsync(held);

        foreach (var workflowId in new[] { cleared, held })
        {
            await FreightDesk.EventuallyAsync(
                () => FreightDesk.SuspensionsAsync(postgres, workflowId),
                rows => rows.Count == 1 && rows[0].Status == WorkflowSuspensionStatus.Suspended,
                TimeSpan.FromSeconds(30),
                "both runs to park before either is cleared",
                desk);
        }

        await WakeWithoutClearingAsync("SHP-7303");
        await Task.Delay(TimeSpan.FromSeconds(5));

        var unmoved = await FreightDesk.SuspensionsAsync(postgres, held);
        unmoved[0].Status.Should().Be(WorkflowSuspensionStatus.Suspended,
            "the waker read the row it was sent to and found the clearance still ungranted");

        await desk.GrantClearanceAsync("SHP-7303");

        await FreightDesk.EventuallyAsync(
            () => FreightDesk.SuspensionsAsync(postgres, held),
            rows => rows[0].Status == WorkflowSuspensionStatus.Resumed,
            TimeSpan.FromMinutes(1),
            "the same row to walk on once the clearance it names is granted",
            desk);

        var other = await FreightDesk.SuspensionsAsync(postgres, cleared);
        other[0].Status.Should().Be(WorkflowSuspensionStatus.Suspended,
            "and nobody cleared that one");
    }

    [Fact]
    public async Task A_shipment_lodged_after_its_clearance_never_parks_at_all()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_clearance_before_park");
        await ClearTheDeskAsync("SHP-7304");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-customs", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        await desk.GrantClearanceAsync("SHP-7304");

        var workflowId = await FreightDesk.SeedAwaitClearanceAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5, shipment: "SHP-7304");

        var clock = Stopwatch.StartNew();
        await desk.ExecuteAsync(workflowId);

        var runs = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.Count == 1 && rows[0].Status == nameof(WorkflowRunStateStatus.Completed),
            TimeSpan.FromSeconds(30),
            "the run to finish without ever waiting",
            desk);
        clock.Stop();

        runs[0].FailureReason.Should().BeNull();
        (await FreightDesk.SuspensionsAsync(postgres, workflowId)).Should().BeEmpty(
            "there was nothing left to wait for, so the run never parked");
        clock.Elapsed.Should().BeLessThan(OneWakeApart,
            "and it did not wait for the pass that would have been its only way out of a park");
    }

    [Fact]
    public async Task One_clearance_wakes_every_run_that_was_waiting_for_it()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_clearance_two_runs");
        await ClearTheDeskAsync("SHP-7305");
        await using var desk = await DispatchReplica.StartAsync(Settings("desk-customs", postgres));
        await FreightDesk.WarmAsync(postgres, desk);

        var first = await FreightDesk.SeedAwaitClearanceAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5, shipment: "SHP-7305");
        var second = await FreightDesk.SeedAwaitClearanceAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5, shipment: "SHP-7305");

        await desk.ExecuteAsync(first);
        await desk.ExecuteAsync(second);

        foreach (var workflowId in new[] { first, second })
        {
            await FreightDesk.EventuallyAsync(
                () => FreightDesk.SuspensionsAsync(postgres, workflowId),
                rows => rows.Count == 1 && rows[0].Status == WorkflowSuspensionStatus.Suspended,
                TimeSpan.FromSeconds(30),
                "both runs to park on the one shipment",
                desk);
        }

        await Task.Delay(SettleWindow + TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();
        await desk.GrantClearanceAsync("SHP-7305");

        foreach (var workflowId in new[] { first, second })
        {
            await FreightDesk.EventuallyAsync(
                () => FreightDesk.SuspensionsAsync(postgres, workflowId),
                rows => rows[0].Status == WorkflowSuspensionStatus.Resumed,
                TimeSpan.FromSeconds(30),
                "both runs to walk on from the one grant",
                desk);
        }

        clock.Stop();
        clock.Elapsed.Should().BeLessThan(OneWakeApart,
            "both were named by the grant, so neither waited for the pass five minutes away");
    }

    private async Task WakeWithoutClearingAsync(string shipment)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(backends.RedisConnectionString);
        var waiters = await redis.GetDatabase().SetMembersAsync(ClearanceDesk.WaitersKey(shipment));
        waiters.Should().NotBeEmpty("the parked run wrote itself into the desk's waiters before it parked");

        foreach (var waiter in waiters)
        {
            await redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal(ClearanceDesk.GrantedChannel), waiter);
        }
    }

    private async Task ClearTheDeskAsync(params string[] shipments)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(backends.RedisConnectionString);
        var db = redis.GetDatabase();
        foreach (var shipment in shipments)
        {
            await db.HashDeleteAsync(ClearanceDesk.ClearancesKey, ClearanceDesk.Slug(shipment));
            await db.KeyDeleteAsync(ClearanceDesk.WaitersKey(shipment));
        }
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString,
        WakerPollInterval = NoPassWillCome
    };
}
