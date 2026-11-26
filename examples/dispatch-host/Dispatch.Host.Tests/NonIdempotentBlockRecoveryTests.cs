using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class NonIdempotentBlockRecoveryTests(ClusterBackends backends)
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AbandonedRunAfter = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task A_run_that_died_inside_a_non_idempotent_block_is_failed_rather_than_replayed()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_non_idempotent");

        await using var dying = await DispatchReplica.StartAsync(Settings("desk-dying", postgres));
        await using var surviving = await DispatchReplica.StartAsync(Settings("desk-surviving", postgres));

        await FreightDesk.WarmAsync(postgres, dying, surviving);

        var workflowId = await FreightDesk.SeedPrintLabelAsync(
            postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-7310", Dwell.TotalSeconds);

        _ = AbandonedRunRecoveryTests.Forget(dying.ExecuteAsync(workflowId));

        var abandoned = (await FreightDesk.EventuallyAsync(
                () => FreightDesk.RunStatesAsync(postgres, workflowId),
                states => states.Any(state => state.CurrentBlockPath == FreightDesk.WorkNodeId),
                TimeSpan.FromSeconds(30),
                "the dying replica to enter the label block",
                dying, surviving))
            .Single();

        dying.Kill();

        var settled = await FreightDesk.EventuallyAsync(
            () => FreightDesk.RunStatesAsync(postgres, workflowId),
            rows => rows.Any(row => row.Status != nameof(WorkflowRunStateStatus.Running)),
            TimeSpan.FromSeconds(60),
            $"the surviving replica to decide what to do with the abandoned row {AbandonedRunAfter.TotalSeconds:0}s after it went quiet",
            surviving);

        var decided = settled.Single(row => row.ExecutionId == abandoned.ExecutionId);
        decided.Status.Should().Be(nameof(WorkflowRunStateStatus.Failed));
        decided.FailureReason.Should().Be($"block_not_idempotent_during_recovery: {FreightDesk.WorkNodeId}",
            "a failure a person has to act on must say which failure it was, and at which block");

        await Task.Delay(SweepInterval * 6);

        (await FreightDesk.RunStatesAsync(postgres, workflowId))
            .Should().ContainSingle("failing the run is instead of re-dispatching it, not as well as");
        (await FreightDesk.JournalAsync(postgres, workflowId))
            .Should().ContainSingle("no second label may be printed")
            .Which.Should().Match<RunJournalEntry>(entry => entry.Status == WorkflowExecutionStatus.Failed
                                                         && entry.ErrorMessage!.Contains(FreightDesk.WorkNodeId),
                "the entry the killed replica left open is closed with the run, naming the block");

        var audit = surviving.Console;
        audit.Should().Contain(WorkflowAuditEventKinds.ClaimedByRecovery,
            "a fleet claiming rows and ending every one of them must not look like a fleet with nothing to do");
        audit.Should().Contain(WorkflowAuditEventKinds.EndedByRecovery);
        audit.Should().Contain($"\"reason\":\"{WorkflowRecoveryEndingReasons.RefusedAtReentry}\"",
            "the bare constant, because a label carrying the block's id is one metric series per node");
        audit.Should().Contain($"\"refusedNodeId\":\"{FreightDesk.WorkNodeId}\"",
            "and the block itself, where a reader can act on it rather than where it multiplies series");
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
