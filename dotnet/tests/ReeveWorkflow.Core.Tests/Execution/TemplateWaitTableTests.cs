using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class TemplateWaitTableTests
{
    private const string Wait = "test.template-table-wait";
    private const string Probe = "test.template-table-probe";
    private const string NIsAParameter = """[{"name":"n","type":"Int","isRequired":true}]""";

    [Theory]
    [InlineData("Label")]
    [InlineData("Missing")]
    public async Task A_park_whose_input_names_no_condition_fails_the_node_with_suspend_config_invalid(string input)
    {
        var (world, probed) = World(input);
        var workflowId = world.AddRaw(Definition("true", NodeErrorPolicy.EmitError), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        world.Suspensions.Rows.Should().BeEmpty("the park was refused");
        ErrorCode(world).Should().Be(WorkflowErrorCodes.SuspendConfigInvalid);
        probed().Should().Be(1, "the node's policy carried the run on");
    }

    [Theory]
    [InlineData("""{"input":"Label","input":"Condition"}""")]
    [InlineData("""{"Input":"Label","input":"Condition"}""")]
    public async Task A_park_whose_config_text_repeats_a_member_name_is_refused_whatever_the_casing(string config)
    {
        var (world, probed) = World(configJson: config);
        var workflowId = world.AddRaw(Definition("true", NodeErrorPolicy.EmitError), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        world.Suspensions.Rows.Should().BeEmpty("the park was refused");
        ErrorCode(world).Should().Be(WorkflowErrorCodes.SuspendConfigInvalid);
        probed().Should().Be(1, "the node's policy carried the run on");
    }

    [Fact]
    public async Task A_condition_that_answers_true_resumes_the_run_satisfied_with_last_evaluation_true()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition("${parameter.n} == 1"), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        (await Waker(world, engine).WakeAndWalkAsync()).Should().Be(1);

        var row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(row.OutcomeJson!).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);
        outcome.GetProperty("_resumeReason").GetString().Should().Be("poll");
        outcome.GetProperty("lastEvaluation").GetBoolean().Should().BeTrue();
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        probed().Should().Be(1);
    }

    [Fact]
    public async Task A_condition_below_the_root_reads_an_optional_parameter_its_call_did_not_supply_as_null()
    {
        var (world, _) = World();
        world.Blocks.LoopBodyInputs =
        [
            new BlockPortDefinition("Index", "Index", PortValueType.Int, false),
            new BlockPortDefinition("opt", "opt", PortValueType.OfDeclaration("Int?"), false),
        ];
        var workflowId = world.AddRaw(WaitInALoop("${parameter.opt} == null"));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);

        (await Waker(world, engine).WakeAndWalkAsync()).Should().Be(1);

        var outcome = JsonDocument.Parse(world.Suspensions.SingleRow.OutcomeJson!).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    private static string WaitInALoop(string condition) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.LoopType, position = new { x = 0, y = 0 }, configuration = new { },
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new
                            {
                                id = "wait", blockTypeId = Wait, position = new { x = 0, y = 0 },
                                configuration = new Dictionary<string, object> { [TemplateWaits.Input] = "expr:" + condition },
                            },
                        },
                        edges = new object[] { new { id = "in-wait", sourceNodeId = "in", targetNodeId = "wait", mappings = new { } } },
                    },
                },
            },
        },
        edges = new object[] { new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } },
    });

    [Fact]
    public async Task A_condition_that_answers_false_leaves_the_row_parked()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition("${parameter.n} == 1"), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 2 });

        (await Waker(world, engine).WakeAndWalkAsync()).Should().Be(0);

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        probed().Should().Be(0);
    }

    [Theory]
    [InlineData(NodeErrorPolicy.FailWorkflow)]
    [InlineData(NodeErrorPolicy.EmitError)]
    public async Task A_condition_that_throws_resumes_the_row_faulted_and_fails_the_node_through_its_policy(NodeErrorPolicy policy)
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition("${parameter.n} / 0 > 1.5", policy), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        (await Waker(world, engine).WakeAndWalkAsync()).Should().Be(1);

        var row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(row.OutcomeJson!).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Faulted);
        outcome.GetProperty("error").GetProperty("code").GetString().Should().Be(WorkflowErrorCodes.ExpressionRuntimeError);
        var log = world.Journal.Single(workflowId);
        if (policy == NodeErrorPolicy.FailWorkflow)
        {
            log.Status.Should().Be(WorkflowExecutionStatus.Failed);
            log.ErrorMessage.Should().NotStartWith(WorkflowAbandonmentReasons.ResumeValueInvalid);
            probed().Should().Be(0);
        }
        else
        {
            log.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
            ErrorCode(world).Should().Be(WorkflowErrorCodes.ExpressionRuntimeError);
            probed().Should().Be(1, "the policy carried the run past the node");
        }
    }

    [Fact]
    public async Task A_pinned_version_that_no_longer_compiles_resolves_the_row_unevaluable_and_the_run_is_abandoned()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition("${parameter.n} == 1"), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var withoutTheBlock = new WorkflowCompilationService(new UncheckedWorkflowBlocks([]), world.RunScopes);

        await Waker(world, engine, withoutTheBlock).WakeAndWalkAsync();

        var row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(row.OutcomeJson!).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Unevaluable);
        outcome.GetProperty("reason").GetString().Should().Be(WorkflowAbandonmentReasons.PinnedVersionUncompilable);
        var log = world.Journal.Single(workflowId);
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().StartWith(WorkflowAbandonmentReasons.PinnedVersionUncompilable,
            "the resumer's own compile succeeds under its registry, so the outcome's reason is the one it abandons with");
        probed().Should().Be(0);
    }

    [Theory]
    [InlineData(WorkflowAbandonmentReasons.ResumeValueInvalid)]
    [InlineData(WorkflowAbandonmentReasons.PinnedVersionUncompilable)]
    public async Task An_unevaluable_answer_abandons_the_run_with_its_reason(string reason)
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition("${parameter.n} == 1"), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;

        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            $$"""{"outcome":"unevaluable","_resumeReason":"poll","reason":"{{reason}}"}""", DateTimeOffset.UtcNow, default))!;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Abandoned);
        world.Journal.Single(workflowId).ErrorMessage.Should().StartWith(reason);
        probed().Should().Be(0);
    }

    [Fact]
    public async Task A_timeout_resolves_the_row_timed_out_without_evaluating()
    {
        var (world, probed) = World(timeoutAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1));
        var workflowId = world.AddRaw(Definition("${parameter.n} / 0 > 1.5"), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        await Waker(world, engine).WakeAndWalkAsync();

        var row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.TimedOut);
        JsonDocument.Parse(row.OutcomeJson!).RootElement.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.TimedOut);
        probed().Should().Be(1, "a timed-out wait goes on down its block's own path");
    }

    private static (DurableSleepWorld World, Func<int> Probed) World(string input = TemplateWaits.Input, DateTimeOffset? timeoutAtUtc = null, string? configJson = null)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        var probed = 0;
        world.Blocks.Extra = id => id switch
        {
            Wait => new LambdaFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                    WorkflowSuspensionKind.WaitTemplate,
                    configJson ?? JsonSerializer.Serialize(new WorkflowWaitTemplateConfig { Input = input }),
                    timeoutAtUtc ?? DateTimeOffset.UtcNow.AddHours(1))),
                inputs: [TemplateWaits.ConditionPort, new BlockPortDefinition("Label", "Label", PortValueType.String, false)]),
            Probe => new LambdaFactory(id, inputs =>
            {
                Interlocked.Increment(ref probed);
                world.Observer.Timeline.Enqueue("code:" + inputs.GetValueOrDefault("code"));
                return new Dictionary<string, object?>();
            }, inputs: [new BlockPortDefinition("code", "code", PortValueType.OfDeclaration("String?"), false)]),
            _ => null
        };
        return (world, () => Volatile.Read(ref probed));
    }

    private static WorkflowSuspensionWaker Waker(DurableSleepWorld world, WorkflowEngine engine, WorkflowCompilationService? compiler = null) =>
        new(new SuspensionInputParameterTests.EveryParkedRow(world.Suspensions), world.Resumer(engine), NullLogger.Instance,
            compiler ?? new WorkflowCompilationService(world.Blocks, world.RunScopes),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero }, scopes: world.RunScopes);

    private static string ErrorCode(DurableSleepWorld world) =>
        world.Observer.Timeline.Last(mark => mark.StartsWith("code:", StringComparison.Ordinal))["code:".Length..];

    private static string Definition(string condition, NodeErrorPolicy policy = NodeErrorPolicy.FailWorkflow) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "wait", blockTypeId = Wait, position = new { x = 0, y = 0 },
                configuration = new Dictionary<string, object> { [TemplateWaits.Input] = "expr:" + condition, ["Label"] = "x" },
                errorPolicy = new { policy = policy.ToString() },
            },
            new { id = "probe", blockTypeId = Probe, position = new { x = 0, y = 0 }, configuration = new { } },
        },
        edges = new object[]
        {
            new { id = "a", sourceNodeId = "start", targetNodeId = "wait", mappings = new { } },
            new
            {
                id = "b", sourceNodeId = "wait", targetNodeId = "probe",
                mappings = policy == NodeErrorPolicy.EmitError
                    ? (object)new Dictionary<string, object>
                    {
                        ["code"] = new { type = "direct", sourceOutputName = "_error", projection = new { path = "$.code" } },
                    }
                    : new { },
            },
        },
    });
}
