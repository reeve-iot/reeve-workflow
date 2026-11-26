using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

internal sealed class RunLifecycleWorld
{
    public const string HoldNodeId = "hold";
    public const string ReplicaId = "replica-1";

    public const string BoxType = "Box";
    public const string PayloadSourceType = "PayloadSource";

    public const string LoopType = "Loop";

    public const string BareType = "Bare";

    public const string TryType = "Try";

    public const string BoomType = "Boom";
    public const string LoopValueType = "test.loop-value";

    public static readonly string TryThatFailsThenHolds = System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "attempt", blockTypeId = TryType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Try"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "tryIn", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "boom", blockTypeId = BoomType, position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "e-boom", sourceNodeId = "tryIn", targetNodeId = "boom", mappings = new { } } }
                    },
                    ["After"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "afterIn", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "e-hold", sourceNodeId = "afterIn", targetNodeId = HoldNodeId, mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-attempt", sourceNodeId = "start", targetNodeId = "attempt", mappings = new { } } }
    });

    public static readonly string SyncInnerLoopThenHold = System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "outer", blockTypeId = LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "outerIn", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new
                            {
                                id = "inner", blockTypeId = LoopType,
                                subCanvases = new Dictionary<string, object>
                                {
                                    ["Body"] = new
                                    {
                                        nodes = new object[]
                                        {
                                            new { id = "innerIn", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                                            new { id = "tick", blockTypeId = "Tick", position = new { x = 0, y = 0 }, configuration = new { } }
                                        },
                                        edges = new object[] { new { id = "e-tick", sourceNodeId = "innerIn", targetNodeId = "tick", mappings = new { } } }
                                    }
                                }, position = new { x = 0, y = 0 }, configuration = new { }
                            },
                            new { id = HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[]
                        {
                            new { id = "e-outer-inner", sourceNodeId = "outerIn", targetNodeId = "inner", mappings = new { } },
                            new { id = "e-inner-hold", sourceNodeId = "inner", targetNodeId = HoldNodeId, mappings = new { } }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-outer", sourceNodeId = "start", targetNodeId = "outer", mappings = new { } } }
    });

    public static readonly string LoopInsideLoop = System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "outer", blockTypeId = LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "outerIn", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new
                            {
                                id = "inner", blockTypeId = LoopType,
                                subCanvases = new Dictionary<string, object>
                                {
                                    ["Body"] = new
                                    {
                                        nodes = new object[]
                                        {
                                            new { id = "innerIn", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                                            new { id = HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } }
                                        },
                                        edges = new object[] { new { id = "e-in-hold", sourceNodeId = "innerIn", targetNodeId = HoldNodeId, mappings = new { } } }
                                    }
                                }, position = new { x = 0, y = 0 }, configuration = new { }
                            }
                        },
                        edges = new object[] { new { id = "e-outer-inner", sourceNodeId = "outerIn", targetNodeId = "inner", mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-outer", sourceNodeId = "start", targetNodeId = "outer", mappings = new { } } }
    });

    public static readonly string LoopAroundTickThenHold = System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "loop", blockTypeId = LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = "tick", blockTypeId = "Tick", position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[]
                        {
                            new { id = "in-tick", sourceNodeId = "in", targetNodeId = "tick", mappings = new { } },
                            new { id = "tick-hold", sourceNodeId = "tick", targetNodeId = HoldNodeId, mappings = new { } }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-loop", sourceNodeId = "start", targetNodeId = "loop", mappings = new { } } }
    });

    public static readonly string LoopAroundTickThenHoldContinuing = LoopAroundTickThenHold.Replace(
        "\"id\":\"loop\",\"blockTypeId\":\"" + LoopType + "\"",
        "\"id\":\"loop\",\"blockTypeId\":\"" + LoopType + "\",\"errorPolicy\":{\"policy\":\"ContinueWithDefault\"}",
        StringComparison.Ordinal);

    public static readonly string LoopAroundHold = System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "loop", blockTypeId = LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new { id = HoldNodeId, blockTypeId = "Hold", position = new { x = 0, y = 0 }, configuration = new { } }
                        },
                        edges = new object[] { new { id = "in-hold", sourceNodeId = "in", targetNodeId = HoldNodeId, mappings = new { } } }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            }
        },
        edges = new object[] { new { id = "start-loop", sourceNodeId = "start", targetNodeId = "loop", mappings = new { } } }
    });
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    public RunLifecycleWorld(
        int? timeoutSeconds = null, string? retryPolicyJson = null, bool tickBeforeHold = false, string? definitionJson = null,
        string? inputParametersJson = null, string? outputParametersJson = null)
    {
        WorkflowId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var storable = WorkflowRowSettings.RetryPolicyFault(retryPolicyJson) is null;
        Workflow = new Workflow(WorkflowId, "wf", null, owner, T0,
            executionTimeoutSeconds: timeoutSeconds, retryPolicyJson: storable ? retryPolicyJson : null);
        if (!storable)
            typeof(Workflow).GetProperty(nameof(Workflow.RetryPolicyJson))!.SetValue(Workflow, retryPolicyJson);
        Version = new WorkflowVersion(Guid.NewGuid(), WorkflowId, 1,
            ReadableWorkflowDocument.CompleteMissingNodeFields(definitionJson ?? Graph(tickBeforeHold)),
            inputParametersJson, outputParametersJson, owner, T0, isPublished: true);
        Workflow.SetCurrentVersion(Version, T0);
        Store = new MemoryWorkflowStore(Workflow, Version);
    }

    public Guid WorkflowId { get; }
    public Workflow Workflow { get; }
    public WorkflowVersion Version { get; }
    public MemoryWorkflowStore Store { get; }
    public MemoryJournal Journal { get; } = new();
    public RecordingAudit Audit { get; } = new();
    public MemoryRunState RunState { get; } = new();
    public MemoryRecoveryStore Recovery => recovery ??= new MemoryRecoveryStore(RunState);
    private MemoryRecoveryStore? recovery;

    public bool ParkAtHold { get; init; }

    public string? EngineReplicaId { get; init; } = ReplicaId;

    public bool HoldIdempotent { get; init; }

    public int LoopIterations { get; init; } = 1;

    public int? HoldsAtEntry { get; init; }

    public int? HoldThrowsAtEntry { get; init; }

    public bool TickIdempotent { get; init; }

    public bool CollectLoopResults { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<WorkflowCompletedSubCanvasCall>>? CapturedLiveCompleted { get; private set; }

    public ConcurrentQueue<int> LoopValueEntries { get; } = new();

    public Guid? ParkedStateId { get; private set; }

    public int HoldEntries => holdEntries;
    private int holdEntries;
    public int FailHoldTimes { get; init; }

    public Func<Exception>? HoldThrows { get; init; }

    public bool HoldWrapsItsCancellation { get; init; }

    public bool HoldCancelsItself { get; init; }

    internal Func<string, IWorkflowBlockFactory?>? AdditionalBlocks { get; set; }

    public Core.Compilation.CompilationOptions? CompilationOptions { get; set; }

    public ConcurrentQueue<int> HoldCallDepths { get; } = new();

    public int TickEntries => tickEntries;
    private int tickEntries;

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WorkflowEngine Engine(
        CancellationToken hostStopping = default, bool handOverOnStop = false,
        TimeSpan? heartbeat = null, IWorkflowRunLockProvider? locks = null,
        IWorkflowRunRecoveryStore? renewals = null, bool durableSleepAtHold = false,
        IWorkflowRunSlotGate? slots = null, IWorkflowSuspensionStore? suspensions = null, IWorkflowMetrics? metrics = null,
        IWorkflowDebugSessions? debugSessions = null, RunStateWriteMode? writeMode = null, bool recoveryReenters = false,
        TimeProvider? timeProvider = null, TimeSpan? idleTimeout = null, IWorkflowStartAdmission? startAdmission = null)
    {
        return new WorkflowEngine(
            new WorkflowEngineSeams
            {
                RunScopes = new Scopes(this),
                Blocks = new Blocks(this),
                Compiler = new Core.Compilation.WorkflowCompilationService(new Blocks(this), new Scopes(this), CompilationOptions),
                ExecutionGate = AlwaysRunningExecutionGate.Instance,
                Metrics = metrics ?? NullWorkflowMetrics.Instance,
                NodeObserver = NullWorkflowNodeObserver.Instance,
                RunStateStore = RunState,
                RunRecoveryStore = renewals ?? (heartbeat is null ? null : Recovery),
                RunLockProvider = locks,
                RunSlotGate = slots,
                FireDedup = slots is null ? null : new ClaimEveryFire(),
                RestartGenerations = slots is null ? null : new InProcessRestartGenerations(),
                SuspensionStore = suspensions,
                DebugSessions = debugSessions,
                StartAdmission = startAdmission,
                HostStopping = hostStopping
            },
            new WorkflowEngineSettings
            {
                ReplicaId = EngineReplicaId,
                IsMultiReplica = slots is not null,
                HandOverRunsOnStop = handOverOnStop,
                RecoveryReentersRuns = recoveryReenters,
                RunHeartbeatInterval = heartbeat,
                DurableSleepBlockTypeIds = durableSleepAtHold ? ["Hold"] : [],
                SleepPollInterval = TimeSpan.FromMilliseconds(20),
                RunStateWriteMode = writeMode ?? RunStateWriteMode.EveryBlock,
                TimeProvider = timeProvider,
                CoordinatorIdleTimeout = idleTimeout ?? new WorkflowEngineSettings().CoordinatorIdleTimeout
            },
            NullLoggerFactory.Instance);
    }

    public WorkflowRunResumer Resumer(
        CancellationToken hostStopping = default, bool handOverOnStop = false, TimeSpan? heartbeat = null,
        IWorkflowRunCoordinatorPool? coordinators = null, Microsoft.Extensions.Logging.ILogger? logger = null,
        IWorkflowSuspensionStore? suspensions = null, IWorkflowMetrics? metrics = null, bool durableSleepAtHold = false,
        bool recoveryReenters = false, int maxPayloadValueBytes = 1024, bool withRunState = true) =>
        new(
            new Scopes(this),
            new WorkflowGraphExecutor(
                new Blocks(this), NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
                NullWorkflowNodeObserver.Instance, NullHelperResolver.Instance,
                options: new WorkflowGraphExecutorOptions { DurableSleepBlockTypeIds = durableSleepAtHold ? ["Hold"] : [] },
                transitions: new WorkflowTransitionRecorder(
                    RunState, NullLogger.Instance, new WorkflowTransitionOptions { ReplicaId = ReplicaId }),
                sleepRunner: durableSleepAtHold
                    ? new WorkflowSleepRunner(RunState, NullLogger.Instance, new WorkflowSleepOptions
                    {
                        CancellationPollInterval = TimeSpan.FromMilliseconds(20), ReplicaId = ReplicaId,
                        HostStopping = hostStopping
                    })
                    : null),
            AlwaysRunningExecutionGate.Instance,
            metrics ?? NullWorkflowMetrics.Instance,
            logger ?? NullLogger.Instance,
            new WorkflowResumeOptions
            {
                ReplicaId = ReplicaId,
                HandOverOnStop = handOverOnStop,
                RecoveryReentersRuns = recoveryReenters,
                MaxPayloadValueBytes = maxPayloadValueBytes,
                HostStopping = hostStopping,
                HeartbeatInterval = heartbeat,
                RunRecoveryStore = heartbeat is null ? null : Recovery
            },
            new Core.Compilation.WorkflowCompilationService(new Blocks(this), new Scopes(this), CompilationOptions),
            withRunState ? RunState : null,
            suspensions,
            coordinators);

    public ResumeContext Parked(int callDepth = 0, Guid? parentExecutionId = null, string blockId = "start")
    {
        var log = new WorkflowExecutionLog(
            Guid.NewGuid(), null, WorkflowId, Version.Id, null, WorkflowExecutionStatus.Running, T0);
        Journal.Logs[log.Id] = log;
        var stateId = RunState.Seed(WorkflowId, WorkflowRunStateStatus.Suspended, ReplicaId, blockId, parentExecutionId);
        ParkedStateId = stateId;

        return new ResumeContext
        {
            SuspensionId = Guid.NewGuid(),
            ExecutionLogId = log.Id,
            WorkflowId = WorkflowId,
            BlockId = blockId,
            Kind = WorkflowSuspensionKind.WaitTemplate,
            OutcomeJson = "{\"outcome\":\"satisfied\"}",
            StatePayloadJson = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
            {
                VariablesJson = "{}",
                PreviousOutputsJson = "{}",
                TriggerEnvelopeJson = "{}",
                ExecutionStateId = stateId,
                CallDepth = callDepth
            }, WorkflowGraphHelpers.JsonOptions),
            SuspendedAtUtc = T0
        };
    }

    private static string Graph(bool tickBeforeHold) => tickBeforeHold
        ? $"{{\"nodes\":[{{\"id\":\"start\",\"blockTypeId\":\"WorkflowInput\"}},"
          + "{\"id\":\"tick\",\"blockTypeId\":\"Tick\"},"
          + $"{{\"id\":\"{HoldNodeId}\",\"blockTypeId\":\"Hold\",\"configuration\":{{\"DurationMs\":60000}}}}],"
          + "\"edges\":[{\"id\":\"e1\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"tick\",\"mappings\":{}},"
          + $"{{\"id\":\"e2\",\"sourceNodeId\":\"tick\",\"targetNodeId\":\"{HoldNodeId}\",\"mappings\":{{}}}}]}}"
        : $"{{\"nodes\":[{{\"id\":\"start\",\"blockTypeId\":\"WorkflowInput\"}},"
          + $"{{\"id\":\"{HoldNodeId}\",\"blockTypeId\":\"Hold\",\"configuration\":{{\"DurationMs\":60000}}}}],"
          + $"\"edges\":[{{\"id\":\"e\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"{HoldNodeId}\",\"mappings\":{{}}}}]}}";

    private sealed class ClaimEveryFire : IWorkflowFireDedup
    {
        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public string Fingerprint(string fireKey) => fireKey;
    }

    internal sealed class MemoryRunState : IWorkflowRunStateStore
    {
        private readonly Lock gate = new();
        private readonly Dictionary<Guid, WorkflowRunStateRow> rows = [];

        public ConcurrentQueue<(Guid Id, WorkflowRunStateStatus Status, string? Reason)> Completions { get; } = new();

        public Guid? LastBegun { get; private set; }

        public Guid? LastSeeded { get; private set; }

        public Guid Seed(
            Guid workflowId, WorkflowRunStateStatus status, string? owner, string path,
            Guid? parentExecutionId = null)
        {
            var id = Guid.NewGuid();
            LastSeeded = id;
            lock (gate)
                rows[id] = new WorkflowRunStateRow
                {
                    ExecutionId = id, WorkflowId = workflowId, Status = status, OwnedByReplicaId = owner,
                    CurrentBlockPath = path, LastTransitionUtc = DateTimeOffset.UtcNow,
                    ParentExecutionId = parentExecutionId
                };
            return id;
        }

        public WorkflowRunStateRow Row(Guid id)
        {
            lock (gate)
                return rows[id];
        }

        public volatile bool ReadsFail;

        public volatile bool ReadsCancel;

        public void Put(Guid id, WorkflowRunStateStatus status, string? owner, string? path = null, string? reason = null)
        {
            lock (gate)
                rows[id] = rows[id] with
                {
                    Status = status, OwnedByReplicaId = owner, LastTransitionUtc = DateTimeOffset.UtcNow,
                    CurrentBlockPath = path ?? rows[id].CurrentBlockPath, FailureReason = reason
                };
        }

        public void Park(Guid id, string path)
        {
            lock (gate)
                rows[id] = rows[id] with { Status = WorkflowRunStateStatus.Suspended, CurrentBlockPath = path };
        }

        public void Checkpointed(Guid id, string variablesJson)
        {
            lock (gate)
                rows[id] = rows[id] with { VariablesJson = variablesJson };
        }

        public void Asleep(Guid id, DateTimeOffset until)
        {
            lock (gate)
                rows[id] = rows[id] with { Status = WorkflowRunStateStatus.Sleeping, WaitUntilUtc = until };
        }

        public void DispatchedBy(Guid id, Guid parentExecutionId)
        {
            lock (gate)
                rows[id] = rows[id] with { ParentExecutionId = parentExecutionId };
        }

        public void Remove(Guid id)
        {
            lock (gate)
                rows.Remove(id);
        }

        public void TakeOver(Guid id, string? owner)
        {
            lock (gate)
                rows[id] = rows[id] with { OwnedByReplicaId = owner, LastTransitionUtc = DateTimeOffset.UtcNow };
        }

        public volatile bool BeginsFail;

        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken)
        {
            if (BeginsFail)
                throw new InvalidOperationException("The run state store cannot open a row.");
            var id = Guid.NewGuid();
            lock (gate)
            {
                rows[id] = new WorkflowRunStateRow
                {
                    ExecutionId = id, WorkflowId = request.WorkflowId, Status = WorkflowRunStateStatus.Running,
                    OwnedByReplicaId = request.OwnedByReplicaId, VariablesJson = request.VariablesJson,
                    LastTransitionUtc = DateTimeOffset.UtcNow
                };
                LastBegun = id;
            }
            return Task.FromResult(id);
        }

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken)
        {
            if (ReadsFail)
                throw new InvalidOperationException("The run state store cannot be read.");
            if (ReadsCancel)
                throw new TaskCanceledException("The store's connection was closed by the stopping host.");
            lock (gate)
                return Task.FromResult(rows.GetValueOrDefault(executionId));
        }

        public TimeSpan LingerAfterParkOrEnding { get; set; }

        public volatile bool TransitionsFail;

        public string? FailWritesBefore { get; set; }

        public int? FailWritesBeforeTimes { get; set; }

        public string? PeerTakesRowBefore { get; set; }

        public int PeerTakesRowAtWrite { get; set; } = 1;
        private int writesBeforePeerTakes;

        public Action? WhileClaimingParkedRow { get; set; }

        public Action<Guid>? AfterRelease { get; set; }

        public volatile bool PreBlockWritesFail;

        public async Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken)
        {
            if (TransitionsFail)
                throw new InvalidOperationException("The run state store cannot be written.");
            if (request is { FromStatus: WorkflowRunStateStatus.Suspended, ToStatus: WorkflowRunStateStatus.Running })
                WhileClaimingParkedRow?.Invoke();
            if (request is { FromStatus: WorkflowRunStateStatus.Running, ToStatus: WorkflowRunStateStatus.Running }
                && request.CurrentBlockPath == PeerTakesRowBefore
                && Interlocked.Increment(ref writesBeforePeerTakes) == PeerTakesRowAtWrite)
                TakeOver(request.ExecutionId, "replica-2");
            if (request is { FromStatus: WorkflowRunStateStatus.Running, ToStatus: WorkflowRunStateStatus.Running }
                && request.CurrentBlockPath == FailWritesBefore
                && (FailWritesBeforeTimes is null || FailWritesBeforeTimes-- > 0))
                throw new TimeoutException("The write before the block timed out.");
            if (PreBlockWritesFail
                && request is { FromStatus: WorkflowRunStateStatus.Running, ToStatus: WorkflowRunStateStatus.Running, OwnedByReplicaId: not null })
                throw new TimeoutException("The write before the block timed out.");
            var moved = Transition(request);
            if (moved && request is { OwnedByReplicaId: null, ExpectedOwnedByReplicaId: not null })
                AfterRelease?.Invoke(request.ExecutionId);
            if (moved && request.ToStatus == WorkflowRunStateStatus.Suspended && LingerAfterParkOrEnding > TimeSpan.Zero)
                await Task.Delay(LingerAfterParkOrEnding, CancellationToken.None);
            return moved;
        }

        private bool Transition(WorkflowRunStateTransition request)
        {
            lock (gate)
            {
                if (!rows.TryGetValue(request.ExecutionId, out var row)
                    || row.Status != request.FromStatus
                    || (request.ExpectedOwnedByReplicaId is { } expected && row.OwnedByReplicaId != expected)
                    || (request.ExpectedCurrentBlockPath is { } path && row.CurrentBlockPath != path))
                    return false;

                rows[request.ExecutionId] = row with
                {
                    Status = request.ToStatus, CurrentBlockPath = request.CurrentBlockPath,
                    VariablesJson = request.VariablesJson, WaitUntilUtc = request.WaitUntilUtc,
                    OwnedByReplicaId = request.OwnedByReplicaId, LastTransitionUtc = DateTimeOffset.UtcNow
                };
                return true;
            }
        }

        public async Task<bool> CompleteAsync(
            Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (!rows.TryGetValue(executionId, out var row)
                    || row.Status is WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed or WorkflowRunStateStatus.Cancelled)
                    return false;

                rows[executionId] = row with { Status = terminalStatus, FailureReason = failureReason };
                Completions.Enqueue((executionId, terminalStatus, failureReason));
            }

            if (LingerAfterParkOrEnding > TimeSpan.Zero)
                await Task.Delay(LingerAfterParkOrEnding, CancellationToken.None);
            return true;
        }

        public volatile bool EndingsFail;

        public volatile bool NextEndingFails;

        public volatile bool NextEndingLandsAndFails;

        public volatile int NextEndingLandsThenFails;
        private volatile int endingsStillFailing;

        public volatile int NextEndingsFail;

        public async Task<bool> CompleteIfOwnedAsync(
            Guid executionId, string? ownedByReplicaId, WorkflowRunStateStatus terminalStatus, string? failureReason,
            CancellationToken cancellationToken)
        {
            if (EndingsFail)
                throw new InvalidOperationException("The run state store cannot be written.");
            if (endingsStillFailing > 0)
            {
                endingsStillFailing--;
                throw new InvalidOperationException("The run state store is still away.");
            }
            if (NextEndingsFail > 0)
            {
                NextEndingsFail--;
                throw new InvalidOperationException("The run state store is away for a moment.");
            }
            if (NextEndingFails)
            {
                NextEndingFails = false;
                throw new InvalidOperationException("The run state store blipped.");
            }
            lock (gate)
            {
                if (!rows.TryGetValue(executionId, out var row)
                    || row.OwnedByReplicaId != ownedByReplicaId
                    || row.Status is WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed or WorkflowRunStateStatus.Cancelled)
                    return false;

                rows[executionId] = row with { Status = terminalStatus, FailureReason = failureReason };
                Completions.Enqueue((executionId, terminalStatus, failureReason));
            }

            if (NextEndingLandsThenFails > 0)
            {
                endingsStillFailing = NextEndingLandsThenFails;
                NextEndingLandsThenFails = 0;
                throw new InvalidOperationException("The answer to the ending was lost, and the store went away.");
            }

            if (NextEndingLandsAndFails)
            {
                NextEndingLandsAndFails = false;
                throw new InvalidOperationException("The answer to the ending was lost.");
            }

            if (LingerAfterParkOrEnding > TimeSpan.Zero)
                await Task.Delay(LingerAfterParkOrEnding, CancellationToken.None);
            return true;
        }

        public ConcurrentQueue<WorkflowRunStateStatus> RenewedRowsIn { get; } = new();

        internal bool Renew(Guid executionId, string? owner)
        {
            lock (gate)
            {
                if (rows.TryGetValue(executionId, out var beaten))
                    RenewedRowsIn.Enqueue(beaten.Status);
                if (!rows.TryGetValue(executionId, out var row) || row.OwnedByReplicaId != owner
                    || row.Status is WorkflowRunStateStatus.Completed or WorkflowRunStateStatus.Failed
                        or WorkflowRunStateStatus.Cancelled)
                    return false;
                rows[executionId] = row with { LastTransitionUtc = DateTimeOffset.UtcNow };
                return true;
            }
        }
    }

    internal sealed class MemoryRecoveryStore(MemoryRunState rows) : IWorkflowRunRecoveryStore
    {
        public int Renewals;

        public TaskCompletionSource FirstRenewal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<WorkflowRunStateRow>> ClaimAbandonedRunsAsync(
            WorkflowRunRecoveryClaim request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowRunStateRow>>([]);

        public Task<bool> RenewClaimAsync(Guid executionId, string? claimedByReplicaId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Renewals);
            var renewed = rows.Renew(executionId, claimedByReplicaId);
            FirstRenewal.TrySetResult();
            return Task.FromResult(renewed);
        }
    }

    internal sealed class MemoryJournal : IWorkflowRunJournal
    {
        public ConcurrentDictionary<Guid, WorkflowExecutionLog> Logs { get; } = new();
        public ConcurrentDictionary<Guid, ConcurrentQueue<WorkflowExecutionLogEntry>> Entries { get; } = new();

        public int DetailsSaved => detailsSaved;
        private int detailsSaved;

        public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken)
        {
            Logs[log.Id] = log;
            return Task.CompletedTask;
        }

        public volatile bool LoadsFail;

        public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            LoadsFail
                ? Task.FromException<WorkflowExecutionLog?>(new TimeoutException("The journal did not answer."))
                : Task.FromResult(Logs.GetValueOrDefault(executionLogId));

        public int RunsSaved => runsSaved;
        private int runsSaved;

        public Task SaveRunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref runsSaved);
            return Task.CompletedTask;
        }

        public Action? WhileSavingDetails { get; set; }

        public Task SaveRunDetailsAsync(
            Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
            IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref detailsSaved);
            if (executionId is { } id)
            {
                var recorded = Entries.GetOrAdd(id, _ => new ConcurrentQueue<WorkflowExecutionLogEntry>());
                foreach (var entry in logEntries)
                    recorded.Enqueue(entry);
            }
            WhileSavingDetails?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    internal sealed class MemoryWorkflowStore(Workflow workflow, WorkflowVersion version) : IWorkflowStore
    {
        public Action? WhileLoadingChild { get; set; }

        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult<Workflow?>(workflowId == workflow.Id ? workflow : null);

        public WorkflowVersion? Older { get; set; }

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult(versionId == version.Id ? version : versionId == Older?.Id ? Older : null);

        public Task<WorkflowVersion?> LoadVersionNumberedAsync(
            Guid workflowId, int versionNumber, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowVersion?>(version.VersionNumber == versionNumber ? version : null);

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
            Guid workflowId, CancellationToken cancellationToken)
        {
            WhileLoadingChild?.Invoke();
            return Task.FromResult<(Guid, string)?>(workflowId == workflow.Id ? (version.Id, version.DefinitionJson) : null);
        }
    }

    private sealed class Scopes(RunLifecycleWorld world) : IWorkflowRunScopeFactory
    {
        public IWorkflowRunScope CreateScope() => new Scope(world);

        private sealed class Scope(RunLifecycleWorld world) : IWorkflowRunScope
        {
            public IServiceProvider Services { get; } = new NoServices();
            public IWorkflowStore Store => world.Store;
            public IWorkflowRunJournal Journal => world.Journal;
            public IWorkflowAuditSink Audit => world.Audit;
            public IWorkflowAuthorization Authorization { get; } = new Everyone();
            public void Dispose() { }
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    internal sealed class RecordingAudit : IWorkflowAuditSink
    {
        public ConcurrentQueue<WorkflowAuditEvent> Events { get; } = new();

        public Action? WhileWriting { get; set; }

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            WhileWriting?.Invoke();
            Events.Enqueue(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class Everyone : IWorkflowAuthorization
    {
        public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class Blocks(RunLifecycleWorld world) : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) => blockTypeId switch
        {
            "Hold" => new HoldFactory(world),
            "Tick" => new TickFactory(world),
            PayloadSourceType => new PayloadSourceFactory(),
            BoxType => new BoxFactory(),
            LoopType => new LoopFactory(world),
            BareType => new BareFactory(),
            TryType => new TryFactory(),
            BoomType => new BoomFactory(),
            LoopValueType => new LoopValueFactory(world),
            _ => world.AdditionalBlocks?.Invoke(blockTypeId)
        };

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll()
        {
            var blocks = new List<IWorkflowBlockFactory>(
                new[] { "Hold", "Tick", PayloadSourceType, BoxType, LoopType, BareType, TryType, BoomType, LoopValueType }.Select(id => Find(id)!));
            foreach (var extraId in new[] { "test.unwritable", "test.counted", "test.produce" })
            {
                if (world.AdditionalBlocks?.Invoke(extraId) is { } extra)
                    blocks.Add(extra);
            }
            return blocks;
        }
    }

    private sealed class HoldFactory(RunLifecycleWorld world) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "Hold";
        public bool IsIdempotent => world.HoldIdempotent;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new HoldBlock(world);
    }

    private sealed class HoldBlock(RunLifecycleWorld world) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            world.HoldCallDepths.Enqueue(WorkflowCallDepthScope.Current);
            var entry = Interlocked.Increment(ref world.holdEntries);
            if (world.HoldThrowsAtEntry == entry)
                throw new InvalidOperationException($"The hold block failed on entry {entry}.");
            if (world.HoldsAtEntry is { } blocksAt && entry != blocksAt)
                return new Dictionary<string, object?> { ["entry"] = entry };
            if (world.HoldsAtEntry == entry)
                world.CapturedLiveCompleted = WorkflowRunFrames.Current.LastOrDefault()?.Completed;
            world.Entered.TrySetResult();
            if (entry <= world.FailHoldTimes)
                throw new InvalidOperationException("The hold block failed.");
            if (world.HoldThrows is { } thrown)
                throw thrown();
            if (world.HoldCancelsItself)
                throw new TaskCanceledException("The block's own client gave up.");
            if (world.ParkAtHold)
                throw new WorkflowSuspendException(
                    new SuspendResult(WorkflowSuspensionKind.WaitForTrigger, "{}", DateTimeOffset.UtcNow.AddHours(1)));
            try
            {
                await world.Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException ex) when (world.HoldWrapsItsCancellation)
            {
                throw new InvalidOperationException("The carrier call failed.", ex);
            }
            return new Dictionary<string, object?>();
        }
    }

    private sealed class BoxFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => BoxType;
        public bool IsIdempotent => true;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<string> SubCanvasNames => ["Body"];
        public IWorkflowBlockPlugin Create() => new BoxBlock();
    }

    private sealed class BoxBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            context.SubCanvasExecutor!.ExecuteAsync("Body", new Dictionary<string, object?>(), cancellationToken);
    }

    private sealed class LoopFactory(RunLifecycleWorld world) : IWorkflowBlockFactory
    {
        public string BlockTypeId => LoopType;
        public bool IsIdempotent => false;
        public bool OnlyDrivesItsSubCanvases => true;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => world.CollectLoopResults
            ? [new BlockPortDefinition("Results", "Results", PortValueType.Json, false)]
            : [];
        public IReadOnlyCollection<string> SubCanvasNames => ["Body"];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasInputs(string subCanvasName) =>
            [new BlockPortDefinition("Index", "Index", PortValueType.Int, false)];
        public IReadOnlyCollection<BlockPortDefinition> GetSubCanvasOutputs(string subCanvasName) =>
            [new BlockPortDefinition("Item", "Item", PortValueType.Int, false)];
        public IWorkflowBlockPlugin Create() => new LoopBlock(world);
    }

    private sealed class LoopBlock(RunLifecycleWorld world) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var results = new List<object?>();
            for (var iteration = 0; iteration < world.LoopIterations; iteration++)
            {
                var result = await context.SubCanvasExecutor!.ExecuteAsync(
                    "Body", new Dictionary<string, object?> { ["Index"] = iteration }, cancellationToken);
                if (world.CollectLoopResults && result.TryGetValue("Item", out var item))
                    results.Add(item);
            }

            return world.CollectLoopResults
                ? new Dictionary<string, object?>
                {
                    ["Results"] = JsonDocument.Parse(JsonSerializer.Serialize(results)).RootElement.Clone()
                }
                : new Dictionary<string, object?> { ["Iterations"] = world.LoopIterations };
        }
    }

    private sealed class LoopValueFactory(RunLifecycleWorld world) : IWorkflowBlockFactory
    {
        public string BlockTypeId => LoopValueType;
        public bool IsIdempotent => true;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [new("Index", "Index", PortValueType.Int, false)];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new("Value", "Value", PortValueType.Int, false)];
        public IWorkflowBlockPlugin Create() => new LoopValueBlock(world);
    }

    private sealed class LoopValueBlock(RunLifecycleWorld world) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var index = Convert.ToInt32(inputs["Index"]);
            world.LoopValueEntries.Enqueue(index);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?> { ["Value"] = index });
        }
    }

    private sealed class BareFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => BareType;
        public bool IsIdempotent => false;
        public bool OnlyDrivesItsSubCanvases => true;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new BareBlock();
    }

    private sealed class BareBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    private sealed class TryFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => TryType;
        public bool IsIdempotent => false;
        public bool OnlyDrivesItsSubCanvases => true;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<string> SubCanvasNames => ["Try", "After"];
        public IWorkflowBlockPlugin Create() => new TryBlock();
    }

    private sealed class TryBlock : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            var tried = await context.SubCanvasExecutor!.TryExecuteAsync("Try", new Dictionary<string, object?>(), cancellationToken);
            await context.SubCanvasExecutor.ExecuteAsync(
                "After", new Dictionary<string, object?> { ["Failed"] = tried.Error is not null }, cancellationToken);
            return new Dictionary<string, object?> { ["Failed"] = tried.Error is not null };
        }
    }

    private sealed class BoomFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => BoomType;
        public bool IsIdempotent => true;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new BoomBlock();
    }

    private sealed class BoomBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The boom block failed.");
    }

    private sealed class PayloadSourceFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => PayloadSourceType;
        public bool IsIdempotent => true;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [new BlockPortDefinition("payload", "payload", PortValueType.Json, false)];
        public IWorkflowBlockPlugin Create() => new PayloadSourceBlock();
    }

    private sealed class PayloadSourceBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    private sealed class TickFactory(RunLifecycleWorld world) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "Tick";
        public bool IsIdempotent => world.TickIdempotent;
        public string DisplayName => BlockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new TickBlock(world);
    }

    private sealed class TickBlock(RunLifecycleWorld world) : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref world.tickEntries);
            return Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    internal sealed class RecordingSuspensions : IWorkflowSuspensionStore
    {
        public ConcurrentQueue<SuspendRequest> Suspended { get; } = new();

        public Action? WhileSuspending { get; set; }

        public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken)
        {
            WhileSuspending?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Suspended.Enqueue(request);
            return Task.FromResult(new WorkflowSuspension(
                Guid.NewGuid(), request.ExecutionLogId, request.WorkflowId, request.BlockId, request.Kind,
                request.ConfigJson, request.StatePayloadJson, request.SuspendedAtUtc, request.TimeoutAtUtc));
        }

        public ConcurrentQueue<Guid> CancelledExecutions { get; } = new();

        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken)
        {
            CancelledExecutions.Enqueue(executionLogId);
            return Task.CompletedTask;
        }

        public ConcurrentQueue<WorkflowSuspension> Registered { get; } = new();

        public void RegisterForResumption(WorkflowSuspension suspension) => Registered.Enqueue(suspension);

        public ConcurrentQueue<Guid> TornDown { get; } = new();

        public void TearDownByExecution(Guid executionLogId) => TornDown.Enqueue(executionLogId);
    }
}
