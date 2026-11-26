using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunResultProjectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private const string Graph = """
        {"nodes":[{"id":"start","blockTypeId":"WorkflowInput"},{"id":"src","blockTypeId":"test.produce"},{"id":"out","blockTypeId":"WorkflowOutput"}],
         "edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"src","mappings":{}},
                  {"id":"out-0","sourceNodeId":"src","targetNodeId":"out","mappings":{"name":{"type":"direct","sourceOutputName":"doc","projection":{"path":"$.name"}}}}]}
        """;

    [Fact]
    public async Task A_fresh_run_whose_root_projection_finds_nothing_fails_with_the_projection_code_and_category()
    {
        var world = World();
        using var engine = world.Engine();

        var thrown = await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, new Dictionary<string, object?>()).WaitAsync(Patience))
            .Should().ThrowAsync<WorkflowNodeExecutionException>();

        thrown.Which.NodeId.Should().Be("out");
        WorkflowFailureCoding.OfNode(thrown.Which).Should().Be(
            new WorkflowFailureCode(WorkflowErrorCodes.JsonProjectionFailed, JsonProjectionException.ValueAbsent));
        var log = world.Journal.Logs.Values.Should().ContainSingle().Subject;
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().StartWith($"{WorkflowErrorCodes.JsonProjectionFailed} ({JsonProjectionException.ValueAbsent})");
        log.OutputsJson.Should().BeNull("a run that failed records no result");
    }

    [Fact]
    public async Task A_resumed_run_whose_root_projection_finds_nothing_fails_with_the_projection_code_and_category()
    {
        var world = World();
        var parked = world.Parked();

        var outcome = await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        outcome.Should().Be(WorkflowResumeOutcome.Resumed);
        var log = world.Journal.Logs[parked.ExecutionLogId];
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().StartWith($"{WorkflowErrorCodes.JsonProjectionFailed} ({JsonProjectionException.ValueAbsent})");
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Failed);
    }

    private static RunLifecycleWorld World()
    {
        var world = new RunLifecycleWorld(definitionJson: Graph, inputParametersJson: "[]",
            outputParametersJson: """[{"name":"name","type":"String"}]""");
        world.AdditionalBlocks = id => id == "test.produce"
            ? new WorkflowRunOutputSanitizationTests.ProducingFactory([("doc", JsonDocument.Parse("{}").RootElement.Clone())])
            : null;
        return world;
    }
}
