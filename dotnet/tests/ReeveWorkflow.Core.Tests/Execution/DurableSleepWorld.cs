using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

internal sealed class DurableSleepWorld
{
    public const string SleepType = "test.sleep";
    public const string AfterType = "test.after";
    public const string HoldType = "test.hold";
    public const string ContainerType = "test.container";

    public const string FanOutType = "test.fanout";

    public const int FanOutCalls = 128;

    public const string LoopType = "test.loop";

    public const string FinallyType = "test.finally";

    public const string CatchType = "test.catch";

    public const string BoomType = "test.boom";

    public const string BothAtOnceType = "test.bothatonce";
    public const string ChildType = "test.child";
    public const string FlakyType = "test.flaky";
    public const string SideType = "test.side";
    public const string SleepNode = "nap";
    public const string AfterNode = "after";

    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid OwnerId = Guid.NewGuid();

    public MemoryWorkflowStore Store { get; } = new();
    public MemoryJournal Journal { get; } = new();
    public MemoryRunState RunState { get; } = new();
    public MemorySuspensions Suspensions { get; }
    public TestBlocks Blocks { get; }
    public RecordingObserver Observer { get; } = new();
    public RecordingAudit Audit { get; } = new();
    public RecordingMetrics Metrics { get; } = new();

    public SuspensionsSeam Seam { get; }

    public DurableSleepWorld()
    {
        Blocks = new TestBlocks(() => Store.Definitions);
        Suspensions = new MemorySuspensions(Journal);
        Seam = new SuspensionsSeam(Suspensions);
    }

    public TimeSpan? ParkWhenAtLeast { get; set; } = TimeSpan.FromMinutes(1);

    public int MaxSuspendPayloadBytes { get; set; } = 256 * 1024;

    public IWorkflowSuspensionStore? SuspensionSeam { get; set; }

    public MemoryRunRecovery? RecoverySeam { get; set; }

    public TimeSpan? RunHeartbeatInterval { get; set; }

    public bool WithRunState { get; set; } = true;

    public bool PoisonScopeOnFailedSave { get; set; }

    public bool ExecutionSuspended { get; set; }

    public Guid AddWorkflow(
        long sleepMs, WorkflowMode mode = WorkflowMode.Single, int maxConcurrentRuns = 1,
        bool passthrough = false, bool holdFirst = false, bool inContainer = false, int secondSleepMs = 0,
        int maxQueuedRuns = 0, WorkflowOverflowBehavior overflow = WorkflowOverflowBehavior.Drop)
    {
        var definition = Definition(sleepMs, passthrough, holdFirst, inContainer, secondSleepMs);
        return Add(definition, mode, maxConcurrentRuns, maxQueuedRuns, overflow);
    }

