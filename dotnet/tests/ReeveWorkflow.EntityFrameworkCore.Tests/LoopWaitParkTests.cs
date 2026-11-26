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
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class LoopWaitParkTests : IDisposable
{
    private const string Hold = "test.hold";

    private readonly SqliteConnection connection = new("Filename=:memory:");

    public LoopWaitParkTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    public static TheoryData<string, int, int, bool, WorkflowSuspensionKind> Cases()
    {
        var data = new TheoryData<string, int, int, bool, WorkflowSuspensionKind>();
        foreach (var loop in LoopWaitGraphs.Loops)
            foreach (var depth in new[] { 0, 1, 2 })
                foreach (var passes in new[] { 1, 2, 3 })
                    foreach (var anotherReplica in new[] { false, true })
                        foreach (var kind in new[] { WorkflowSuspensionKind.Sleep, WorkflowSuspensionKind.WaitTemplate })
                            data.Add(loop, depth, passes, anotherReplica, kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_wait_inside_a_loop_parks_once_per_pass_and_the_run_finishes(
        string loop, int depth, int passes, bool anotherReplica, WorkflowSuspensionKind kind)
    {
        var parking = Build();
        var workflowId = Seed(parking, LoopWaitGraphs.Definition(loop, depth, passes, WaitNode(kind)));
        await parking.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null).WaitAsync(TimeSpan.FromSeconds(30));

        var waking = parking;
        if (anotherReplica)
        {
            await parking.DisposeAsync();
            waking = Build();
        }

        try
        {
            await WalkEveryParkAsync(waking, workflowId, passes, kind);
        }
        finally
        {
            await waking.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("start")]
    public async Task Two_waits_that_share_a_node_id_in_two_canvases_of_one_run_park_each_in_turn(string secondAfter)
    {
        var wait = (JsonObject)WaitNode(WorkflowSuspensionKind.WaitTemplate).DeepClone();
        wait["id"] = LoopWaitGraphs.WaitNodeId;
        JsonObject Branch(string id) => new()
        {
            ["id"] = id, ["blockTypeId"] = "IfElse", ["configuration"] = new JsonObject { ["Condition"] = true },
            ["subCanvases"] = new JsonObject
            {
                ["Then"] = new JsonObject
                {
                    ["nodes"] = new JsonArray(new JsonObject { ["id"] = "in", ["blockTypeId"] = "WorkflowInput", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() }, wait.DeepClone()),
                    ["edges"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "b", ["sourceNodeId"] = "in", ["targetNodeId"] = LoopWaitGraphs.WaitNodeId, ["mappings"] = new JsonObject()
                    })
                }
            }, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }
        };
        var definition = new JsonObject
        {
            ["nodes"] = new JsonArray(new JsonObject { ["id"] = "start", ["blockTypeId"] = "WorkflowInput", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() }, Branch("a"), Branch("b")),
            ["edges"] = new JsonArray(
                new JsonObject { ["id"] = "e1", ["sourceNodeId"] = "start", ["targetNodeId"] = "a", ["mappings"] = new JsonObject() },
                new JsonObject { ["id"] = "e2", ["sourceNodeId"] = secondAfter, ["targetNodeId"] = "b", ["mappings"] = new JsonObject() })
        }.ToJsonString();

        await using var desk = Build();
        var workflowId = Seed(desk, definition);
        await desk.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null).WaitAsync(TimeSpan.FromSeconds(30));

        await WalkEveryParkAsync(desk, workflowId, passes: 2, WorkflowSuspensionKind.WaitTemplate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_late_resume_of_a_pass_whose_run_parked_at_the_same_wait_again_is_refused(int depth)
    {
        await using var desk = Build();
        var workflowId = Seed(desk, LoopWaitGraphs.Definition("ForLoop", depth, passes: 2, WaitNode(WorkflowSuspensionKind.WaitTemplate)));
        await desk.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null).WaitAsync(TimeSpan.FromSeconds(30));
        var resumer = desk.GetRequiredService<WorkflowRunResumer>();
        var first = (await desk.GetRequiredService<IWorkflowSuspensionResumeStore>().TryResolveAsync(
            (await RowsAsync(desk, workflowId)).Single().Id, WorkflowSuspensionStatus.Resumed, """{"outcome":"satisfied"}""",
            DateTimeOffset.UtcNow, default))!;
        (await resumer.ResumeFromSuspensionAsync(first, default)).Should().Be(WorkflowResumeOutcome.Resumed);
        var second = (await RowsAsync(desk, workflowId)).Should()
            .ContainSingle(row => row.Status == WorkflowSuspensionStatus.Suspended, "the second pass parked at the wait").Subject;

        (await resumer.ResumeFromSuspensionAsync(first, default)).Should().Be(WorkflowResumeOutcome.HeldElsewhere);

        (await RowsAsync(desk, workflowId)).Single(row => row.Id == second.Id).Status
            .Should().Be(WorkflowSuspensionStatus.Suspended, "the late resume walked nothing");
        (await desk.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync()).Should().Be(1);
        using var scope = desk.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        (await db.WorkflowExecutionLogs.AsNoTracking().SingleAsync(log => log.WorkflowId == workflowId))
            .Status.Should().Be(WorkflowExecutionStatus.Succeeded, "the wait the run is parked on is still its to resume");
    }

    [Fact]
    public async Task The_EF_waker_uses_the_registered_namespace_through_its_scoped_adapter()
    {
        var factoryCalls = 0;
        var scopes = new System.Collections.Concurrent.ConcurrentQueue<ExpressionNamespaceScope>();
        await using var desk = Build(registerHostNamespace: true, scanUnexpiredWaits: true, registerTriggerNamespace: true, factory: (scope, _) =>
        {
            scope.Services.GetRequiredService<WorkflowDbContext>();
            Interlocked.Increment(ref factoryCalls);
            scopes.Enqueue(scope);
            return Task.FromResult<IExpressionNamespaceResolver>(new ReadyNamespace());
        });
        var workflowId = Seed(desk, new JsonObject
        {
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "start", ["blockTypeId"] = "WorkflowInput", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() },
                new JsonObject { ["id"] = "wait", ["blockTypeId"] = Hold, ["configuration"] = new JsonObject { ["Condition"] = "expr:${host.ready} == true" }, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 } }),
            ["edges"] = new JsonArray(new JsonObject
            {
                ["id"] = "e1", ["sourceNodeId"] = "start", ["targetNodeId"] = "wait", ["mappings"] = new JsonObject()
            })
        }.ToJsonString());
        var triggerId = Guid.NewGuid();
        var firedAt = DateTimeOffset.UtcNow;
        await desk.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId,
            new WorkflowFire { TriggerId = triggerId, FiredAtUtc = firedAt, Payload = new Dictionary<string, object?> { ["sample"] = "envelope" } },
            null).WaitAsync(TimeSpan.FromSeconds(30));

        (await desk.GetRequiredService<WorkflowSuspensionWaker>().WakeAndWalkAsync()).Should().Be(1);

        factoryCalls.Should().Be(3,
            "the initial run walk, parked-row condition check, and resumed run walk each resolve one scoped snapshot");
        scopes.Should().HaveCount(3);
        var parkedScope = scopes.ToArray()[1];
        parkedScope.TriggerId.Should().Be(triggerId);
        parkedScope.TriggerEnvelope.Should().NotBeNull();
        parkedScope.TriggerEnvelope!.FiredAtUtc.Should().Be(firedAt);
        parkedScope.TriggerEnvelope.Payload["sample"].ToString().Should().Be("envelope");
    }

    private static async Task WalkEveryParkAsync(
        ServiceProvider waking, Guid workflowId, int passes, WorkflowSuspensionKind kind)
    {
        var waker = waking.GetRequiredService<WorkflowSuspensionWaker>();
        for (var pass = 1; pass <= passes; pass++)
        {
            var parked = await RowsAsync(waking, workflowId);
            parked.Should().HaveCount(pass, $"pass {pass} parked a row of its own").And
                .ContainSingle(row => row.Status == WorkflowSuspensionStatus.Suspended, $"pass {pass} is the one waiting");
            (await waker.WakeAndWalkAsync()).Should().Be(1, $"pass {pass}'s park is taken");
        }

        (await waker.WakeAndWalkAsync()).Should().Be(0, "nothing is left parked");
        using var scope = waking.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var journal = await db.WorkflowExecutionLogs.AsNoTracking().SingleAsync(log => log.WorkflowId == workflowId);
        journal.Status.Should().Be(WorkflowExecutionStatus.Succeeded, journal.ErrorMessage);
        (await db.WorkflowExecutionStates.AsNoTracking().SingleAsync(state => state.WorkflowId == workflowId))
            .Status.Should().Be(nameof(WorkflowRunStateStatus.Completed));

        var rows = await RowsAsync(waking, workflowId);
        rows.Should().HaveCount(passes, "one park per pass, and no other");
        rows.Select(row => row.Id).Should().OnlyHaveUniqueItems();
        rows.Should().OnlyContain(row => row.BlockId == LoopWaitGraphs.WaitNodeId && row.Kind == kind
            && row.ExecutionLogId == journal.Id
            && (row.Status == WorkflowSuspensionStatus.Resumed || row.Status == WorkflowSuspensionStatus.TimedOut));
    }

    private static JsonObject WaitNode(WorkflowSuspensionKind kind) => kind switch
    {
        WorkflowSuspensionKind.Sleep => new JsonObject
        {
            ["blockTypeId"] = "WaitForDuration", ["configuration"] = new JsonObject { ["DurationMs"] = 2_000 }, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }
        },
        WorkflowSuspensionKind.WaitTemplate => new JsonObject
        {
            ["blockTypeId"] = Hold, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject { ["Condition"] = "expr:false" }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private ServiceProvider Build(bool registerHostNamespace = false, bool scanUnexpiredWaits = false, bool registerTriggerNamespace = false,
        Func<ExpressionNamespaceScope, CancellationToken, Task<IExpressionNamespaceResolver>>? factory = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory, HoldFactory>();
        services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(context => context.UseSqlite(connection));
                options.Defaults.DurableSleepBlockTypeIds.Add("WaitForDuration");
                if (registerHostNamespace)
                    options.Defaults.ExpressionNamespaces.Add(new WorkflowExpressionNamespace("host", 1,
                        new ReadyCheck(), factory ?? ((_, _) => Task.FromResult<IExpressionNamespaceResolver>(new ReadyNamespace()))));
                if (registerTriggerNamespace)
                    options.Defaults.ExpressionNamespaces.Add(new WorkflowExpressionNamespace("trigger", 1,
                        new TriggerCheck(), (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new TriggerNamespace())));
            })
            .AddWorkflowEngine(options => options.DurableSleep.ParkWhenAtLeast = TimeSpan.FromSeconds(1));
        services.AddSingleton<IWorkflowSuspensionResumeStore>(sp => new HourLaterScan(
            sp.GetRequiredService<IServiceScopeFactory>(), scanUnexpiredWaits));

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
        var workflow = new Workflow(Guid.NewGuid(), "loop-wait", null, owner, now);
        db.Workflows.Add(workflow);
        db.SaveChanges();
        var version = new WorkflowVersion(Guid.NewGuid(), workflow.Id, 1, definitionJson, null, null, owner, now, isPublished: true);
        db.WorkflowVersions.Add(version);
        db.SaveChanges();
        workflow.SetCurrentVersion(version, now);
        db.SaveChanges();
        return workflow.Id;
    }

    private static async Task<List<WorkflowSuspension>> RowsAsync(IServiceProvider provider, Guid workflowId)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowSuspensions
            .AsNoTracking().Where(row => row.WorkflowId == workflowId).ToListAsync();
    }

    private sealed class HoldFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => Hold;
        public string DisplayName => Hold;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
            [new BlockPortDefinition("Condition", "Condition", PortValueType.OfDeclaration("Expression<Boolean>"), false)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool IsIdempotent => true;
        public IWorkflowBlockPlugin Create() => new HoldBlock();
    }

    private sealed class HoldBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitTemplate,
                JsonSerializer.Serialize(new WorkflowWaitTemplateConfig { Input = "Condition" }),
                DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    private sealed class ReadyCheck : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "host";
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "Boolean");
    }

    private sealed class TriggerCheck : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "trigger";
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "Json?");
    }

    private sealed class TriggerNamespace : IExpressionNamespaceResolver
    {
        public string Namespace => "trigger";
        public ResolveResult Resolve(string[] segments) => ResolveResult.Failed();
    }

    private sealed class ReadyNamespace : IExpressionNamespaceResolver
    {
        public string Namespace => "host";
        public ResolveResult Resolve(string[] segments) => segments is ["ready"]
            ? ResolveResult.Resolved(true)
            : ResolveResult.Failed();
    }

    private sealed class HourLaterScan(IServiceScopeFactory scopes, bool scanUnexpiredWaits = false) : IWorkflowSuspensionResumeStore
    {
        private readonly EfWorkflowSuspensionResumeStore store = new(scopes, []);

        public async Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken)
        {
            var now = scanUnexpiredWaits ? DateTimeOffset.UtcNow.AddMinutes(1) : DateTimeOffset.UtcNow.AddHours(1);
            using var scope = scopes.CreateScope();
            var rows = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowSuspensions
                .AsNoTracking().Where(row => row.Status == WorkflowSuspensionStatus.Suspended).ToListAsync(cancellationToken);
            return new WorkflowSuspensionScan(now, rows
                .Where(row => scanUnexpiredWaits || row.TimeoutAtUtc <= now)
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
