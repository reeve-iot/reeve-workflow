using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public class SingleReplicaSeamEquivalenceTests
{
    [Theory]
    [InlineData(WorkflowMode.Single, 1, false)]
    [InlineData(WorkflowMode.Single, 1, true)]
    [InlineData(WorkflowMode.Restart, 1, false)]
    [InlineData(WorkflowMode.Restart, 1, true)]
    [InlineData(WorkflowMode.Parallel, 2, false)]
    [InlineData(WorkflowMode.Parallel, 2, true)]
    [InlineData(WorkflowMode.Queued, 1, false)]
    [InlineData(WorkflowMode.Queued, 1, true)]
    public async Task The_first_run_is_admitted_the_same_way_with_the_seams_as_without(
        WorkflowMode mode, int maxConcurrentRuns, bool isMultiReplica)
    {
        var withoutSeams = await AdmitAsync(mode, maxConcurrentRuns, named: false, isMultiReplica);
        var withSeams = await AdmitAsync(mode, maxConcurrentRuns, named: true, isMultiReplica);

        withSeams.Outcome.Should().Be(withoutSeams.Outcome);
        (withSeams.Lease is not null).Should().Be(withoutSeams.Lease is not null);
    }

    [Theory]
    [InlineData(WorkflowMode.Single, false)]
    [InlineData(WorkflowMode.Single, true)]
    [InlineData(WorkflowMode.Parallel, false)]
    [InlineData(WorkflowMode.Parallel, true)]
    public async Task A_budget_already_spent_refuses_the_same_way_with_the_seams_as_without(
        WorkflowMode mode, bool isMultiReplica)
    {
        var (withoutFirst, withoutSecond) = await AdmitTwiceAsync(mode, named: false, isMultiReplica);
        var (withFirst, withSecond) = await AdmitTwiceAsync(mode, named: true, isMultiReplica);

        withFirst.Outcome.Should().Be(withoutFirst.Outcome);
        withSecond.Outcome.Should().Be(withoutSecond.Outcome,
            "the in-process permit is the budget on one replica, and naming the cluster seams must not "
            + "quietly hand a second run a slot the process does not have");
        (withSecond.Lease is not null).Should().Be(withoutSecond.Lease is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_budget_comes_back_when_the_run_ends_with_the_seams_as_without(bool named)
    {
        var coordinator = Build(WorkflowMode.Single, maxConcurrentRuns: 1, named, isMultiReplica: true);

        var first = await coordinator.AcquireAsync(CancellationToken.None);
        first.Lease.Should().NotBeNull();
        await first.Lease!.DisposeAsync();

        var second = await coordinator.AcquireAsync(CancellationToken.None);
        second.Lease.Should().NotBeNull("the run ended, so its budget is back");
    }

    [Fact]
    public async Task A_waiter_promoted_off_the_queue_is_admitted_the_same_way_with_the_seams_as_without()
    {
        var withoutSeams = await PromoteAWaiterAsync(named: false);
        var withSeams = await PromoteAWaiterAsync(named: true);

        withSeams.Outcome.Should().Be(withoutSeams.Outcome);
        (withSeams.Lease is not null).Should().Be(withoutSeams.Lease is not null);
    }

    private static async Task<WorkflowRunDecision> PromoteAWaiterAsync(bool named)
    {
        var coordinator = Build(
            WorkflowMode.Queued, maxConcurrentRuns: 1, named, isMultiReplica: true, maxQueuedRuns: 1);

        var running = await coordinator.AcquireAsync(CancellationToken.None);
        var waiting = coordinator.AcquireAsync(CancellationToken.None);
        await running.Lease!.DisposeAsync();

        return await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<WorkflowRunDecision> AdmitAsync(
        WorkflowMode mode, int maxConcurrentRuns, bool named, bool isMultiReplica)
    {
        var coordinator = Build(mode, maxConcurrentRuns, named, isMultiReplica);
        return await coordinator.AcquireAsync(CancellationToken.None);
    }

    private static async Task<(WorkflowRunDecision First, WorkflowRunDecision Second)> AdmitTwiceAsync(
        WorkflowMode mode, bool named, bool isMultiReplica)
    {
        var coordinator = Build(mode, maxConcurrentRuns: 1, named, isMultiReplica);
        var first = await coordinator.AcquireAsync(CancellationToken.None);
        var second = await coordinator.AcquireAsync(CancellationToken.None);
        return (first, second);
    }

    private static WorkflowRunCoordinator Build(
        WorkflowMode mode, int maxConcurrentRuns, bool named, bool isMultiReplica, int maxQueuedRuns = 0) =>
        new(Guid.NewGuid(), mode, maxConcurrentRuns, maxQueuedRuns, WorkflowOverflowBehavior.Drop,
            NullWorkflowMetrics.Instance,
            logger: null,
            lockProvider: named ? new SingleReplicaRunLockProvider() : null,
            slotGate: named ? new SingleReplicaRunSlotGate() : null,
            isMultiReplica: isMultiReplica);
}