    public Guid AddParentWorkflow() => Add(JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "child", blockTypeId = ChildType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[] { new { id = "start-child", sourceNodeId = "start", targetNodeId = "child", mappings = new { } } }
    }), WorkflowMode.Parallel, 5, 0, WorkflowOverflowBehavior.Drop);

    public Guid AddRaw(
        string definition, WorkflowMode mode = WorkflowMode.Single, string? retryPolicyJson = null,
        string? inputParametersJson = null, string? outputParametersJson = null) =>
        Add(definition, mode, 1, 0, WorkflowOverflowBehavior.Drop, retryPolicyJson, inputParametersJson, outputParametersJson);

    private Guid Add(
        string definition, WorkflowMode mode, int maxConcurrentRuns, int maxQueuedRuns,
        WorkflowOverflowBehavior overflow, string? retryPolicyJson = null, string? inputParametersJson = null,
        string? outputParametersJson = null)
    {
        definition = ReadableWorkflowDocument.CompleteMissingNodeFields(definition);
        var workflowId = Guid.NewGuid();
        var workflow = new Workflow(
            workflowId, "wf", null, OwnerId, T0,
            retryPolicyJson: retryPolicyJson);
        workflow.ConfigureConcurrency(mode, maxConcurrentRuns, maxQueuedRuns, overflow, T0);
        workflow.SetCurrentVersion(new WorkflowVersion(
            Guid.NewGuid(), workflowId, 1, definition, inputParametersJson, outputParametersJson, OwnerId, T0, isPublished: true), T0);
        Store.Add(workflow);
        return workflowId;
    }

    internal static object Direct(string targetInput, string sourceOutput) =>
        new Dictionary<string, object> { [targetInput] = new { type = "direct", sourceOutputName = sourceOutput } };

    private static string Definition(long sleepMs, bool passthrough, bool holdFirst, bool inContainer, int secondSleepMs)
    {
        var nodes = new List<object> { new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } } };
        var edges = new List<object>();
        var previous = "start";

        void Chain(string id, object node, object? mappings = null)
        {
            nodes.Add(node);
            edges.Add(new { id = $"{previous}-{id}", sourceNodeId = previous, targetNodeId = id, mappings = mappings ?? new { } });
            previous = id;
        }

        if (holdFirst)
            Chain("hold", new { id = "hold", blockTypeId = HoldType, position = new { x = 0, y = 0 }, configuration = new { } });

        var sleep = new Dictionary<string, object?>
        {
            ["id"] = SleepNode, ["blockTypeId"] = SleepType,
            ["configuration"] = new Dictionary<string, object?> { ["DurationMs"] = sleepMs, ["label"] = "kept" }
        };
        if (passthrough)
            sleep["passthrough"] = new[] { "label" };

        if (inContainer)
        {
            Chain("box", new
            {
                id = "box", blockTypeId = ContainerType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[] { new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } }, sleep },
                        edges = new object[] { new { id = "in-nap", sourceNodeId = "in", targetNodeId = SleepNode, mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            });
            Chain(AfterNode, new { id = AfterNode, blockTypeId = AfterType, position = new { x = 0, y = 0 }, configuration = new { } });
        }
        else
        {
            Chain(SleepNode, sleep);
            Chain(AfterNode, new { id = AfterNode, blockTypeId = AfterType, position = new { x = 0, y = 0 }, configuration = new { } },
                DurableSleepWorld.Direct("slept", "DurationMs"));
        }

        if (secondSleepMs > 0)
        {
            Chain("nap2", new
            {
                id = "nap2", blockTypeId = SleepType,
                configuration = new Dictionary<string, object?> { ["DurationMs"] = secondSleepMs }, position = new { x = 0, y = 0 }
            });
            Chain("after2", new { id = "after2", blockTypeId = AfterType, position = new { x = 0, y = 0 }, configuration = new { } },
                DurableSleepWorld.Direct("slept", "DurationMs"));
        }

        return JsonSerializer.Serialize(new { nodes, edges });
    }

    public WorkflowEngineSettings Settings(string replicaId) => new()
    {
        ReplicaId = replicaId,
        TraceMode = TraceMode,
        DurableSleepBlockTypeIds = [SleepType],
        SleepPollInterval = TimeSpan.FromMilliseconds(20),
        ParkSleepWhenAtLeast = ParkWhenAtLeast,
        MaxSuspendPayloadBytes = MaxSuspendPayloadBytes,
        RunHeartbeatInterval = RunHeartbeatInterval,
        RunStateWriteMode = WriteMode,
        RetryOnlyIdempotentBlocks = RetryOnlyIdempotentBlocks,
        ExpressionNamespaces = ExpressionNamespaces
    };

    public IReadOnlyList<WorkflowExpressionNamespace> ExpressionNamespaces { get; set; } = [];

    public bool RetryOnlyIdempotentBlocks { get; set; } = true;

    public RunStateWriteMode WriteMode { get; set; } = RunStateWriteMode.EveryBlock;

    public Core.Compilation.CompilationOptions? CompilationOptions { get; set; }
    public IHelperResolver HelperResolver { get; set; } = NullHelperResolver.Instance;

    public WorkflowEngine Engine(string replicaId = "replica-1") => new(
        new WorkflowEngineSeams
        {
            RunScopes = RunScopes,
            Blocks = Blocks,
            Compiler = new Core.Compilation.WorkflowCompilationService(Blocks, RunScopes, CompilationOptions),
            ExecutionGate = new Gate(this),
            Metrics = Metrics,
            NodeObserver = Observer,
            RunStateStore = WithRunState ? RunState : null,
            RunRecoveryStore = RecoverySeam,
            SuspensionStore = SuspensionSeam
        },
        Settings(replicaId),
        NullLoggerFactory.Instance);

    public TimeSpan StateRowTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public bool HandOverRunsOnStop { get; set; }

    public CancellationToken HostStopping { get; set; }

    public WorkflowTraceMode TraceMode { get; set; }

    public WorkflowRunResumer Resumer(WorkflowEngine? engine, string replicaId = "replica-1")
    {
        var settings = Settings(replicaId);
        var walk = new WorkflowGraphExecutor(
            Blocks, NullLoggerFactory.Instance, Metrics, Observer, HelperResolver,
            WorkflowGraphExecutorOptions.For(settings),
            transitions: new WorkflowTransitionRecorder(
                RunState, NullLogger.Instance, new WorkflowTransitionOptions { ReplicaId = replicaId }),
            sleepRunner: new WorkflowSleepRunner(RunState, NullLogger.Instance, new WorkflowSleepOptions
            {
                CancellationPollInterval = settings.SleepPollInterval,
                ReplicaId = replicaId
            }));

        return new WorkflowRunResumer(
            new Scopes(this), walk, AlwaysRunningExecutionGate.Instance, Metrics, NullLogger.Instance,
            new WorkflowResumeOptions
            {
                ReplicaId = replicaId, TraceMode = WorkflowTraceMode.Full, StateRowTimeout = StateRowTimeout,
                HandOverOnStop = HandOverRunsOnStop, HostStopping = HostStopping,
                ParkSleepWhenAtLeast = ParkWhenAtLeast
            }, new Core.Compilation.WorkflowCompilationService(Blocks, RunScopes, CompilationOptions), RunState, SuspensionSeam, engine?.Coordinators);
    }

    public WorkflowSuspensionWaker Waker(IWorkflowRunResumer resumer) =>
        new(Suspensions, resumer, NullLogger.Instance, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero });

    public void PassTime(TimeSpan by) => Suspensions.Now = DateTimeOffset.UtcNow + by;

    public static async Task EventuallyAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {because}.");
            await Task.Delay(10);
        }
    }

    private sealed class Gate(DurableSleepWorld world) : IWorkflowExecutionGate
    {
        public bool IsExecutionSuspended => world.ExecutionSuspended;
    }

    public IWorkflowRunScopeFactory RunScopes => scopes ??= new Scopes(this);

    private IWorkflowRunScopeFactory? scopes;

    private sealed class Scopes(DurableSleepWorld world) : IWorkflowRunScopeFactory
    {
        public IWorkflowRunScope CreateScope() => new Scope(world);

        private sealed class Scope(DurableSleepWorld world) : IWorkflowRunScope
        {
            public IServiceProvider Services { get; } = new NoServices();
            public IWorkflowStore Store => world.Store;
            public IWorkflowRunJournal Journal { get; } = new ScopedJournal(world);
            public IWorkflowAuditSink Audit => world.Audit;
            public IWorkflowAuthorization Authorization { get; } = new Permissive();
            public void Dispose() { }
        }

        private sealed class ScopedJournal(DurableSleepWorld world) : IWorkflowRunJournal
        {
            private bool poisoned;

            public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken) =>
                world.Journal.OpenRunAsync(log, cancellationToken);
            public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
                world.Journal.LoadRunAsync(executionLogId, cancellationToken);
            public Task SaveRunDetailsAsync(Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
                IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken) =>
                world.Journal.SaveRunDetailsAsync(executionId, recordLogEntries, logEntries, executionSteps, cancellationToken);

            public async Task SaveRunAsync(CancellationToken cancellationToken)
            {
                if (poisoned)
                    throw new InvalidOperationException("this unit of work failed to save earlier");
                try
                {
                    await world.Journal.SaveRunAsync(cancellationToken);
                }
                catch
                {
                    poisoned = world.PoisonScopeOnFailedSave;
                    throw;
                }
            }
        }

        private sealed class NoServices : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }

        private sealed class Permissive : IWorkflowAuthorization
        {
            public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken) =>
                Task.FromResult(true);
        }
    }
}

