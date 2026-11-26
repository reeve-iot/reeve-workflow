using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class GracefulStopTests(ClusterBackends backends)
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ShutdownWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan AbandonedRunAfter = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task A_run_on_a_replica_stopped_mid_block_is_released_and_finished_by_its_peer()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_graceful_stop");

        await using var stopping = await DispatchReplica.StartAsync(Settings("desk-stopping", postgres));
        await using var staying = await DispatchReplica.StartAsync(Settings("desk-staying", postgres));
        await FreightDesk.WarmAsync(postgres, stopping, staying);

        var workflowId = await FreightDesk.SeedAssignCarrierAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-9101", carrier: "Nordbahn", Dwell.TotalSeconds);

        _ = AbandonedRunRecoveryTests.Forget(stopping.ExecuteAsync(workflowId));
        var inFlight = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                rows => rows.Any(row => row.OwnedByReplicaId == stopping.Id
                                        && row.Status == nameof(WorkflowRunStateStatus.Running)),
                TimeSpan.FromSeconds(30),
                "the stopping replica to be in the block",
                stopping, staying))
            .Single();

        (await stopping.StopAsync(TimeSpan.FromSeconds(30))).Should().Be(0,
            "a stop is a shutdown, not a crash:\n" + stopping.Console);

        var released = await FreightDesk.RunStatesAsync(postgres, workflowId);
        released.Should().ContainSingle().Which.Should().Match<RunState>(row =>
                row.ExecutionId == inFlight.ExecutionId
                && row.Status == nameof(WorkflowRunStateStatus.Running)
                && (row.OwnedByReplicaId == null || row.OwnedByReplicaId == staying.Id),
            "the stopping replica released the row before it exited, rather than closing it or leaving its name on it; "
            + "a released row is the peer's to take at its next sweep, which may already have run");
        var journal = await FreightDesk.JournalAsync(postgres, workflowId);
        journal.Should().ContainSingle().Which.Should().Match<RunJournalEntry>(entry =>
                entry.Status == WorkflowExecutionStatus.Running
                && entry.ErrorMessage!.StartsWith(WorkflowRunHandover.StoppedReason),
            "the entry stays open for the peer that carries the run on, and says why this replica stopped");

        _ = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.Any(row => row.ExecutionId == inFlight.ExecutionId
                                    && row.Status == nameof(WorkflowRunStateStatus.Completed))
                    && rows.All(row => row.Status == nameof(WorkflowRunStateStatus.Completed)),
            TimeSpan.FromSeconds(90),
            "the staying replica to take the released row and walk the run to its end",
            staying);
        (await FreightDesk.EventuallyAsync(
                () => FreightDesk.JournalAsync(postgres, workflowId),
                entries => entries.All(entry => entry.Status != WorkflowExecutionStatus.Running),
                TimeSpan.FromSeconds(30), "the peer to close the entry it carried the run on under", staying))
            .Should().ContainSingle("the peer finished the run under its entry")
            .Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        (await FreightDesk.RunStatesAsync(postgres, workflowId)).Should().ContainSingle("the run went on in the row it had");
    }

    private DispatchReplicaSettings Settings(string replicaId, string postgres) => new()
    {
        ReplicaId = replicaId,
        PostgresConnectionString = postgres,
        RedisConnectionString = backends.RedisConnectionString,
        AbandonedRunAfter = AbandonedRunAfter,
        RecoverySweepInterval = TimeSpan.FromMilliseconds(500),
        ShutdownTimeout = ShutdownWindow
    };
}
