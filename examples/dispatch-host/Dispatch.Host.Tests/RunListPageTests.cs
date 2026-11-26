using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class RunListPageTests(ClusterBackends backends)
{
    [Fact]
    public async Task Paging_walks_every_run_exactly_once_newest_first()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_list_pages");
        var (store, workflowId) = await DeskWithRunsAsync(postgres, runs: 25);

        var seen = new List<WorkflowRunStateSummary>();
        var sizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await store.ListByWorkflowAsync(
                workflowId, new WorkflowRunStatePageRequest { Limit = 10, After = cursor }, default);

            page.Rows.Should().NotBeEmpty("a page that says there is more to read must hand back some of it");
            seen.AddRange(page.Rows);
            sizes.Add(page.Rows.Count);
            cursor = page.NextCursor;
        }
        while (cursor is not null && sizes.Count < 10);

        sizes.Should().Equal([10, 10, 5],
            "a page of ten holds ten while there are ten left — a list that quietly handed back nine would "
            + "have a caller rendering a short page and never knowing why");
        seen.Select(row => row.ExecutionId).Should().OnlyHaveUniqueItems(
            "a page boundary that repeats a row is a list that shows one run twice");
        seen.Should().HaveCount(25, "and one that skips a row is a list that loses one");
        seen.Select(row => row.StartedAtUtc).Should().BeInDescendingOrder("newest first, which is what a list is");
    }

    [Fact]
    public async Task A_page_never_reads_the_checkpoints_of_the_runs_it_lists()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_list_columns");
        var (_, workflowId) = await DeskWithRunsAsync(postgres, runs: 3);

        var commands = new QueryPlans.RecordingCommands();
        await using var container = QueryPlans.Container(postgres, commands);
        var recorded = new EfWorkflowRunStateStore(container.GetRequiredService<IServiceScopeFactory>());

        await recorded.ListByWorkflowAsync(workflowId, new WorkflowRunStatePageRequest(), default);

        var sql = commands.All.Should().ContainSingle().Which;
        sql.Should().NotContain("variables_json", "the checkpoint stays in the database");
        sql.Should().NotContain("pinned_versions_json", "and so does everything else a list does not render");
        sql.Should().Contain("started_at_utc", "while what a list does render is read");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(WorkflowRunStatePageRequest.MaxLimit + 1)]
    [InlineData(int.MaxValue)]
    public async Task A_limit_this_store_cannot_keep_is_refused(int limit)
    {
        var postgres = await backends.CreateDatabaseAsync($"dispatch_run_list_limit_{Math.Abs((long)limit)}");
        var (store, workflowId) = await DeskWithRunsAsync(postgres, runs: 1);

        var read = () => store.ListByWorkflowAsync(
            workflowId, new WorkflowRunStatePageRequest { Limit = limit }, default);

        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_page_carries_what_a_list_renders_and_not_the_runs_whole_checkpoint()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_list_shape");
        var (store, workflowId) = await DeskWithRunsAsync(postgres, runs: 1);

        var page = await store.ListByWorkflowAsync(workflowId, new WorkflowRunStatePageRequest(), default);

        var only = page.Rows.Should().ContainSingle().Which;
        only.WorkflowId.Should().Be(workflowId);
        only.Status.Should().Be(WorkflowRunStateStatus.Completed);
        only.OwnedByReplicaId.Should().Be("desk-list", "a finished run still names who finished it");
        only.CompletedAtUtc.Should().NotBeNull();
        only.WorkflowVersion.Should().Be(1);
        page.NextCursor.Should().BeNull("there is no second page of one run");
    }

    [Fact]
    public async Task A_page_asked_for_one_status_answers_with_that_status_only()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_list_status");
        var (store, workflowId) = await DeskWithRunsAsync(postgres, runs: 6, failEvery: 2);

        var failed = await store.ListByWorkflowAsync(
            workflowId,
            new WorkflowRunStatePageRequest { Statuses = [WorkflowRunStateStatus.Failed] },
            default);

        failed.Rows.Should().HaveCount(3);
        failed.Rows.Should().OnlyContain(row => row.Status == WorkflowRunStateStatus.Failed);
        failed.Rows.Should().OnlyContain(row => row.FailureReason != null,
            "a list of failures that does not say why is a list nobody can act on");
    }

    [Fact]
    public async Task Runs_that_started_in_the_same_instant_are_paged_through_one_by_one()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_list_ties");
        var (store, workflowId) = await DeskWithRunsAsync(postgres, runs: 0);

        await InsertRunsAsync(postgres, workflowId, count: 4, at: DateTimeOffset.UtcNow.AddMinutes(-10), spread: false);
        await InsertRunsAsync(postgres, workflowId, count: 4, at: DateTimeOffset.UtcNow.AddMinutes(-5), spread: true);

        var seen = new List<Guid>();
        var sizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = await store.ListByWorkflowAsync(
                workflowId, new WorkflowRunStatePageRequest { Limit = 2, After = cursor }, default);
            page.Rows.Should().NotBeEmpty("a cursor is a promise that there is another row");
            seen.AddRange(page.Rows.Select(row => row.ExecutionId));
            sizes.Add(page.Rows.Count);
            cursor = page.NextCursor;
        }
        while (cursor is not null && sizes.Count < 10);

        seen.Should().OnlyHaveUniqueItems("a group read twice is a list that shows a run twice");
        seen.Should().HaveCount(8, "the four that share an instant are read once each, not lost below a cursor");
        sizes.Should().Equal([2, 2, 2, 2],
            "including the pages that cut through the group of four, which is the case a cursor made of an "
            + "instant alone cannot express");
    }

    [Fact]
    public async Task Runs_of_one_instant_come_back_in_an_order_that_is_theirs_and_not_the_databases()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_list_tie_order");
        var (store, workflowId) = await DeskWithRunsAsync(postgres, runs: 0);
        await InsertRunsAsync(postgres, workflowId, count: 8, at: DateTimeOffset.UtcNow.AddMinutes(-3), spread: false);

        var page = await store.ListByWorkflowAsync(
            workflowId, new WorkflowRunStatePageRequest { Limit = 8 }, default);

        page.Rows.Should().HaveCount(8);
        page.Rows.Select(row => row.ExecutionId).Should().BeInDescendingOrder(
            "eight runs of one instant are ordered by the only thing that tells them apart");
    }

    [Fact]
    public async Task A_cursor_this_store_did_not_issue_is_refused_rather_than_read()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_list_cursor");
        var (store, workflowId) = await DeskWithRunsAsync(postgres, runs: 2);

        var read = () => store.ListByWorkflowAsync(
            workflowId, new WorkflowRunStatePageRequest { After = "page=2" }, default);

        await read.Should().ThrowAsync<ArgumentException>(
            "a cursor guessed at is a page that starts somewhere this store never promised anything about");
    }

    [Fact]
    public async Task The_run_list_reads_the_index_it_was_given_rather_than_sorting_the_history()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_run_list_plan");
        var (_, workflowId) = await DeskWithRunsAsync(postgres, runs: 1);
        await InsertRunsAsync(postgres, workflowId, count: 20_000, at: DateTimeOffset.UtcNow.AddDays(-3), spread: true);
        await FreightDesk.ExecuteSqlAsync(postgres, "ANALYZE automation.workflow_executions_state;");

        var commands = new QueryPlans.RecordingCommands();
        await using var container = QueryPlans.Container(postgres, commands);
        var recorded = new EfWorkflowRunStateStore(container.GetRequiredService<IServiceScopeFactory>());

        await recorded.ListByWorkflowAsync(workflowId, new WorkflowRunStatePageRequest { Limit = 20 }, default);

        var plan = await QueryPlans.ExplainAsync(postgres, commands.Readers.Should().ContainSingle(
            "a page is one query, not a count beside a read").Which);
        plan.Should().Contain("ix_workflow_executions_state_workflow_started",
            "the index this release added is the one a page is meant to ride");
        plan.Should().NotContain("Seq Scan", "and it must not read the whole table to find twenty rows");
        plan.Should().NotContain("Sort", "nor sort a workflow's whole history to put twenty of them in order");
    }

    private async Task<(EfWorkflowRunStateStore Store, Guid WorkflowId)> DeskWithRunsAsync(
        string postgres, int runs, int failEvery = 0)
    {
        await using (var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
                     {
                         ReplicaId = "desk-list",
                         PostgresConnectionString = postgres,
                         RedisConnectionString = backends.RedisConnectionString
                     }))
        {
            await FreightDesk.WarmAsync(postgres, desk);
        }

        var workflowId = await FreightDesk.SeedPrintLabelAsync(
            postgres, ReeveWorkflow.Core.Model.WorkflowMode.Parallel, maxConcurrentRuns: 5,
            shipment: "SHP-7400", dwellSeconds: 0);

        for (var i = 0; i < runs; i++)
        {
            var failed = failEvery > 0 && i % failEvery == 0;
            await InsertRunAsync(
                postgres, workflowId, DateTimeOffset.UtcNow.AddMinutes(-runs + i),
                failed ? "Failed" : "Completed", failed ? "the carrier refused the shipment" : null);
        }

        var container = QueryPlans.Container(postgres, new QueryPlans.RecordingCommands());
        return (new EfWorkflowRunStateStore(container.GetRequiredService<IServiceScopeFactory>()), workflowId);
    }

    private static Task InsertRunsAsync(
        string postgres, Guid workflowId, int count, DateTimeOffset at, bool spread) =>
        FreightDesk.ExecuteSqlAsync(postgres, $$"""
            INSERT INTO automation.workflow_executions_state
                (execution_id, workflow_id, workflow_version, status, current_block_path, variables_json,
                 last_transition_utc, started_at_utc, completed_at_utc, owned_by_replica_id, version)
            SELECT gen_random_uuid(), '{{workflowId}}', 1, 'Completed', 'work', '{}',
                   now(), timestamptz '{{at:O}}' {{(spread ? "+ g * interval '1 second'" : "")}},
                   now(), 'desk-list', 1
            FROM generate_series(1, {{count}}) g;
            """);

    private static Task InsertRunAsync(
        string postgres, Guid workflowId, DateTimeOffset startedAt, string status, string? failureReason) =>
        FreightDesk.ExecuteSqlAsync(postgres, $$"""
            INSERT INTO automation.workflow_executions_state
                (execution_id, workflow_id, workflow_version, status, current_block_path, variables_json,
                 last_transition_utc, started_at_utc, completed_at_utc, owned_by_replica_id, failure_reason, version)
            VALUES (gen_random_uuid(), '{{workflowId}}', 1, '{{status}}', 'work', '{}',
                    now(), timestamptz '{{startedAt:O}}', now(), 'desk-list',
                    {{(failureReason is null ? "NULL" : $"'{failureReason}'")}}, 1);
            """);
}
