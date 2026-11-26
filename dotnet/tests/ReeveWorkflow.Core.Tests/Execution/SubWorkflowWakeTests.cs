using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class SubWorkflowWakeTests
{
    private const string SubRunType = "test.subrun";

    private static readonly Guid Child = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task A_parent_waits_while_the_run_it_started_is_still_going()
    {
        var world = Waiting(out _);
        var workflowId = world.AddRaw(OneSubRun());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        Journal(world, WorkflowExecutionStatus.Running);

        (await Waker(world, engine).WakeOnceAsync(default)).Should().Be(0);

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_parent_moves_again_once_the_run_it_started_has_ended()
    {
        var world = Waiting(out var answers);
        var workflowId = world.AddRaw(OneSubRun());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        Journal(world, WorkflowExecutionStatus.Succeeded, """{"total":7}""", resultOutputsJson: """{"total":7}""");

        (await Waker(world, engine).WakeOnceAsync(default)).Should().Be(1);

        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Resumed);
        answers.Should().ContainSingle();
        answers[0]!.Succeeded.Should().BeTrue();
        answers[0]!.OutputsJson.Should().Be("""{"total":7}""");
        world.Blocks.AfterInputs.Should().ContainSingle("the run went on past the block");
    }

    [Fact]
    public async Task A_parked_child_gives_the_parent_its_unmasked_result()
    {
        var world = Waiting(out var answers);
        var workflowId = world.AddRaw(OneSubRun());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var note = new string('n', 20_000);
        var raw = JsonSerializer.Serialize(new { auth = new { token = "t-raw" }, note });
        Journal(world, WorkflowExecutionStatus.Succeeded,
            """{"auth":{"token":"***"},"note":"...[truncated, 20030 bytes]"}""",
            resultOutputsJson: raw);

        (await Waker(world, engine).WakeOnceAsync(default)).Should().Be(1);

        answers.Should().ContainSingle();
        answers[0]!.OutputsJson.Should().Be(raw, "the parked SubWorkflow node receives the child's complete unmasked result");
        JsonDocument.Parse(answers[0]!.OutputsJson!).RootElement.GetProperty("note").GetString().Should().Be(note);
    }

    [Fact]
    public async Task A_child_without_a_raw_result_abandons_its_parent_and_never_answers_from_its_masked_outputs()
    {
        var world = Waiting(out var answers);
        var workflowId = world.AddRaw(OneSubRun());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        Journal(world, WorkflowExecutionStatus.Succeeded, """{"auth":{"token":"***"}}""");

        await Waker(world, engine).WakeOnceAsync(default);

        answers.Should().BeEmpty("the execution log's outputs are never a substitute for the raw result");
        world.Journal.Single(workflowId).ErrorMessage.Should().StartWith(WorkflowAbandonmentReasons.ResumeValueInvalid);
    }

    [Fact]
    public async Task A_parent_whose_run_failed_is_told_that_and_why()
    {
        var world = Waiting(out var answers);
        var workflowId = world.AddRaw(OneSubRun(continuePast: true));
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        Journal(world, WorkflowExecutionStatus.Failed, error: "the child threw");

        (await Waker(world, engine).WakeOnceAsync(default)).Should().Be(1);

        answers.Should().ContainSingle();
        answers[0]!.Succeeded.Should().BeFalse();
        answers[0]!.Error.Should().Contain("the child threw");
    }

    [Fact]
    public async Task A_waker_whose_kinds_leave_out_the_new_one_says_so_rather_than_stranding_the_run()
    {
        var world = Waiting(out _);
        var workflowId = world.AddRaw(OneSubRun());
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        Journal(world, WorkflowExecutionStatus.Succeeded, """{"total":7}""");
        var logger = new Warnings();
        world.Suspensions.Now = DateTimeOffset.UtcNow.AddMinutes(1);
        var waker = new WorkflowSuspensionWaker(
            world.Suspensions, world.Resumer(engine), logger,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.Zero,
                Kinds = [WorkflowSuspensionKind.WaitTemplate, WorkflowSuspensionKind.Sleep]
            },
            scopes: world.RunScopes);

        (await waker.WakeOnceAsync(default)).Should().Be(0, "the row is not a kind this waker reads");

        logger.Lines.Should().ContainSingle(line => line.Contains("Waker.Kinds") && line.Contains("SubWorkflow"),
            "and the only place that can say so is the waker, because nothing else looks at this row");
    }

    private sealed class Warnings : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning)
                Lines.Add(formatter(state, exception));
        }
    }

    private static WorkflowSuspensionWaker Waker(DurableSleepWorld world, WorkflowEngine engine)
    {
        world.Suspensions.Now = DateTimeOffset.UtcNow.AddMinutes(1);
        return new(world.Suspensions, world.Resumer(engine), NullLogger.Instance, ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions
            {
                SettleFor = TimeSpan.Zero,
                Kinds = [WorkflowSuspensionKind.SubWorkflow]
            },
            scopes: world.RunScopes);
    }

    private static void Journal(
        DurableSleepWorld world, WorkflowExecutionStatus status, string? outputsJson = null, string? error = null,
        string? resultOutputsJson = null)
    {
        var log = new WorkflowExecutionLog(
            Child, null, Guid.NewGuid(), Guid.NewGuid(), null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        if (status != WorkflowExecutionStatus.Running)
            log.Complete(status, DateTimeOffset.UtcNow, error, outputsJson);
        if (resultOutputsJson is not null)
            log.RecordResultForParent(resultOutputsJson);
        world.Journal.Runs[Child] = log;
    }

    private static DurableSleepWorld Waiting(out List<WorkflowSubRunAnswer?> answers)
    {
        var seen = new List<WorkflowSubRunAnswer?>();
        answers = seen;
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.Extra = id => id == SubRunType
            ? new LambdaFactory(id, _ =>
            {
                if (WorkflowSubRunResumeScope.TakeFor("subrun") is not { } answer)
                {
                    throw new WorkflowSuspendException(new SuspendResult(
                        WorkflowSuspensionKind.SubWorkflow,
                        JsonSerializer.Serialize(new { ChildExecutionLogId = Child }),
                        DateTimeOffset.MaxValue));
                }

                seen.Add(answer);
                if (!answer.Succeeded)
                    throw new InvalidOperationException(answer.Error ?? "it did not succeed");
                return new Dictionary<string, object?>();
            })
            : null;
        return world;
    }

    private static string OneSubRun(bool continuePast = false) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            continuePast
                ? new
                {
                    id = "subrun", blockTypeId = SubRunType,
                    errorPolicy = new { policy = nameof(NodeErrorPolicy.ContinueWithDefault) }, position = new { x = 0, y = 0 }, configuration = new { }
                }
                : (object)new { id = "subrun", blockTypeId = SubRunType, position = new { x = 0, y = 0 }, configuration = new { } },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "start-subrun", sourceNodeId = "start", targetNodeId = "subrun", mappings = new { } },
            new { id = "subrun-after", sourceNodeId = "subrun", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        }
    });
}
