using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class PredicateWaitTableTests
{
    private const string Wait = "test.predicate-wait";
    private const string Probe = "test.predicate-probe";
    private const string NIsAParameter = """[{"name":"n","type":"Int","isRequired":true}]""";
    private const string Above20 = "${value} > 20";

    [Fact]
    public async Task A_wake_whose_predicate_is_true_resumes_with_the_blocks_typed_outputs_and_audits_resumed_by_poll()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;

        (await WakeAsync(world, engine, row.Id, new() { ["value"] = 25 })).Should().BeTrue();

        row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(row.OutcomeJson!).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);
        outcome.GetProperty("_resumeReason").GetString().Should().Be("poll");
        outcome.GetProperty("value").GetInt64().Should().Be(25, "a variable is written in its term's JSON form");
        outcome.TryGetProperty("lastEvaluation", out _).Should().BeFalse("a predicate outcome carries no lastEvaluation");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        probed().Should().Be(1);
        world.Observer.Timeline.Should().Contain(["outcome:satisfied", "got:25:Int64"],
            "the block read Value through context.Types, so it is the Int term's CLR type");
        world.Audit.Kinds.Should().Contain(WorkflowAuditEventKinds.ResumedByPoll);
    }

    [Fact]
    public async Task A_wake_whose_predicate_is_false_leaves_the_row_parked()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;
        var metrics = new WakeOutcomes();

        (await WakeAsync(world, engine, row.Id, new() { ["value"] = 7 }, metrics: metrics)).Should().BeFalse();

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        metrics.Outcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.NotReady);
        probed().Should().Be(0);
    }

    [Theory]
    [InlineData(NodeErrorPolicy.FailWorkflow)]
    [InlineData(NodeErrorPolicy.EmitError)]
    public async Task A_predicate_that_throws_resumes_the_row_faulted_and_fails_the_node_through_its_policy(NodeErrorPolicy policy)
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition("${value} / 0 > 1", policy), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;

        await WakeAsync(world, engine, row.Id, new() { ["value"] = 25 });

        row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(row.OutcomeJson!).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Faulted);
        outcome.GetProperty("error").GetProperty("code").GetString().Should().Be(WorkflowErrorCodes.ExpressionRuntimeError);
        var log = world.Journal.Single(workflowId);
        if (policy == NodeErrorPolicy.FailWorkflow)
        {
            log.Status.Should().Be(WorkflowExecutionStatus.Failed);
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
    public async Task A_waker_whose_compile_of_the_pinned_version_throws_leaves_the_row_parked()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;
        var catalogue = new ThrowingBlocks(world.Blocks);
        var throwing = new WorkflowCompilationService(catalogue, world.RunScopes);
        catalogue.Armed = true;

        (await WakeAsync(world, engine, row.Id, new() { ["value"] = 25 }, compiler: throwing)).Should().BeFalse();

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running);
        probed().Should().Be(0);
    }

    [Fact]
    public async Task An_unreadable_parked_payload_resolves_the_row_unevaluable_and_the_resumer_abandons_the_run()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;
        world.Suspensions.Corrupt(row.Id, payload => WithParameter(payload, "n", "not an Int"));

        await WakeAsync(world, engine, row.Id, new() { ["value"] = 25 });

        row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        var outcome = JsonDocument.Parse(row.OutcomeJson!).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Unevaluable);
        outcome.GetProperty("reason").GetString().Should().Be(WorkflowAbandonmentReasons.ResumeValueInvalid);
        var log = world.Journal.Single(workflowId);
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().StartWith(WorkflowAbandonmentReasons.ResumeValueInvalid);
        probed().Should().Be(0);
        world.Audit.Kinds.Should().NotContain(WorkflowAuditEventKinds.ResumedByPoll, "an abandoned run writes no resume audit");
    }

    public static TheoryData<string, string, int?> RefusedWakes => new()
    {
        { "missing variable", WorkflowSuspensionWakeReasons.Poll, null },
        { "undeclared variable", WorkflowSuspensionWakeReasons.Poll, 25 },
        { "refused by the gate", WorkflowSuspensionWakeReasons.Poll, 25 },
        { "no variables", WorkflowSuspensionWakeReasons.Poll, null },
        { "helper change", WorkflowSuspensionWakeReasons.HelperChange, 25 },
        { "trigger", WorkflowSuspensionWakeReasons.Trigger, 25 },
    };

    [Theory]
    [MemberData(nameof(RefusedWakes))]
    public async Task A_wake_the_row_refuses_is_counted_dropped_and_the_row_stays_parked(string wake, string reason, int? value)
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;
        var metrics = new WakeOutcomes();
        Dictionary<string, object?>? variables = wake switch
        {
            "missing variable" => [],
            "undeclared variable" => new() { ["value"] = value, ["other"] = 1 },
            "refused by the gate" => new() { ["value"] = "twenty-five" },
            "no variables" => null,
            _ => new() { ["value"] = value },
        };

        (await WakeAsync(world, engine, row.Id, variables, reason, metrics)).Should().BeFalse();

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        metrics.Outcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Dropped);
        probed().Should().Be(0);
    }

    [Fact]
    public async Task A_predicate_outcome_holds_the_variables_and_not_the_wakes_data()
    {
        var (world, _) = World();
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;

        (await WakeAsync(world, engine, row.Id, new() { ["value"] = 25 }, data: new() { ["extra"] = "from the host" }))
            .Should().BeTrue();

        var outcome = JsonDocument.Parse(world.Suspensions.SingleRow.OutcomeJson!).RootElement;
        outcome.GetProperty("value").GetInt64().Should().Be(25);
        outcome.TryGetProperty("extra", out _).Should().BeFalse("the wake's Data is not part of a predicate's outcome");
    }

    [Fact]
    public async Task A_wake_carrying_variables_for_a_row_of_another_kind_is_counted_dropped()
    {
        var (world, _) = World();
        world.Blocks.Extra = id => id == Wait
            ? new LambdaFactory(id, _ => throw TemplateWaits.Park(), inputs: [TemplateWaits.ConditionPort])
            : null;
        var workflowId = world.AddRaw(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new
                {
                    id = "wait", blockTypeId = Wait, position = new { x = 0, y = 0 },
                    configuration = new Dictionary<string, object> { [TemplateWaits.Input] = "expr:true" },
                },
            },
            edges = new object[] { new { id = "a", sourceNodeId = "start", targetNodeId = "wait", mappings = new { } } },
        }));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        row.Kind.Should().Be(WorkflowSuspensionKind.WaitTemplate);
        var metrics = new WakeOutcomes();

        (await WakeAsync(world, engine, row.Id, new() { ["value"] = 25 }, metrics: metrics)).Should().BeFalse();

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended,
            "the template is true, so only the refused wake kept it parked");
        metrics.Outcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.Dropped);
    }

    [Fact]
    public async Task A_satisfied_answer_whose_resume_is_deferred_is_not_put_back()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;
        var waker = new WorkflowSuspensionWaker(new WakeableRows(world.Suspensions), new DeferringResumer(), NullLogger.Instance,
            new WorkflowCompilationService(world.Blocks, world.RunScopes),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero }, scopes: world.RunScopes);

        await waker.WakeNowAsync(PollWake(row.Id, new() { ["value"] = 25 }), default);
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));

        row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.Resumed, "nothing but a new wake would answer it again");
        JsonDocument.Parse(row.OutcomeJson!).RootElement.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.Satisfied);
        probed().Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_lookup_that_throws_while_the_block_builds_its_outputs_defers_the_resume_and_the_next_one_resumes(bool asJson)
    {
        var (world, probed) = World();
        var down = false;
        world.ExpressionNamespaces =
        [
            new WorkflowExpressionNamespace("device", 1, new AnyDevice(), (_, _) => Volatile.Read(ref down)
                ? throw (asJson ? new JsonException("torn cache entry") : new InvalidOperationException("redis down"))
                : Task.FromResult<IExpressionNamespaceResolver>(new NoDevices()))
        ];
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var row = world.Suspensions.SingleRow;
        Volatile.Write(ref down, true);
        var resumer = new RecordingResumer(world.Resumer(engine));
        var waker = new WorkflowSuspensionWaker(new WakeableRows(world.Suspensions), resumer, NullLogger.Instance,
            new WorkflowCompilationService(world.Blocks, world.RunScopes),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero }, scopes: world.RunScopes);

        await waker.WakeNowAsync(PollWake(row.Id, new() { ["value"] = 25 }), default);
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));

        resumer.Outcomes.Should().Equal(WorkflowResumeOutcome.Deferred);
        world.Journal.Single(workflowId).Status.Should().NotBe(WorkflowExecutionStatus.Failed);
        probed().Should().Be(0);

        Volatile.Write(ref down, false);
        (await world.Resumer(engine).ResumeFromSuspensionAsync(resumer.Contexts.Single(), default))
            .Should().Be(WorkflowResumeOutcome.Resumed);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Observer.Timeline.Should().Contain(["outcome:satisfied", "got:25:Int64"]);
    }

    [Fact]
    public async Task The_timeout_answers_timed_out_and_audits_resumed_by_timeout()
    {
        var (world, probed) = World(timeoutAtUtc: DateTimeOffset.UtcNow.AddSeconds(-1));
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        (await Waker(world, engine).WakeAndWalkAsync()).Should().Be(1);

        var row = world.Suspensions.SingleRow;
        row.Status.Should().Be(WorkflowSuspensionStatus.TimedOut);
        var outcome = JsonDocument.Parse(row.OutcomeJson!).RootElement;
        outcome.GetProperty("outcome").GetString().Should().Be(WorkflowWaitOutcomes.TimedOut);
        outcome.GetProperty("_resumeReason").GetString().Should().Be("timeout");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        probed().Should().Be(1);
        world.Observer.Timeline.Should().Contain(["outcome:timed_out", "got:"]);
        world.Audit.Kinds.Should().Contain(WorkflowAuditEventKinds.ResumedByTimeout);
    }

    [Fact]
    public async Task A_pass_without_a_wake_leaves_a_row_before_its_timeout_parked()
    {
        var (world, probed) = World();
        var workflowId = world.AddRaw(Definition("true"), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        (await Waker(world, engine).WakeAndWalkAsync()).Should().Be(0);

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        probed().Should().Be(0);
    }

    [Theory]
    [InlineData(Mode.WithoutResumedOutputs, TemplateWaits.Input)]
    [InlineData(Mode.Reads, "Label")]
    public async Task A_park_the_executor_refuses_fails_the_node_with_suspend_config_invalid(Mode mode, string input)
    {
        var (world, probed) = World(mode: mode, input: input);
        var workflowId = world.AddRaw(Definition(Above20, NodeErrorPolicy.EmitError), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        world.Suspensions.Rows.Should().BeEmpty("the park was refused");
        ErrorCode(world).Should().Be(WorkflowErrorCodes.SuspendConfigInvalid);
        probed().Should().Be(1, "the node's policy carried the run on");
    }

    [Theory]
    [InlineData("""{"input":"Label","input":"Condition","keys":["k"]}""")]
    [InlineData("""{"Input":"Label","input":"Condition","keys":["k"]}""")]
    [InlineData("""{"input":"Condition","keys":["k"],"host":{"a":1,"a":2}}""")]
    public async Task A_park_whose_config_text_repeats_a_member_name_is_refused_whatever_the_casing(string config)
    {
        var (world, probed) = World(configJson: config);
        var workflowId = world.AddRaw(Definition(Above20, NodeErrorPolicy.EmitError), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        world.Suspensions.Rows.Should().BeEmpty("the park was refused");
        ErrorCode(world).Should().Be(WorkflowErrorCodes.SuspendConfigInvalid);
        probed().Should().Be(1, "the node's policy carried the run on");
    }

    [Theory]
    [InlineData(Mode.Throws)]
    [InlineData(Mode.AnswersNull)]
    public async Task A_block_that_cannot_build_its_resumed_outputs_fails_the_node_with_block_failed(Mode mode)
    {
        var (world, probed) = World(mode: mode);
        var workflowId = world.AddRaw(Definition(Above20, NodeErrorPolicy.EmitError), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        await WakeAsync(world, engine, world.Suspensions.SingleRow.Id, new() { ["value"] = 25 });

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        ErrorCode(world).Should().Be(WorkflowErrorCodes.BlockFailed);
        probed().Should().Be(1, "the policy carried the run past the node");
    }

    [Fact]
    public async Task A_throwing_helper_read_for_resumed_outputs_fails_the_node_without_deferring_the_resume()
    {
        var resolver = new ThrowOnSecondReadResolver();
        var (world, _) = World(mode: Mode.ReadsExpression);
        world.HelperResolver = resolver;
        world.CompilationOptions = new CompilationOptions { HelperResolver = resolver };
        var workflowId = world.AddRaw(Definition("${helper.x} + ${value} > 20", NodeErrorPolicy.EmitError), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        await WakeAsync(world, engine, world.Suspensions.SingleRow.Id, new() { ["value"] = 25 });

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        resolver.ReadCalls.Should().Be(2);
        ErrorCode(world).Should().Be(WorkflowErrorCodes.ExpressionRuntimeError);
    }

    [Fact]
    public async Task A_helper_free_resume_does_not_check_or_read_helpers()
    {
        var resolver = new NoHelperCallsResolver();
        var (world, _) = World(mode: Mode.ReadsExpression);
        world.HelperResolver = resolver;
        world.CompilationOptions = new CompilationOptions { HelperResolver = resolver };
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        await WakeAsync(world, engine, world.Suspensions.SingleRow.Id, new() { ["value"] = 25 });

        resolver.CheckCalls.Should().Be(0);
        resolver.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_block_whose_resumed_read_is_off_its_term_abandons_the_run_with_resume_value_invalid()
    {
        var (world, probed) = World(mode: Mode.ReadsOffTerm);
        var workflowId = world.AddRaw(Definition(Above20, NodeErrorPolicy.EmitError), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });

        await WakeAsync(world, engine, world.Suspensions.SingleRow.Id, new() { ["value"] = 25 });

        var log = world.Journal.Single(workflowId);
        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.ErrorMessage.Should().StartWith(WorkflowAbandonmentReasons.ResumeValueInvalid);
        probed().Should().Be(0);
    }

    [Fact]
    public async Task A_waker_whose_kinds_leave_predicate_out_answers_its_wakes_not_mine()
    {
        var (world, _) = World();
        var workflowId = world.AddRaw(Definition(Above20), inputParametersJson: NIsAParameter);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, new Dictionary<string, object?> { ["n"] = 1 });
        var metrics = new WakeOutcomes();

        (await WakeAsync(world, engine, world.Suspensions.SingleRow.Id, new() { ["value"] = 25 }, metrics: metrics,
            kinds: [WorkflowSuspensionKind.WaitTemplate])).Should().BeFalse();

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        metrics.Outcomes.Should().Equal(WorkflowSuspensionWakeOutcomes.NotMine);
    }

    [Theory]
    [InlineData("", "k")]
    [InlineData(" ", "k")]
    [InlineData("Predicate", "")]
    [InlineData("Predicate", "  ")]
    public void A_predicate_config_with_an_empty_input_or_a_blank_key_throws(string input, string key)
    {
        var build = () => new WorkflowPredicateWaitConfig(input, [key], null);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_predicate_rows_dependency_keys_are_its_config_keys_behind_the_predicate_prefix()
    {
        var config = JsonSerializer.Serialize(new WorkflowPredicateWaitConfig("Predicate", ["ship-1/temp", "ship-1/door"], null));

        WorkflowSuspensionWaker.DependencyKeysOf(WorkflowSuspensionKind.Predicate, config,
                Core.Expressions.ExpressionDependencyWalker.Instance, WorkflowGraphHelpers.JsonOptions)
            .Should().BeEquivalentTo(["predicate-key:ship-1/temp", "predicate-key:ship-1/door"]);
    }

    public enum Mode
    {
        Reads,
        ReadsExpression,
        ReadsOffTerm,
        Throws,
        AnswersNull,
        WithoutResumedOutputs,
    }

    private static WorkflowSuspensionWake PollWake(Guid suspensionId, Dictionary<string, object?>? variables,
        string reason = WorkflowSuspensionWakeReasons.Poll, Dictionary<string, object?>? data = null) =>
        new() { SuspensionId = suspensionId, Reason = reason, Variables = variables, Data = data };

    private static async Task<bool> WakeAsync(
        DurableSleepWorld world, WorkflowEngine engine, Guid suspensionId, Dictionary<string, object?>? variables,
        string reason = WorkflowSuspensionWakeReasons.Poll, WakeOutcomes? metrics = null, WorkflowCompilationService? compiler = null,
        IReadOnlyCollection<WorkflowSuspensionKind>? kinds = null, Dictionary<string, object?>? data = null)
    {
        var waker = new WorkflowSuspensionWaker(new WakeableRows(world.Suspensions), world.Resumer(engine), NullLogger.Instance,
            compiler ?? new WorkflowCompilationService(world.Blocks, world.RunScopes, world.CompilationOptions),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, Kinds = kinds }, helperResolver: world.HelperResolver,
            scopes: world.RunScopes, metrics: metrics);
        var taken = await waker.WakeNowAsync(PollWake(suspensionId, variables, reason, data), default);
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return taken;
    }

    private static WorkflowSuspensionWaker Waker(DurableSleepWorld world, WorkflowEngine engine) =>
        new(new WakeableRows(world.Suspensions), world.Resumer(engine), NullLogger.Instance,
            new WorkflowCompilationService(world.Blocks, world.RunScopes),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero }, helperResolver: world.HelperResolver, scopes: world.RunScopes);

    private static (DurableSleepWorld World, Func<int> Probed) World(
        Mode mode = Mode.Reads, string input = TemplateWaits.Input, DateTimeOffset? timeoutAtUtc = null, string? configJson = null)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        var probed = 0;
        world.Blocks.Extra = id => id switch
        {
            Wait => new PredicateWaitFactory(mode, input, timeoutAtUtc ?? DateTimeOffset.UtcNow.AddHours(1), configJson),
            Probe => new LambdaFactory(id, inputs =>
            {
                Interlocked.Increment(ref probed);
                world.Observer.Timeline.Enqueue("code:" + inputs.GetValueOrDefault("code"));
                world.Observer.Timeline.Enqueue("outcome:" + inputs.GetValueOrDefault("outcome"));
                world.Observer.Timeline.Enqueue(inputs.GetValueOrDefault("got") is { } got ? $"got:{got}:{got.GetType().Name}" : "got:");
                return new Dictionary<string, object?>();
            }, inputs:
            [
                new BlockPortDefinition("code", "code", PortValueType.OfDeclaration("String?"), false),
                new BlockPortDefinition("outcome", "outcome", PortValueType.OfDeclaration("String?"), false),
                new BlockPortDefinition("got", "got", PortValueType.OfDeclaration("Int?"), false),
            ]),
            _ => null
        };
        return (world, () => Volatile.Read(ref probed));
    }

    private static string ErrorCode(DurableSleepWorld world) =>
        world.Observer.Timeline.Last(mark => mark.StartsWith("code:", StringComparison.Ordinal))["code:".Length..];

    private static string WithParameter(string payload, string name, string value)
    {
        var root = JsonNode.Parse(payload)!.AsObject();
        var key = root.Select(p => p.Key).Single(k => string.Equals(k, "PreviousOutputsJson", StringComparison.OrdinalIgnoreCase));
        var outputs = JsonNode.Parse(root[key]!.GetValue<string>())!;
        outputs["start"]![name] = value;
        root[key] = outputs.ToJsonString();
        return root.ToJsonString();
    }

    private static string Definition(string predicate, NodeErrorPolicy policy = NodeErrorPolicy.FailWorkflow) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "wait", blockTypeId = Wait, position = new { x = 0, y = 0 },
                configuration = new Dictionary<string, object> { [TemplateWaits.Input] = "expr:" + predicate, ["Label"] = "x" },
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
                    : new Dictionary<string, object>
                    {
                        ["outcome"] = new { type = "direct", sourceOutputName = "Outcome" },
                        ["got"] = new { type = "direct", sourceOutputName = "Value" },
                    },
            },
        },
    });

    private sealed class PredicateWaitFactory(Mode mode, string input, DateTimeOffset timeoutAtUtc, string? configJson)
        : IWorkflowBlockFactory
    {
        public string BlockTypeId => Wait;
        public string DisplayName => Wait;
        public string Description => string.Empty;
        public string Category => "test";
        public bool CanSuspend => true;

        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [
            new(TemplateWaits.Input, TemplateWaits.Input, PortValueType.OfDeclaration("Expression<Boolean>"), true)
            {
                ScopeVariables = [new ExpressionScopeVariable("value", "Int")],
            },
            new("Label", "Label", PortValueType.String, false),
        ];

        public IReadOnlyCollection<BlockPortDefinition> Outputs =>
        [
            new("Outcome", "Outcome", PortValueType.String, false),
            new("Value", "Value", PortValueType.OfDeclaration("Int?"), false),
        ];

        public IWorkflowBlockPlugin Create() =>
            mode == Mode.WithoutResumedOutputs ? new Parking(input, timeoutAtUtc, configJson) : new Resuming(mode, input, timeoutAtUtc, configJson);

        private class Parking(string input, DateTimeOffset timeoutAtUtc, string? configJson) : IWorkflowBlockPlugin
        {
            public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
                IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
                throw new WorkflowSuspendException(new SuspendResult(WorkflowSuspensionKind.Predicate,
                    configJson ?? JsonSerializer.Serialize(new WorkflowPredicateWaitConfig(input, ["ship-1/temp"], null)), timeoutAtUtc));
        }

        private sealed class Resuming(Mode mode, string input, DateTimeOffset timeoutAtUtc, string? configJson)
            : Parking(input, timeoutAtUtc, configJson), IWorkflowResumedOutputs
        {
        public IReadOnlyDictionary<string, object?> ResumedOutputs(JsonElement outcome, WorkflowBlockContext context) => mode switch
        {
                Mode.Throws => throw new InvalidOperationException("this block cannot read its outcome"),
                Mode.AnswersNull => null!,
                Mode.ReadsOffTerm => new Dictionary<string, object?>
                {
                    ["Value"] = context.Types["Value"].FromJson(outcome.GetProperty("outcome")),
                },
                Mode.ReadsExpression => ReadExpressionThenBuild(outcome, context),
                _ => new Dictionary<string, object?>
                {
                    ["Outcome"] = outcome.GetProperty("outcome").GetString(),
                    ["Value"] = outcome.TryGetProperty("value", out var value) ? context.Types["Value"].FromJson(value) : null,
                },
            };

            private IReadOnlyDictionary<string, object?> ReadExpressionThenBuild(JsonElement outcome, WorkflowBlockContext context)
            {
                _ = context.Expressions.Evaluate(input, new Dictionary<string, object?> { ["value"] = outcome.GetProperty("value").GetInt64() });
                return new Dictionary<string, object?>
                {
                    ["Outcome"] = outcome.GetProperty("outcome").GetString(),
                    ["Value"] = outcome.GetProperty("value").GetInt64(),
                };
            }
        }
    }

    private sealed class ThrowOnSecondReadResolver : IHelperResolver
    {
        public int ReadCalls { get; private set; }
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(
                paths.ToDictionary(path => path, _ => new NamespacePathCheckAnswer(NamespacePathCheck.Yes, "Int")));
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return ReadCalls == 2
                ? ValueTask.FromException<IReadOnlyDictionary<HelperPath, object?>>(new InvalidOperationException("resume read failed"))
                : ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(paths.ToDictionary(path => path, _ => (object?)1L));
        }
    }

    private sealed class NoHelperCallsResolver : IHelperResolver
    {
        public int CheckCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            CheckCalls++;
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(new Dictionary<HelperPath, NamespacePathCheckAnswer>());
        }
        public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(
            IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken)
        {
            ReadCalls++;
            return ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
        }
    }

    private sealed class WakeableRows(MemorySuspensions inner) : IWorkflowSuspensionResumeStore
    {
        private readonly SuspensionInputParameterTests.EveryParkedRow rows = new(inner);

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            rows.ScanAsync(afterPosition, limit, cancellationToken);

        public Task<WorkflowParkedRowRead?> GetParkedAsync(Guid? suspensionId, Guid? executionLogId, CancellationToken cancellationToken)
        {
            var row = inner.Rows.FirstOrDefault(r => r.Id == suspensionId && r.Status == WorkflowSuspensionStatus.Suspended);
            return Task.FromResult<WorkflowParkedRowRead?>(new WorkflowParkedRowRead(DateTimeOffset.UtcNow, row is null
                ? null
                : new WorkflowSuspensionCandidate(row.Id, row.ExecutionLogId, row.WorkflowId, row.BlockId, row.Kind,
                    row.ConfigJson, row.StatePayloadJson, row.SuspendedAtUtc, row.TimeoutAtUtc)));
        }

        public Task<ResumeContext?> TryResolveAsync(Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken) =>
            inner.TryResolveAsync(suspensionId, status, outcomeJson, resolvedAtUtc, cancellationToken);

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(suspensionId, takenAs, cancellationToken);
    }

    private sealed class RecordingResumer(IWorkflowRunResumer inner) : IWorkflowRunResumer
    {
        public ConcurrentQueue<ResumeContext> Contexts { get; } = new();
        public ConcurrentQueue<WorkflowResumeOutcome> Outcomes { get; } = new();

        public async Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken)
        {
            Contexts.Enqueue(ctx);
            var outcome = await inner.ResumeFromSuspensionAsync(ctx, cancellationToken);
            Outcomes.Enqueue(outcome);
            return outcome;
        }
    }

    private sealed record AnyDevice : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "device";
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "Json?");
    }

    private sealed class NoDevices : IExpressionNamespaceResolver
    {
        public string Namespace => "device";
        public ResolveResult Resolve(string[] segments) => ResolveResult.Failed();
    }

    private sealed class DeferringResumer : IWorkflowRunResumer
    {
        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken) =>
            Task.FromResult(WorkflowResumeOutcome.Deferred);
    }

    private sealed class ThrowingBlocks(IWorkflowBlocks inner) : IWorkflowBlocks
    {
        public bool Armed { get; set; }
        public string Generation => Armed ? "armed" : inner.Generation;
        public IWorkflowBlockFactory? Find(string blockTypeId) => Armed ? throw NotAnswering() : inner.Find(blockTypeId);
        public bool ContainsPrivilegedBlock(string? definitionJson) => Armed ? throw NotAnswering() : inner.ContainsPrivilegedBlock(definitionJson);
        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => Armed ? throw NotAnswering() : inner.GetAll();
        private static InvalidOperationException NotAnswering() => new("the catalogue is not answering");
    }

    private sealed class WakeOutcomes : IWorkflowMetrics
    {
        public List<string> Outcomes { get; } = [];
        public void RecordSuspensionWake(string outcome) => Outcomes.Add(outcome);
        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }
}