internal sealed class MemoryWorkflowStore : IWorkflowStore
{
    private readonly ConcurrentDictionary<Guid, Workflow> workflows = new();

    public void Add(Workflow workflow) => workflows[workflow.Id] = workflow;

    public void Remove(Guid workflowId) => workflows.TryRemove(workflowId, out _);

    public IEnumerable<string> Definitions =>
        workflows.Values.Select(w => w.CurrentVersion?.DefinitionJson).OfType<string>();

    public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken) =>
        Task.FromResult(workflows.GetValueOrDefault(workflowId));

    public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        Task.FromResult(workflows.Values.Select(w => w.CurrentVersion).FirstOrDefault(v => v?.Id == versionId));

    public bool FailCurrentVersionLookups { get; set; }

    public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
        Guid workflowId, CancellationToken cancellationToken) =>
        FailCurrentVersionLookups
            ? Task.FromException<(Guid, string)?>(new InvalidOperationException("the database went away"))
            : Task.FromResult<(Guid, string)?>(null);
}

internal sealed class MemoryJournal : IWorkflowRunJournal
{
    public ConcurrentDictionary<Guid, WorkflowExecutionLog> Runs { get; } = new();
    public ConcurrentQueue<WorkflowExecutionStep> Steps { get; } = new();

    public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken)
    {
        Runs[log.Id] = log;
        return Task.CompletedTask;
    }

    public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
        Task.FromResult(Runs.GetValueOrDefault(executionLogId));

    public int FailSaves;

    public Task SaveRunAsync(CancellationToken cancellationToken) =>
        Interlocked.Decrement(ref FailSaves) >= 0
            ? Task.FromException(new InvalidOperationException("the database went away"))
            : Task.CompletedTask;

    public Task SaveRunDetailsAsync(
        Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
        IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken)
    {
        foreach (var step in executionSteps)
            Steps.Enqueue(step);
        return Task.CompletedTask;
    }

    public WorkflowExecutionLog Single(Guid workflowId) => Runs.Values.Single(r => r.WorkflowId == workflowId);
}

internal sealed class MemoryRunState : IWorkflowRunStateStore
{
    private readonly object gate = new();
    public Dictionary<Guid, WorkflowRunStateRow> Rows { get; } = [];

    public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        lock (gate)
        {
            Rows[id] = new WorkflowRunStateRow
            {
                ExecutionId = id, WorkflowId = request.WorkflowId, Status = WorkflowRunStateStatus.Running,
                VariablesJson = request.VariablesJson, OwnedByReplicaId = request.OwnedByReplicaId,
                ParentExecutionId = request.ParentExecutionId
            };
        }
        return Task.FromResult(id);
    }

    public TaskCompletionSource? HoldGet { get; set; }

    public int Reads;

    public bool FailGet { get; set; }

    public bool KeepsNoFence { get; set; }

    public async Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reads);
        if (HoldGet is { } hold)
            await hold.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (FailGet)
            throw new TimeoutException("the read timed out");
        WorkflowRunStateRow? read;
        lock (gate)
        {
            var row = Rows.GetValueOrDefault(executionId);
            read = row is not null && KeepsNoFence ? row with { WaitForEventKey = null, Version = 0 } : row;
        }
        if (AfterRead is { } afterRead)
            await afterRead(Reads);
        return read;
    }

    public Func<int, Task>? AfterRead { get; set; }

    public Func<Task>? BeforeParkThrows { get; set; }

    public void Bump(Guid executionId, string? owner = null)
    {
        lock (gate)
        {
            var row = Rows[executionId];
            Rows[executionId] = row with
            {
                OwnedByReplicaId = owner ?? row.OwnedByReplicaId, Version = row.Version + 1, LastTransitionUtc = DateTimeOffset.UtcNow
            };
        }
    }

    public int FailParks { get; set; }

    public SuspendFailure FailParksAs { get; set; } = SuspendFailure.BeforeWrite;

    public SuspendFailure FailNextFlip { get; set; }

    public TaskCompletionSource? HoldFlip { get; set; }

    public Queue<SuspendFailure> ParkFailures { get; } = new();

    public Func<Task>? AfterParkLanded { get; set; }

    public Func<Task>? AfterFlipLanded { get; set; }

    public async Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken)
    {
        var flip = request.FromStatus == WorkflowRunStateStatus.Suspended && request.ToStatus == WorkflowRunStateStatus.Running;
        if (HoldFlip is { } hold && flip)
            return await HeldAsync(hold, cancellationToken);
        var failure = flip ? FailNextFlip : SuspendFailure.None;
        if (flip)
            FailNextFlip = SuspendFailure.None;
        var park = request.FromStatus == WorkflowRunStateStatus.Running && request.ToStatus == WorkflowRunStateStatus.Suspended;
        if (park && ParkFailures.Count > 0)
            failure = ParkFailures.Dequeue();
        else if (park && FailParks > 0)
        {
            FailParks--;
            failure = FailParksAs;
        }
        if (failure == SuspendFailure.BeforeWrite)
        {
            if (park && BeforeParkThrows is { } beforeThrow)
                await beforeThrow();
            throw new TimeoutException("the swap never reached the database");
        }
        if (KeepsNoFence)
            request = request with { ExpectedWaitForEventKey = null, ExpectedVersion = null };

        lock (gate)
        {
            if (!Rows.TryGetValue(request.ExecutionId, out var row) || row.Status != request.FromStatus
                || (request.ExpectedOwnedByReplicaId is { } owner && row.OwnedByReplicaId != owner)
                || (request.ExpectedWaitForEventKey is { } key && row.WaitForEventKey != key)
                || (request.ExpectedVersion is { } version && row.Version != version))
                return false;

            Rows[request.ExecutionId] = row with
            {
                Status = request.ToStatus, CurrentBlockPath = request.CurrentBlockPath,
                VariablesJson = request.VariablesJson, WaitUntilUtc = request.WaitUntilUtc,
                WaitForEventKey = request.WaitForEventKey, OwnedByReplicaId = request.OwnedByReplicaId,
                Version = row.Version + 1, LastTransitionUtc = DateTimeOffset.UtcNow
            };
            OnTransition?.Invoke(request);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (failure == SuspendFailure.AfterWrite)
        {
            if (park && AfterParkLanded is { } gap)
                await gap();
            if (flip && AfterFlipLanded is { } flipGap)
                await flipGap();
            throw new TimeoutException("the swap committed but its answer was lost");
        }
        return true;
    }

    public Task<bool> CompleteAsync(
        Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!Rows.TryGetValue(executionId, out var row)
                || row.Status is WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed or WorkflowRunStateStatus.Cancelled)
                return Task.FromResult(false);

            Rows[executionId] = row with { Status = terminalStatus };
            return Task.FromResult(true);
        }
    }

    private static async Task<bool> HeldAsync(TaskCompletionSource hold, CancellationToken cancellationToken)
    {
        await hold.Task.WaitAsync(cancellationToken);
        return false;
    }

    public Action<WorkflowRunStateTransition>? OnTransition { get; set; }

    public void Force(Guid executionId, WorkflowRunStateStatus status)
    {
        lock (gate) Rows[executionId] = Rows[executionId] with { Status = status };
    }

    public bool Renew(Guid executionId, string? claimedByReplicaId)
    {
        lock (gate)
        {
            if (!Rows.TryGetValue(executionId, out var row) || row.OwnedByReplicaId != claimedByReplicaId
                || row.Status is WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed
                    or WorkflowRunStateStatus.Cancelled)
                return false;
            Rows[executionId] = row with { Version = row.Version + 1, LastTransitionUtc = DateTimeOffset.UtcNow };
            return true;
        }
    }
}

