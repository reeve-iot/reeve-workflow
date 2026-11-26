using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class ParkedLoopCollectionTests : IDisposable
{
    private readonly SqliteConnection connection = new("Filename=:memory:");

    public ParkedLoopCollectionTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    [Theory]
    [InlineData(2.0, 1.0, "3")]
    [InlineData(1.5, 1.0, "2.5")]
    [InlineData(2.0, 1.0, "3", 10)]
    public async Task A_parked_loop_collects_one_element_per_pass(double a, double b, string element, int durationMs = 2_000)
    {
        var provider = Build();
        var workflowId = Seed(provider, Definition(a, b, durationMs));
        await provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null).WaitAsync(TimeSpan.FromSeconds(30));
        var waker = provider.GetRequiredService<WorkflowSuspensionWaker>();
        for (var pass = 0; pass < 3; pass++)
            await waker.WakeAndWalkAsync();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var journal = await db.WorkflowExecutionLogs.AsNoTracking().SingleAsync(log => log.WorkflowId == workflowId);
        var steps = await db.WorkflowExecutionSteps.AsNoTracking().Where(s => s.ExecutionLogId == journal.Id).ToListAsync();
        var loopSteps = steps.Where(s => s.NodeId == "loop").Select(s => $"{s.Status}:{s.OutputsJson}:{s.ErrorMessage}").ToList();
        var parks = await db.WorkflowSuspensions.AsNoTracking().CountAsync(row => row.WorkflowId == workflowId);
        var report = $"parks={parks} status={journal.Status} error={journal.ErrorMessage} loopSteps=[{string.Join(" | ", loopSteps)}]";

        journal.Status.Should().Be(WorkflowExecutionStatus.Succeeded, report);
        loopSteps.Should().Contain(s => s.Contains($"\"Sum\":[{element},{element}]"), report);
    }

    private static string Definition(double a, double b, int durationMs)
    {
        JsonObject Node(string id, string type, JsonObject? config = null) => new()
        {
            ["id"] = id, ["blockTypeId"] = type, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = config ?? new JsonObject(),
        };
        JsonObject Edge(string from, string to, JsonObject? mappings = null) => new()
        {
            ["id"] = $"{from}-{to}", ["sourceNodeId"] = from, ["targetNodeId"] = to, ["mappings"] = mappings ?? new JsonObject(),
        };

        var body = new JsonObject
        {
            ["nodes"] = new JsonArray(
                Node("in", "WorkflowInput"),
                Node("m", "Math", new JsonObject { ["A"] = a, ["B"] = b, ["Op"] = "Add" }),
                Node("wait", "WaitForDuration", new JsonObject { ["DurationMs"] = durationMs }),
                Node("out", "WorkflowOutput")),
            ["edges"] = new JsonArray(
                Edge("in", "m"),
                Edge("m", "wait"),
                Edge("m", "out", new JsonObject { ["s"] = new JsonObject { ["type"] = "direct", ["sourceOutputName"] = "Result" } })),
        };
        var loop = Node("loop", "ForLoop", new JsonObject { ["From"] = 0, ["To"] = 2 });
        loop["outputPorts"] = new JsonArray(new JsonObject
        {
            ["name"] = "Sum", ["displayName"] = "Sum", ["type"] = "Number", ["sources"] = new JsonObject { ["Body"] = "s" },
        });
        loop["subCanvases"] = new JsonObject { ["Body"] = body };
        return new JsonObject
        {
            ["nodes"] = new JsonArray(Node("start", "WorkflowInput"), loop),
            ["edges"] = new JsonArray(Edge("start", "loop")),
        }.ToJsonString();
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddStandardWorkflowBlocks();
        services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(context => context.UseSqlite(connection));
                options.Defaults.DurableSleepBlockTypeIds.Add("WaitForDuration");
            })
            .AddWorkflowEngine(options =>
            {
                options.DurableSleep.ParkWhenAtLeast = TimeSpan.FromSeconds(1);
                options.TraceMode = WorkflowTraceMode.Full;
            });
        services.AddSingleton<IWorkflowSuspensionResumeStore>(sp => new HourLaterScan(sp.GetRequiredService<IServiceScopeFactory>()));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        return provider;
    }

    private static Guid Seed(IServiceProvider provider, string definitionJson)
    {
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(Guid.NewGuid(), "parked-loop", null, owner, now);
        db.Workflows.Add(workflow);
        db.SaveChanges();
        var version = new WorkflowVersion(Guid.NewGuid(), workflow.Id, 1, definitionJson, null, null, owner, now, isPublished: true);
        db.WorkflowVersions.Add(version);
        db.SaveChanges();
        workflow.SetCurrentVersion(version, now);
        db.SaveChanges();
        return workflow.Id;
    }

    private sealed class HourLaterScan(IServiceScopeFactory scopes) : IWorkflowSuspensionResumeStore
    {
        private readonly EfWorkflowSuspensionResumeStore store = new(scopes, []);

        public async Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow.AddHours(1);
            using var scope = scopes.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowSuspensions
                .AsNoTracking().Where(row => row.Status == WorkflowSuspensionStatus.Suspended).ToListAsync(cancellationToken);
            return new WorkflowSuspensionScan(now, rows
                .Where(row => row.TimeoutAtUtc <= now)
                .Select(row => new WorkflowSuspensionCandidate(row.Id, row.ExecutionLogId, row.WorkflowId, row.BlockId,
                    row.Kind, row.ConfigJson, row.StatePayloadJson, row.SuspendedAtUtc, row.TimeoutAtUtc))
                .ToList(), 0);
        }

        public Task<ResumeContext?> TryResolveAsync(Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) =>
            store.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            store.ReleaseAsync(suspensionId, takenAs, cancellationToken);
    }
}
