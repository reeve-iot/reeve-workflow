using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class WakerPagePlanTests(ClusterBackends backends)
{
    private const string PageIndex = "ix_workflow_suspensions_parked_speculative";

    [Fact]
    public async Task A_page_sixty_thousand_parked_rows_deep_is_read_along_the_index_from_its_cursor()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_waker_page_plan");
        await SeedDeepAsync(postgres, "desk-plan", "SHP-7008");

        var (cursor, expected, kinds) = await DeepPageAsync(
            postgres, skip: 40_000, take: 50, SpeculativeKindNumbers);
        kinds.Should().Contain(WorkflowSuspensionKind.WaitTemplate).And.Contain(WorkflowSuspensionKind.SubWorkflow,
            "a page that read one kind only would say nothing about the other");

        var commands = new QueryPlans.RecordingCommands();
        await using var container = QueryPlans.Container(postgres, commands);
        IWorkflowSuspensionResumeStore store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);

        var scan = await store.ScanAsync(cursor, 50, default);

        scan.Candidates.Select(row => row.SuspensionId).Should().Equal(expected);
        var page = commands.Readers.Should().ContainSingle(command => System.Text.RegularExpressions.Regex.IsMatch(command.Text, @"ORDER BY \w+\.suspended_at_utc"),
            "the speculative page is the one query in order of parking").Subject;
        var plan = await QueryPlans.ExplainAsync(postgres, page);
        plan.Should().Contain($"Index Scan using {PageIndex}", plan)
            .And.Contain("Index Cond: (suspended_at_utc >=", "the page starts at its cursor rather than filtering its way there");
        plan.Should().NotContain("Seq Scan", plan).And.NotContain("Sort", plan);
    }

    [Fact]
    public async Task The_page_a_host_indexes_from_is_read_along_that_same_index()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_wake_index_plan");
        await SeedDeepAsync(postgres, "desk-wake-plan", "SHP-7009");

        var (cursor, expected, kinds) = await DeepPageAsync(
            postgres, skip: 40_000, take: 50, ParkedWaitKindNumbers);
        kinds.Should().Contain(WorkflowSuspensionKind.WaitForTrigger,
            "a page that read no trigger wait would say nothing about the kind this index was widened for");

        var commands = new QueryPlans.RecordingCommands();
        await using var container = QueryPlans.Container(postgres, commands);
        IWorkflowSuspensionResumeStore store = new EfWorkflowSuspensionResumeStore(container.GetRequiredService<IServiceScopeFactory>(), []);

        var scan = await store.ScanParkedWaitsAsync(cursor, 50, null, default);

        scan.Waits.Select(wait => wait.SuspensionId).Should().Equal(expected);
        var page = commands.Readers.Should().ContainSingle(command => System.Text.RegularExpressions.Regex.IsMatch(command.Text, @"ORDER BY \w+\.suspended_at_utc"),
            "the index page is the one query in order of parking").Subject;
        var plan = await QueryPlans.ExplainAsync(postgres, page);
        plan.Should().Contain($"Index Scan using {PageIndex}", plan)
            .And.Contain("Index Cond: (suspended_at_utc >=", "the page starts at its cursor rather than filtering its way there");
        plan.Should().NotContain("Seq Scan", plan).And.NotContain("Sort", plan);
    }


    private static readonly int[] SpeculativeKindNumbers =
        [(int)WorkflowSuspensionKind.WaitTemplate, (int)WorkflowSuspensionKind.SubWorkflow];

    private static readonly int[] ParkedWaitKindNumbers =
    [
        (int)WorkflowSuspensionKind.WaitForTrigger,
        (int)WorkflowSuspensionKind.WaitTemplate,
        (int)WorkflowSuspensionKind.SubWorkflow
    ];

    private async Task SeedDeepAsync(string postgres, string replicaId, string shipment)
    {
        await using (var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
                     {
                         ReplicaId = replicaId,
                         PostgresConnectionString = postgres,
                         RedisConnectionString = backends.RedisConnectionString
                     }))
        {
            await FreightDesk.WarmAsync(postgres, desk);
            var workflowId = await FreightDesk.SeedHoldUntilAsync(
                postgres, WorkflowMode.Parallel, maxConcurrentRuns: 5,
                shipment: shipment, DateTimeOffset.UtcNow.AddHours(6));
            await desk.ExecuteAsync(workflowId);
            _ = await FreightDesk.EventuallyAsync(
                () => FreightDesk.SuspensionsAsync(postgres, workflowId),
                rows => rows.Count == 1,
                TimeSpan.FromSeconds(30),
                "the hold to be parked",
                desk);
        }

        await FreightDesk.ExecuteSqlAsync(postgres, $"""
            INSERT INTO automation.workflow_suspensions
                (id, execution_log_id, workflow_id, block_id, kind, status, config_json, state_payload_json,
                 suspended_at_utc, timeout_at_utc, take_count)
            SELECT gen_random_uuid(), s.execution_log_id, s.workflow_id, 'hold-' || g,
                   CASE WHEN g % 10 = 0 THEN {(int)WorkflowSuspensionKind.SubWorkflow}
                        WHEN g % 7 = 0 THEN {(int)WorkflowSuspensionKind.WaitForTrigger}
                        ELSE s.kind END, s.status,
                   s.config_json, s.state_payload_json,
                   now() - interval '2 hours' + (g / 3) * interval '1 millisecond', now() + interval '1 day', 0
            FROM automation.workflow_suspensions s CROSS JOIN generate_series(1, 60000) g;
            ANALYZE automation.workflow_suspensions;
            """);
    }

    private static async Task<(WorkflowSuspensionScanCursor Cursor, List<Guid> Expected, List<WorkflowSuspensionKind> Kinds)>
        DeepPageAsync(string postgres, int skip, int take, int[] kindNumbers)
    {
        await using var connection = new NpgsqlConnection(postgres);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT id, suspended_at_utc, kind FROM automation.workflow_suspensions
            WHERE status = 'Suspended' AND kind IN ({string.Join(", ", kindNumbers)})
            ORDER BY suspended_at_utc, id OFFSET {skip - 1} LIMIT {take + 1}
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(Guid Id, DateTimeOffset At, WorkflowSuspensionKind Kind)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), (WorkflowSuspensionKind)reader.GetInt32(2)));

        var last = rows[0];
        return (new WorkflowSuspensionScanCursor(skip) { AfterSuspendedAtUtc = last.At, AfterSuspensionId = last.Id },
            rows.Skip(1).Select(row => row.Id).ToList(),
            rows.Skip(1).Select(row => row.Kind).ToList());
    }
}