internal sealed class MemoryRunRecovery(MemoryRunState rows) : IWorkflowRunRecoveryStore
{
    public int Renewals;

    public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
        WorkflowRunRecoveryClaim request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>([]);

    public Task<bool> RenewClaimAsync(Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Renewals);
        return Task.FromResult(rows.Renew(executionId, claimedByReplicaId));
    }
}

internal sealed class MemorySuspensions(MemoryJournal journal)
    : IWorkflowSuspensionStore, IWorkflowParkedSleepStore, IWorkflowSuspensionResumeStore
{
    private readonly object gate = new();

    public sealed record Row(
        Guid Id, Guid ExecutionLogId, Guid WorkflowId, string BlockId, WorkflowSuspensionKind Kind,
        WorkflowSuspensionStatus Status, string ConfigJson, string StatePayloadJson,
        DateTimeOffset SuspendedAtUtc, DateTimeOffset TimeoutAtUtc, string? OutcomeJson, DateTimeOffset? ResolvedAtUtc = null,
        int TakeCount = 0);

    public List<Row> Rows { get; } = [];
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public int ParkedQueries;

    public Row SingleRow
    {
        get { lock (gate) return Rows.Single(); }
    }

    public void Corrupt(Guid id, Func<string, string> rewrite)
    {
        lock (gate)
        {
            var index = Rows.FindIndex(row => row.Id == id);
            var rewritten = rewrite(Rows[index].StatePayloadJson);
            if (rewritten == Rows[index].StatePayloadJson)
                throw new InvalidOperationException("The payload is not the shape this test meant to corrupt.");
            Rows[index] = Rows[index] with { StatePayloadJson = rewritten };
        }
    }

    public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        lock (gate)
        {
            Rows.Add(new Row(id, request.ExecutionLogId, request.WorkflowId, request.BlockId, request.Kind,
                WorkflowSuspensionStatus.Suspended, request.ConfigJson, request.StatePayloadJson,
                request.SuspendedAtUtc, request.TimeoutAtUtc, null));
        }
        return Task.FromResult(new WorkflowSuspension(id, request.ExecutionLogId, request.WorkflowId, request.BlockId,
            request.Kind, request.ConfigJson, request.StatePayloadJson, request.SuspendedAtUtc, request.TimeoutAtUtc));
    }

    public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            for (var i = 0; i < Rows.Count; i++)
                if (Rows[i].ExecutionLogId == executionLogId && Rows[i].Status == WorkflowSuspensionStatus.Suspended)
                    Rows[i] = Rows[i] with { Status = WorkflowSuspensionStatus.Cancelled };
        }
        return Task.CompletedTask;
    }

    public void RegisterForResumption(WorkflowSuspension suspension) { }

    public void TearDownByExecution(Guid executionLogId) { }

    public Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ParkedQueries);
        lock (gate)
            return Task.FromResult(ParkedSleeps(workflowId).Any());
    }

    public Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<WorkflowParkedSleep>>(ParkedSleeps(workflowId)
                .Select(r => new WorkflowParkedSleep(r.Id, r.ExecutionLogId, r.BlockId, r.Status, r.StatePayloadJson, r.SuspendedAtUtc))
                .ToList());
    }

    private List<Row> ParkedSleeps(Guid workflowId)
    {
        return Rows
            .Where(r => r.WorkflowId == workflowId
                && journal.Runs.GetValueOrDefault(r.ExecutionLogId)?.Status == WorkflowExecutionStatus.Running)
            .GroupBy(r => r.ExecutionLogId)
            .Select(run => run.Last())
            .Where(r => r.Kind == WorkflowSuspensionKind.Sleep
                && (r.Status == WorkflowSuspensionStatus.Suspended
                    || r.Status is WorkflowSuspensionStatus.Resumed or WorkflowSuspensionStatus.TimedOut))
            .ToList();
    }

    public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var i = Rows.FindIndex(r => r.Id == suspensionId && r.Kind == WorkflowSuspensionKind.Sleep
                && r.Status is WorkflowSuspensionStatus.Suspended or WorkflowSuspensionStatus.Resumed
                    or WorkflowSuspensionStatus.TimedOut);
            if (i < 0)
                return Task.FromResult(false);
            Rows[i] = Rows[i] with { Status = WorkflowSuspensionStatus.Cancelled };
            return Task.FromResult(true);
        }
    }

    public Task<WorkflowAbandonedTakeScan> ScanAbandonedTakesAsync(
        WorkflowAbandonedTakeCursor after, TimeSpan abandonedAfter, int limit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var takenBefore = Now - abandonedAfter;
            var taken = Rows
                .Where(r => r.Status is WorkflowSuspensionStatus.Resumed or WorkflowSuspensionStatus.TimedOut
                    && r.ResolvedAtUtc is { } at && at <= takenBefore
                    && (after.AfterTakenAtUtc is not { } cursor || at > cursor
                        || (at == cursor && after.AfterSuspensionId is { } id && r.Id.CompareTo(id) > 0))
                    && journal.Runs.GetValueOrDefault(r.ExecutionLogId)?.Status == WorkflowExecutionStatus.Running
                    && Rows.Last(o => o.ExecutionLogId == r.ExecutionLogId).Id == r.Id)
                .OrderBy(r => r.ResolvedAtUtc).ThenBy(r => r.Id)
                .Take(limit)
                .Select(r => new WorkflowSuspensionCandidate(r.Id, r.ExecutionLogId, r.WorkflowId, r.BlockId, r.Kind,
                    r.ConfigJson, r.StatePayloadJson, r.SuspendedAtUtc, r.TimeoutAtUtc)
                {
                    Status = r.Status, OutcomeJson = r.OutcomeJson, TakeCount = r.TakeCount, TakenAtUtc = r.ResolvedAtUtc
                })
                .ToList();

            var next = taken.Count == 0
                ? new WorkflowAbandonedTakeCursor { AfterTakenAtUtc = takenBefore }
                : new WorkflowAbandonedTakeCursor
                {
                    AfterTakenAtUtc = taken[^1].TakenAtUtc, AfterSuspensionId = taken[^1].SuspensionId
                };
            return Task.FromResult(new WorkflowAbandonedTakeScan(taken, next));
        }
    }

    public Task<ResumeContext?> TryRetakeAsync(WorkflowSuspensionCandidate abandoned, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var i = Rows.FindIndex(r => r.Id == abandoned.SuspensionId && r.Status == abandoned.Status
                && r.TakeCount == abandoned.TakeCount);
            if (i < 0)
                return Task.FromResult<ResumeContext?>(null);
            var row = Rows[i] = Rows[i] with { ResolvedAtUtc = Now, TakeCount = Rows[i].TakeCount + 1 };
            return Task.FromResult<ResumeContext?>(row.OutcomeJson is null ? null : new ResumeContext
            {
                SuspensionId = row.Id, ExecutionLogId = row.ExecutionLogId, WorkflowId = row.WorkflowId,
                BlockId = row.BlockId, Kind = row.Kind, OutcomeJson = row.OutcomeJson,
                StatePayloadJson = row.StatePayloadJson, SuspendedAtUtc = row.SuspendedAtUtc
            });
        }
    }

    public Task<bool> HasRunStartedSinceAsync(Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc,
        CancellationToken cancellationToken) =>
        Task.FromResult(journal.Runs.Values.Any(run => run.WorkflowId == workflowId && run.Id != exceptExecutionLogId
            && run.StartedAtUtc > sinceUtc));

    public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
        ScanAsync(WorkflowSuspensionScanCursor.Start, limit, null, cancellationToken);

    public Task<WorkflowSuspensionScan> ScanAsync(
        WorkflowSuspensionScanCursor after, int limit, IReadOnlyCollection<WorkflowSuspensionKind>? kinds,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var mine = Rows.Where(r => r.Status == WorkflowSuspensionStatus.Suspended
                && (kinds is null || kinds.Contains(r.Kind)));
            var due = mine.Where(r => r.TimeoutAtUtc <= Now)
                .Concat(mine.Where(r => r.TimeoutAtUtc > Now
                    && r.Kind is WorkflowSuspensionKind.WaitTemplate or WorkflowSuspensionKind.SubWorkflow))
                .Take(limit)
                .Select(r => new WorkflowSuspensionCandidate(r.Id, r.ExecutionLogId, r.WorkflowId, r.BlockId, r.Kind,
                    r.ConfigJson, r.StatePayloadJson, r.SuspendedAtUtc, r.TimeoutAtUtc))
                .ToList();
            return Task.FromResult(new WorkflowSuspensionScan(Now, due, 0));
        }
    }

    public Task<ResumeContext?> TryResolveAsync(
        Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var i = Rows.FindIndex(r => r.Id == suspensionId && r.Status == WorkflowSuspensionStatus.Suspended);
            if (i < 0)
                return Task.FromResult<ResumeContext?>(null);
            var row = Rows[i] = Rows[i] with
            {
                Status = status, OutcomeJson = outcomeJson, ResolvedAtUtc = resolvedAtUtc, TakeCount = Rows[i].TakeCount + 1
            };
            return Task.FromResult<ResumeContext?>(new ResumeContext
            {
                SuspensionId = row.Id, ExecutionLogId = row.ExecutionLogId, WorkflowId = row.WorkflowId,
                BlockId = row.BlockId, Kind = row.Kind, OutcomeJson = outcomeJson,
                StatePayloadJson = row.StatePayloadJson, SuspendedAtUtc = row.SuspendedAtUtc
            });
        }
    }

    public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var i = Rows.FindIndex(r => r.Id == suspensionId && r.Status == takenAs);
            if (i < 0 || journal.Runs.GetValueOrDefault(Rows[i].ExecutionLogId)?.Status != WorkflowExecutionStatus.Running)
                return Task.FromResult(false);
            Rows[i] = Rows[i] with { Status = WorkflowSuspensionStatus.Suspended, OutcomeJson = null, ResolvedAtUtc = null };
            return Task.FromResult(true);
        }
    }
}

