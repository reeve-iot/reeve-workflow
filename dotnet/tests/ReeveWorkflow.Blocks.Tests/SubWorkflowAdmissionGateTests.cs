using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class SubWorkflowAdmissionGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_real_subworkflow_resumes_a_parked_child_through_the_waker_with_unmasked_parent_parity()
    {
        var world = new World();
        var note = new string('n', 20_000);
        const string resultType = "test.child-result";
        var parameters = """[{"name":"auth","type":"Json"},{"name":"note","type":"String"}]""";
        world.PayloadMode = true;
        world.AddPayloadResult(resultType, note);
        var child = world.Add(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "result", blockTypeId = resultType, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "hold", blockTypeId = World.Hold, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "out", blockTypeId = "WorkflowOutput", position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[]
            {
                new { id = "e1", sourceNodeId = "start", targetNodeId = "result", mappings = new { } },
                new { id = "e2", sourceNodeId = "result", targetNodeId = "hold", mappings = new { } },
                new
                {
                    id = "e3", sourceNodeId = "result", targetNodeId = "out",
                    mappings = new { auth = new { type = "direct", sourceOutputName = "auth", projection = new { path = "$" } }, note = new { type = "direct", sourceOutputName = "note" } }
                }
            }
        }), parameters);
        var parent = world.Add(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "call", blockTypeId = "SubWorkflow", configuration = new { WorkflowId = child.ToString() }, position = new { x = 0, y = 0 } },
                new { id = "out", blockTypeId = "WorkflowOutput", position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[]
            {
                new { id = "e", sourceNodeId = "start", targetNodeId = "call", mappings = new { } },
                new
                {
                    id = "e2", sourceNodeId = "call", targetNodeId = "out",
                    mappings = new
                    {
                        auth = new { type = "direct", sourceOutputName = "auth", projection = new { path = "$" } },
                        note = new { type = "direct", sourceOutputName = "note" },
                    }
                }
            }
        }), parameters);
        using var engine = world.Engine();

        await engine.ExecuteAsync(parent, null, null);
        var childLog = world.Journal.Logs.Values.Single(log => log.WorkflowId == child);
        var parentLog = world.Journal.Logs.Values.Single(log => log.WorkflowId == parent);
        childLog.Status.Should().Be(WorkflowExecutionStatus.Running);
        parentLog.Status.Should().Be(WorkflowExecutionStatus.Running);

        world.PayloadReady = true;
        var childSuspension = world.Suspensions.Rows.Single(row => row.Suspension.WorkflowId == child).Suspension;
        var childContext = await world.Suspensions.TryResolveAsync(
            childSuspension.Id, WorkflowSuspensionStatus.Resumed, "{}", DateTimeOffset.UtcNow, CancellationToken.None);
        (await world.Resumer(engine).ResumeFromSuspensionAsync(childContext!, CancellationToken.None))
            .Should().Be(WorkflowResumeOutcome.Resumed);
        var resumedChildLog = (await world.Journal.LoadRunAsync(childLog.Id, CancellationToken.None))!;
        resumedChildLog.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        resumedChildLog.OutputsJson.Should().Contain("\"token\":\"***\"");
        resumedChildLog.OutputsJson.Should().Contain("...[truncated]");
        resumedChildLog.ResultOutputsJson.Should().NotBeNull();
        using (var childRaw = JsonDocument.Parse(resumedChildLog.ResultOutputsJson!))
            childRaw.RootElement.GetProperty("note").GetString().Should().Be(note);

        (await world.Waker(engine).WakeOnceAsync(CancellationToken.None)).Should().Be(1);

        var parentNodeOutputs = world.NodeObserver.Completed.Last(item => item.NodeId == "call").Outputs;
        ((JsonElement)parentNodeOutputs["auth"]!).GetProperty("token").GetString().Should().Be("t-raw");
        parentNodeOutputs["note"].Should().Be(note);
        parentLog.OutputsJson.Should().Contain("\"token\":\"***\"");

        var direct = await engine.ExecuteAsync(parent, null, null);
        JsonSerializer.Serialize(direct).Should().Be(JsonSerializer.Serialize(parentNodeOutputs));
        direct["note"].Should().Be(note);
    }

    [Fact]
    public async Task K07_through_the_engine_a_literal_into_WorkflowId_is_refused_before_the_first_node()
    {
        var world = new World();
        var configured = world.Add(Graph());
        var literal = world.Add(Graph());
        var root = world.Add(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "hold", blockTypeId = World.Hold, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "call", blockTypeId = "SubWorkflow", configuration = new { WorkflowId = configured.ToString() }, position = new { x = 0, y = 0 } }
            },
            edges = new object[]
            {
                new { id = "e1", sourceNodeId = "start", targetNodeId = "hold", mappings = new { } },
                new
                {
                    id = "e2", sourceNodeId = "hold", targetNodeId = "call",
                    mappings = new Dictionary<string, object>
                    {
                        ["WorkflowId"] = new { type = "literal", literalValue = literal.ToString() }
                    }
                }
            }
        }));
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(root, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        refusal.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ReferenceInputMapped && d.NodeId == "call");
        world.Entered.Task.IsCompleted.Should().BeFalse("the hold before the call is the first node, and it never ran");
    }

    [Fact]
    public async Task A_child_is_started_with_the_nodes_other_inputs_and_not_its_WorkflowId()
    {
        var world = new World();
        var child = world.Add(Graph(("c", World.Leaf)));
        var root = world.Add(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "call", blockTypeId = "SubWorkflow", configuration = new { WorkflowId = child.ToString() }, position = new { x = 0, y = 0 } }
            },
            edges = new object[] { new { id = "e", sourceNodeId = "start", targetNodeId = "call", mappings = new { } } }
        }));
        using var engine = world.Engine();

        await engine.ExecuteAsync(root, null, null).WaitAsync(Patience);

        var childRow = world.Journal.Logs.Values.Should().ContainSingle(l => l.WorkflowId == child).Which;
        (childRow.Status, childRow.FailureReason).Should().Be((WorkflowExecutionStatus.Succeeded, (string?)null),
            "the child declares no parameters, and an argument it does not declare would refuse its start");
    }

    [Fact]
    public async Task A_call_into_a_single_mode_child_that_is_already_running_fails_the_parent_s_step_with_the_reason()
    {
        var world = new World();
        var child = world.Add(Graph(("hold", World.Hold)), mode: WorkflowMode.Single);
        var parent = world.Add(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "call", blockTypeId = "SubWorkflow", configuration = new { WorkflowId = child.ToString() }, position = new { x = 0, y = 0 } },
                new { id = "after", blockTypeId = World.Leaf, position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[]
            {
                new { id = "e1", sourceNodeId = "start", targetNodeId = "call", mappings = new { } },
                new { id = "e2", sourceNodeId = "call", targetNodeId = "after", mappings = new { } }
            }
        }));
        using var engine = world.Engine();

        var inFlight = engine.ExecuteAsync(child, null, null);
        try
        {
            await world.Entered.Task.WaitAsync(Patience);

            var failure = (await FluentActions.Awaiting(() => engine.ExecuteAsync(parent, null, null).WaitAsync(Patience))
                .Should().ThrowAsync<WorkflowNodeExecutionException>()).Which;

            failure.NodeId.Should().Be("call");
            failure.Message.Should().Contain("not started").And.Contain("single_mode_already_running");
        }
        finally
        {
            world.Release.TrySetResult();
        }
        await inFlight.WaitAsync(Patience);

        world.NodeObserver.Failed.Should().ContainSingle(f => f.WorkflowId == parent)
            .Which.Should().Match<(Guid WorkflowId, string NodeId, string Error)>(
                f => f.NodeId == "call" && f.Error.Contains("single_mode_already_running"),
                "the step is recorded failed, with why, rather than green with no outputs");
        world.NodeObserver.Completed.Should().NotContain(c => c.NodeId == "after", "the run did not walk on past the call");
        var parentRow = world.Journal.Logs.Values.Should().ContainSingle(l => l.WorkflowId == parent).Which;
        parentRow.Status.Should().Be(WorkflowExecutionStatus.Failed);
        world.Journal.Logs.Values.Should().ContainSingle(l => l.WorkflowId == child, "the refused call started no run of its own");
    }

    [Fact]
    public async Task The_child_the_block_starts_is_refused_as_did_not_compile_when_its_version_no_longer_compiles()
    {
        var world = new World();
        var child = world.Add(Graph(("c", World.Leaf)));
        var root = world.Add(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "hold", blockTypeId = World.Hold, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "call", blockTypeId = "SubWorkflow", configuration = new { WorkflowId = child.ToString() }, position = new { x = 0, y = 0 } }
            },
            edges = new object[]
            {
                new { id = "e1", sourceNodeId = "start", targetNodeId = "hold", mappings = new { } },
                new { id = "e2", sourceNodeId = "hold", targetNodeId = "call", mappings = new { } }
            }
        }));
        using var engine = world.Engine();

        var run = engine.ExecuteAsync(root, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        world.Remove(World.Leaf);
        world.Release.SetResult();
        await FluentActions.Awaiting(() => run.WaitAsync(Patience)).Should().ThrowAsync<Exception>();

        var childRow = world.Journal.Logs.Values.Should().ContainSingle(l => l.WorkflowId == child).Which;
        childRow.Status.Should().Be(WorkflowExecutionStatus.Failed);
        childRow.FailureReason.Should().Be(WorkflowFailureReasons.DidNotCompile, "a child is refused at admission as a root start is, though it runs under its parent's manifest");
        childRow.ErrorMessage.Should().Contain(World.Leaf);
    }

    [Fact]
    public async Task A_parent_whose_pinned_child_version_is_gone_fails_at_the_call_saying_not_found_and_no_child_row_is_written()
    {
        var world = new World();
        var child = world.Add(Graph(("c", World.Leaf)));
        var v1 = world.Workflows.Items[child].CurrentVersion!.Id;
        var root = world.Add(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "hold", blockTypeId = World.Hold, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "call", blockTypeId = "SubWorkflow", configuration = new { WorkflowId = child.ToString() }, position = new { x = 0, y = 0 } }
            },
            edges = new object[]
            {
                new { id = "e1", sourceNodeId = "start", targetNodeId = "hold", mappings = new { } },
                new { id = "e2", sourceNodeId = "hold", targetNodeId = "call", mappings = new { } }
            }
        }));
        using var engine = world.Engine();

        var run = engine.ExecuteAsync(root, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        var v2 = new WorkflowVersion(Guid.NewGuid(), child, 2, Graph(("c", World.Leaf)), null, null, Guid.NewGuid(), DateTimeOffset.UtcNow, isPublished: true);
        world.Workflows.Versions[v2.Id] = v2;
        world.Workflows.Items[child].SetCurrentVersion(v2, DateTimeOffset.UtcNow);
        world.Workflows.Versions.TryRemove(v1, out _);
        world.Release.SetResult();
        var failure = (await FluentActions.Awaiting(() => run.WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>()).Which;

        failure.InnerException.Should().BeOfType<PinnedWorkflowVersionNotFoundException>().Which.VersionId.Should().Be(v1);
        var row = world.Journal.Logs.Values.Should().ContainSingle("the child's admission refused it before its row").Which;
        (row.WorkflowId, row.Status, row.FailureReason).Should().Be((root, WorkflowExecutionStatus.Failed, (string?)null));
        row.ErrorMessage.Should().Be(failure.InnerException!.Message);
    }

    [Fact]
    public async Task The_block_s_child_is_a_sub_workflow_start_the_host_is_asked_for_after_the_root_s_own()
    {
        var world = new World();
        var child = world.Add(Graph(("c", World.Leaf)));
        var root = world.Add(CallingGraph(child));
        var host = new RecordingHost();
        using var engine = world.Engine(host);

        await engine.ExecuteAsync(root, null, null).WaitAsync(Patience);

        var rootLog = world.Journal.Logs.Values.Single(l => l.WorkflowId == root);
        host.Asked.Select(r => (r.WorkflowId, r.Kind)).Should().Equal(
            (root, WorkflowStartKind.Manual), (child, WorkflowStartKind.SubWorkflow));
        host.Asked[1].ParentExecutionId.Should().Be(rootLog.Id);
        host.Asked[1].CallDepth.Should().Be(1);
    }

    [Fact]
    public async Task A_child_the_host_refuses_fails_the_calling_node_with_the_refusal_and_has_no_run_of_its_own()
    {
        var world = new World();
        var child = world.Add(Graph(("c", World.Leaf)));
        var root = world.Add(CallingGraph(child));
        var host = new RecordingHost
        {
            Decision = r => r.WorkflowId == child
                ? WorkflowStartDecision.Refuse("workflow-disabled", "The child is disabled.")
                : WorkflowStartDecision.Admit
        };
        using var engine = world.Engine(host);

        var failure = (await FluentActions.Awaiting(() => engine.ExecuteAsync(root, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>()).Which;

        failure.NodeId.Should().Be("call");
        var refusal = failure.InnerException.Should().BeOfType<WorkflowStartRefusedException>().Which;
        (refusal.WorkflowId, refusal.Code).Should().Be((child, "workflow-disabled"));
        world.Journal.Logs.Values.Should().ContainSingle("the child was refused before it had a row").Which
            .WorkflowId.Should().Be(root);
        host.Asked.Should().HaveCount(2, "the refused child is asked about once, not once per try");
    }

    [Fact]
    public async Task A_parent_replayed_from_its_start_is_not_asked_but_the_child_it_starts_again_is()
    {
        var world = new World();
        var child = world.Add(Graph(("c", World.Leaf)));
        var root = world.Add(CallingGraph(child));
        var host = new RecordingHost { Decision = r => r.WorkflowId == root ? WorkflowStartDecision.Refuse("no", "No.") : WorkflowStartDecision.Admit };
        using var engine = world.Engine(host);
        var abandoned = world.RunState.Seed(root);

        (await ((IWorkflowEngine)engine).ReplayAbandonedRunAsync(abandoned, root).WaitAsync(Patience))
            .Should().Be(WorkflowReplayOutcome.Finished);

        var asked = host.Asked.Should().ContainSingle("the replayed root was admitted once already").Which;
        (asked.WorkflowId, asked.Kind).Should().Be((child, WorkflowStartKind.SubWorkflow));
    }

    private static string CallingGraph(Guid child) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = "call", blockTypeId = "SubWorkflow", configuration = new { WorkflowId = child.ToString() }, position = new { x = 0, y = 0 } }
        },
        edges = new object[] { new { id = "e", sourceNodeId = "start", targetNodeId = "call", mappings = new { } } }
    });

    private sealed class RecordingHost : IWorkflowStartAdmission
    {
        private readonly ConcurrentQueue<WorkflowStartRequest> asked = new();

        public IReadOnlyList<WorkflowStartRequest> Asked => [.. asked];

        public Func<WorkflowStartRequest, WorkflowStartDecision> Decision { get; init; } = _ => WorkflowStartDecision.Admit;

        public ValueTask<WorkflowStartDecision> DecideAsync(WorkflowStartRequest request, CancellationToken cancellationToken)
        {
            asked.Enqueue(request);
            return ValueTask.FromResult(Decision(request));
        }
    }

    private static string Graph(params (string Id, string Block)[] nodes)
    {
        var all = new List<object> { new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } } };
        var edges = new List<object>();
        var previous = "start";
        foreach (var (id, block) in nodes)
        {
            all.Add(new { id, blockTypeId = block, position = new { x = 0, y = 0 }, configuration = new { } });
            edges.Add(new { id = $"{previous}-{id}", sourceNodeId = previous, targetNodeId = id, mappings = new { } });
            previous = id;
        }

        return JsonSerializer.Serialize(new { nodes = all, edges });
    }

    private sealed class World : IWorkflowBlocks, IWorkflowRunScopeFactory
    {
        public const string Leaf = "Leaf";
        public const string Hold = "Hold";

        private static readonly DateTimeOffset T0 = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        private readonly ConcurrentDictionary<string, IWorkflowBlockFactory> catalogue = new(StringComparer.OrdinalIgnoreCase);
        private readonly Guid owner = Guid.NewGuid();
        private int generation;
        private IWorkflowEngine? engine;

        public World()
        {
            foreach (var factory in new IWorkflowBlockFactory[]
                     {
                         new SubWorkflowBlockFactory(), new TestFactory(Leaf, this, false), new TestFactory(Hold, this, true),
                     })
                catalogue[factory.BlockTypeId] = factory;
        }

        public Store Workflows { get; } = new();
        public Journal Journal { get; } = new();
        public StateStore RunState { get; } = new();
        public SuspensionStore Suspensions { get; } = new();
        public RecordingObserver NodeObserver { get; } = new();
        public bool PayloadReady;
        public bool PayloadMode;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Remove(string blockTypeId)
        {
            catalogue.TryRemove(blockTypeId, out _);
            Interlocked.Increment(ref generation);
        }

        public void AddPayloadResult(string blockTypeId, string note) =>
            catalogue[blockTypeId] = new PayloadResultFactory(blockTypeId, note);

        private static string CompleteMissingNodeFields(string json)
        {
            var root = JsonNode.Parse(json);
            CompleteCanvas(root as JsonObject);
            return root?.ToJsonString() ?? json;
        }

        private static void CompleteCanvas(JsonObject? canvas)
        {
            if (canvas is null)
                return;
            if (!canvas.ContainsKey("edges")) canvas["edges"] = new JsonArray();
            if (canvas["edges"] is JsonArray edges)
                foreach (var edge in edges.OfType<JsonObject>()) edge.TryAdd("mappings", new JsonObject());
            if (canvas["nodes"] is not JsonArray nodes) return;
            foreach (var node in nodes.OfType<JsonObject>())
            {
                node.TryAdd("position", new JsonObject { ["x"] = 0, ["y"] = 0 });
                node.TryAdd("configuration", new JsonObject());
                if (node["subCanvases"] is JsonObject branches)
                    foreach (var branch in branches.Select(pair => pair.Value).OfType<JsonObject>())
                        CompleteCanvas(branch);
            }
        }

        public Guid Add(string definitionJson, string? outputParametersJson = null, WorkflowMode mode = WorkflowMode.Parallel)
        {
            definitionJson = CompleteMissingNodeFields(definitionJson);
            var id = Guid.NewGuid();
            var workflow = new Workflow(id, "wf", null, owner, T0);
            workflow.ConfigureConcurrency(mode, 4, 10, WorkflowOverflowBehavior.Drop, T0);
            var version = new WorkflowVersion(Guid.NewGuid(), id, 1, definitionJson, null, outputParametersJson, owner, T0, isPublished: true);
            workflow.SetCurrentVersion(version, T0);
            Workflows.Items[id] = workflow;
            Workflows.Versions[version.Id] = version;
            return id;
        }

        public WorkflowEngine Engine(IWorkflowStartAdmission? startAdmission = null)
        {
            var built = new WorkflowEngine(
                new WorkflowEngineSeams
                {
                    RunScopes = this,
                    Blocks = this,
                    Compiler = new WorkflowCompilationService(this, this),
                    ExecutionGate = AlwaysRunningExecutionGate.Instance,
                    Metrics = NullWorkflowMetrics.Instance,
                    NodeObserver = NodeObserver,
                    RunStateStore = RunState,
                    SuspensionStore = Suspensions,
                    StartAdmission = startAdmission
                },
                new WorkflowEngineSettings { ReplicaId = "replica-1" },
                NullLoggerFactory.Instance);
            engine = built;
            return built;
        }

        public IWorkflowBlockFactory? Find(string blockTypeId) => catalogue.GetValueOrDefault(blockTypeId);

        public WorkflowRunResumer Resumer(WorkflowEngine engine) => new(
            this,
            new WorkflowGraphExecutor(this, NullLoggerFactory.Instance, NullWorkflowMetrics.Instance,
                NodeObserver, NullHelperResolver.Instance),
            AlwaysRunningExecutionGate.Instance,
            NullWorkflowMetrics.Instance,
            NullLogger.Instance,
            new WorkflowResumeOptions
            {
                ReplicaId = "replica-1", StateRowTimeout = TimeSpan.FromSeconds(1), MaxPayloadValueBytes = 1_024
            },
            new Core.Compilation.WorkflowCompilationService(this, this),
            RunState,
            Suspensions,
            engine.Coordinators);

        public WorkflowSuspensionWaker Waker(WorkflowEngine engine) => new(
            Suspensions, Resumer(engine), NullLogger.Instance, new WorkflowCompilationService(this, this),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero }, scopes: this);

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [.. catalogue.Values];

        public string Generation => Volatile.Read(ref generation).ToString();

        public IWorkflowRunScope CreateScope() => new Scope(this);

        private sealed class Scope(World world) : IWorkflowRunScope, IServiceProvider
        {
            public IServiceProvider Services => this;
            public IWorkflowStore Store => world.Workflows;
            public IWorkflowRunJournal Journal => world.Journal;
            public IWorkflowAuditSink Audit { get; } = new NoAudit();
            public IWorkflowAuthorization Authorization { get; } = new Everyone();

            public object? GetService(Type serviceType) =>
                serviceType == typeof(IWorkflowEngine) ? world.engine : null;

            public void Dispose() { }
        }

        private sealed class TestFactory(string id, World world, bool holds) : IWorkflowBlockFactory
        {
            public string BlockTypeId => id;
            public string DisplayName => id;
            public string Description => string.Empty;
            public string Category => "test";
            public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
            public IReadOnlyCollection<BlockPortDefinition> Outputs => holds && world.PayloadMode
                ? [new BlockPortDefinition("auth", "auth", PortValueType.Json, false),
                   new BlockPortDefinition("note", "note", PortValueType.String, false)]
                : [];
            public IWorkflowBlockPlugin Create() => new TestBlock(world, holds);
        }

        private sealed class TestBlock(World world, bool holds) : IWorkflowBlockPlugin
        {
            public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
            {
                if (holds)
                {
                    if (world.PayloadMode)
                    {
                        if (!world.PayloadReady)
                            throw new WorkflowSuspendException(new SuspendResult(
                                WorkflowSuspensionKind.WaitForTrigger, "{}", DateTimeOffset.MaxValue));
                        return new Dictionary<string, object?>
                        {
                            ["auth"] = JsonSerializer.SerializeToElement(new { token = "t-raw" }),
                            ["note"] = new string('n', 20_000)
                        };
                    }
                    world.Entered.TrySetResult();
                    await world.Release.Task.WaitAsync(cancellationToken);
                }

                return new Dictionary<string, object?>();
            }
        }

        private sealed class PayloadResultFactory(string id, string note) : IWorkflowBlockFactory
        {
            public string BlockTypeId => id;
            public string DisplayName => id;
            public string Description => string.Empty;
            public string Category => "test";
            public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
            public IReadOnlyCollection<BlockPortDefinition> Outputs =>
            [
                new BlockPortDefinition("auth", "auth", PortValueType.Json, false),
                new BlockPortDefinition("note", "note", PortValueType.String, false)
            ];
            public bool IsIdempotent => true;
            public IWorkflowBlockPlugin Create() => new PayloadResultBlock(note);
        }

        private sealed class PayloadResultBlock(string note) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
                CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, object?>>(
                new Dictionary<string, object?>
                {
                    ["auth"] = JsonSerializer.SerializeToElement(new { token = "t-raw" }),
                    ["note"] = note
                });
        }

    }

    private sealed class Store : IWorkflowStore
    {
        public ConcurrentDictionary<Guid, Workflow> Items { get; } = new();
        public ConcurrentDictionary<Guid, WorkflowVersion> Versions { get; } = new();

        public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(workflowId));

        public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult(Versions.GetValueOrDefault(versionId));

        public Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
            Guid workflowId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.GetValueOrDefault(workflowId)?.CurrentVersion is { } current
                ? ((Guid, string)?)(current.Id, current.DefinitionJson)
                : null);
    }

    private sealed class Journal : IWorkflowRunJournal
    {
        public ConcurrentDictionary<Guid, WorkflowExecutionLog> Logs { get; } = new();

        public Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken)
        {
            Logs[log.Id] = log;
            return Task.CompletedTask;
        }

        public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
            Task.FromResult(Logs.GetValueOrDefault(executionLogId));

        public Task SaveRunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveRunDetailsAsync(
            Guid? executionId, bool recordLogEntries, IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
            IReadOnlyList<WorkflowExecutionStep> executionSteps, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StateStore : IWorkflowRunStateStore
    {
        private readonly ConcurrentDictionary<Guid, WorkflowRunStateRow> rows = new();

        public Guid Seed(Guid workflowId)
        {
            var id = Guid.NewGuid();
            rows[id] = new WorkflowRunStateRow
            {
                ExecutionId = id, WorkflowId = workflowId, Status = WorkflowRunStateStatus.Running,
                OwnedByReplicaId = "replica-1", CurrentBlockPath = "start", LastTransitionUtc = DateTimeOffset.UtcNow
            };
            return id;
        }

        public Task<Guid> BeginAsync(WorkflowRunStateBegin request, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            rows[id] = new WorkflowRunStateRow
            {
                ExecutionId = id, WorkflowId = request.WorkflowId, WorkflowVersion = request.WorkflowVersion,
                Status = WorkflowRunStateStatus.Running, VariablesJson = request.VariablesJson,
                OwnedByReplicaId = request.OwnedByReplicaId, ParentExecutionId = request.ParentExecutionId,
                PinnedVersionsJson = request.PinnedVersionsJson, StartedAtUtc = DateTimeOffset.UtcNow,
                LastTransitionUtc = DateTimeOffset.UtcNow
            };
            return Task.FromResult(id);
        }

        public Task<WorkflowRunStateRow?> GetAsync(Guid executionId, CancellationToken cancellationToken) =>
            Task.FromResult(rows.GetValueOrDefault(executionId));

        public Task<bool> TransitionAsync(WorkflowRunStateTransition request, CancellationToken cancellationToken)
        {
            if (!rows.TryGetValue(request.ExecutionId, out var row)) return Task.FromResult(false);
            rows[request.ExecutionId] = row with
            {
                Status = request.ToStatus, CurrentBlockPath = request.CurrentBlockPath,
                VariablesJson = request.VariablesJson, WaitUntilUtc = request.WaitUntilUtc,
                WaitForEventKey = request.WaitForEventKey, OwnedByReplicaId = request.OwnedByReplicaId,
                LastTransitionUtc = DateTimeOffset.UtcNow, Version = row.Version + 1
            };
            return Task.FromResult(true);
        }

        public Task<bool> CompleteAsync(
            Guid executionId, WorkflowRunStateStatus terminalStatus, string? failureReason, CancellationToken cancellationToken)
        {
            if (!rows.TryGetValue(executionId, out var row)) return Task.FromResult(false);
            rows[executionId] = row with
            {
                Status = terminalStatus, FailureReason = failureReason,
                CompletedAtUtc = DateTimeOffset.UtcNow, LastTransitionUtc = DateTimeOffset.UtcNow, Version = row.Version + 1
            };
            return Task.FromResult(true);
        }
    }

    private sealed class SuspensionStore : IWorkflowSuspensionStore, IWorkflowSuspensionResumeStore
    {
        public sealed record Parked(
            WorkflowSuspension Suspension, WorkflowSuspensionStatus Status, string? OutcomeJson, int TakeCount);

        private readonly ConcurrentDictionary<Guid, Parked> rows = new();

        public ICollection<Parked> Rows => rows.Values;

        public Task<WorkflowSuspension> SuspendAsync(SuspendRequest request, CancellationToken cancellationToken)
        {
            var suspension = new WorkflowSuspension(Guid.NewGuid(), request.ExecutionLogId, request.WorkflowId,
                request.BlockId, request.Kind, request.ConfigJson, request.StatePayloadJson,
                request.SuspendedAtUtc, request.TimeoutAtUtc);
            rows[suspension.Id] = new Parked(suspension, WorkflowSuspensionStatus.Suspended, null, 0);
            return Task.FromResult(suspension);
        }

        public Task CancelByExecutionAsync(Guid executionLogId, CancellationToken cancellationToken)
        {
            foreach (var row in rows.Values.Where(item => item.Suspension.ExecutionLogId == executionLogId))
                rows.TryRemove(row.Suspension.Id, out _);
            return Task.CompletedTask;
        }

        public void RegisterForResumption(WorkflowSuspension suspension) { }
        public void TearDownByExecution(Guid executionLogId) { }

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken)
        {
            var candidates = rows.Values.Where(row => row.Status == WorkflowSuspensionStatus.Suspended)
                .Take(limit)
                .Select(row => new WorkflowSuspensionCandidate(
                    row.Suspension.Id, row.Suspension.ExecutionLogId, row.Suspension.WorkflowId,
                    row.Suspension.BlockId, row.Suspension.Kind, row.Suspension.ConfigJson,
                    row.Suspension.StatePayloadJson, row.Suspension.SuspendedAtUtc, row.Suspension.TimeoutAtUtc))
                .ToArray();
            return Task.FromResult(new WorkflowSuspensionScan(DateTimeOffset.UtcNow, candidates, 0));
        }

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken)
        {
            if (!rows.TryGetValue(suspensionId, out var row) || row.Status != WorkflowSuspensionStatus.Suspended)
                return Task.FromResult<ResumeContext?>(null);
            rows[suspensionId] = row with { Status = status, OutcomeJson = outcomeJson, TakeCount = row.TakeCount + 1 };
            return Task.FromResult<ResumeContext?>(new ResumeContext
            {
                SuspensionId = row.Suspension.Id, ExecutionLogId = row.Suspension.ExecutionLogId,
                WorkflowId = row.Suspension.WorkflowId, BlockId = row.Suspension.BlockId,
                Kind = row.Suspension.Kind, OutcomeJson = outcomeJson,
                StatePayloadJson = row.Suspension.StatePayloadJson, SuspendedAtUtc = row.Suspension.SuspendedAtUtc
            });
        }

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
        {
            if (!rows.TryGetValue(suspensionId, out var row) || row.Status != takenAs) return Task.FromResult(false);
            rows[suspensionId] = row with { Status = WorkflowSuspensionStatus.Suspended, OutcomeJson = null };
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingObserver : IWorkflowNodeObserver
    {
        public ConcurrentQueue<(string NodeId, IReadOnlyDictionary<string, object?> Outputs)> Completed { get; } = new();
        public Task NotifyNodeStartedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task NotifyNodeCompletedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, IReadOnlyDictionary<string, object?> outputs, long durationMs,
            CancellationToken cancellationToken = default)
        {
            Completed.Enqueue((nodeId, outputs));
            return Task.CompletedTask;
        }
        public ConcurrentQueue<(Guid WorkflowId, string NodeId, string Error)> Failed { get; } = new();
        public Task NotifyNodeFailedAsync(Guid workflowId, Guid executionId, string nodeId,
            IReadOnlyDictionary<string, object?> inputs, string error, long durationMs,
            CancellationToken cancellationToken = default)
        {
            Failed.Enqueue((workflowId, nodeId, error));
            return Task.CompletedTask;
        }
    }

    private sealed class NoAudit : IWorkflowAuditSink
    {
        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Everyone : IWorkflowAuthorization
    {
        public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
