using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class NullDefinitionRunTests
{
    [Theory]
    [InlineData("null")]
    [InlineData(" \n null \t")]
    public async Task A_start_of_the_literal_null_is_refused_at_admission_as_unreadable(string definition)
    {
        var world = new DurableSleepWorld();
        var workflowId = world.AddRaw(definition);
        using var engine = world.Engine();

        var run = async () => await engine.ExecuteAsync(workflowId, null, null);

        (await run.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        var log = world.Journal.Single(workflowId);
        (log.Status, log.FailureReason).Should().Be((WorkflowExecutionStatus.Failed, WorkflowFailureReasons.DidNotCompile));
    }

    [Theory]
    [InlineData("null")]
    [InlineData(" \n null \t")]
    public async Task A_run_under_a_manifest_it_was_handed_is_refused_on_the_literal_null_as_an_unreadable_definition(string definition)
    {
        var world = new DurableSleepWorld();
        var workflowId = world.AddRaw(definition);
        var version = (await world.Store.LoadAsync(workflowId, default))!.CurrentVersion!.Id;
        using var engine = world.Engine();

        var run = async () =>
        {
            using (WorkflowRunPinScope.Push(new WorkflowRunPin
                   {
                       VersionId = version, Manifest = new Dictionary<Guid, Guid> { [workflowId] = version }
                   }))
                await engine.ExecuteAsync(workflowId, null, null);
        };

        (await run.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        var log = world.Journal.Single(workflowId);
        (log.Status, log.FailureReason).Should().Be((WorkflowExecutionStatus.Failed, WorkflowFailureReasons.DidNotCompile));
    }

    [Fact]
    public async Task A_graph_with_an_empty_node_list_still_completes()
    {
        var world = new DurableSleepWorld();
        var workflowId = world.AddRaw("""{"nodes":[],"edges":[]}""");
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Theory]
    [InlineData("""{"nodes":null,"edges":[]}""")]
    [InlineData("{}")]
    public async Task A_canvas_with_a_null_or_absent_node_list_is_refused_not_run(string definition)
    {
        var world = new DurableSleepWorld();
        var workflowId = world.AddRaw(definition);
        using var engine = world.Engine();

        var run = async () => await engine.ExecuteAsync(workflowId, null, null);

        (await run.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Message.Should().Contain("definition-unreadable");
    }

    private const string NullEndpoint =
        """{"nodes":[{"id":"a","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":null,"targetNodeId":"a","mappings":{}}]}""";

    private const string RootMapping =
        """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[],"outputMappings":[{"sourceNodeId":"start","sourceOutput":"y","workflowOutput":"x"}]}""";

    [Fact]
    public async Task A_start_on_a_stored_null_edge_endpoint_is_refused_at_admission_as_unreadable()
    {
        var world = new DurableSleepWorld();
        var workflowId = world.AddRaw(NullEndpoint);
        using var engine = world.Engine();

        var run = async () => await engine.ExecuteAsync(workflowId, null, null);

        (await run.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        var log = world.Journal.Single(workflowId);
        (log.Status, log.FailureReason).Should().Be((WorkflowExecutionStatus.Failed, WorkflowFailureReasons.DidNotCompile));
    }

    [Fact]
    public async Task A_run_under_a_handed_manifest_on_a_null_edge_endpoint_is_refused_as_an_unreadable_definition()
    {
        var world = new DurableSleepWorld();
        var workflowId = world.AddRaw(NullEndpoint);
        var version = (await world.Store.LoadAsync(workflowId, default))!.CurrentVersion!.Id;
        using var engine = world.Engine();

        var run = async () =>
        {
            using (WorkflowRunPinScope.Push(new WorkflowRunPin
                   {
                       VersionId = version, Manifest = new Dictionary<Guid, Guid> { [workflowId] = version }
                   }))
                await engine.ExecuteAsync(workflowId, null, null);
        };

        (await run.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        var log = world.Journal.Single(workflowId);
        (log.Status, log.FailureReason).Should().Be((WorkflowExecutionStatus.Failed, WorkflowFailureReasons.DidNotCompile));
    }

    private static async Task<(DurableSleepWorld World, WorkflowEngine Engine, Guid WorkflowId, Workflow Workflow)> ParkedAsync()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.Extra = id => id == "test.wait"
            ? new LambdaFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitForTrigger, "{}", DateTimeOffset.UtcNow.AddHours(1))))
            : null;
        var workflowId = world.AddRaw(System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "pause", blockTypeId = "test.wait", position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[] { new { id = "a", sourceNodeId = "start", targetNodeId = "pause", mappings = new { } } }
        }));
        var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        return (world, engine, workflowId, (await world.Store.LoadAsync(workflowId, default))!);
    }

    private static async Task<WorkflowResumeOutcome> ResumeAsync(DurableSleepWorld world, WorkflowEngine engine)
    {
        var ctx = (await world.Suspensions.TryResolveAsync(world.Suspensions.SingleRow.Id, WorkflowSuspensionStatus.TimedOut,
            """{"outcome":"timedOut"}""", DateTimeOffset.UtcNow, default))!;
        return await world.Resumer(engine).ResumeFromSuspensionAsync(ctx, default);
    }

    [Theory]
    [InlineData("null")]
    [InlineData(NullEndpoint)]
    public async Task A_resume_whose_pinned_version_is_unreadable_fails_the_run_and_is_abandoned(string text)
    {
        var (world, engine, workflowId, workflow) = await ParkedAsync();
        using var _ = engine;
        workflow.CurrentVersion!.UpdateDefinition(text, null, null);

        var outcome = await ResumeAsync(world, engine);

        outcome.Should().Be(WorkflowResumeOutcome.Abandoned);
        var log = world.Journal.Single(workflowId);
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().Be($"pinned_version_unreadable: {workflow.CurrentVersion.Id}");
    }

    [Fact]
    public async Task A_resume_on_a_version_left_as_written_with_root_output_mappings_is_abandoned_naming_the_rule()
    {
        var (world, engine, workflowId, workflow) = await ParkedAsync();
        using var _ = engine;
        workflow.CurrentVersion!.UpdateDefinition(RootMapping, null, null);

        var outcome = await ResumeAsync(world, engine);

        outcome.Should().Be(WorkflowResumeOutcome.Abandoned);
        var log = world.Journal.Single(workflowId);
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().StartWith("pinned_version_uncompilable: ").And.Contain(CompilationRules.OutputMappingsUnread);
    }
}