public enum SuspendFailure { None, BeforeWrite, AfterWrite }

internal sealed class SuspensionsSeam(MemorySuspensions inner) : IWorkflowSuspensionStore, IWorkflowParkedSleepStore
{
    public volatile bool ListNothing;
    public TaskCompletionSource? HoldQuestion;
    public TaskCompletionSource QuestionAsked { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SuspendFailure FailNextSuspend { get; set; }

    public TaskCompletionSource? HoldSuspend;
    public TaskCompletionSource SuspendAsked { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken)
    {
        SuspendAsked.TrySetResult();
        if (HoldSuspend is { } holdWrite)
            await holdWrite.Task.WaitAsync(cancellationToken);
        var failure = FailNextSuspend;
        FailNextSuspend = SuspendFailure.None;
        if (failure == SuspendFailure.BeforeWrite)
            throw new InvalidOperationException("the database went away");
        var written = await inner.SuspendAsync(request, cancellationToken);
        return failure == SuspendFailure.AfterWrite
            ? throw new InvalidOperationException("the connection dropped before the commit was acknowledged")
            : written;
    }

    public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken) =>
        inner.CancelByExecutionAsync(executionLogId, cancellationToken);
    public void RegisterForResumption(WorkflowSuspension suspension) { }
    public void TearDownByExecution(Guid executionLogId) { }

