using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class AbandonedTakeTests(ClusterBackends backends)
{
    private static readonly TimeSpan Cutoff = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task A_take_older_than_the_cutoff_whose_run_is_still_running_is_offered_with_what_it_was_taken_with()
    {
        await using var desk = await ParkedDesk.OpenAsync(backends, "abandoned_take_offered");
        await desk.TakenAsync(ago: TimeSpan.FromHours(1));

        var offered = await desk.LookAsync();

        var take = offered.Should().ContainSingle().Subject;
        take.SuspensionId.Should().Be(desk.SuspensionId);
        take.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        take.OutcomeJson.Should().Be(ParkedDesk.Outcome);
        take.TakeCount.Should().Be(1);
    }

    [Fact]
    public async Task A_take_younger_than_the_cutoff_is_its_takers_still()
    {
        await using var desk = await ParkedDesk.OpenAsync(backends, "abandoned_take_young");
        await desk.TakenAsync(ago: TimeSpan.FromSeconds(5));

        (await desk.LookAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_take_whose_run_has_ended_is_not_offered()
    {
        await using var desk = await ParkedDesk.OpenAsync(backends, "abandoned_take_ended");
        await desk.TakenAsync(ago: TimeSpan.FromHours(1));
        await desk.EndRunAsync();

        (await desk.LookAsync()).Should().BeEmpty(
            "re-entering an ended run would walk the rest of its graph after its ending");
    }

    [Fact]
    public async Task A_take_whose_run_has_parked_again_since_is_not_offered()
    {
        await using var desk = await ParkedDesk.OpenAsync(backends, "abandoned_take_reparked");
        await desk.TakenAsync(ago: TimeSpan.FromHours(1));
        await desk.ParkAgainLaterAsync();

        (await desk.LookAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_look_reads_the_takes_after_its_cursor_a_page_at_a_time_and_each_once()
    {
        await using var desk = await ParkedDesk.OpenAsync(backends, "abandoned_take_pages");
        await desk.TakenAsync(ago: TimeSpan.FromHours(2));
        var copies = await desk.AbandonedCopiesAsync(4);
        var order = new[] { desk.SuspensionId }.Concat(copies).ToList();

        var read = new List<Guid>();
        var cursor = WorkflowAbandonedTakeCursor.Start;
        for (var look = 0; look < 4; look++)
        {
            var scan = await desk.Store.ScanAbandonedTakesAsync(cursor, Cutoff, 2, CancellationToken.None);
            read.AddRange(scan.Candidates.Select(take => take.SuspensionId));
            cursor = scan.NextCursor;
        }

        read.Should().Equal(order, "in the order they were taken, and none of them twice");
    }

    [Fact]
    public async Task A_take_that_commits_after_a_look_has_read_past_its_stamp_is_read_by_the_next_while_it_trails_by_less_than_the_cutoff()
    {
        var cutoff = TimeSpan.FromSeconds(2);
        await using var desk = await ParkedDesk.OpenAsync(backends, "abandoned_take_late_commit");
        var first = await desk.Store.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, cutoff, 10, CancellationToken.None);
        first.Candidates.Should().BeEmpty();

        await desk.TakenAsync(ago: TimeSpan.FromSeconds(1.5));
        await Task.Delay(TimeSpan.FromSeconds(1));
        var next = await desk.Store.ScanAbandonedTakesAsync(first.NextCursor, cutoff, 10, CancellationToken.None);

        next.Candidates.Should().ContainSingle().Which.SuspensionId.Should().Be(desk.SuspensionId);
    }

    [Fact]
    public async Task A_look_from_its_cursor_does_not_read_through_every_wait_ever_taken()
    {
        await using var desk = await ParkedDesk.OpenAsync(backends, "abandoned_take_plan");
        await desk.TakenAsync(ago: TimeSpan.FromHours(1));
        await desk.EndedHistoryAsync(60_000);

        var commands = new QueryPlans.RecordingCommands();
        await using var container = QueryPlans.Container(desk.Postgres, commands);
        IWorkflowSuspensionResumeStore store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);
        var first = await store.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, Cutoff, 10, CancellationToken.None);
        first.Candidates.Should().ContainSingle().Which.SuspensionId.Should().Be(desk.SuspensionId);
        var sent = commands.Readers.Count;
        await store.ScanAbandonedTakesAsync(first.NextCursor, Cutoff, 10, CancellationToken.None);

        var look = commands.Readers.Skip(sent).Should().ContainSingle(command => command.Text.Contains("resolved_at_utc"),
            "the look is the one query that reads when a wait was taken").Subject;
        var plan = await QueryPlans.ExplainAsync(desk.Postgres, look);
        plan.Should().Contain("Index Scan using ix_workflow_suspensions_taken", plan)
            .And.MatchRegex(@"Index Cond: .*\(resolved_at_utc > ", "the look starts at its cursor rather than reading its way there");
        plan.Should().NotContain("Seq Scan on workflow_suspensions", plan);
    }

    [Fact]
    public async Task Two_replicas_taking_over_one_abandoned_take_at_once_move_it_once_and_it_is_not_offered_again_at_once()
    {
        await using var desk = await ParkedDesk.OpenAsync(backends, "abandoned_take_race");
        await desk.TakenAsync(ago: TimeSpan.FromHours(1));
        var offered = (await desk.LookAsync()).Single();

        var contexts = await Task.WhenAll(
            desk.Store.TryRetakeAsync(offered, CancellationToken.None),
            desk.Store.TryRetakeAsync(offered, CancellationToken.None));

        contexts.Where(context => context is not null).Should().ContainSingle()
            .Which!.OutcomeJson.Should().Be(ParkedDesk.Outcome);
        (await desk.LookAsync()).Should().BeEmpty(
            "the takeover re-stamps the row, so a run that is merely slow is not offered on every pass");
    }

    private sealed class ParkedDesk : IAsyncDisposable
    {
        public const string Outcome = """{"outcome":"satisfied"}""";

        private readonly ServiceProvider container;

        private ParkedDesk(ServiceProvider container, string postgres, Guid suspensionId, Guid executionLogId)
        {
            this.container = container;
            Postgres = postgres;
            SuspensionId = suspensionId;
            ExecutionLogId = executionLogId;
            Store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);
        }

        public string Postgres { get; }
        public Guid SuspensionId { get; }
        public Guid ExecutionLogId { get; }
        public IWorkflowSuspensionResumeStore Store { get; }

        public static async Task<ParkedDesk> OpenAsync(ClusterBackends backends, string database)
        {
            var postgres = await backends.CreateDatabaseAsync(database);
            await using var replica = await DispatchReplica.StartAsync(new DispatchReplicaSettings
            {
                ReplicaId = "desk-parking",
                PostgresConnectionString = postgres,
                RedisConnectionString = backends.RedisConnectionString
            });
            await FreightDesk.WarmAsync(postgres, replica);

            var workflowId = await FreightDesk.SeedHoldUntilAsync(
                postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
                shipment: "SHP-8000", DateTimeOffset.UtcNow.AddDays(1));
            await replica.ExecuteAsync(workflowId);
            var parked = await FreightDesk.EventuallyAsync(
                () => FreightDesk.SuspensionsAsync(postgres, workflowId),
                rows => rows.Count == 1,
                TimeSpan.FromSeconds(30),
                "the replica to park the run",
                replica);
            replica.Kill();

            var container = FreightDesk.Container(postgres);
            await using var scope = container.CreateAsyncScope();
            var logId = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowSuspensions
                .Where(row => row.Id == parked[0].Id)
                .Select(row => row.ExecutionLogId)
                .SingleAsync();
            return new ParkedDesk(container, postgres, parked[0].Id, logId);
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();

        public async Task TakenAsync(TimeSpan ago)
        {
            await using var scope = container.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var resolvedAt = DateTimeOffset.UtcNow - ago;
            (await db.WorkflowSuspensions.Where(row => row.Id == SuspensionId)
                .ExecuteUpdateAsync(row => row
                    .SetProperty(s => s.Status, WorkflowSuspensionStatus.Resumed)
                    .SetProperty(s => s.OutcomeJson, Outcome)
                    .SetProperty(s => s.ResolvedAtUtc, resolvedAt)
                    .SetProperty(s => s.TakeCount, 1))).Should().Be(1);
        }

        public async Task EndRunAsync()
        {
            await using var scope = container.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            (await db.WorkflowExecutionLogs.Where(log => log.Id == ExecutionLogId)
                .ExecuteUpdateAsync(log => log.SetProperty(l => l.Status, WorkflowExecutionStatus.Succeeded))).Should().Be(1);
        }

        public async Task ParkAgainLaterAsync()
        {
            await using var scope = container.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var first = await db.WorkflowSuspensions.AsNoTracking().SingleAsync(row => row.Id == SuspensionId);
            db.WorkflowSuspensions.Add(new WorkflowSuspension(
                Guid.NewGuid(), first.ExecutionLogId, first.WorkflowId, "a-later-wait", first.Kind, first.ConfigJson,
                first.StatePayloadJson, first.SuspendedAtUtc.AddMinutes(1), first.TimeoutAtUtc));
            await db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<WorkflowSuspensionCandidate>> LookAsync() =>
            (await Store.ScanAbandonedTakesAsync(WorkflowAbandonedTakeCursor.Start, Cutoff, 10, CancellationToken.None)).Candidates;

        public async Task<List<Guid>> AbandonedCopiesAsync(int count)
        {
            var copies = Enumerable.Range(1, count).Select(_ => (Log: Guid.NewGuid(), Take: Guid.NewGuid())).ToList();
            var values = string.Join(", ", copies.Select((copy, i) => $"('{copy.Log}'::uuid, '{copy.Take}'::uuid, {i + 1})"));
            await FreightDesk.ExecuteSqlAsync(Postgres, $"""
                CREATE TEMP TABLE copies (log_id uuid, suspension_id uuid, n int);
                INSERT INTO copies VALUES {values};
                INSERT INTO automation.workflow_execution_logs
                SELECT (jsonb_populate_record(NULL::automation.workflow_execution_logs,
                        to_jsonb(l) || jsonb_build_object('id', c.log_id))).*
                FROM automation.workflow_execution_logs l CROSS JOIN copies c WHERE l.id = '{ExecutionLogId}';
                INSERT INTO automation.workflow_suspensions
                SELECT (jsonb_populate_record(NULL::automation.workflow_suspensions,
                        to_jsonb(s) || jsonb_build_object('id', c.suspension_id, 'execution_log_id', c.log_id,
                            'resolved_at_utc', s.resolved_at_utc + c.n * interval '1 minute'))).*
                FROM automation.workflow_suspensions s CROSS JOIN copies c WHERE s.id = '{SuspensionId}';
                """);
            return copies.Select(copy => copy.Take).ToList();
        }

        public Task EndedHistoryAsync(int runs) => FreightDesk.ExecuteSqlAsync(Postgres, $"""
            CREATE TEMP TABLE history AS
                SELECT gen_random_uuid() AS log_id, gen_random_uuid() AS suspension_id FROM generate_series(1, {runs});
            INSERT INTO automation.workflow_execution_logs
            SELECT (jsonb_populate_record(NULL::automation.workflow_execution_logs,
                    to_jsonb(l) || jsonb_build_object('id', h.log_id, 'status', {(int)WorkflowExecutionStatus.Succeeded}))).*
            FROM automation.workflow_execution_logs l CROSS JOIN history h WHERE l.id = '{ExecutionLogId}';
            INSERT INTO automation.workflow_suspensions
            SELECT (jsonb_populate_record(NULL::automation.workflow_suspensions,
                    to_jsonb(s) || jsonb_build_object('id', h.suspension_id, 'execution_log_id', h.log_id,
                        'resolved_at_utc', now() - interval '2 hours'))).*
            FROM automation.workflow_suspensions s CROSS JOIN history h WHERE s.id = '{SuspensionId}';
            ANALYZE automation.workflow_execution_logs;
            ANALYZE automation.workflow_suspensions;
            ANALYZE automation.workflow_executions_state;
            """);
    }
}
