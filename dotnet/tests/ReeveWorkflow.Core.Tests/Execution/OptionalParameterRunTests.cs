using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using static ReeveWorkflow.Core.Tests.Execution.AdmissionGateWorld;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class OptionalParameterRunTests
{
    private const string Parameters = """[{"name":"Mode","type":"String"},{"name":"Zone","type":"String","isRequired":true}]""";

    [Fact]
    public async Task An_optional_parameter_left_out_reads_as_null_and_the_node_runs()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("x", Leaf, new { v = "expr:${parameter.Mode}" })), inputParametersJson: Parameters);
        using var engine = world.Engine();

        await engine.ExecuteAsync(id, null, new Dictionary<string, object?> { ["Zone"] = "kitchen" });

        world.Executed.Should().Equal(["x"]);
        world.Received["x"].GetValueOrDefault("v").Should().BeNull();
    }

    [Fact]
    public async Task An_optional_parameter_that_was_supplied_reads_as_supplied()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("x", Leaf, new { v = "expr:${parameter.Mode}" })), inputParametersJson: Parameters);
        using var engine = world.Engine();

        await engine.ExecuteAsync(id, null, new Dictionary<string, object?> { ["Zone"] = "kitchen", ["Mode"] = "away" });

        world.Received["x"]["v"].Should().Be("away");
    }

    [Fact]
    public async Task A_parameter_a_JSON_body_supplied_reads_as_its_string_value()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("x", Leaf, new { v = "expr:${parameter.Mode}", w = "expr:${parameter.Zone}" })), inputParametersJson: Parameters);
        using var engine = world.Engine();
        var body = JsonSerializer.Deserialize<ExecuteWorkflowRequest>("""{"InputParameters":{"Zone":"kitchen","Mode":"away"}}""")!;
        body.InputParameters!["Mode"].Should().BeOfType<JsonElement>();

        await engine.ExecuteAsync(id, null, body.InputParameters);

        world.Received["x"]["v"].Should().Be("away");
        world.Received["x"]["w"].Should().Be("kitchen");
    }

    [Fact]
    public async Task A_required_parameter_left_out_refuses_the_start_before_the_first_node()
    {
        var world = new AdmissionGateWorld();
        var id = world.AddWorkflow(Graph(("x", Leaf, new { v = "expr:${parameter.Zone}" })), inputParametersJson: Parameters);
        using var engine = world.Engine();

        var refusal = (await FluentActions.Awaiting(() => engine.ExecuteAsync(id, null, null)).Should().ThrowAsync<WorkflowStartFindingsException>()).Which;

        var finding = refusal.Findings.Should().ContainSingle().Subject;
        (finding.Code, finding.Parameter).Should().Be((WorkflowStartArgumentCodes.Missing, "Zone"));
        world.Executed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_resumed_after_a_park_reads_an_optional_parameter_left_out_as_null()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Suspensions;
        var definition = JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, configuration = new { } },
                new { id = DurableSleepWorld.SleepNode, blockTypeId = DurableSleepWorld.SleepType, configuration = new { DurationMs = 600_000 } },
                new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, configuration = new { v = "expr:${parameter.Mode}" } }
            },
            edges = new object[]
            {
                new { id = "a", sourceNodeId = "start", targetNodeId = DurableSleepWorld.SleepNode, mappings = new { } },
                new { id = "b", sourceNodeId = DurableSleepWorld.SleepNode, targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
            }
        });
        var id = world.AddRaw(definition, inputParametersJson: """[{"name":"Mode","type":"String"}]""");
        using var engine = world.Engine();
        await engine.ExecuteAsync(id, null, null);

        world.PassTime(TimeSpan.FromHours(1));
        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(1);

        world.Journal.Single(id).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().ContainSingle().Which.GetValueOrDefault("v").Should().BeNull();
    }
}