    public async Task<bool> HasParkedSleepAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        QuestionAsked.TrySetResult();
        if (HoldQuestion is { } hold)
            await hold.Task.WaitAsync(cancellationToken);
        return await inner.HasParkedSleepAsync(workflowId, cancellationToken);
    }

    public TaskCompletionSource? HoldList;
    public int HoldListOnCall;
    public int ListCalls;
    public TaskCompletionSource ListAsked { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<IReadOnlyList<WorkflowParkedSleep>> ListParkedSleepsAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref ListCalls) == HoldListOnCall && HoldList is { } hold)
        {
            ListAsked.TrySetResult();
            await hold.Task.WaitAsync(cancellationToken);
        }

        return ListNothing ? [] : await inner.ListParkedSleepsAsync(workflowId, cancellationToken);
    }

    public volatile bool FailCancelParkedSleep;

    public Task<bool> CancelParkedSleepAsync(Guid suspensionId, CancellationToken cancellationToken) =>
        FailCancelParkedSleep
            ? Task.FromException<bool>(new InvalidOperationException("the database went away"))
            : inner.CancelParkedSleepAsync(suspensionId, cancellationToken);

    public Task<bool> HasRunStartedSinceAsync(Guid workflowId, Guid exceptExecutionLogId, DateTimeOffset sinceUtc,
        CancellationToken cancellationToken) =>
        inner.HasRunStartedSinceAsync(workflowId, exceptExecutionLogId, sinceUtc, cancellationToken);
}

internal sealed class PlainSuspensions(IWorkflowSuspensionStore inner) : IWorkflowSuspensionStore
{
    public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken) =>
        inner.SuspendAsync(request, cancellationToken);
    public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken) =>
        inner.CancelByExecutionAsync(executionLogId, cancellationToken);
    public void RegisterForResumption(WorkflowSuspension suspension) { }
    public void TearDownByExecution(Guid executionLogId) { }
}

