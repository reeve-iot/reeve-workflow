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

public sealed class LostParkReentryTests : IDisposable
{
    private const string Hold = "test.hold";
    private const string Node = "wait";

    private readonly SqliteConnection connection = new("Filename=:memory:");

    public LostParkReentryTests() => connection.Open();

    public void Dispose() => connection.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_lost_park_is_superseded_when_recovery_re_enters_the_run_at_its_wait(bool inLoop)
    {
        var states = new FlakyStates();
        await using var desk = Build(states, new FlakySuspensions());
        var workflowId = Seed(desk, inLoop ? LoopDefinition() : RootDefinition());

        states.FailNextPark = true;
        await desk.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null).WaitAsync(TimeSpan.FromSeconds(30));
        var lost = (await RowsAsync(desk, workflowId)).Should().ContainSingle().Subject;
        (await StateAsync(desk, workflowId)).Status.Should().Be(nameof(WorkflowRunStateStatus.Running), "the park's state write threw");

        (await desk.GetRequiredService<WorkflowRunRecoverySweep>().SweepAsync(default)).Should().Be(1);

        var rows = await RowsAsync(desk, workflowId);
        rows.Should().HaveCount(2).And.Contain(row => row.Id == lost.Id && row.Status == WorkflowSuspensionStatus.Cancelled);
        var parked = rows.Should().ContainSingle(row => row.Status == WorkflowSuspensionStatus.Suspended,
            "the run is parked on the wait its re-entry reached, and on no other").Subject;
        parked.BlockId.Should().Be(Node);
        var byRun = await desk.GetRequiredService<IWorkflowSuspensionResumeStore>()
            .GetParkedAsync(null, lost.ExecutionLogId, default);
        byRun!.Row!.SuspensionId.Should().Be(parked.Id, "a wake that names only the run finds the one parked wait");

