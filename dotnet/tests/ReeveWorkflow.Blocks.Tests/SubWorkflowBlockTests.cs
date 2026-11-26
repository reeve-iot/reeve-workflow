using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class SubWorkflowBlockTests
{
    [Fact]
    public void The_factory_says_its_ports_come_from_elsewhere_and_names_a_real_reference_input()
    {
        var factory = new SubWorkflowBlockFactory();

        factory.PortsAreResolvedElsewhere.Should().BeTrue();
        factory.RunsWorkflowFrom.Should().NotBeNullOrEmpty();
        factory.Inputs.Should().Contain(p => p.Name == factory.RunsWorkflowFrom);
        factory.ConfigurationSchema.Should().Contain(f => f.Name == factory.RunsWorkflowFrom);
    }

    [Fact]
    public async Task A_workflow_that_reaches_itself_again_is_refused()
    {
        var a = Guid.NewGuid();
        var reentries = 0;
        Exception? reentry = null;
        IServiceProvider services = null!;

        var engine = new ReentrantEngine(async _ =>
        {
            if (reentries++ < 50)
                reentry = await Record.ExceptionAsync(() => Run(services, a));
            return Empty;
        });
        services = Container(engine);

        await Run(services, a);

        reentry.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("cycle");
    }

    [Fact]
    public async Task Two_workflows_that_reach_each_other_are_refused()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var calls = 0;
        Exception? cycle = null;
        IServiceProvider services = null!;

        var engine = new ReentrantEngine(async id =>
        {
            if (++calls > 50) return Empty;
            var ex = await Record.ExceptionAsync(() => Run(services, id == a ? b : a));
            if (ex is not null) cycle = ex;
            return Empty;
        });
        services = Container(engine);

        await Run(services, b);

        cycle.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Contain("cycle");
    }

    [Fact]
    public async Task A_chain_of_distinct_workflows_is_still_capped()
    {
        const int maxDepth = 10;
        var ids = Enumerable.Range(0, maxDepth + 1).Select(_ => Guid.NewGuid()).ToArray();
        var next = new Queue<Guid>(ids.Skip(1));
        var outcomes = new List<Exception?>();
        IServiceProvider services = null!;

        var engine = new ReentrantEngine(async _ =>
        {
            if (next.TryDequeue(out var id))
                outcomes.Add(await Record.ExceptionAsync(() => Run(services, id)));
            return Empty;
        });
        services = Container(engine);

        await Run(services, ids[0]);

        outcomes.Should().HaveCount(maxDepth);
        outcomes[0].Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("depth");
        outcomes.Skip(1).Should().AllSatisfy(e => e.Should().BeNull());
    }

    [Fact]
    public async Task Nesting_without_a_back_edge_succeeds_and_leaves_no_chain_behind()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var executed = new List<Guid>();
        IServiceProvider services = null!;

        var engine = new ReentrantEngine(async id =>
        {
            executed.Add(id);
            if (id == a) await Run(services, b);
            return Empty;
        });
        services = Container(engine);

        await Run(services, a);
        await Run(services, b);

        executed.Should().Equal([a, b, b], "b at the top level after the chain unwound is not a cycle");
    }

    [Fact]
    public async Task The_child_runs_the_version_the_root_run_pinned_for_it()
    {
        var child = Guid.NewGuid();
        var childVersion = Guid.NewGuid();
        WorkflowRunPin? seen = null;
        var engine = new ReentrantEngine(_ =>
        {
            seen = WorkflowRunPinScope.Consume();
            return Task.FromResult(Empty);
        });

        using (WorkflowVersionPinScope.Push(new Dictionary<Guid, Guid> { [child] = childVersion }))
            await Run(Container(engine), child);

        seen!.VersionId.Should().Be(childVersion,
            "a child republished mid-run must not change under the parent");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 7)]
    public async Task The_child_is_handed_one_call_depth_more_than_the_run_the_block_is_in(int runDepth, int childDepth)
    {
        WorkflowRunPin? seen = null;
        var engine = new ReentrantEngine(_ =>
        {
            seen = WorkflowRunPinScope.Consume();
            return Task.FromResult(Empty);
        });

        using (WorkflowCallDepthScope.Push(runDepth))
            await Run(Container(engine), Guid.NewGuid());

        seen!.CallDepth.Should().Be(childDepth);
    }

    [Fact]
    public async Task A_child_the_root_run_never_pinned_is_refused_rather_than_run_at_its_newest()
    {
        var engine = new ReentrantEngine(_ => Task.FromResult(Empty));
        var services = Container(engine);

        var act = async () =>
        {
            using (WorkflowVersionPinScope.Push(new Dictionary<Guid, Guid> { [Guid.NewGuid()] = Guid.NewGuid() }))
                await Run(services, Guid.NewGuid());
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*pinned*");
    }

    [Fact]
    public async Task K07_a_literal_into_WorkflowId_walked_under_the_configured_childs_manifest_fails_at_the_block()
    {
        var configuredChild = Guid.NewGuid();
        var configuredChildVersion = Guid.NewGuid();
        var literalChild = Guid.NewGuid();
        var root = RootWithMappingIntoWorkflowId(configuredChild, Lit(literalChild));

        var manifest = await BuildManifest(root, configuredChild, configuredChildVersion);
        manifest.Should().ContainKey(configuredChild).WhoseValue.Should().Be(configuredChildVersion,
            "the manifest reads the configured id and never a mapping");

        var engine = new CapturingEngine();
        var run = () => WalkRoot(root, manifest, engine, runInputs: null);

        (await run.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain(
            $"Sub-workflow '{literalChild}' is not in the run's pinned version manifest",
            "ApplyMappings hands the block the literal, which the manifest never pinned");
        engine.Executed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task K13_K14_a_wire_whose_value_is_the_configured_child_or_nothing_runs_the_configured_child(bool wireProducesValue)
    {
        var configuredChild = Guid.NewGuid();
        var configuredChildVersion = Guid.NewGuid();
        var root = RootWithMappingIntoWorkflowId(configuredChild, Wire());

        var manifest = await BuildManifest(root, configuredChild, configuredChildVersion);
        manifest.Should().ContainKey(configuredChild).WhoseValue.Should().Be(configuredChildVersion);

        var engine = new CapturingEngine();
        var runInputs = wireProducesValue
            ? new Dictionary<string, object?> { ["childId"] = configuredChild.ToString() }
            : null;
        await WalkRoot(root, manifest, engine, runInputs);

        engine.Executed.Should().Equal([configuredChild]);
    }

    [Fact]
    public async Task K13b_a_wire_producing_a_different_child_than_configured_runs_the_wires_value_and_is_refused()
    {
        var configuredChild = Guid.NewGuid();
        var configuredChildVersion = Guid.NewGuid();
        var wiredChild = Guid.NewGuid();
        var root = RootWithMappingIntoWorkflowId(configuredChild, Wire());

        var manifest = await BuildManifest(root, configuredChild, configuredChildVersion);
        manifest.Should().ContainKey(configuredChild).WhoseValue.Should().Be(configuredChildVersion,
            "the manifest still reads the configured id, never a mapping");

        var engine = new CapturingEngine();
        var runInputs = new Dictionary<string, object?> { ["childId"] = wiredChild.ToString() };
        var run = () => WalkRoot(root, manifest, engine, runInputs);

        (await run.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain(
            $"Sub-workflow '{wiredChild}' is not in the run's pinned version manifest",
            "ApplyMappings hands the block the wire's value, which the manifest never pinned");
        engine.Executed.Should().BeEmpty();
    }

    private static async Task WalkRoot(
        WorkflowDefinition root, IReadOnlyDictionary<Guid, Guid> manifest, IWorkflowEngine engine,
        IReadOnlyDictionary<string, object?>? runInputs)
    {
        var blocks = new RegisteredWorkflowBlocks([new SubWorkflowBlockFactory()]);
        var executor = new WorkflowGraphExecutor(
            blocks, NullLoggerFactory.Instance, new NullWorkflowMetrics(), new NullWorkflowNodeObserver(),
            NullHelperResolver.Instance);

        var seeded = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["start"] = runInputs ?? new Dictionary<string, object?>(),
        };

        using (WorkflowVersionPinScope.Push(manifest))
        {
            await executor.ExecuteGraphAsync(
                seeded, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Container(engine),
                [], [], WorkflowTraceMode.Full, false, null, null, depth: 0, visitedNodeIds: [],
                isInsideTryCatch: false, inputParametersForRun: runInputs, triggerIdForRun: null,
                triggerEnvelopeForRun: null, cancellationToken: CancellationToken.None,
                compiled: TestPrograms.Walk(root, blocks));
        }
    }

    private const string WorkflowIdField = "WorkflowId";

    private static object Lit(Guid value) => new { type = "literal", literalValue = value.ToString() };
    private static object Wire(string sourceOutput = "childId") => new { type = "direct", sourceOutputName = sourceOutput };

    private static WorkflowDefinition RootWithMappingIntoWorkflowId(Guid configuredChild, object mapping)
    {
        var start = new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId };
        var call = new WorkflowNodeDefinition
        {
            Id = "call",
            BlockTypeId = WorkflowGraphHelpers.SubWorkflowBlockTypeId,
            Config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(new { WorkflowId = configuredChild.ToString() })),
        };

        return new WorkflowDefinition
        {
            Nodes = [start, call],
            Edges =
            [
                new WorkflowConnectionDefinition
                {
                    Id = "e1", SourceNodeId = "start", TargetNodeId = "call",
                    Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { [WorkflowIdField] = mapping }),
                },
            ],
        };
    }

    private static WorkflowDefinition TrivialChild() => new()
    {
        Nodes = [new WorkflowNodeDefinition { Id = "start", BlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId }],
    };

    private static string Serialize(WorkflowDefinition definition) =>
        StoredWorkflowDocument.Write(definition, out _)!;

    private static Task<IReadOnlyDictionary<Guid, Guid>> BuildManifest(WorkflowDefinition root, Guid child, Guid childVersion) =>
        WorkflowVersionManifestBuilder.BuildAsync(
            Guid.NewGuid(), Guid.NewGuid(), Serialize(root),
            fetchCurrent: (id, _) => Task.FromResult(id == child
                ? ((Guid VersionId, string DefinitionJson)?)(childVersion, Serialize(TrivialChild()))
                : null),
            CancellationToken.None);

    [Fact]
    public async Task With_no_pin_in_scope_the_child_is_left_unpinned()
    {
        var ran = false;
        WorkflowRunPin? seen = null;
        var engine = new ReentrantEngine(_ =>
        {
            ran = true;
            seen = WorkflowRunPinScope.Consume();
            return Task.FromResult(Empty);
        });

        await Run(Container(engine), Guid.NewGuid());

        ran.Should().BeTrue();
        seen!.VersionId.Should().BeNull();
    }

    [Fact]
    public async Task A_host_that_refuses_the_child_stops_it_before_anything_of_it_runs()
    {
        var engine = new ReentrantEngine(_ => Task.FromResult(Empty));
        var services = Container(engine);
        var child = Guid.NewGuid();

        var act = () => new RefusingBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = child.ToString() },
            Context(services), CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedAccessException>()).Which.Message.Should().Contain(child.ToString());
        engine.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_is_reached_before_the_pin_manifest_can_report_on_the_child()
    {
        var services = Container(new ReentrantEngine(_ => Task.FromResult(Empty)));

        var act = async () =>
        {
            using (WorkflowVersionPinScope.Push(new Dictionary<Guid, Guid> { [Guid.NewGuid()] = Guid.NewGuid() }))
                await new RefusingBlock().ExecuteAsync(
                    new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() },
                    Context(services), CancellationToken.None);
        };

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task The_node_inputs_become_the_child_runs_parameters_and_the_parent_run_its_parent()
    {
        var child = Guid.NewGuid();
        var parentExecution = Guid.NewGuid();
        var engine = new CapturingEngine();
        var services = Container(engine);

        await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["WorkflowId"] = child.ToString(), ["Temperature"] = 21, ["Label"] = "kitchen"
            },
            new WorkflowBlockContext
            {
                ExecutionId = parentExecution, UserId = Guid.NewGuid(),
                Logger = NullLogger.Instance, Services = services
            },
            CancellationToken.None);

        engine.LastWorkflowId.Should().Be(child);
        engine.LastFire.Should().BeNull("nothing fired: the parent reached this block");
        engine.LastInputParameters!["Temperature"].Should().Be(21);
        engine.LastInputParameters["Label"].Should().Be("kitchen");
        engine.LastParentExecutionId.Should().Be(parentExecution);
    }

    [Fact]
    public async Task The_child_records_its_own_log_entries_whatever_the_parent_was_doing()
    {
        WorkflowRunObservation? seen = null;
        var engine = new ReentrantEngine(_ =>
        {
            seen = WorkflowRunObservationScope.Consume();
            return Task.FromResult(Empty);
        });

        await Run(Container(engine), Guid.NewGuid());

        seen!.RecordLogEntries.Should().BeTrue();
    }

    [Fact]
    public async Task The_childs_outputs_are_the_nodes_outputs_unchanged()
    {
        var engine = new CapturingEngine
        {
            Output = new Dictionary<string, object?> { ["Result"] = "ok", ["Count"] = 3 }
        };

        var result = await Run(Container(engine), Guid.NewGuid());

        result["Result"].Should().Be("ok");
        result["Count"].Should().Be(3);
    }

    [Fact]
    public async Task The_runs_cancellation_reaches_the_child()
    {
        var engine = new CapturingEngine();
        using var cts = new CancellationTokenSource();

        await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() },
            Context(Container(engine)), cts.Token);

        engine.LastCancellationToken.Should().Be(cts.Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("guid-object")]
    public async Task A_WorkflowId_that_is_not_a_guid_string_starts_nothing(string? workflowId)
    {
        var engine = new CapturingEngine();
        var inputs = workflowId switch
        {
            null => new Dictionary<string, object?>(),
            "guid-object" => new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid() },
            _ => new Dictionary<string, object?> { ["WorkflowId"] = workflowId }
        };

        var act = () => new SubWorkflowBlock().ExecuteAsync(inputs, Context(Container(engine)), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*WorkflowId*");
        engine.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_child_that_parked_parks_the_block_that_started_it_rather_than_answering_with_nothing()
    {
        var child = Guid.NewGuid();
        var engine = new ParkingEngine(child);

        var parked = await new SubWorkflowBlock()
            .Awaiting(b => b.ExecuteAsync(
                new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() },
                Context(Container(engine)), CancellationToken.None))
            .Should().ThrowAsync<WorkflowSuspendException>();

        parked.Which.SuspendResult.Kind.Should().Be(WorkflowSuspensionKind.SubWorkflow);
        parked.Which.SuspendResult.ConfigJson.Should().Contain(child.ToString(),
            "what the parent waits on is the run it started");
    }

    [Fact]
    public async Task A_child_that_finished_answers_the_block_with_its_outputs_as_it_always_did()
    {
        var engine = new ParkingEngine(Guid.NewGuid(), parked: false)
        {
            Output = new Dictionary<string, object?> { ["total"] = 7 }
        };

        var outputs = await Run(Container(engine), Guid.NewGuid());

        outputs.Should().ContainKey("total").WhoseValue.Should().Be(7);
    }

    [Fact]
    public async Task A_child_the_engine_did_not_start_fails_the_block_with_the_reason()
    {
        var child = Guid.NewGuid();
        var engine = new NotStartingEngine("skipped (single_mode_already_running)");

        var failed = await new SubWorkflowBlock()
            .Awaiting(b => b.ExecuteAsync(
                new Dictionary<string, object?> { ["WorkflowId"] = child.ToString() },
                Context(Container(engine), nodeId: "sub"), CancellationToken.None))
            .Should().ThrowAsync<WorkflowNodeExecutionException>()
            .WithMessage($"*'{child}'*not started*single_mode_already_running*");

        failed.Which.NodeId.Should().Be("sub", "the node's error policy is what answers for it");
    }

    [Fact]
    public async Task An_engine_that_never_reports_back_leaves_the_block_answering_as_it_did()
    {
        var engine = new CapturingEngine { Output = new Dictionary<string, object?> { ["total"] = 7 } };

        var outputs = await Run(Container(engine), Guid.NewGuid());

        outputs.Should().ContainKey("total").WhoseValue.Should().Be(7);
    }

    [Fact]
    public async Task A_resumed_block_answers_with_what_the_child_produced_and_does_not_start_it_again()
    {
        var engine = new CapturingEngine();
        var context = Context(Container(engine), nodeId: "sub");
        WorkflowSubRunResumeScope.Swap(new WorkflowSubRunAnswer(
            "sub", Guid.NewGuid(), succeeded: true, """{"total":7}""", null));

        var outputs = await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() },
            context, CancellationToken.None);

        engine.Executed.Should().BeEmpty("the run it was waiting on has already run");
        outputs.Should().ContainKey("total").WhoseValue.Should().Be(7);

        await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() },
            context, CancellationToken.None);

        engine.Executed.Should().ContainSingle(
            "the answer is taken, so a second pass through the node starts the run it is there to start");
    }

    [Fact]
    public async Task A_resumed_parent_block_receives_the_same_unmasked_untruncated_outputs_as_a_running_child()
    {
        var note = new string('n', 20_000);
        var raw = JsonSerializer.Serialize(new { auth = new { token = "t-raw" }, note });
        var workflowId = Guid.NewGuid();
        var resumedEngine = new CapturingEngine();
        var context = Context(Container(resumedEngine), nodeId: "sub");
        WorkflowSubRunResumeScope.Swap(new WorkflowSubRunAnswer(
            "sub", Guid.NewGuid(), succeeded: true, raw, null));

        var resumedParentOutputs = await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = workflowId.ToString() }, context, CancellationToken.None);

        resumedEngine.Executed.Should().BeEmpty("the child already finished and the parent takes its result");
        var directEngine = new CapturingEngine { Output = PersistedValueCodec.ReadChildAnswer(raw, new Dictionary<string, ClosedPortType>(), "sub") };
        var directParentOutputs = await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = workflowId.ToString() },
            Context(Container(directEngine), nodeId: "sub"), CancellationToken.None);

        JsonSerializer.Serialize(resumedParentOutputs).Should().Be(raw);
        JsonSerializer.Serialize(directParentOutputs).Should().Be(raw);
        JsonSerializer.Serialize(resumedParentOutputs).Should().Be(JsonSerializer.Serialize(directParentOutputs));
    }

    [Fact]
    public async Task A_resumed_block_whose_child_did_not_succeed_throws_rather_than_answering()
    {
        var engine = new CapturingEngine();
        var context = Context(Container(engine), nodeId: "sub");
        WorkflowSubRunResumeScope.Swap(new WorkflowSubRunAnswer(
            "sub", Guid.NewGuid(), succeeded: false, null, "the child threw"));

        var thrown = await new SubWorkflowBlock()
            .Awaiting(b => b.ExecuteAsync(
                new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() },
                context, CancellationToken.None))
            .Should().ThrowAsync<Exception>();

        thrown.Which.Message.Should().Contain("the child threw");
        engine.Executed.Should().BeEmpty("a child that failed is not run a second time to find that out again");
    }

    [Fact]
    public async Task An_answer_left_for_another_node_is_not_this_block_s_to_take()
    {
        var engine = new CapturingEngine { Output = new Dictionary<string, object?> { ["total"] = 1 } };
        var context = Context(Container(engine), nodeId: "sub");
        WorkflowSubRunResumeScope.Swap(new WorkflowSubRunAnswer(
            "another", Guid.NewGuid(), succeeded: true, """{"total":7}""", null));

        var outputs = await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() },
            context, CancellationToken.None);

        engine.Executed.Should().ContainSingle("this block has a run of its own to start");
        outputs.Should().ContainKey("total").WhoseValue.Should().Be(1);
        WorkflowSubRunResumeScope.Swap(null).Should().NotBeNull("the other node's answer is still waiting for it");
    }

    [Fact]
    public async Task A_fresh_answer_is_read_into_the_CLR_type_of_its_port_and_one_that_does_not_read_stays_as_written()
    {
        var engine = new CapturingEngine
        {
            Output = new Dictionary<string, object?>
            {
                ["doc"] = new Dictionary<string, object?> { ["k"] = "v" },
                ["count"] = "five",
            },
        };
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = NullLogger.Instance, Services = Container(engine),
            Types = new Dictionary<string, ClosedPortType>
            {
                ["doc"] = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse("Json", PortTermGrammar.Document))!,
                ["count"] = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse("Int", PortTermGrammar.Document))!,
            },
        };

        var outputs = await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() }, context, CancellationToken.None);

        outputs["doc"].Should().BeOfType<JsonElement>().Which.GetProperty("k").GetString().Should().Be("v");
        outputs["count"].Should().Be("five", "a value that does not read as its port's type stays as written, for the gate to refuse");
    }

    [Theory]
    [InlineData("soon")]
    [InlineData(5)]
    public async Task A_DateTime_answer_that_is_no_date_stays_as_written_for_the_gate(object answer)
    {
        var at = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse("DateTime", PortTermGrammar.Document))!;
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = NullLogger.Instance,
            Services = Container(new CapturingEngine { Output = new Dictionary<string, object?> { ["at"] = answer } }),
            Types = new Dictionary<string, ClosedPortType> { ["at"] = at },
        };

        var outputs = await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() }, context, CancellationToken.None);

        outputs["at"].Should().Be(answer);
    }

    [Fact]
    public async Task A_resumed_answer_reads_an_instant_as_a_DateTimeOffset_by_its_output_term()
    {
        var at = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse("DateTime", PortTermGrammar.Document))!;
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = NullLogger.Instance,
            Services = Container(new CapturingEngine()), NodeId = "sub",
            Types = new Dictionary<string, ClosedPortType> { ["at"] = at },
            OutputTypes = new Dictionary<string, ClosedPortType> { ["at"] = at },
        };
        WorkflowSubRunResumeScope.Swap(new WorkflowSubRunAnswer(
            "sub", Guid.NewGuid(), succeeded: true, """{"at":"2026-10-14T09:30:00+02:00"}""", null));

        var outputs = await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() }, context, CancellationToken.None);

        outputs["at"].Should().BeOfType<DateTimeOffset>()
            .Which.Should().Be(new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public async Task A_resumed_answer_key_that_names_only_an_input_is_not_read_by_that_inputs_term()
    {
        var context = new WorkflowBlockContext
        {
            ExecutionId = Guid.NewGuid(), UserId = Guid.NewGuid(), Logger = NullLogger.Instance,
            Services = Container(new CapturingEngine()), NodeId = "sub",
            Types = new Dictionary<string, ClosedPortType>
            {
                ["limit"] = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse("Int", PortTermGrammar.Document))!,
            },
            OutputTypes = new Dictionary<string, ClosedPortType>(),
        };
        WorkflowSubRunResumeScope.Swap(new WorkflowSubRunAnswer(
            "sub", Guid.NewGuid(), succeeded: true, """{"limit":"five"}""", null));

        var outputs = await new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = Guid.NewGuid().ToString() }, context, CancellationToken.None);

        outputs["limit"].Should().Be("five");
    }

    private sealed class NotStartingEngine(string reason) : IWorkflowEngine
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            Guid workflowId, WorkflowFire? fire, IReadOnlyDictionary<string, object?>? inputParameters,
            Guid? parentExecutionId = null, int? iterationIndex = null, string? callStackJson = null,
            CancellationToken cancellationToken = default)
        {
            WorkflowSubRunOutcomeScope.Current?.ReportRefused(reason);
            return Task.FromResult(Empty);
        }

        public Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> IsBlockIdempotentAsync(
            Guid workflowId, string blockPath, Guid? versionId = null,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class ParkingEngine(Guid executionLogId, bool parked = true) : IWorkflowEngine
    {
        public IReadOnlyDictionary<string, object?> Output { get; init; } = new Dictionary<string, object?>();

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            Guid workflowId, WorkflowFire? fire, IReadOnlyDictionary<string, object?>? inputParameters,
            Guid? parentExecutionId = null, int? iterationIndex = null, string? callStackJson = null,
            CancellationToken cancellationToken = default)
        {
            WorkflowSubRunOutcomeScope.Current?.Report(parked, executionLogId);
            return Task.FromResult(Output);
        }

        public Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> IsBlockIdempotentAsync(
            Guid workflowId, string blockPath, Guid? versionId = null,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    private static Task<IReadOnlyDictionary<string, object?>> Run(IServiceProvider services, Guid workflowId) =>
        new SubWorkflowBlock().ExecuteAsync(
            new Dictionary<string, object?> { ["WorkflowId"] = workflowId.ToString() },
            Context(services), CancellationToken.None);

    private static IServiceProvider Container(IWorkflowEngine engine) =>
        new ServiceCollection().AddSingleton(engine).BuildServiceProvider();

    private static WorkflowBlockContext Context(IServiceProvider services, string? nodeId = null) => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = NullLogger.Instance,
        Services = services,
        NodeId = nodeId
    };

    private sealed class RefusingBlock : SubWorkflowBlock
    {
        protected override Task AuthorizeAsync(
            Guid workflowId, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException($"workflow '{workflowId}' is not yours to run");
    }

    private sealed class CapturingEngine : IWorkflowEngine
    {
        public List<Guid> Executed { get; } = [];
        public IReadOnlyDictionary<string, object?> Output { get; init; } = new Dictionary<string, object?>();
        public Guid LastWorkflowId { get; private set; }
        public WorkflowFire? LastFire { get; private set; }
        public IReadOnlyDictionary<string, object?>? LastInputParameters { get; private set; }
        public Guid? LastParentExecutionId { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            Guid workflowId, WorkflowFire? fire, IReadOnlyDictionary<string, object?>? inputParameters,
            Guid? parentExecutionId = null, int? iterationIndex = null, string? callStackJson = null,
            CancellationToken cancellationToken = default)
        {
            Executed.Add(workflowId);
            LastWorkflowId = workflowId;
            LastFire = fire;
            LastInputParameters = inputParameters;
            LastParentExecutionId = parentExecutionId;
            LastCancellationToken = cancellationToken;
            return Task.FromResult(Output);
        }

        public Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> IsBlockIdempotentAsync(
            Guid workflowId, string blockPath, Guid? versionId = null,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class ReentrantEngine(Func<Guid, Task<IReadOnlyDictionary<string, object?>>> onExecute)
        : IWorkflowEngine
    {
        public List<Guid> Executed { get; } = [];

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            Guid workflowId, WorkflowFire? fire, IReadOnlyDictionary<string, object?>? inputParameters,
            Guid? parentExecutionId = null, int? iterationIndex = null, string? callStackJson = null,
            CancellationToken cancellationToken = default)
        {
            Executed.Add(workflowId);
            return onExecute(workflowId);
        }

        public Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> IsBlockIdempotentAsync(
            Guid workflowId, string blockPath, Guid? versionId = null,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