internal sealed class TestBlocks(Func<IEnumerable<string>> definitions) : IWorkflowBlocks
{
    public ConcurrentQueue<IReadOnlyDictionary<string, object?>> AfterInputs { get; } = new();
    public ConcurrentQueue<IReadOnlyDictionary<string, object?>> OwnSleeps { get; } = new();
    public TaskCompletionSource HoldReached { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<Task>? RunChild { get; set; }

    public bool FlakyFails { get; set; } = true;
    public int FlakyCalls;
    public int SideCalls;

    public int LoopIterations { get; set; } = 1;

    public IReadOnlyCollection<BlockPortDefinition>? LoopBodyInputs { get; set; }

    public Dictionary<string, int> IterationsByNode { get; } = new(StringComparer.Ordinal);

    public int LoopEntries;

    public int LoopFailsOnEntry;

    public Action<int>? OnLoopEntered;

    public int CleanupsAttempted;

    public Func<string, IWorkflowBlockFactory?>? Extra
    {
        get => extra;
        set { extra = value; Interlocked.Increment(ref extraSets); }
    }

    private Func<string, IWorkflowBlockFactory?>? extra;
    private int extraSets;

    public PortValueType? ContainerOutput { get; set; }

    public IWorkflowBlockFactory? Find(string blockTypeId) => Extra?.Invoke(blockTypeId) ?? blockTypeId switch
    {
        DurableSleepWorld.SleepType => new Factory(blockTypeId, () => new OwnSleep(this),
            outputs: [new BlockPortDefinition("Completed", "Completed", PortValueType.Boolean, false),
                new BlockPortDefinition("DurationMs", "Duration", PortValueType.Int, false)]),
        DurableSleepWorld.AfterType => new Factory(blockTypeId, () => new After(this),
            inputs: [new BlockPortDefinition("slept", "Slept", PortValueType.Int, false)]),
        DurableSleepWorld.HoldType => new Factory(blockTypeId, () => new Hold(this)),
        DurableSleepWorld.ContainerType => new Factory(blockTypeId, () => new Container(), ["Body"],
            subCanvasOutputs: ContainerOutput is { } type ? [new BlockPortDefinition("out", "out", type, false)] : null),
        DurableSleepWorld.FanOutType => new Factory(blockTypeId, () => new FanOut(), ["Body"]),
        DurableSleepWorld.LoopType => new Factory(blockTypeId, () => new Loop(this), ["Body"], reenters: true,
            subCanvasInputs: LoopBodyInputs),
        DurableSleepWorld.FinallyType => new Factory(blockTypeId, () => new Cleanup(this), ["Try", "Finally"], reenters: true),
        DurableSleepWorld.CatchType => new Factory(blockTypeId, () => new Catching(), ["Try", "Catch"], reenters: true),
        DurableSleepWorld.BothAtOnceType => new Factory(blockTypeId, () => new BothAtOnce(), ["First", "Second"], reenters: true),
        DurableSleepWorld.BoomType => new LambdaFactory(blockTypeId, _ => throw new InvalidOperationException("boom")),
        DurableSleepWorld.ChildType => new Factory(blockTypeId, () => new Child(this)),
        DurableSleepWorld.FlakyType => new Factory(blockTypeId, () => new Counting(this, flaky: true)),
        DurableSleepWorld.SideType => new Factory(blockTypeId, () => new Counting(this, flaky: false)),
        _ => null
    };

    public bool ContainsPrivilegedBlock(string? definitionJson) => false;

    public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [.. Named().Select(Find).OfType<IWorkflowBlockFactory>()];

    public string Generation => Volatile.Read(ref extraSets) + ":" + string.Join(",", Named());

    private IEnumerable<string> Named() => definitions().SelectMany(WorkflowDefinitionBlockTypes.Collect)
        .Where(id => !string.Equals(id, WorkflowGraphHelpers.WorkflowInputBlockTypeId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(id, WorkflowGraphHelpers.WorkflowOutputBlockTypeId, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase);

    private sealed class Factory(
        string id, Func<IWorkflowBlockPlugin> create, IReadOnlyCollection<string>? subCanvases = null,
        bool reenters = false, IReadOnlyCollection<BlockPortDefinition>? inputs = null,
        IReadOnlyCollection<BlockPortDefinition>? outputs = null,
        IReadOnlyCollection<BlockPortDefinition>? subCanvasOutputs = null,
        IReadOnlyCollection<BlockPortDefinition>? subCanvasInputs = null) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs ?? [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs ?? [];
        public IReadOnlyCollection<string> SubCanvasNames => subCanvases ?? [];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) => subCanvasOutputs ?? [];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) => subCanvasInputs ?? [];
        public bool IsIdempotent => !reenters;
        public bool OnlyDrivesItsSubCanvases => reenters;
        public IWorkflowBlockPlugin Create() => create();
    }

    private sealed class FanOut : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var executor = context.SubCanvasExecutor!;
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = Enumerable.Range(0, DurableSleepWorld.FanOutCalls).Select(async _ =>
            {
                await go.Task.ConfigureAwait(false);
                await executor.ExecuteAsync("Body", new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);
            }).ToArray();
            go.SetResult();
            await Task.WhenAll(calls).ConfigureAwait(false);

            return new Dictionary<string, object?>();
        }
    }

    private sealed class BothAtOnce : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await Task.WhenAll(
                context.SubCanvasExecutor!.ExecuteAsync("First", new Dictionary<string, object?>(), cancellationToken),
                context.SubCanvasExecutor.ExecuteAsync("Second", new Dictionary<string, object?>(), cancellationToken));
            return new Dictionary<string, object?>();
        }
    }

    private sealed class Catching : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var tried = await context.SubCanvasExecutor!.TryExecuteAsync(
                "Try", new Dictionary<string, object?>(), cancellationToken);
            if (tried.Error is not null)
            {
                await context.SubCanvasExecutor.ExecuteAsync(
                    "Catch", new Dictionary<string, object?>(), cancellationToken);
            }

            return new Dictionary<string, object?> { ["CaughtError"] = tried.Error is not null };
        }
    }

    private sealed class Cleanup(TestBlocks blocks) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            try
            {
                await context.SubCanvasExecutor!.TryExecuteAsync("Try", new Dictionary<string, object?>(), cancellationToken);
            }
            finally
            {
                blocks.CleanupsAttempted++;
                await context.SubCanvasExecutor!.ExecuteAsync("Finally", new Dictionary<string, object?>(), cancellationToken);
            }

            return new Dictionary<string, object?>();
        }
    }

    private sealed class Loop(TestBlocks blocks) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var entry = Interlocked.Increment(ref blocks.LoopEntries);
            blocks.OnLoopEntered?.Invoke(entry);
            var iterations = blocks.IterationsByNode.TryGetValue(context.NodeId ?? "", out var mine)
                ? mine
                : blocks.LoopIterations;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                await context.SubCanvasExecutor!.ExecuteAsync(
                    "Body", new Dictionary<string, object?> { ["Index"] = iteration }, cancellationToken);
            }

            if (entry == blocks.LoopFailsOnEntry)
            {
                throw new HttpRequestException("this pass through the container fails");
            }

            return new Dictionary<string, object?> { ["Iterations"] = iterations };
        }
    }

    private sealed class OwnSleep(TestBlocks blocks) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            blocks.OwnSleeps.Enqueue(inputs);
            var ms = WorkflowExecutionHelpers.ResolveSleepDurationMs(inputs);
            await Task.Delay(TimeSpan.FromMilliseconds(ms), cancellationToken);
            return new Dictionary<string, object?> { ["Completed"] = true, ["DurationMs"] = ms };
        }
    }

    private sealed class After(TestBlocks blocks) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            blocks.AfterInputs.Enqueue(inputs);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class Counting(TestBlocks blocks, bool flaky) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            if (!flaky)
                Interlocked.Increment(ref blocks.SideCalls);
            else if (Interlocked.Increment(ref blocks.FlakyCalls) > 0 && blocks.FlakyFails)
                throw new InvalidOperationException("device offline");
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class Child(TestBlocks blocks) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            await blocks.RunChild!();
            return new Dictionary<string, object?>();
        }
    }

    private sealed class Hold(TestBlocks blocks) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            blocks.HoldReached.TrySetResult();
            await blocks.Release.Task.WaitAsync(cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private sealed class Container : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            context.SubCanvasExecutor!.ExecuteAsync("Body", new Dictionary<string, object?>(), cancellationToken);
    }
}

