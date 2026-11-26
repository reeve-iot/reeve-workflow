using FluentAssertions;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed class RunStateStoreDefaultsTests
{
    [Theory]
    [InlineData(WorkflowRunStateStatus.Completed)]
    [InlineData(WorkflowRunStateStatus.Failed)]
    [InlineData(WorkflowRunStateStatus.Cancelled)]
    public async Task A_run_that_has_ended_is_not_ended_again_by_the_replica_that_ended_it(
        WorkflowRunStateStatus terminal)
    {
        var store = new HostStore { Status = terminal, Owner = "replica-1" };

        var ended = await ((IWorkflowRunStateStore)store).CompleteIfOwnedAsync(
            store.ExecutionId, "replica-1", WorkflowRunStateStatus.Failed, "the execution timed out",
            CancellationToken.None);

        ended.Should().BeFalse("the row is terminal, whoever it names");
        store.Completions.Should().BeEmpty(
            "and the write is not attempted at all: a store that ends what it is asked to end would have "
            + "rewritten the status, the reason and the instant of a run that was already over");
    }

    [Fact]
    public async Task A_run_another_replica_holds_is_still_refused()
    {
        var store = new HostStore { Status = WorkflowRunStateStatus.Running, Owner = "replica-2" };

        (await ((IWorkflowRunStateStore)store).CompleteIfOwnedAsync(
            store.ExecutionId, "replica-1", WorkflowRunStateStatus.Failed, null, CancellationToken.None))
            .Should().BeFalse();
        store.Completions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_the_store_has_never_heard_of_is_not_ended()
    {
        var store = new HostStore { Status = WorkflowRunStateStatus.Running, Owner = "replica-1" };

        (await ((IWorkflowRunStateStore)store).CompleteIfOwnedAsync(
            Guid.NewGuid(), "replica-1", WorkflowRunStateStatus.Failed, "the execution timed out",
            CancellationToken.None))
            .Should().BeFalse("no row is not the same answer as a row that says yes");
        store.Completions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowRunStateStatus.Pending)]
    [InlineData(WorkflowRunStateStatus.Running)]
    [InlineData(WorkflowRunStateStatus.Sleeping)]
    [InlineData(WorkflowRunStateStatus.Waiting)]
    [InlineData(WorkflowRunStateStatus.Suspended)]
    public async Task A_run_that_has_not_ended_yet_still_ends_in_its_owners_name(WorkflowRunStateStatus status)
    {
        var store = new HostStore { Status = status, Owner = "replica-1" };

        (await ((IWorkflowRunStateStore)store).CompleteIfOwnedAsync(
            store.ExecutionId, "replica-1", WorkflowRunStateStatus.Completed, null, CancellationToken.None))
            .Should().BeTrue();
        store.Completions.Should().Equal([WorkflowRunStateStatus.Completed]);
    }

    [Fact]
    public async Task A_host_whose_rows_name_no_owner_still_ends_its_runs()
    {
        var store = new HostStore { Status = WorkflowRunStateStatus.Running, Owner = null };

        (await ((IWorkflowRunStateStore)store).CompleteIfOwnedAsync(
            store.ExecutionId, null, WorkflowRunStateStatus.Completed, null, CancellationToken.None))
            .Should().BeTrue("an unnamed row and an unnamed caller are the same replica");
        store.Completions.Should().Equal([WorkflowRunStateStatus.Completed]);
    }

    [Fact]
    public async Task A_store_that_lists_nothing_answers_an_empty_page_rather_than_throwing()
    {
        var store = new HostStore { Status = WorkflowRunStateStatus.Running, Owner = "replica-1" };

        var page = await ((IWorkflowRunStateStore)store).ListByWorkflowAsync(
            Guid.NewGuid(), new WorkflowRunStatePageRequest(), CancellationToken.None);

        page.Rows.Should().BeEmpty();
        page.NextCursor.Should().BeNull("a cursor would ask the caller to come back for more of nothing");
    }

    private sealed class HostStore : IWorkflowRunStateStore
    {
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public required WorkflowRunStateStatus Status { get; init; }
        public required string? Owner { get; init; }

        public List<WorkflowRunStateStatus> Completions { get; } = [];

        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            Task.FromResult(ExecutionId);

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowRunStateRow?>(executionId != ExecutionId
                ? null
                : new WorkflowRunStateRow
                {
                    ExecutionId = ExecutionId,
                    WorkflowId = Guid.NewGuid(),
                    Status = Status,
                    OwnedByReplicaId = Owner
                });

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> CompleteAsync(
            Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason,
            CancellationToken cancellationToken)
        {
            Completions.Add(terminalStatus);
            return Task.FromResult(true);
        }
    }
}