        await FinishAsync(desk, workflowId);
        (await RowsAsync(desk, workflowId)).Select(row => (row.Id, row.Status)).Should().Equal(
            (lost.Id, WorkflowSuspensionStatus.Cancelled), (parked.Id, WorkflowSuspensionStatus.TimedOut));
    }

    [Fact]
    public async Task A_re_entry_whose_supersession_cannot_be_written_still_parks_the_run()
    {
        var states = new FlakyStates();
        var suspensions = new FlakySuspensions();
        await using var desk = Build(states, suspensions);
        var workflowId = Seed(desk, LoopDefinition());

        states.FailNextPark = true;
        await desk.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null).WaitAsync(TimeSpan.FromSeconds(30));
        suspensions.FailNextCancel = true;
        (await desk.GetRequiredService<WorkflowRunRecoverySweep>().SweepAsync(default)).Should().Be(1);

        suspensions.FailNextCancel.Should().BeFalse("the re-entry asked to supersede the lost park");
        (await RowsAsync(desk, workflowId)).Should().HaveCount(2)
            .And.OnlyContain(row => row.Status == WorkflowSuspensionStatus.Suspended && row.BlockId == Node);
        var state = await StateAsync(desk, workflowId);
        (state.Status, state.CurrentBlockPath).Should().Be((nameof(WorkflowRunStateStatus.Suspended), Node));
        using var scope = desk.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionLogs.AsNoTracking()
            .SingleAsync(log => log.WorkflowId == workflowId)).Status.Should().Be(WorkflowExecutionStatus.Running);
    }

    private static async Task FinishAsync(ServiceProvider desk, Guid workflowId)
    {
        var waker = desk.GetRequiredService<WorkflowSuspensionWaker>();
        for (var pass = 0; pass < 3; pass++)
        {
            if (await waker.WakeAndWalkAsync() == 0)
                break;
        }

        using var scope = desk.CreateScope();
        var journal = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionLogs.AsNoTracking()
            .SingleAsync(log => log.WorkflowId == workflowId);
        journal.Status.Should().Be(WorkflowExecutionStatus.Succeeded, journal.ErrorMessage);
        (await StateAsync(desk, workflowId)).Status.Should().Be(nameof(WorkflowRunStateStatus.Completed));
    }

    private static string RootDefinition() => Canvas(WaitNode()).ToJsonString();

    private static string LoopDefinition() => Canvas(new JsonObject
    {
        ["id"] = "loop", ["blockTypeId"] = "ForLoop", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
        ["configuration"] = new JsonObject { ["From"] = 0, ["To"] = 1 },
        ["subCanvases"] = new JsonObject { ["Body"] = Canvas(WaitNode()) }
    }).ToJsonString();

    private static JsonObject Placed(JsonObject node)
    {
        node["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 };
        node["configuration"] ??= new JsonObject();
        return node;
    }

    private static JsonObject WaitNode() => Placed(new() { ["id"] = Node, ["blockTypeId"] = Hold, ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
        ["configuration"] = new JsonObject { ["Condition"] = "expr:false" } });

    private static JsonObject Canvas(JsonObject node) => new()
    {
        ["nodes"] = new JsonArray(Placed(new JsonObject { ["id"] = "in", ["blockTypeId"] = "WorkflowInput", ["position"] = new JsonObject { ["x"] = 0, ["y"] = 0 }, ["configuration"] = new JsonObject() }), node),
        ["edges"] = new JsonArray(new JsonObject
        {
            ["id"] = "e", ["sourceNodeId"] = "in", ["targetNodeId"] = node["id"]!.GetValue<string>(), ["mappings"] = new JsonObject()
        })
    };

    private ServiceProvider Build(FlakyStates states, FlakySuspensions suspensions)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddStandardWorkflowBlocks();
        services.AddSingleton<IWorkflowBlockFactory, HoldFactory>();
        services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(context => context.UseSqlite(connection));
                options.Defaults.ReplicaId = "replica-1";
                options.Seams.RunStateStore.Use(sp =>
                {
                    states.Inner = new EfWorkflowRunStateStore(sp.GetRequiredService<IServiceScopeFactory>());
                    return states;
                });
                options.Seams.SuspensionStore.Use(sp =>
                {
                    suspensions.Inner = new EfWorkflowSuspensionStore(sp.GetRequiredService<IServiceScopeFactory>(), []);
                    return suspensions;
                });
            })
            .AddWorkflowEngine(options => options.Recovery.Enabled = true);
        services.AddSingleton<IWorkflowSuspensionResumeStore>(sp => new HourLaterScan(sp.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<IWorkflowRunRecoveryStore>(sp => new ClaimEveryRunning(
            sp.GetRequiredService<IWorkflowRunStateStore>(), sp.GetRequiredService<IServiceScopeFactory>()));

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
        var workflow = new Workflow(Guid.NewGuid(), "lost-park", null, owner, now);
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
        var rows = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowSuspensions
            .AsNoTracking().Where(row => row.WorkflowId == workflowId).ToListAsync();
        return [.. rows.OrderBy(row => row.SuspendedAtUtc)];
    }

    private static async Task<WorkflowExecutionState> StateAsync(IServiceProvider provider, Guid workflowId)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionStates
            .AsNoTracking().SingleAsync(state => state.WorkflowId == workflowId);
    }

    private sealed class FlakyStates : IWorkflowRunStateStore
    {
        public IWorkflowRunStateStore Inner { get; set; } = null!;
        public bool FailNextPark { get; set; }

        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken) =>
            Inner.BeginAsync(request, cancellationToken);

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            Inner.GetAsync(executionId, cancellationToken);

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken)
        {
            if (FailNextPark && request.ToStatus == WorkflowRunStateStatus.Suspended)
            {
                FailNextPark = false;
                throw new TimeoutException("the park's state write timed out");
            }
            return Inner.TransitionAsync(request, cancellationToken);
        }

        public Task<bool> CompleteAsync(Guid executionId, WorkflowRunStateStatus status, string? failureReason,
            CancellationToken cancellationToken) => Inner.CompleteAsync(executionId, status, failureReason, cancellationToken);

        public Task<bool> CompleteIfOwnedAsync(Guid executionId, string? ownedByReplicaId, WorkflowRunStateStatus terminalStatus,
            string? failureReason, CancellationToken cancellationToken) =>
            Inner.CompleteIfOwnedAsync(executionId, ownedByReplicaId, terminalStatus, failureReason, cancellationToken);

        public Task<WorkflowRunStatePage> ListByWorkflowAsync(Guid workflowId, WorkflowRunStatePageRequest request,
            CancellationToken cancellationToken) => Inner.ListByWorkflowAsync(workflowId, request, cancellationToken);
    }

    private sealed class FlakySuspensions : IWorkflowSuspensionStore
    {
        public IWorkflowSuspensionStore Inner { get; set; } = null!;
        public bool FailNextCancel { get; set; }

        public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken) =>
            Inner.SuspendAsync(request, cancellationToken);

        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken)
        {
            if (!FailNextCancel)
                return Inner.CancelByExecutionAsync(executionLogId, cancellationToken);
            FailNextCancel = false;
            throw new TimeoutException("the cancel timed out");
        }

        public void RegisterForResumption(WorkflowSuspension suspension) => Inner.RegisterForResumption(suspension);

        public void TearDownByExecution(Guid executionLogId) => Inner.TearDownByExecution(executionLogId);
    }

    private sealed class ClaimEveryRunning(IWorkflowRunStateStore states, IServiceScopeFactory scopes) : IWorkflowRunRecoveryStore
    {
        public async Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
            WorkflowRunRecoveryClaim request, CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var ids = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionStates.AsNoTracking()
                .Where(state => state.Status == nameof(WorkflowRunStateStatus.Running))
                .Select(state => state.ExecutionId).ToListAsync(cancellationToken);
            var rows = new List<WorkflowRunStateRow>();
            foreach (var id in ids)
                if (await states.GetAsync(id, cancellationToken) is { } row)
                    rows.Add(row);
            return rows;
        }

        public Task<bool> RenewClaimAsync(Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
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

        public Task<WorkflowParkedRowRead?> GetParkedAsync(Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken) =>
            store.GetParkedAsync(suspensionId, executionLogId, cancellationToken);

        public Task<ResumeContext?> TryResolveAsync(Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) =>
            store.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            store.ReleaseAsync(suspensionId, takenAs, cancellationToken);
    }
}