internal sealed class AsyncLambdaFactory(
    string id, Func<WorkflowBlockContext, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> body)
    : IWorkflowBlockFactory
{
    public string BlockTypeId => id;
    public string DisplayName => id;
    public string Description => string.Empty;
    public string Category => "test";
    public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
    public bool IsIdempotent => true;
    public IWorkflowBlockPlugin Create() => new Plugin(body);

    private sealed class Plugin(Func<WorkflowBlockContext, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> body)
        : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            body(context, cancellationToken);
    }
}

internal sealed class LambdaFactory(
    string id, Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> body,
    IReadOnlyCollection<BlockPortDefinition>? inputs = null, IReadOnlyCollection<BlockPortDefinition>? outputs = null)
    : IWorkflowBlockFactory
{
    public static BlockPortDefinition Port(string name, PortValueType type) => new(name, name, type, false);

    public string BlockTypeId => id;
    public string DisplayName => id;
    public string Description => string.Empty;
    public string Category => "test";
    public IReadOnlyCollection<BlockPortDefinition> Inputs => inputs ?? [];
    public IReadOnlyCollection<BlockPortDefinition> Outputs => outputs ?? [];
    public bool IsIdempotent => true;
    public IWorkflowBlockPlugin Create() => new Plugin(body);

    private sealed class Plugin(Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> body)
        : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult(body(inputs));
    }
}

internal sealed class RecordingObserver : IWorkflowNodeObserver
{
    public ConcurrentQueue<(string NodeId, IReadOnlyDictionary<string, object?> Outputs)> Completed { get; } = new();

    public ConcurrentQueue<string> Timeline { get; } = new();

    public Task NotifyNodeStartedAsync(Guid workflowId, Guid executionId, string nodeId,
        IReadOnlyDictionary<string, object?> inputs, CancellationToken cancellationToken = default)
    {
        Timeline.Enqueue($"started:{executionId}:{nodeId}");
        return Task.CompletedTask;
    }

    public Task NotifyNodeCompletedAsync(Guid workflowId, Guid executionId, string nodeId,
        IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> outputs, long durationMs,
        CancellationToken cancellationToken = default)
    {
        Completed.Enqueue((nodeId, outputs));
        return Task.CompletedTask;
    }

    public ConcurrentQueue<(Guid ExecutionId, string NodeId, string Error)> Failed { get; } = new();

    public Task NotifyNodeFailedAsync(Guid workflowId, Guid executionId, string nodeId,
        IReadOnlyDictionary<string, object?> inputs, string error, long durationMs,
        CancellationToken cancellationToken = default)
    {
        Failed.Enqueue((executionId, nodeId, error));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAudit : IWorkflowAuditSink
{
    public ConcurrentQueue<WorkflowAuditEvent> Events { get; } = new();

    public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Events.Enqueue(auditEvent);
        return Task.CompletedTask;
    }

    public IEnumerable<string> Kinds => Events.Select(e => e.Kind);
}

internal sealed class RecordingMetrics : IWorkflowMetrics
{
    public ConcurrentQueue<string> SuspensionsStarted { get; } = new();
    public ConcurrentQueue<(string Kind, string Outcome)> SuspensionsResolved { get; } = new();
    public ConcurrentQueue<(string Mode, string Outcome)> Admissions { get; } = new();

    public void RecordExecutionStarted() { }
    public bool ThrowOnRunEnd { get; set; }

    public ConcurrentQueue<string> RunEndings { get; } = new();

    public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null)
    {
        if (ThrowOnRunEnd)
            throw new InvalidOperationException("the metrics exporter is broken");
        RunEndings.Enqueue(status);
    }

    public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
    public void RecordSupersession(Guid workflowId, string mode, string outcome) => Admissions.Enqueue((mode, outcome));
    public void RecordDebugSession(string outcome) { }
    public void RecordDebugPauseDuration(double durationSeconds) { }
    public void RecordSuspensionStarted(string kind) => SuspensionsStarted.Enqueue(kind);
    public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) =>
        SuspensionsResolved.Enqueue((kind, outcome));
}

internal static class WakerWalks
{
    public static async Task<int> WakeAndWalkAsync(this WorkflowSuspensionWaker waker)
    {
        var moved = await waker.WakeOnceAsync(default);
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return moved;
    }
}

internal static class TemplateWaits
{
    public const string Input = "Condition";

    public static readonly BlockPortDefinition ConditionPort = new(Input, Input, PortValueType.OfDeclaration("Expression<Boolean>"), false);

    public static WorkflowSuspendException Park(DateTimeOffset? timeoutAtUtc = null) => new(new SuspendResult(
        WorkflowSuspensionKind.WaitTemplate,
        System.Text.Json.JsonSerializer.Serialize(new WorkflowWaitTemplateConfig { Input = Input }),
        timeoutAtUtc ?? DateTimeOffset.UtcNow.AddHours(1)));
}
