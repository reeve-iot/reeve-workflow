using System.Text.Json;
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

public sealed class DurableSleepParkingTests
{
    private const int Long = 600_000;
    private const int Short = 30;

    [Fact]
    public async Task A_long_root_sleep_parks_the_run_instead_of_waiting_in_it()
    {
        var walk = new Walk(new DurableSleepWorld(), Long);

        var run = () => walk.RunAsync();

        var parked = (await run.Should().ThrowAsync<SuspendedWorkflowException>()).Which;
        parked.BlockId.Should().Be(DurableSleepWorld.SleepNode);
        parked.SuspendResult.Kind.Should().Be(WorkflowSuspensionKind.Sleep);
        var config = JsonSerializer.Deserialize<WorkflowSleepConfig>(parked.SuspendResult.ConfigJson, WorkflowGraphHelpers.JsonOptions)!;
        config.DurationMs.Should().Be(Long);
        config.WakeAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMilliseconds(Long), TimeSpan.FromSeconds(5));
        parked.SuspendResult.TimeoutAtUtc.Should().Be(config.WakeAtUtc, "the waker reads the row's timeout, not its config");
        walk.World.Blocks.AfterInputs.Should().BeEmpty();
    }

    public enum Unmet { NoThreshold, BelowThreshold, CallerCannotPark, DebugHook, NoStateRow, Passthrough, PayloadOverCap }

    [Theory]
    [InlineData(Unmet.NoThreshold)]
    [InlineData(Unmet.BelowThreshold)]
    [InlineData(Unmet.CallerCannotPark)]
    [InlineData(Unmet.DebugHook)]
    [InlineData(Unmet.NoStateRow)]
    [InlineData(Unmet.Passthrough)]
    [InlineData(Unmet.PayloadOverCap)]
    public async Task Each_condition_that_is_not_met_sleeps_in_memory_as_before(Unmet unmet)
    {
        var world = new DurableSleepWorld();
        world.ParkWhenAtLeast = unmet switch
        {
            Unmet.NoThreshold => null,
            Unmet.BelowThreshold => TimeSpan.FromMilliseconds(Short + 1),
            _ => TimeSpan.FromMilliseconds(1)
        };
        if (unmet == Unmet.PayloadOverCap)
            world.MaxSuspendPayloadBytes = 64;

        var walk = new Walk(world, Short, passthrough: unmet == Unmet.Passthrough)
        {
            MayPark = unmet != Unmet.CallerCannotPark,
            DebugHook = unmet == Unmet.DebugHook ? new SilentHook() : null,
            WithStateRow = unmet != Unmet.NoStateRow
        };

        var outputs = await walk.RunAsync();

        outputs[DurableSleepWorld.SleepNode].Should().Contain("Completed", true).And.Contain("DurationMs", Short);
        world.Blocks.AfterInputs.Should().ContainSingle();
    }

    [Fact]
    public async Task The_same_walk_with_every_condition_met_but_a_short_sleep_parks_once_the_threshold_allows_it()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(Short) };

        var run = () => new Walk(world, Short).RunAsync();

        await run.Should().ThrowAsync<SuspendedWorkflowException>();
    }

    [Theory]
    [InlineData(SuspendFailure.BeforeWrite)]
    [InlineData(SuspendFailure.AfterWrite)]
    public async Task A_sleep_whose_park_fails_to_persist_sleeps_in_the_process_and_leaves_no_row_behind(SuspendFailure failure)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1), TraceMode = WorkflowTraceMode.Full };
        world.SuspensionSeam = world.Seam;
        world.Seam.FailNextSuspend = failure;
        var workflowId = world.AddWorkflow(Short);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        var run = world.Journal.Single(workflowId);
        run.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().ContainSingle();
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Suspensions.Rows.Should().NotContain(r => r.Status == WorkflowSuspensionStatus.Suspended,
            "a row the failed write left behind would wake a run that already ran its rest");
        world.Journal.Steps.Count(step => step.NodeId == DurableSleepWorld.SleepNode).Should().Be(1,
            "the sleep that did not park has one step, as any sleep in the process has");
    }

    [Theory]
    [InlineData(SuspendFailure.BeforeWrite)]
    [InlineData(SuspendFailure.AfterWrite)]
    public async Task A_resumed_run_whose_second_park_fails_to_persist_sleeps_in_the_process(SuspendFailure failure)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Short, secondSleepMs: Short);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var run = world.Journal.Single(workflowId);
        var row = world.Suspensions.SingleRow;
        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), world.Suspensions.Now, default))!;
        world.Seam.FailNextSuspend = failure;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        run.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().HaveCount(2);
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Suspensions.Rows.Should().NotContain(r => r.Status == WorkflowSuspensionStatus.Suspended);
    }

    [Theory]
    [InlineData(1, SuspendFailure.BeforeWrite)]
    [InlineData(1, SuspendFailure.AfterWrite)]
    [InlineData(2, SuspendFailure.AfterWrite)]
    [InlineData(2, SuspendFailure.BeforeWrite)]
    public async Task A_sleep_whose_state_row_park_fails_once_is_parked_by_the_retry(int failures, SuspendFailure failure)
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Seam;
        world.RunState.FailParks = failures;
        world.RunState.FailParksAs = failure;
        var workflowId = world.AddWorkflow(600_000);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Suspended);
        world.Suspensions.SingleRow.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sleep_whose_state_row_cannot_be_parked_sleeps_in_the_process_and_withdraws_its_row()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        world.RunState.FailParks = 3;
        var workflowId = world.AddWorkflow(Short);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().ContainSingle();
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Suspensions.Rows.Should().NotContain(r => r.Status == WorkflowSuspensionStatus.Suspended);
    }

    [Fact]
    public async Task A_resumed_run_whose_second_state_row_park_fails_sleeps_in_the_process()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Short, secondSleepMs: Short);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var row = world.Suspensions.SingleRow;
        var context = (await world.Suspensions.TryResolveAsync(row.Id, WorkflowSuspensionStatus.Resumed,
            WorkflowDurableSleep.OutcomeJson(row.ConfigJson, row.SuspendedAtUtc, row.TimeoutAtUtc), world.Suspensions.Now, default))!;
        world.RunState.FailParks = 3;

        (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default)).Should().Be(WorkflowResumeOutcome.Resumed);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().HaveCount(2);
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Completed);
        world.Suspensions.Rows.Should().NotContain(r => r.Status == WorkflowSuspensionStatus.Suspended);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sleep_after_an_output_json_cannot_hold_sleeps_in_the_process(bool thresholdSet)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = thresholdSet ? TimeSpan.FromMilliseconds(1) : null };
        world.SuspensionSeam = world.Suspensions;
        world.CompilationOptions = new CompilationOptions
        {
            TypeRegistry = new WorkflowTypeRegistry(
            [
                new WorkflowTypeRecord("Knot", typeof(Knot))
                {
                    JsonTypeInfo = StrictJson.Info(typeof(Knot)),
                    Claims = new HashSet<WorkflowCapability> { WorkflowCapability.JsonSerializable },
                },
            ]),
        };
        world.Blocks.Extra = id => id == "test.pow"
            ? new LambdaFactory(id, _ => new Dictionary<string, object?> { ["Result"] = Knotted() }, outputs: [LambdaFactory.Port("Result", PortValueType.Of("Knot"))])
            : null;
        var workflowId = world.AddRaw(JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "pow", blockTypeId = "test.pow", position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = DurableSleepWorld.SleepNode, blockTypeId = DurableSleepWorld.SleepType, configuration = new { DurationMs = Short }, position = new { x = 0, y = 0 } },
                new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[]
            {
                new { id = "a", sourceNodeId = "start", targetNodeId = "pow", mappings = new { } },
                new { id = "b", sourceNodeId = "pow", targetNodeId = DurableSleepWorld.SleepNode, mappings = new { } },
                new { id = "c", sourceNodeId = DurableSleepWorld.SleepNode, targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
            }
        }));
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded,
            "an output JSON cannot store (a cycle) is a reason not to park, not a failure of the run");
        world.Suspensions.Rows.Should().BeEmpty();
        world.Blocks.AfterInputs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_sleep_inside_a_container_sleeps_in_memory()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };

        var outputs = await new Walk(world, Short, inContainer: true).RunAsync();

        outputs.Should().ContainKey("box");
        world.Blocks.AfterInputs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_sleep_inside_a_container_that_only_drives_its_sub_canvases_parks_with_its_position()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Suspensions;
        world.Blocks.LoopIterations = 2;
        var workflowId = world.AddRaw(SleepInsideALoop());
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        row.Kind.Should().Be(WorkflowSuspensionKind.Sleep);
        var payload = System.Text.Json.JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(
            row.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
        WorkflowRunCheckpoint.Continuation(payload).Should().ContainSingle()
            .Which.NodeInFlight.Should().Be(DurableSleepWorld.SleepNode);
        world.Blocks.AfterInputs.Should().BeEmpty("the run is parked, not past the container");

        for (var resumes = 0; resumes < 4; resumes++)
        {
            var next = world.Suspensions.Rows.FirstOrDefault(r => r.Status == WorkflowSuspensionStatus.Suspended);
            if (next is null)
                break;
            var context = (await world.Suspensions.TryResolveAsync(next.Id, WorkflowSuspensionStatus.Resumed,
                WorkflowDurableSleep.OutcomeJson(next.ConfigJson, next.SuspendedAtUtc, next.TimeoutAtUtc),
                world.Suspensions.Now, default))!;
            (await world.Resumer(engine).ResumeFromSuspensionAsync(context, default))
                .Should().Be(WorkflowResumeOutcome.Resumed);
        }

        world.Suspensions.Rows.Should().HaveCount(2, "one sleep per iteration, each parked rather than held");
        world.Blocks.AfterInputs.Should().ContainSingle("the run went on past the container, once");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    private static string SleepInsideALoop() => System.Text.Json.JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "box", blockTypeId = DurableSleepWorld.LoopType,
                subCanvases = new Dictionary<string, object>
                {
                    ["Body"] = new
                    {
                        nodes = new object[]
                        {
                            new { id = "in", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                            new
                            {
                                id = DurableSleepWorld.SleepNode, blockTypeId = DurableSleepWorld.SleepType,
                                configuration = new Dictionary<string, object?> { ["DurationMs"] = Short }, position = new { x = 0, y = 0 }
                            }
                        },
                        edges = new object[]
                        {
                            new { id = "in-nap", sourceNodeId = "in", targetNodeId = DurableSleepWorld.SleepNode, mappings = new { } }
                        }
                    }
                }, position = new { x = 0, y = 0 }, configuration = new { }
            },
            new { id = DurableSleepWorld.AfterNode, blockTypeId = DurableSleepWorld.AfterType, position = new { x = 0, y = 0 }, configuration = new { } }
        },
        edges = new object[]
        {
            new { id = "start-box", sourceNodeId = "start", targetNodeId = "box", mappings = new { } },
            new { id = "box-after", sourceNodeId = "box", targetNodeId = DurableSleepWorld.AfterNode, mappings = new { } }
        }
    });

    [Fact]
    public async Task A_run_that_reaches_a_long_sleep_parks_and_its_rows_say_so()
    {
        var world = new DurableSleepWorld();
        world.SuspensionSeam = world.Suspensions;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        var row = world.Suspensions.SingleRow;
        row.Kind.Should().Be(WorkflowSuspensionKind.Sleep);
        row.Status.Should().Be(WorkflowSuspensionStatus.Suspended);
        world.RunState.Rows.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Suspended,
            "a Running row would be reclaimed by recovery and the run would happen twice");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running);
        world.Metrics.SuspensionsStarted.Should().Equal("Sleep");
        world.Blocks.AfterInputs.Should().BeEmpty();
    }

    public enum RunUnmet { ChildRun, StoreWithoutParityQueries, NoRunState }

    [Theory]
    [InlineData(RunUnmet.ChildRun)]
    [InlineData(RunUnmet.StoreWithoutParityQueries)]
    [InlineData(RunUnmet.NoRunState)]
    public async Task A_run_that_cannot_keep_its_promises_about_a_parked_sleep_sleeps_in_memory(RunUnmet unmet)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = unmet == RunUnmet.StoreWithoutParityQueries
            ? new PlainSuspensions(world.Suspensions)
            : world.Suspensions;
        world.WithRunState = unmet != RunUnmet.NoRunState;
        var workflowId = world.AddWorkflow(Short);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null,
            parentExecutionId: unmet == RunUnmet.ChildRun ? Guid.NewGuid() : null);

        world.Suspensions.Rows.Should().BeEmpty();
        world.Blocks.AfterInputs.Should().ContainSingle();
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task The_same_run_parks_once_it_is_a_root_run_over_both_stores()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Suspensions;
        var workflowId = world.AddWorkflow(Short);
        using var engine = world.Engine();

        await engine.ExecuteAsync(workflowId, null, null);

        world.Suspensions.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task A_sleep_whose_wait_is_still_being_written_when_the_caller_stops_parks_anyway()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromMilliseconds(1) };
        world.SuspensionSeam = world.Seam;
        var workflowId = world.AddWorkflow(Long);
        using var engine = world.Engine();
        world.Seam.HoldSuspend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Seam.SuspendAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        var run = Task.Run(() => engine.ExecuteAsync(workflowId, null, null, cancellationToken: stop.Token));
        await world.Seam.SuspendAsked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        world.Seam.HoldSuspend.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        world.Suspensions.Rows.Should().ContainSingle()
            .Which.Status.Should().Be(WorkflowSuspensionStatus.Suspended, "the wait a waker will wake is written whole");
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Suspended);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running, "a parked run has not ended");
    }

    private sealed class Walk(DurableSleepWorld world, int sleepMs, bool passthrough = false, bool inContainer = false)
    {
        public DurableSleepWorld World => world;
        public bool MayPark { get; init; } = true;
        public IWorkflowDebugHook? DebugHook { get; init; }
        public bool WithStateRow { get; init; } = true;

        public async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> RunAsync()
        {
            var workflowId = world.AddWorkflow(sleepMs, passthrough: passthrough, inContainer: inContainer);
            var workflow = (await world.Store.LoadAsync(workflowId, default))!;
            var definition = JsonSerializer.Deserialize<WorkflowDefinition>(
                workflow.CurrentVersion!.DefinitionJson, WorkflowGraphHelpers.JsonOptions)!;

            var settings = world.Settings("replica-1");
            var executor = new WorkflowGraphExecutor(
                world.Blocks, NullLoggerFactory.Instance, world.Metrics, world.Observer, NullHelperResolver.Instance,
                WorkflowGraphExecutorOptions.For(settings),
                transitions: new WorkflowTransitionRecorder(
                    world.RunState, NullLogger.Instance, new WorkflowTransitionOptions { ReplicaId = "replica-1" }),
                sleepRunner: new WorkflowSleepRunner(world.RunState, NullLogger.Instance, new WorkflowSleepOptions
                {
                    CancellationPollInterval = TimeSpan.FromMilliseconds(10), ReplicaId = "replica-1"
                }));

            Guid? stateRow = WithStateRow
                ? await world.RunState.BeginAsync(new WorkflowRunStateBegin
                {
                    WorkflowId = workflowId, WorkflowVersion = 1, VariablesJson = "{}", OwnedByReplicaId = "replica-1"
                }, default)
                : null;

            var executed = new Dictionary<string, IReadOnlyDictionary<string, object?>>
            {
                ["start"] = new Dictionary<string, object?>()
            };

            return await executor.ExecuteGraphAsync(
                executed, workflowId, Guid.NewGuid(), Guid.NewGuid(), new NoServices(), [], [],
                WorkflowTraceMode.None, debugMode: false, iterationIndex: null, callStackJson: null, depth: 0,
                visitedNodeIds: [], isInsideTryCatch: false, inputParametersForRun: null, triggerIdForRun: null,
                triggerEnvelopeForRun: null, CancellationToken.None,
                compiled: TestPrograms.Walk(definition, world.Blocks),
                debugHook: DebugHook, debugCounter: DebugHook is null ? null : new DebugPausedCounter(),
                stateExecutionId: stateRow, mayParkSleep: MayPark);
        }
    }

    private sealed class Knot
    {
        public Knot? Self { get; set; }
    }

    private static Knot Knotted()
    {
        var knot = new Knot();
        knot.Self = knot;
        return knot;
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class SilentHook : IWorkflowDebugHook
    {
        public CancellationToken KillToken => CancellationToken.None;

        public Task OnNodeAsync(string nodeId, IReadOnlyDictionary<string, object?> inputs,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> allOutputs, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task OnNodeCompletedAsync(string nodeId, IReadOnlyDictionary<string, object?> outputs,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
