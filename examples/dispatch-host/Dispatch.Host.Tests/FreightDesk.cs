using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Dispatch.Host.Freight;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

public static class FreightDesk
{
    public static readonly Guid Operator = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public const string WorkNodeId = "work";

    public static Task<Guid> SeedAssignCarrierAsync(
        string connectionString, WorkflowMode mode, int maxConcurrentRuns,
        string shipment, string carrier, double dwellSeconds) =>
        SeedAsync(connectionString, mode, maxConcurrentRuns, $"Dispatch {shipment}",
            blockTypeId: "dispatch.assign-carrier",
            outputName: "waybill",
            configJson: $$"""
                "shipment": "{{shipment}}",
                "carrier": "{{carrier}}",
                "dwellSeconds": {{Number(dwellSeconds)}}
                """);

    public static Task<Guid> SeedAssignCarrierChainAsync(string connectionString, string shipment, int blocks)
    {
        var nodes = Enumerable.Range(1, blocks).Select(i => $$"""
            { "id": "assign-{{i}}", "blockTypeId": "dispatch.assign-carrier", "position": { "x": {{i * 240}}, "y": 0 },
              "configuration": { "shipment": "{{shipment}}", "carrier": "carrier-{{i}}", "dwellSeconds": 0 } }
            """);
        var edges = Enumerable.Range(1, blocks).Select(i => $$"""
            { "id": "edge-{{i}}", "sourceNodeId": "{{(i == 1 ? "start" : $"assign-{i - 1}")}}", "targetNodeId": "assign-{{i}}", "mappings": {} }
            """);
        var definitionJson = $$"""
            {
              "nodes": [ { "id": "start", "blockTypeId": "WorkflowInput", "position": { "x": 0, "y": 0 }, "configuration": {} }, {{string.Join(", ", nodes)}} ],
              "edges": [ {{string.Join(", ", edges)}} ]
            }
            """;
        return SeedAsync(connectionString, WorkflowMode.Parallel, 5, $"Chain {shipment}", definitionJson);
    }

    public static Task<Guid> SeedPrintLabelAsync(
        string connectionString, WorkflowMode mode, int maxConcurrentRuns,
        string shipment, double dwellSeconds) =>
        SeedAsync(connectionString, mode, maxConcurrentRuns, $"Label {shipment}",
            blockTypeId: "dispatch.print-label",
            outputName: "labelId",
            configJson: $$"""
                "shipment": "{{shipment}}",
                "dwellSeconds": {{Number(dwellSeconds)}}
                """);

    public static Task<Guid> SeedHoldUntilAsync(
        string connectionString, WorkflowMode mode, int maxConcurrentRuns,
        string shipment, DateTimeOffset untilUtc) =>
        SeedAsync(connectionString, mode, maxConcurrentRuns, $"Hold {shipment}",
            blockTypeId: HoldUntilBlockFactory.TypeId,
            outputName: "outcome",
            configJson: $$"""
                "shipment": "{{shipment}}",
                "untilUtc": "{{untilUtc.ToString("O", CultureInfo.InvariantCulture)}}",
                "released": "{{ReleasedWhen(untilUtc)}}"
                """);

    public static string ReleasedWhen(DateTimeOffset untilUtc) =>
        $"expr:${{now.epoch}} >= {untilUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";

    public static Task<Guid> SeedAwaitClearanceAsync(
        string connectionString, WorkflowMode mode, int maxConcurrentRuns, string shipment) =>
        SeedAsync(connectionString, mode, maxConcurrentRuns, $"Clearance {shipment}",
            blockTypeId: AwaitClearanceBlockFactory.TypeId,
            outputName: "outcome",
            configJson: $$"""
                "shipment": "{{shipment}}",
                "cleared": "expr:${depot.clearance.{{ClearanceDesk.Slug(shipment)}}}"
                """);

    public static async Task<IReadOnlyList<ParkedRun>> SuspensionsAsync(string connectionString, Guid workflowId)
    {
        await using var db = Open(connectionString);
        return await db.WorkflowSuspensions.AsNoTracking()
            .Where(row => row.WorkflowId == workflowId)
            .OrderBy(row => row.SuspendedAtUtc)
            .Select(row => new ParkedRun(row.Id, row.BlockId, row.Status, row.OutcomeJson))
            .ToListAsync();
    }

