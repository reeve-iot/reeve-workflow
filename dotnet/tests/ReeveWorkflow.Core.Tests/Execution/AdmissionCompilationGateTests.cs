using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Compilation;
using Xunit;
using static ReeveWorkflow.Core.Tests.Execution.AdmissionGateWorld;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class AdmissionCompilationGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_duplicate_node_id_refuses_run_admission_in_the_parent_or_a_pinned_child(bool inBody, bool asChild)
    {
        const string duplicate = """{"nodes":[{"id":"twin","position":{"x":0,"y":0},"configuration":{}},{"id":"twin","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
        var definition = inBody
            ? """{"nodes":[{"id":"loop","subCanvases":{"Body":DOMAIN},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""".Replace("DOMAIN", duplicate)
            : duplicate;
        var world = new AdmissionGateWorld();
        var unreadable = world.AddWorkflow(definition);
        var id = asChild ? world.AddWorkflow(Graph(("call", Call, Calls(unreadable)))) : unreadable;
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        var diagnostic = refusal.Diagnostics.Should().ContainSingle().Which;
        diagnostic.RuleId.Should().Be(CompilationRules.DuplicateNodeId);
        diagnostic.NodeId.Should().Be("twin");
        diagnostic.CanvasPath.Should().Equal(inBody ? [new CanvasPathSegment("loop", "Body")] : []);
        diagnostic.Message.Should().Contain(inBody ? "/nodes/0/subCanvases/Body/nodes/1/id" : "/nodes/1/id");
        (refusal.WorkflowId, refusal.WorkflowVersionId).Should().Be((unreadable, world.Store.Workflows[unreadable].CurrentVersion!.Id));
        world.Executed.Should().BeEmpty();
        world.Journal.Logs.Values.Should().ContainSingle().Which.FailureReason.Should().Be(WorkflowFailureReasons.DidNotCompile);
    }

    [Fact]
    public async Task A_sub_workflow_called_with_parameters_is_a_cache_hit_on_the_second_admission()
    {
        var world = new AdmissionGateWorld();
        const string parameters = """[{"name":"payload","type":"String"}]""";
        var child = world.AddWorkflow(Graph(("n", Leaf, null)), inputParametersJson: parameters, outputParametersJson: parameters);
        var root = world.AddWorkflow(Graph(("call", Call, Calls(child))));
        var childVersion = world.Store.Workflows[child].CurrentVersion!;
        var admission = Replica(world, null);

        (await admission.AdmitAsync(new WorkflowAdmissionRequest(root), Coordinator(WorkflowMode.Parallel), CancellationToken.None))
            .Decision!.Lease!.Dispose();
        var built = WorkflowVersionDataCache.Get(childVersion);
        var digest = built.DigestFor(parameters, parameters);
        (await admission.AdmitAsync(new WorkflowAdmissionRequest(root), Coordinator(WorkflowMode.Parallel), CancellationToken.None))
            .Decision!.Lease!.Dispose();

        WorkflowVersionDataCache.Get(childVersion).Should().BeSameAs(built,
            "the manifest builder and the compile must key the child's entry the same way, or each admission rebuilds it");
        built.DigestFor(parameters, parameters).Should().BeSameAs(digest);
    }

    [Fact]
    public async Task A_warm_admission_allocates_the_same_for_a_small_and_a_large_definition()
    {
        var small = await MeasureWarmAdmissions(5_000);
        var large = await MeasureWarmAdmissions(200_000);

        Math.Abs(large - small).Should().BeLessThan(20_000,
            "a warm run reads cached document data, so the definition's size must not reach the allocation");
    }

    private static async Task<long> MeasureWarmAdmissions(int payloadLength)
    {
        var world = new AdmissionGateWorld();
        var payload = new string('x', payloadLength);
        var child = world.AddWorkflow(Graph(("n", Leaf, new { payload })));
        var root = world.AddWorkflow(Graph(("call", Call, Calls(child))));
        var admission = Replica(world, null);
        var request = new WorkflowAdmissionRequest(root);

        async Task AdmitOnce() =>
            (await admission.AdmitAsync(request, Coordinator(WorkflowMode.Parallel), CancellationToken.None)).Decision!.Lease!.Dispose();

        for (var i = 0; i < 5; i++)
            await AdmitOnce();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
            await AdmitOnce();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public async Task A_start_whose_version_does_not_compile_is_refused_before_its_first_node()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n1", Leaf, null), ("n2", "gone.block", null)));
        var version = world.Store.Workflows[id].CurrentVersion!;
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        refusal.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.BlockTypeUnknown)
            .Which.NodeId.Should().Be("n2");
        (refusal.WorkflowId, refusal.WorkflowVersionId).Should().Be((id, version.Id));
        world.Executed.Should().BeEmpty("n1 would run first, and a refusal happens before any node");
    }

    [Fact]
    public async Task A_refused_start_leaves_one_ended_row_that_names_why_and_carries_every_diagnostic()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n1", "gone.one", null), ("n2", "gone.two", null)));
        var version = world.Store.Workflows[id].CurrentVersion!;
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        var row = world.Journal.Logs.Values.Should().ContainSingle().Which;
        (row.WorkflowId, row.WorkflowVersionId, row.Status).Should().Be((id, version.Id, WorkflowExecutionStatus.Failed));
        row.FailureReason.Should().Be(WorkflowFailureReasons.DidNotCompile);
        row.CompletedAtUtc.Should().NotBeNull();
        row.ErrorMessage.Should().Be(refusal.Message);

        var entries = world.Journal.Entries[row.Id];
        entries.Select(e => e.NodeId).Should().Equal(refusal.Diagnostics.Select(d => d.NodeId));
        entries.Should().OnlyContain(e => e.Level == LogEntryLevel.Error);
        entries.Select(e => JsonDocument.Parse(e.DataJson!).RootElement.GetProperty("ruleId").GetString())
            .Should().Equal(refusal.Diagnostics.Select(d => d.RuleId));
    }

    [Fact]
    public async Task A_start_is_refused_when_a_workflow_its_manifest_pins_does_not_compile_and_names_that_workflow()
    {
        var world = new AdmissionGateWorld();
        var grandchild = world.AddWorkflow(Graph(("g1", "gone.block", null)));
        var child = world.AddWorkflow(Graph(("call", Call, Calls(grandchild))));
        var root = world.AddWorkflow(Graph(("call", Call, Calls(child)), ("after", Leaf, null)));
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(root, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        (refusal.WorkflowId, refusal.WorkflowVersionId)
            .Should().Be((grandchild, world.Store.Workflows[grandchild].CurrentVersion!.Id));
        world.Executed.Should().BeEmpty();
        world.Journal.Logs.Values.Should().ContainSingle()
            .Which.WorkflowVersionId.Should().Be(world.Store.Workflows[root].CurrentVersion!.Id,
                "the row is the refused run's, whichever of its versions did not compile");
    }

    [Fact]
    public async Task A_literal_mapped_into_the_reference_input_is_refused_at_the_door()
    {
        var world = new AdmissionGateWorld();
        var configured = world.AddWorkflow(Graph());
        var literal = world.AddWorkflow(Graph());
        var root = world.AddWorkflow(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "call", blockTypeId = Call, configuration = Calls(configured), position = new { x = 0, y = 0 } }
            },
            edges = new object[]
            {
                new
                {
                    id = "e", sourceNodeId = "start", targetNodeId = "call",
                    mappings = new Dictionary<string, object>
                    {
                        ["WorkflowId"] = new { type = "literal", literalValue = literal.ToString() }
                    }
                }
            }
        }));
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(root, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        refusal.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ReferenceInputMapped && d.NodeId == "call");
        world.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_compile_that_faults_refuses_the_start_as_retryable_and_the_next_start_runs()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("x", Leaf, new { v = "expr:${host.q}" })));
        var version = world.Store.Workflows[id].CurrentVersion!;
        var fault = new InvalidOperationException("check down");
        Exception? next = fault;
        var host = new WorkflowExpressionNamespace("host", 1, new CompilationBackstopTests.HostCheck(() => next),
            (_, _) => Task.FromResult<IExpressionNamespaceResolver>(new AnsweringHost()));
        using var engine = world.Engine(new CompilationOptions { NamespaceRegistrations = [host] }, hostNamespaces: [host]);

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowCompilationUnansweredException>()).Which;

        (refusal.WorkflowId, refusal.WorkflowVersionId).Should().Be((id, version.Id));
        refusal.InnerException.Should().BeOfType<CompilationFaultException>().Which.InnerException.Should().BeSameAs(fault);
        var row = world.Journal.Logs.Values.Should().ContainSingle().Which;
        (row.Status, row.FailureReason).Should().Be((WorkflowExecutionStatus.Failed, WorkflowFailureReasons.CompilationUnanswered));
        world.Executed.Should().BeEmpty();

        next = null;
        await engine.ExecuteAsync(id, null, null);

        world.Executed.Should().Equal(["x"], "a fault is no verdict, so the start after it compiles again");
    }

    [Fact]
    public async Task A_resolver_that_throws_refuses_the_start_as_retryable_with_its_own_exception_inside()
    {
        var world = new AdmissionGateWorld();
        var fault = new TimeoutException("resolver down");
        world.Resolvers.Add(new ThrowingResolver(fault));
        var id = world.AddWorkflow(Graph(("r", Resolved, null)));
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowCompilationUnansweredException>()).Which;

        refusal.InnerException.Should().BeSameAs(fault);
        var row = world.Journal.Logs.Values.Should().ContainSingle().Which;
        row.FailureReason.Should().Be(WorkflowFailureReasons.CompilationUnanswered);
        row.ErrorMessage.Should().Be(refusal.Message);
        var entry = world.Journal.Entries[row.Id].Should().ContainSingle().Which;
        entry.Level.Should().Be(LogEntryLevel.Error);
        entry.Message.Should().Be($"{refusal.Message} The fault was a {typeof(TimeoutException).FullName}; this host's log has it.")
            .And.NotContain("resolver down", "a host's fault is logged, and the row is served to whoever reads the run");
    }

    [Fact]
    public async Task A_start_whose_resolver_faults_is_refused_although_its_version_compiled_before()
    {
        var world = new AdmissionGateWorld();
        Exception? next = null;
        var resolver = new CountingResolver(() => next);
        world.Resolvers.Add(resolver);
        var id = world.AddWorkflow(Graph(("r", Resolved, null)));
        using var engine = world.Engine();
        await engine.ExecuteAsync(id, null, null);
        var (scopes, asked) = (world.Scopes.Opened, resolver.Asked);

        await engine.ExecuteAsync(id, null, null);
        (world.Scopes.Opened - scopes, resolver.Asked - asked).Should().Be((2, 1),
            "a node a resolver answers for is asked on every start, through a scope the compile opens beside the run's");

        next = new TimeoutException("resolver down");
        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowCompilationUnansweredException>("the verdict is keyed on what the resolver answers");
        world.Executed.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_timeout_thrown_as_a_cancellation_on_a_live_token_refuses_as_unanswered(bool fromTheStore)
    {
        var world = new AdmissionGateWorld();
        var timeout = new TaskCanceledException("command timeout");
        Guid id;
        if (fromTheStore)
        {
            var child = world.AddWorkflow(Graph(("c", Leaf, null)));
            id = world.AddWorkflow(Graph(("call", Call, Calls(child))));
            world.Store.FailVersionLoad = world.Store.Workflows[child].CurrentVersion!.Id;
            world.Store.FailWith = timeout;
        }
        else
        {
            world.Resolvers.Add(new CountingResolver(() => timeout));
            id = world.AddWorkflow(Graph(("r", Resolved, null)));
        }
        using var engine = world.Engine();

        (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
                .Should().ThrowAsync<WorkflowCompilationUnansweredException>("the caller did not cancel")).Which
            .InnerException.Should().BeSameAs(timeout);
        world.Journal.Logs.Values.Should().ContainSingle()
            .Which.FailureReason.Should().Be(WorkflowFailureReasons.CompilationUnanswered);
    }

    [Fact]
    public async Task A_pinned_version_gone_since_the_manifest_named_it_refuses_as_unanswered_naming_it()
    {
        var world = new AdmissionGateWorld();
        var child = world.AddWorkflow(Graph(("c", Leaf, null)));
        var root = world.AddWorkflow(Graph(("call", Call, Calls(child))));
        var childVersion = world.Store.Workflows[child].CurrentVersion!.Id;
        world.Store.Versions.TryRemove(childVersion, out _);
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(root, null, null))
            .Should().ThrowAsync<WorkflowCompilationUnansweredException>()).Which;

        (refusal.WorkflowId, refusal.WorkflowVersionId).Should().Be((child, childVersion));
        refusal.InnerException.Should().BeOfType<PinnedWorkflowVersionNotFoundException>();
    }

    [Fact]
    public async Task A_refused_fires_row_carries_its_trigger_and_automation_and_each_entry_the_version_it_points_into()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", "gone.block", null)));
        var version = world.Store.Workflows[id].CurrentVersion!.Id;
        using var engine = world.Engine();
        var fire = new WorkflowFire { TriggerId = Guid.NewGuid(), AutomationId = Guid.NewGuid() };

        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, fire, null)).Should().ThrowAsync<WorkflowDoesNotCompileException>();

        var row = world.Journal.Logs.Values.Should().ContainSingle().Which;
        (row.TriggerId, row.AutomationId).Should().Be((fire.TriggerId, fire.AutomationId));
        var data = JsonDocument.Parse(world.Journal.Entries[row.Id].Should().ContainSingle().Which.DataJson!).RootElement;
        (data.GetProperty("workflowId").GetGuid(), data.GetProperty("workflowVersionId").GetGuid()).Should().Be((id, version));
    }

    [Fact]
    public async Task A_refusal_message_longer_than_the_column_is_cut_to_it()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", "gone." + new string('x', 5000), null)));
        using var engine = world.Engine();

        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null)).Should().ThrowAsync<WorkflowDoesNotCompileException>();

        var row = world.Journal.Logs.Values.Should().ContainSingle().Which;
        row.ErrorMessage!.Length.Should().Be(2000);

        var entries = world.Journal.Entries[row.Id];
        entries.Select(e => JsonDocument.Parse(e.DataJson!).RootElement.GetProperty("ruleId").GetString())
            .Should().BeEquivalentTo([CompilationRules.BlockTypeUnknown, CompilationRules.BlockTypeIdTooLong]);
        entries.Should().OnlyContain(e => e.Message.Length == 4000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task One_event_reaching_two_replicas_is_one_outcome_and_one_row(bool compiles)
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(compiles ? Graph(("n", Leaf, null)) : Graph(("n", "gone.block", null)));
        var dedup = new ClaimOnce();
        var request = new WorkflowAdmissionRequest(id) { FireDedupKey = "device:42/event:7", IsMultiReplica = true };
        var outcomes = new List<string>();

        foreach (var replica in new[] { Replica(world, dedup), Replica(world, dedup) })
        {
            try
            {
                var result = await replica.AdmitAsync(request, Coordinator(WorkflowMode.Parallel), CancellationToken.None);
                outcomes.Add(result.Outcome);
                result.Decision?.Lease?.Dispose();
            }
            catch (WorkflowDoesNotCompileException)
            {
                outcomes.Add(WorkflowFailureReasons.DidNotCompile);
            }
        }

        outcomes.Should().Equal(
            compiles ? WorkflowAdmissionOutcomes.Started : WorkflowFailureReasons.DidNotCompile, WorkflowAdmissionOutcomes.Dropped);
        world.Journal.Logs.Should().HaveCount(compiles ? 0 : 1, "the replica that loses the claim compiles nothing");
    }

    [Theory]
    [InlineData(WorkflowMode.Single)]
    [InlineData(WorkflowMode.Parallel)]
    public async Task A_fire_the_lease_would_skip_or_drop_is_refused_as_did_not_compile_first(WorkflowMode mode)
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", "gone.block", null)));
        var coordinator = new WorkflowRunCoordinator(id, mode, 1, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);
        using var held = (await coordinator.AcquireAsync(CancellationToken.None)).Lease;

        await FluentActions.Awaiting(() => Replica(world, null).AdmitAsync(
                new WorkflowAdmissionRequest(id), _ => coordinator, CancellationToken.None))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>();

        world.Journal.Logs.Values.Should().ContainSingle().Which.FailureReason.Should().Be(WorkflowFailureReasons.DidNotCompile);
    }

    [Fact]
    public async Task A_pinned_version_admission_cannot_read_refuses_the_start_as_retryable()
    {
        var world = new AdmissionGateWorld();
        var child = world.AddWorkflow(Graph(("c", Leaf, null)));
        var root = world.AddWorkflow(Graph(("call", Call, Calls(child))));
        var childVersion = world.Store.Workflows[child].CurrentVersion!.Id;
        world.Store.FailVersionLoad = childVersion;
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(root, null, null))
            .Should().ThrowAsync<WorkflowCompilationUnansweredException>()).Which;

        (refusal.WorkflowId, refusal.WorkflowVersionId).Should().Be((child, childVersion));
        refusal.InnerException.Should().BeOfType<TimeoutException>();
    }

    [Fact]
    public async Task The_starts_own_token_is_the_one_the_compile_is_asked_under_and_a_cancelled_one_leaves_no_row()
    {
        var world = new AdmissionGateWorld();
        using var caller = new CancellationTokenSource();
        var resolver = new CancellingResolver(caller);
        world.Resolvers.Add(resolver);
        var id = world.AddWorkflow(Graph(("r", Resolved, null)));
        using var engine = world.Engine();

        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null, cancellationToken: caller.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        resolver.Asked.Should().Be(caller.Token);
        world.Journal.Logs.Should().BeEmpty("a start its caller cancelled was neither run nor refused");
    }

    [Fact]
    public async Task A_refusal_whose_row_cannot_be_written_is_still_the_refusal()
    {
        var world = new AdmissionGateWorld();
        world.Journal.OpenFails = true;
        var id = world.AddWorkflow(Graph(("n", "gone.block", null)));
        using var engine = world.Engine();

        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>();
    }

    [Fact]
    public async Task A_restart_whose_new_version_does_not_compile_is_refused_without_superseding_the_run_in_flight()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("hold", Hold, null)), WorkflowMode.Restart);
        using var engine = world.Engine();
        var first = engine.ExecuteAsync(id, null, null);
        await world.Entered.Task.WaitAsync(Patience);

        world.Publish(id, Graph(("n", "gone.block", null)));
        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>();

        first.IsCompleted.Should().BeFalse("a refused start takes no lease, so it cancels nothing");
        world.Release.SetResult();
        await first.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_second_start_of_an_unchanged_version_opens_no_scope_of_its_own_and_reads_each_pinned_version_once_more()
    {
        var world = new AdmissionGateWorld();
        var child = world.AddWorkflow(Graph(("c", Leaf, null)));
        var root = world.AddWorkflow(Graph(("call", Call, Calls(child))));
        var childVersion = world.Store.Workflows[child].CurrentVersion!.Id;
        using var engine = world.Engine();
        await engine.ExecuteAsync(root, null, null);
        var scopes = world.Scopes.Opened;
        while (world.Store.Reads.TryDequeue(out _)) { }

        await engine.ExecuteAsync(root, null, null);

        world.Scopes.Opened.Should().Be(scopes + 1, "the run's own scope, and no other");
        world.Store.Reads.Should().Equal(
            $"workflow {root}", $"current {child}", $"version {childVersion}");
    }

    [Fact]
    public async Task A_run_started_under_a_manifest_it_was_handed_is_still_refused_when_its_own_version_no_longer_compiles()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)));
        var workflow = world.Store.Workflows[id];
        workflow.Update("wf", null, DateTimeOffset.UtcNow, retryPolicyJson: """{"maxAttempts":3}""");
        var version = workflow.CurrentVersion!;
        using var engine = world.Engine();
        world.Remove(Leaf);

        using (WorkflowRunPinScope.Push(new WorkflowRunPin
               {
                   VersionId = version.Id, Manifest = new Dictionary<Guid, Guid> { [id] = version.Id }
               }))
        {
            var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
                .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

            refusal.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.BlockTypeUnknown).Which.NodeId.Should().Be("n");
            (refusal.WorkflowId, refusal.WorkflowVersionId).Should().Be((id, version.Id));
        }

        var row = world.Journal.Logs.Values.Should().ContainSingle("a refusal is one row, not an attempt for each retry").Which;
        (row.Status, row.FailureReason).Should().Be((WorkflowExecutionStatus.Failed, WorkflowFailureReasons.DidNotCompile));
        world.Journal.Entries[row.Id].Should().ContainSingle(e => e.NodeId == "n");
        world.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replay_of_an_abandoned_run_whose_row_names_its_version_is_refused_as_a_start_is_when_the_version_no_longer_compiles()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)));
        var version = world.Store.Workflows[id].CurrentVersion!.Id;
        var runState = new MemoryRunState();
        var abandoned = Guid.NewGuid();
        runState.Rows[abandoned] = new WorkflowRunStateRow
        {
            ExecutionId = abandoned, WorkflowId = id, Status = WorkflowRunStateStatus.Running,
            PinnedVersionsJson = $"{{\"{id}\":\"{version}\"}}"
        };
        using var engine = world.Engine(runState: runState);
        world.Remove(Leaf);
        await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>("a start of the same version is judged now");

        var replay = (await FluentActions.Awaiting(() => engine.ReplayAbandonedRunAsync(abandoned, id))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>("the replay's own version is judged, as a start's is")).Which;

        replay.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        world.Journal.Logs.Values.Should().HaveCount(2).And.OnlyContain(l => l.FailureReason == WorkflowFailureReasons.DidNotCompile);
        world.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replay_whose_row_pins_a_version_that_has_gone_is_refused_as_not_found_and_leaves_no_row()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", Leaf, null)));
        var gone = Guid.NewGuid();
        var runState = new MemoryRunState();
        var abandoned = Guid.NewGuid();
        runState.Rows[abandoned] = new WorkflowRunStateRow
        {
            ExecutionId = abandoned, WorkflowId = id, Status = WorkflowRunStateStatus.Running,
            PinnedVersionsJson = $"{{\"{id}\":\"{gone}\"}}"
        };
        using var engine = world.Engine(runState: runState);

        (await FluentActions.Awaiting(() => engine.ReplayAbandonedRunAsync(abandoned, id))
            .Should().ThrowAsync<PinnedWorkflowVersionNotFoundException>()).Which.VersionId.Should().Be(gone);
        world.Journal.Logs.Should().BeEmpty();
    }

    [Fact]
    public async Task One_event_reaching_five_replicas_of_a_single_workflow_is_refused_on_each_and_leaves_one_row()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single);
        var dedup = new ClaimOnce();
        var request = new WorkflowAdmissionRequest(id) { FireDedupKey = "device:42/event:7", IsMultiReplica = true };

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
            Outcome(() => Replica(world, dedup).AdmitAsync(request, Coordinator(WorkflowMode.Single), CancellationToken.None)))));

        outcomes.Should().AllBe(nameof(WorkflowDoesNotCompileException), "Single skips the fire claim, so each replica judges");
        world.Journal.Logs.Should().ContainSingle("only the replica that claims the event's row writes it");
        dedup.Windows.Should().HaveCount(5).And.AllBeEquivalentTo(TimeSpan.FromSeconds(10), "the fire claim's window");
    }

    [Fact]
    public async Task A_row_claim_that_throws_still_writes_the_row()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single);

        (await Outcome(() => Replica(world, new ClaimThrows()).AdmitAsync(
                new WorkflowAdmissionRequest(id) { FireDedupKey = "device:42/event:10", IsMultiReplica = true },
                Coordinator(WorkflowMode.Single), CancellationToken.None)))
            .Should().Be(nameof(WorkflowDoesNotCompileException));

        world.Journal.Logs.Should().ContainSingle("a second row is the recoverable direction, a lost one is not");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_event_whose_compile_cannot_be_answered_on_two_replicas_of_a_single_workflow_leaves_one_row(
        bool fromTheStore)
    {
        var world = new AdmissionGateWorld();
        Guid id;
        if (fromTheStore)
        {
            var child = world.AddWorkflow(Graph(("c", Leaf, null)));
            id = world.AddWorkflow(Graph(("call", Call, Calls(child))), WorkflowMode.Single);
            world.Store.FailVersionLoad = world.Store.Workflows[child].CurrentVersion!.Id;
        }
        else
        {
            world.Resolvers.Add(new ThrowingResolver(new TimeoutException("resolver down")));
            id = world.AddWorkflow(Graph(("r", Resolved, null)), WorkflowMode.Single);
        }

        var dedup = new ClaimOnce();
        var request = new WorkflowAdmissionRequest(id) { FireDedupKey = "device:42/event:8", IsMultiReplica = true };

        foreach (var replica in new[] { Replica(world, dedup), Replica(world, dedup) })
            (await Outcome(() => replica.AdmitAsync(request, Coordinator(WorkflowMode.Single), CancellationToken.None)))
                .Should().Be(nameof(WorkflowCompilationUnansweredException));

        world.Journal.Logs.Values.Should().ContainSingle()
            .Which.FailureReason.Should().Be(WorkflowFailureReasons.CompilationUnanswered);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_single_fire_with_no_key_or_on_one_replica_leaves_a_row_for_each_refusal(bool withKey, bool multiReplica)
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single);
        var dedup = new ClaimOnce();
        var request = new WorkflowAdmissionRequest(id)
        {
            FireDedupKey = withKey ? "device:42/event:9" : null, IsMultiReplica = multiReplica
        };

        foreach (var replica in new[] { Replica(world, dedup), Replica(world, dedup) })
            (await Outcome(() => replica.AdmitAsync(request, Coordinator(WorkflowMode.Single), CancellationToken.None)))
                .Should().Be(nameof(WorkflowDoesNotCompileException));

        world.Journal.Logs.Should().HaveCount(2, "with nothing to claim, or no peer to share one with, nothing is collapsed");
        dedup.Claimed.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_single_workflows_one_event_fires_under_one_key_leave_a_row_each_on_three_replicas()
    {
        var world = new AdmissionGateWorld();
        var ids = new[] { world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single),
            world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single) };
        var dedup = new ClaimOnce();

        foreach (var replica in Enumerable.Range(0, 3).Select(_ => Replica(world, dedup)))
            foreach (var id in ids)
                (await Outcome(() => replica.AdmitAsync(
                        new WorkflowAdmissionRequest(id) { FireDedupKey = "device:42/event:11", IsMultiReplica = true },
                        Coordinator(WorkflowMode.Single), CancellationToken.None)))
                    .Should().Be(nameof(WorkflowDoesNotCompileException));

        ids.Select(id => world.Journal.Logs.Values.Count(l => l.WorkflowId == id)).Should().Equal([1, 1],
            "a key naming only the event is the host's to scope, and each workflow's refusal claims its own row");
    }

    [Theory]
    [InlineData("trigger:6f1c|evt-9", "trigger:6f1c|evt-9")]
    [InlineData("reeveworkflow:x", "reeveworkflow:fire:reeveworkflow:x")]
    [InlineData("REEVEWORKFLOW:x", "REEVEWORKFLOW:x")]
    [InlineData(" reeveworkflow:x", " reeveworkflow:x")]
    [InlineData("reeveworkflow", "reeveworkflow")]
    public async Task A_fire_claims_its_key_as_given_unless_it_starts_with_the_reserved_prefix_in_that_case(
        string key, string claimed)
    {
        var world = new AdmissionGateWorld();
        var single = world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single);

        var (fire, row) = await ClaimsOf(world, world.AddWorkflow(Graph(("n", Leaf, null))), single, key);

        fire.Should().Be(claimed);
        row.Should().Be("reeveworkflow:refused-row:" + single.ToString("N") + ":" + key);
    }

    [Fact]
    public async Task A_refused_row_claims_the_reserved_prefix_its_workflow_and_the_key_and_no_two_claims_meet()
    {
        var random = new Random(19);
        string[] atoms = ["reeveworkflow:", "fire:", "refused-row:", "reeveworkflow", ":", "REEVEWORKFLOW:", "a", "k", " ", "\u00e9"];
        var world = new AdmissionGateWorld();
        var parallel = world.AddWorkflow(Graph(("n", Leaf, null)));
        var workflows = new[] { world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single),
            world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single) };
        atoms = [.. atoms, .. workflows.Select(w => w.ToString("N") + ":")];
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (keys.Count < 2000)
            keys.Add(string.Concat(Enumerable.Range(0, random.Next(1, 6)).Select(_ => atoms[random.Next(atoms.Length)])));

        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            foreach (var workflow in workflows)
            {
                var (fire, row) = await ClaimsOf(world, parallel, workflow, key);
                row.Should().Be("reeveworkflow:refused-row:" + workflow.ToString("N") + ":" + key);
                fire.Should().Be(key.StartsWith("reeveworkflow:", StringComparison.Ordinal) ? "reeveworkflow:fire:" + key : key);
                foreach (var (claim, owner) in new[] { (fire, "fire " + key), (row, "row " + workflow + " " + key) })
                    owners.GetValueOrDefault(claim, owner).Should().Be(owner, $"'{claim}' is claimed by one fire or one row");
                owners[fire] = "fire " + key;
                owners[row] = "row " + workflow + " " + key;
            }
        }
    }

    [Theory]
    [InlineData(WorkflowMode.Single, WorkflowAdmissionOutcomes.Started)]
    [InlineData(WorkflowMode.Parallel, WorkflowAdmissionOutcomes.Dropped)]
    public async Task A_retry_of_an_unanswered_fire_under_its_key_inside_the_window_starts_only_in_single_mode(
        WorkflowMode mode, string retried)
    {
        var world = new AdmissionGateWorld();
        Exception? next = new TimeoutException("resolver down");
        world.Resolvers.Add(new CountingResolver(() => next));
        var id = world.AddWorkflow(Graph(("r", Resolved, null)), mode);
        var dedup = new ClaimOnce();
        var request = new WorkflowAdmissionRequest(id) { FireDedupKey = "device:42/event:12", IsMultiReplica = true };

        (await Outcome(() => Replica(world, dedup).AdmitAsync(request, Coordinator(mode), CancellationToken.None)))
            .Should().Be(nameof(WorkflowCompilationUnansweredException));
        next = null;

        (await Outcome(() => Replica(world, dedup).AdmitAsync(request, Coordinator(mode), CancellationToken.None)))
            .Should().Be(retried, "outside Single the unanswered start holds the fire's claim, and nothing releases it");
    }

    [Fact]
    public async Task A_parallel_retry_admitted_after_the_fire_window_leaves_a_row_of_its_own()
    {
        var world = new AdmissionGateWorld();
        var clock = new ManualClock();
        var calls = 0;
        world.Resolvers.Add(new CountingResolver(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                clock.Now += TimeSpan.FromSeconds(8);
            return new TimeoutException("resolver down");
        }));
        var id = world.AddWorkflow(Graph(("r", Resolved, null)), WorkflowMode.Parallel);
        var dedup = new Expiring(clock);
        var request = new WorkflowAdmissionRequest(id) { FireDedupKey = "trigger:1|evt", IsMultiReplica = true };

        (await Outcome(() => Replica(world, dedup).AdmitAsync(request, Coordinator(WorkflowMode.Parallel), CancellationToken.None)))
            .Should().Be(nameof(WorkflowCompilationUnansweredException));
        clock.Now = ManualClock.T0 + TimeSpan.FromSeconds(13);
        (await Outcome(() => Replica(world, dedup).AdmitAsync(request, Coordinator(WorkflowMode.Parallel), CancellationToken.None)))
            .Should().Be(nameof(WorkflowCompilationUnansweredException));

        world.Journal.Logs.Values.Should().HaveCount(2, "the host saw two refused starts, and only a Single start claims a row");
    }

    [Fact]
    public async Task A_single_refire_under_the_same_key_inside_the_window_is_refused_with_no_row_of_its_own()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", "gone.block", null)), WorkflowMode.Single);
        var dedup = new ClaimOnce();
        var request = new WorkflowAdmissionRequest(id) { FireDedupKey = "state:dev-7|sha:1f2e", IsMultiReplica = true };

        foreach (var _ in Enumerable.Range(0, 2))
            (await Outcome(() => Replica(world, dedup).AdmitAsync(request, Coordinator(WorkflowMode.Single), CancellationToken.None)))
                .Should().Be(nameof(WorkflowDoesNotCompileException));

        world.Journal.Logs.Should().ContainSingle("the row claim cannot tell a replica's copy of the event from a re-fire");
    }

    private static async Task<(string Fire, string Row)> ClaimsOf(AdmissionGateWorld world, Guid parallel, Guid single, string key)
    {
        var fires = new Grants();
        (await Outcome(() => Replica(world, fires).AdmitAsync(
                new WorkflowAdmissionRequest(parallel) { FireDedupKey = key, IsMultiReplica = true },
                Coordinator(WorkflowMode.Parallel), CancellationToken.None)))
            .Should().Be(WorkflowAdmissionOutcomes.Started);
        var rows = new Grants();
        (await Outcome(() => Replica(world, rows).AdmitAsync(
                new WorkflowAdmissionRequest(single) { FireDedupKey = key, IsMultiReplica = true },
                Coordinator(WorkflowMode.Single), CancellationToken.None)))
            .Should().Be(nameof(WorkflowDoesNotCompileException));
        return (fires.Claimed.Should().ContainSingle().Which, rows.Claimed.Should().ContainSingle().Which);
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("1", true)]
    [InlineData("""{"policy":1}""", false)]
    public async Task A_numeric_error_policy_the_engine_binds_or_not_is_refused_as_unreadable_and_leaves_a_row(
        string errorPolicy, bool asChild)
    {
        var numericPolicy = "{\"nodes\":[{\"id\":\"start\",\"blockTypeId\":\"WorkflowInput\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"n\",\"blockTypeId\":\"Leaf\","
            + "\"errorPolicy\":" + errorPolicy + ",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[{\"id\":\"e\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"n\",\"mappings\":{}}]}";
        var world = new AdmissionGateWorld();
        var unreadable = world.AddWorkflow(numericPolicy);
        var id = asChild ? world.AddWorkflow(Graph(("call", Call, Calls(unreadable)))) : unreadable;
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        refusal.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        refusal.WorkflowId.Should().Be(unreadable);
        world.Journal.Logs.Values.Should().ContainSingle().Which.FailureReason.Should().Be(WorkflowFailureReasons.DidNotCompile);
    }

    [Fact]
    public async Task The_runs_own_version_is_judged_before_any_version_its_manifest_pins()
    {
        var world = new AdmissionGateWorld();
        var child = world.AddWorkflow(Graph(("c", "gone.child", null)));
        var root = world.AddWorkflow(Graph(("call", Call, Calls(child)), ("n", "gone.root", null)));
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(root, null, null))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        refusal.WorkflowId.Should().Be(root);
    }

    [Fact]
    public async Task A_start_pinned_to_a_version_by_its_id_alone_is_judged()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("n", "gone.block", null)));
        var pinned = world.Store.Workflows[id].CurrentVersion!.Id;
        world.Publish(id, Graph(("n", Leaf, null)));

        var refusal = (await FluentActions.Awaiting(() => Replica(world, null).AdmitAsync(
                new WorkflowAdmissionRequest(id) { PinnedVersionId = pinned }, Coordinator(WorkflowMode.Parallel),
                CancellationToken.None))
            .Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which;

        refusal.WorkflowVersionId.Should().Be(pinned);
    }

    [Fact]
    public async Task An_unanswered_refusal_is_logged_at_warning_with_its_fault()
    {
        var world = new AdmissionGateWorld();
        var fault = new TimeoutException("resolver down");
        world.Resolvers.Add(new ThrowingResolver(fault));
        var id = world.AddWorkflow(Graph(("r", Resolved, null)));
        var log = new WarningLog();

        await FluentActions.Awaiting(() => Replica(world, null, log).AdmitAsync(
                new WorkflowAdmissionRequest(id), Coordinator(WorkflowMode.Parallel), CancellationToken.None))
            .Should().ThrowAsync<WorkflowCompilationUnansweredException>();

        log.Written.Should().ContainSingle(e => e.Exception == fault).Which.Level.Should().Be(LogLevel.Warning);
    }

    private static async Task<string> Outcome(Func<Task<WorkflowAdmissionResult>> admit)
    {
        try
        {
            var result = await admit();
            result.Decision?.Lease?.Dispose();
            return result.Outcome;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static WorkflowRunAdmission Replica(AdmissionGateWorld world, IWorkflowFireDedup? dedup, ILogger? logger = null) =>
        new(world.Store, world.Blocks, world.Compiler(), world.Journal, AlwaysRunningExecutionGate.Instance, new Everyone(),
            new NoAudit(), NullWorkflowMetrics.Instance, logger ?? NullLogger.Instance, dedup);

    private static Func<Workflow, WorkflowRunCoordinator> Coordinator(WorkflowMode mode) =>
        workflow => new WorkflowRunCoordinator(workflow.Id, mode, 4, 0, WorkflowOverflowBehavior.Drop, NullWorkflowMetrics.Instance);

    private sealed class ClaimThrows : IWorkflowFireDedup
    {
        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken) =>
            Task.FromException<bool>(new TimeoutException("dedup down"));

        public string Fingerprint(string fireKey) => "fp";
    }

    private sealed class ClaimOnce : IWorkflowFireDedup
    {
        private readonly List<string> claimed = [];
        private readonly List<TimeSpan> windows = [];

        public IReadOnlyList<TimeSpan> Windows
        {
            get
            {
                lock (claimed)
                    return [.. windows];
            }
        }

        public IReadOnlyList<string> Claimed
        {
            get
            {
                lock (claimed)
                    return [.. claimed];
            }
        }

        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken)
        {
            lock (claimed)
            {
                windows.Add(window);
                if (claimed.Contains(fireKey, StringComparer.Ordinal))
                    return Task.FromResult(false);
                claimed.Add(fireKey);
                return Task.FromResult(true);
            }
        }

        public string Fingerprint(string fireKey) => "fp";
    }

    private sealed class ManualClock
    {
        public static readonly DateTimeOffset T0 = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Now { get; set; } = T0;
    }

    private sealed class Expiring(ManualClock clock) : IWorkflowFireDedup
    {
        private readonly Dictionary<string, DateTimeOffset> held = new(StringComparer.Ordinal);

        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken)
        {
            if (held.TryGetValue(fireKey, out var until) && until > clock.Now)
                return Task.FromResult(false);
            held[fireKey] = clock.Now + window;
            return Task.FromResult(true);
        }

        public string Fingerprint(string fireKey) => "fp";
    }

    private sealed class Grants : IWorkflowFireDedup
    {
        public List<string> Claimed { get; } = [];

        public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken)
        {
            Claimed.Add(fireKey);
            return Task.FromResult(true);
        }

        public string Fingerprint(string fireKey) => "fp";
    }

    private sealed class WarningLog : ILogger
    {
        private readonly List<(LogLevel Level, Exception? Exception)> written = [];

        public IReadOnlyList<(LogLevel Level, Exception? Exception)> Written
        {
            get
            {
                lock (written)
                    return [.. written];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (written)
                written.Add((logLevel, exception));
        }
    }

    private sealed class Everyone : IWorkflowAuthorization
    {
        public Task<bool> HasCapabilityAsync(Guid principalId, string capability, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class NoAudit : IWorkflowAuditSink
    {
        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingResolver(Func<Exception?> fault) : INodePortResolver
    {
        private int asked;

        public int Asked => Volatile.Read(ref asked);

        public string BlockTypeId => Resolved;

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref asked);
            return fault() is { } ex
                ? Task.FromException<NodePortResolution>(ex)
                : Task.FromResult(NodePortResolution.Resolved(null, CanvasSignature.Empty));
        }
    }

    private sealed class ThrowingResolver(Exception fault) : INodePortResolver
    {
        public string BlockTypeId => Resolved;

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken) =>
            Task.FromException<NodePortResolution>(fault);
    }

    private sealed class AnsweringHost : IExpressionNamespaceResolver
    {
        public string Namespace => "host";

        public ResolveResult Resolve(string[] segments) => ResolveResult.Resolved("answer");
    }

    private sealed class CancellingResolver(CancellationTokenSource caller) : INodePortResolver
    {
        public CancellationToken? Asked { get; private set; }

        public string BlockTypeId => Resolved;

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken)
        {
            Asked = cancellationToken;
            caller.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(NodePortResolution.Resolved(null, CanvasSignature.Empty));
        }
    }
}
