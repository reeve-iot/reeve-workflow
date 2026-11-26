using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class EfWorkflowRunStateStoreTests
{
    private static WorkflowRunStateTransition Transition(
        Guid executionId,
        WorkflowRunStateStatus from,
        WorkflowRunStateStatus to,
        string? owner = null,
        string? expectedOwner = null) => new()
        {
            ExecutionId = executionId,
            FromStatus = from,
            ToStatus = to,
            CurrentBlockPath = "node-2",
            VariablesJson = "{\"a\":1}",
            OwnedByReplicaId = owner,
            ExpectedOwnedByReplicaId = expectedOwner
        };

    private static async Task<(SqliteWorkflowDatabase Database, IWorkflowRunStateStore Store, Guid ExecutionId)>
        StartRunAsync(string? owner = null)
    {
        var database = new SqliteWorkflowDatabase();
        var workflowId = database.SeedWorkflow();
        var store = database.Services.GetRequiredService<IWorkflowRunStateStore>();

        var executionId = await store.BeginAsync(
            new WorkflowRunStateBegin
            {
                WorkflowId = workflowId,
                WorkflowVersion = 1,
                VariablesJson = "{}",
                TriggerSource = "test",
                OwnedByReplicaId = owner
            },
            CancellationToken.None);

        return (database, store, executionId);
    }

    [Fact]
    public async Task A_started_run_reads_back_as_running_where_it_was_opened()
    {
        var (database, store, executionId) = await StartRunAsync(owner: "replica-1");
        using var _ = database;

        var row = await store.GetAsync(executionId, CancellationToken.None);

        row.Should().NotBeNull();
        row!.ExecutionId.Should().Be(executionId);
        row.Status.Should().Be(WorkflowRunStateStatus.Running);
        row.OwnedByReplicaId.Should().Be("replica-1");
        row.VariablesJson.Should().Be("{}");
    }

    [Fact]
    public async Task An_id_nothing_was_started_under_reads_back_as_nothing()
    {
        using var database = new SqliteWorkflowDatabase();
        var store = database.Services.GetRequiredService<IWorkflowRunStateStore>();

        (await store.GetAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_transition_moves_the_row_and_says_it_won()
    {
        var (database, store, executionId) = await StartRunAsync();
        using var _ = database;

        var moved = await store.TransitionAsync(
            Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Sleeping),
            CancellationToken.None);

        moved.Should().BeTrue();
        var row = await store.GetAsync(executionId, CancellationToken.None);
        row!.Status.Should().Be(WorkflowRunStateStatus.Sleeping);
        row.CurrentBlockPath.Should().Be("node-2");
        row.VariablesJson.Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task A_transition_from_a_status_the_row_is_not_in_loses_and_changes_nothing()
    {
        var (database, store, executionId) = await StartRunAsync();
        using var _ = database;

        var moved = await store.TransitionAsync(
            Transition(executionId, WorkflowRunStateStatus.Sleeping, WorkflowRunStateStatus.Completed),
            CancellationToken.None);

        moved.Should().BeFalse();
        (await store.GetAsync(executionId, CancellationToken.None))!.Status
            .Should().Be(WorkflowRunStateStatus.Running);
    }

    [Fact]
    public async Task A_transition_expecting_an_owner_the_row_no_longer_has_loses()
    {
        var (database, store, executionId) = await StartRunAsync(owner: "replica-1");
        using var _ = database;

        await store.TransitionAsync(
            Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Running,
                owner: "replica-2"),
            CancellationToken.None);

        var moved = await store.TransitionAsync(
            Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Sleeping,
                owner: "replica-1", expectedOwner: "replica-1"),
            CancellationToken.None);

        moved.Should().BeFalse();
        (await store.GetAsync(executionId, CancellationToken.None))!.OwnedByReplicaId.Should().Be("replica-2");
    }

    [Fact]
    public async Task A_resume_takes_a_parked_row_only_with_the_token_it_was_parked_with()
    {
        var (database, store, executionId) = await StartRunAsync();
        using var _ = database;
        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Suspended)
            with { WaitForEventKey = "parked:second" }, CancellationToken.None)).Should().BeTrue();

        (await store.GetAsync(executionId, CancellationToken.None))!.WaitForEventKey.Should().Be("parked:second");
        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Suspended, WorkflowRunStateStatus.Running)
            with { ExpectedWaitForEventKey = "parked:first" }, CancellationToken.None)).Should().BeFalse();
        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Suspended, WorkflowRunStateStatus.Running)
            with { ExpectedWaitForEventKey = "parked:second", WaitForEventKey = "resumed:second" }, CancellationToken.None))
            .Should().BeTrue();
        (await store.GetAsync(executionId, CancellationToken.None))!.WaitForEventKey.Should().Be("resumed:second");
    }

    [Fact]
    public async Task A_transition_with_an_expected_version_lands_only_on_the_row_it_read()
    {
        var (database, store, executionId) = await StartRunAsync();
        using var _ = database;
        var read = (await store.GetAsync(executionId, CancellationToken.None))!;
        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Running),
            CancellationToken.None)).Should().BeTrue();

        var moved = (await store.GetAsync(executionId, CancellationToken.None))!;
        moved.Version.Should().BeGreaterThan(read.Version);
        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Suspended)
            with { ExpectedVersion = read.Version }, CancellationToken.None)).Should().BeFalse();
        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Suspended)
            with { ExpectedVersion = moved.Version }, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Completing_a_run_keeps_the_name_of_the_replica_that_finished_it()
    {
        var (database, store, executionId) = await StartRunAsync(owner: "replica-1");
        using var _ = database;

        var completed = await store.CompleteAsync(
            executionId, WorkflowRunStateStatus.Completed, failureReason: null, CancellationToken.None);

        completed.Should().BeTrue();
        var row = await store.GetAsync(executionId, CancellationToken.None);
        row!.Status.Should().Be(WorkflowRunStateStatus.Completed);
        row.OwnedByReplicaId.Should().Be("replica-1");
    }

    [Fact]
    public async Task Completing_an_already_finished_run_loses_rather_than_throwing()
    {
        var (database, store, executionId) = await StartRunAsync();
        using var _ = database;

        await store.CompleteAsync(
            executionId, WorkflowRunStateStatus.Completed, null, CancellationToken.None);

        var second = await store.CompleteAsync(
            executionId, WorkflowRunStateStatus.Failed, "late", CancellationToken.None);

        second.Should().BeFalse();
        (await store.GetAsync(executionId, CancellationToken.None))!.Status
            .Should().Be(WorkflowRunStateStatus.Completed);
    }

    [Fact]
    public async Task Completing_a_run_in_its_owners_name_ends_it_with_its_reason()
    {
        var (database, store, executionId) = await StartRunAsync(owner: "replica-1");
        using var _ = database;

        (await store.CompleteIfOwnedAsync(
            executionId, "replica-1", WorkflowRunStateStatus.Failed, "the block failed", CancellationToken.None))
            .Should().BeTrue();

        var row = await store.GetAsync(executionId, CancellationToken.None);
        row!.Status.Should().Be(WorkflowRunStateStatus.Failed);
        row.OwnedByReplicaId.Should().Be("replica-1",
            "the fence is what the ending was made through, and the row keeps the name it was ended in");
        row.FailureReason.Should().Be("the block failed");
    }

    [Fact]
    public async Task A_run_that_completed_itself_reads_back_with_no_reason_and_one_a_sweep_completed_with_its_own()
    {
        var (database, store, finished) = await StartRunAsync(owner: "replica-1");
        using var _ = database;
        var replayed = await store.BeginAsync(new WorkflowRunStateBegin
        {
            WorkflowId = (await store.GetAsync(finished, CancellationToken.None))!.WorkflowId,
            WorkflowVersion = 1,
            VariablesJson = "{}",
            TriggerSource = "test",
            OwnedByReplicaId = "replica-1"
        }, CancellationToken.None);

        await store.CompleteIfOwnedAsync(finished, "replica-1", WorkflowRunStateStatus.Completed, null, CancellationToken.None);
        await store.CompleteAsync(replayed, WorkflowRunStateStatus.Completed, "replayed_by_recovery", CancellationToken.None);

        (await store.GetAsync(finished, CancellationToken.None))!.FailureReason.Should().BeNull();
        (await store.GetAsync(replayed, CancellationToken.None))!.FailureReason.Should().Be("replayed_by_recovery");
    }

    [Fact]
    public async Task Completing_a_run_in_the_name_of_a_replica_that_no_longer_holds_it_leaves_it_to_the_peer()
    {
        var (database, store, executionId) = await StartRunAsync(owner: "replica-1");
        using var _ = database;
        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Running,
            owner: "replica-2"), CancellationToken.None)).Should().BeTrue();
        var before = await store.GetAsync(executionId, CancellationToken.None);

        (await store.CompleteIfOwnedAsync(
            executionId, "replica-1", WorkflowRunStateStatus.Failed, "late", CancellationToken.None))
            .Should().BeFalse();

        (await store.GetAsync(executionId, CancellationToken.None)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Completing_a_run_in_no_replicas_name_ends_only_a_row_that_names_none()
    {
        var (database, store, executionId) = await StartRunAsync(owner: null);
        using var _ = database;

        (await store.CompleteIfOwnedAsync(
            executionId, "replica-1", WorkflowRunStateStatus.Completed, null, CancellationToken.None))
            .Should().BeFalse("a row that names nobody is not this replica's");
        (await store.CompleteIfOwnedAsync(
            executionId, null, WorkflowRunStateStatus.Completed, null, CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Completing_a_run_that_was_never_started_loses()
    {
        using var database = new SqliteWorkflowDatabase();
        var store = database.Services.GetRequiredService<IWorkflowRunStateStore>();

        (await store.CompleteAsync(
            Guid.NewGuid(), WorkflowRunStateStatus.Completed, null, CancellationToken.None))
            .Should().BeFalse();
    }
    [Fact]
    public async Task A_swap_expecting_the_row_at_another_node_changes_nothing()
    {
        var (database, store, executionId) = await StartRunAsync(owner: "replica-1");
        using var _ = database;
        await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Suspended,
            owner: "replica-1") with { CurrentBlockPath = "wait-2" }, CancellationToken.None);
        var before = await store.GetAsync(executionId, CancellationToken.None);

        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Suspended, WorkflowRunStateStatus.Running,
            owner: "replica-2") with { CurrentBlockPath = "wait-1", ExpectedCurrentBlockPath = "wait-1" }, CancellationToken.None))
            .Should().BeFalse();

        (await store.GetAsync(executionId, CancellationToken.None)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_swap_expecting_the_row_at_the_node_it_is_at_moves_it()
    {
        var (database, store, executionId) = await StartRunAsync(owner: "replica-1");
        using var _ = database;
        await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Running, WorkflowRunStateStatus.Suspended,
            owner: "replica-1") with { CurrentBlockPath = "wait-1" }, CancellationToken.None);

        (await store.TransitionAsync(Transition(executionId, WorkflowRunStateStatus.Suspended, WorkflowRunStateStatus.Running,
            owner: "replica-2") with { CurrentBlockPath = "wait-1", ExpectedCurrentBlockPath = "wait-1" }, CancellationToken.None))
            .Should().BeTrue();

        (await store.GetAsync(executionId, CancellationToken.None))!.OwnedByReplicaId.Should().Be("replica-2");
    }
}