    public static async Task<IReadOnlyList<RunState>> RunStatesAsync(string connectionString, Guid workflowId)
    {
        await using var db = Open(connectionString);
        return await db.WorkflowExecutionStates.AsNoTracking()
            .Where(state => state.WorkflowId == workflowId)
            .OrderBy(state => state.StartedAtUtc)
            .Select(state => new RunState(
                state.ExecutionId, state.Status, state.OwnedByReplicaId, state.CurrentBlockPath, state.FailureReason))
            .ToListAsync();
    }

    public static async Task<(long Issued, long Started)> RestartGenerationsAsync(string connectionString, Guid workflowId)
    {
        await using var db = Open(connectionString);
        var row = await db.WorkflowRestartGenerations.AsNoTracking()
            .Where(generation => generation.WorkflowId == workflowId)
            .Select(generation => new { generation.Issued, generation.Started })
            .FirstOrDefaultAsync();
        return (row?.Issued ?? 0, row?.Started ?? 0);
    }

    public static async Task<IReadOnlyList<RunJournalEntry>> JournalAsync(string connectionString, Guid workflowId)
    {
        await using var db = Open(connectionString);
        return await db.WorkflowExecutionLogs.AsNoTracking()
            .Where(log => log.WorkflowId == workflowId)
            .OrderBy(log => log.StartedAtUtc)
            .Select(log => new RunJournalEntry(log.Id, log.Status, log.StartedAtUtc) { ErrorMessage = log.ErrorMessage, CompletedAtUtc = log.CompletedAtUtc })
            .ToListAsync();
    }

    public static async Task<T> EventuallyAsync<T>(
        Func<Task<T>> read, Func<T, bool> condition, TimeSpan within, string expectation,
        params DispatchReplica[] mustSurvive)
    {
        var deadline = DateTimeOffset.UtcNow + within;
        T last = await read();
        while (!condition(last))
        {
            foreach (var replica in mustSurvive.Where(replica => replica.HasExited))
            {
                throw new InvalidOperationException(
                    $"Replica '{replica.Id}' died while waiting for {expectation}." +
                    $"{Environment.NewLine}{replica.Console}");
            }

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Waited {within.TotalSeconds:0.#}s for {expectation}; last saw {Describe(last)}.");

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            last = await read();
        }

        return last;
    }

    public static async Task WarmAsync(string connectionString, params DispatchReplica[] replicas)
    {
        var workflowId = await SeedAssignCarrierAsync(
            connectionString, WorkflowMode.Parallel, maxConcurrentRuns: 100,
            shipment: "SHP-0000", carrier: "Warmup", dwellSeconds: 0);

        foreach (var replica in replicas)
            await replica.ExecuteAsync(workflowId);

        await RunStatesAsync(connectionString, workflowId);
        await JournalAsync(connectionString, workflowId);
    }

    private static Task<Guid> SeedAsync(
        string connectionString, WorkflowMode mode, int maxConcurrentRuns,
        string name, string blockTypeId, string outputName, string configJson) =>
        SeedAsync(connectionString, mode, maxConcurrentRuns, name, DefinitionJson(blockTypeId, outputName, configJson),
            OutputParametersJson(outputName));

    public static Task<Guid> SeedDefinitionAsync(string connectionString, string name, string definitionJson) =>
        SeedAsync(connectionString, WorkflowMode.Parallel, 5, name, CompleteAuthoredDefinition(definitionJson));

    public static Task<Guid> SeedDefinitionAsync(
        string connectionString, WorkflowMode mode, string name, string definitionJson) =>
        SeedAsync(connectionString, mode, 5, name, CompleteAuthoredDefinition(definitionJson));

    public static Task<Guid> SeedDefinitionAsync(
        string connectionString, WorkflowMode mode, string name, string definitionJson, string outputParametersJson) =>
        SeedAsync(connectionString, mode, 5, name, CompleteAuthoredDefinition(definitionJson), outputParametersJson);

    private static async Task<Guid> SeedAsync(
        string connectionString, WorkflowMode mode, int maxConcurrentRuns, string name, string definitionJson,
        string? outputParametersJson = null)
    {
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var db = Open(connectionString);

        var workflow = new Workflow(
            workflowId, name, description: null,
            createdByUserId: Operator, createdAtUtc: now);
        workflow.ConfigureConcurrency(mode, maxConcurrentRuns, maxQueuedRuns: 10, WorkflowOverflowBehavior.Drop, now);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var version = new WorkflowVersion(
            versionId, workflowId, versionNumber: 1, definitionJson,
            inputParametersJson: null, outputParametersJson: outputParametersJson,
            createdByUserId: Operator, createdAtUtc: now, changeMessage: null, isPublished: true, publishedAtUtc: now);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();

        workflow.SetCurrentVersion(version, now);
        await db.SaveChangesAsync();

        return workflowId;
    }

    private static string CompleteAuthoredDefinition(string text)
    {
        var definition = JsonNode.Parse(text);
        CompleteCanvas(definition, 0);
        return definition?.ToJsonString() ?? text;
    }

    private static void CompleteCanvas(JsonNode? value, int depth)
    {
        if (value is not JsonObject canvas)
            return;
        if (canvas["nodes"] is not JsonArray nodes)
            return;

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is not JsonObject node)
                continue;
            if (!node.ContainsKey("position"))
                node["position"] = new JsonObject { ["x"] = i * 240, ["y"] = depth * 160 };
            if (!node.ContainsKey("configuration"))
                node["configuration"] = new JsonObject();
            if (node["subCanvases"] is JsonObject branches)
                foreach (var branch in branches)
                    CompleteCanvas(branch.Value, depth + 1);
        }
    }

    private static string Describe<T>(T value) => value switch
    {
        System.Collections.IEnumerable items and not string =>
            "[" + string.Join(", ", items.Cast<object?>().Select(item => item?.ToString())) + "]",
        _ => value?.ToString() ?? "null"
    };

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static async Task ExecuteSqlAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync();
    }

    private static WorkflowDbContext Open(string connectionString) =>
        new(new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(connectionString).Options);

    public static ServiceProvider Container(string connectionString) =>
        new ServiceCollection()
            .AddDbContext<WorkflowDbContext>(db => db.UseNpgsql(connectionString))
            .BuildServiceProvider();

    public static string AssignCarrierDefinition(string shipment, string carrier, double dwellSeconds) =>
        DefinitionJson("dispatch.assign-carrier", "waybill", $$"""
            "shipment": "{{shipment}}",
            "carrier": "{{carrier}}",
            "dwellSeconds": {{Number(dwellSeconds)}}
            """);

    public static string AssignCarrierOutputParametersJson { get; } = OutputParametersJson("waybill");

    private static string OutputParametersJson(string outputName) =>
        $$"""[{ "name": "{{outputName}}", "type": "String", "isRequired": false }]""";

    private static string DefinitionJson(string blockTypeId, string outputName, string configJson) =>
        $$"""
        {
          "nodes": [
            { "id": "start", "blockTypeId": "WorkflowInput", "position": { "x": 0, "y": 0 }, "configuration": {} },
            {
              "id": "{{WorkNodeId}}",
              "blockTypeId": "{{blockTypeId}}",
              "position": { "x": 240, "y": 0 },
              "configuration": { {{configJson}} }
            },
            { "id": "end", "blockTypeId": "WorkflowOutput", "position": { "x": 480, "y": 0 }, "configuration": {} }
          ],
          "edges": [
            { "id": "start-to-work", "sourceNodeId": "start", "targetNodeId": "{{WorkNodeId}}", "mappings": {} },
            {
              "id": "work-to-end", "sourceNodeId": "{{WorkNodeId}}", "targetNodeId": "end",
              "mappings": { "{{outputName}}": { "type": "direct", "sourceOutputName": "{{outputName}}" } }
            }
          ]
        }
        """;
}

public sealed record ParkedRun(Guid Id, string BlockId, WorkflowSuspensionStatus Status, string? OutcomeJson);

public sealed record RunState(
    Guid ExecutionId, string Status, string? OwnedByReplicaId, string CurrentBlockPath, string? FailureReason);

public sealed record RunJournalEntry(Guid ExecutionId, WorkflowExecutionStatus Status, DateTimeOffset StartedAtUtc)
{
    public string? ErrorMessage { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }
}
