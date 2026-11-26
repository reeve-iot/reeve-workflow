using System.Diagnostics;
using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DurableSleepWalkOutsidePassTests
{
    [Fact]
    public async Task A_resumed_segment_that_sleeps_in_the_process_does_not_hold_the_waker_pass()
    {
        var world = World();
        var workflowId = world.AddWorkflow(1_500, WorkflowMode.Single, secondSleepMs: 6_000);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromMinutes(1));
        world.Seam.FailNextSuspend = SuspendFailure.BeforeWrite;
        var waker = world.Waker(world.Resumer(engine));

        var took = Stopwatch.StartNew();
        (await waker.TakeOnceAsync(default)).Should().Be(1);
        took.Stop();

        took.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "the second sleep is the walk's, not the pass's");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Running);
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Blocks.AfterInputs.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_waker_whose_walks_fill_its_cap_leaves_the_next_row_parked()
    {
        var world = World();
        var first = world.AddWorkflow(1_500, WorkflowMode.Single, secondSleepMs: 1_500);
        var second = world.AddWorkflow(1_500, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(first, null, null);
        await engine.ExecuteAsync(second, null, null);
        world.PassTime(TimeSpan.FromMinutes(1));
        world.Seam.FailNextSuspend = SuspendFailure.BeforeWrite;
        var waker = new WorkflowSuspensionWaker(world.Suspensions, world.Resumer(engine),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            ParkedTemplates.Compiler(),
            new WorkflowSuspensionWakeOptions { SettleFor = TimeSpan.Zero, MaxConcurrentResumes = 1 });

        (await waker.TakeOnceAsync(default)).Should().Be(1);
        (await waker.TakeOnceAsync(default)).Should().Be(0, "the one walk this waker may run is still sleeping");
        world.Suspensions.Rows.Count(r => r.Status == WorkflowSuspensionStatus.Suspended).Should().Be(1);

        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));
        (await waker.TakeOnceAsync(default)).Should().Be(1);
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));
        world.Journal.Single(first).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Journal.Single(second).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_walk_the_waker_began_is_cancelled_by_the_stop_it_was_taken_under()
    {
        var (world, waker, probe, workflowId) = await ProbedWalkAsync(out var stopping);

        probe.Token.IsCancellationRequested.Should().BeFalse("the walk is under way, and nothing has stopped yet");
        await stopping.CancelAsync();
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));

        probe.Token.IsCancellationRequested.Should().BeTrue("a stopping host stops the walk, as it stops a run it started");
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Cancelled, "saved before the host goes");
        world.RunState.Rows.Values.Single().Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        stopping.Dispose();
    }

    [Fact]
    public async Task A_walk_a_host_with_its_own_sweep_stops_is_left_running_for_that_sweep()
    {
        var (world, waker, probe, workflowId) = await ProbedWalkAsync(out var stopping, handsOver: true);

        await stopping.CancelAsync();
        await waker.WaitForResumesAsync().WaitAsync(TimeSpan.FromSeconds(30));

        probe.Token.IsCancellationRequested.Should().BeTrue("the walk stops either way; what changes is what it leaves");
        var row = world.RunState.Rows.Values.Single();
        row.Status.Should().Be(WorkflowRunStateStatus.Running, "a handed-over row waits for a sweep to carry it on");
        row.OwnedByReplicaId.Should().BeNull("nobody owns it now, which is what lets a sweep take it");
        var entry = world.Journal.Single(workflowId);
        entry.Status.Should().Be(WorkflowExecutionStatus.Running, "the entry is not over: the sweep that takes the row carries the run on under it");
        entry.ErrorMessage.Should().StartWith(WorkflowRunHandover.StoppedReason,
            "a history must tell a deploy from an operator's cancel");
        stopping.Dispose();
    }

    private sealed class TokenProbe : ReeveWorkflow.Abstractions.IWorkflowBlockFactory, ReeveWorkflow.Abstractions.IWorkflowBlockPlugin
    {
        public const string Type = "test.probe";
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public string BlockTypeId => Type;
        public string DisplayName => Type;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<ReeveWorkflow.Abstractions.BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<ReeveWorkflow.Abstractions.BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<string> SubCanvasNames => [];
        public bool IsIdempotent => true;
        public ReeveWorkflow.Abstractions.IWorkflowBlockPlugin Create() => this;

        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, ReeveWorkflow.Abstractions.WorkflowBlockContext context,
            CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new Dictionary<string, object?>();
        }
    }

    private static Task<(DurableSleepWorld World, WorkflowSuspensionWaker Waker, TokenProbe Probe, Guid WorkflowId)> ProbedWalkAsync(
        out CancellationTokenSource stopping, bool handsOver = false)
    {
        stopping = new CancellationTokenSource();
        return WalkAsync(stopping.Token, handsOver);
    }

    private static async Task<(DurableSleepWorld World, WorkflowSuspensionWaker Waker, TokenProbe Probe, Guid WorkflowId)> WalkAsync(
        CancellationToken stopping, bool handsOver = false)
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = null };
        world.SuspensionSeam = world.Suspensions;
        world.HandOverRunsOnStop = handsOver;
        world.HostStopping = handsOver ? stopping : default;
        var probe = new TokenProbe();
        world.Blocks.Extra = id => id switch
        {
            "test.wait" => new LambdaFactory(id, _ => throw new WorkflowSuspendException(new SuspendResult(
                WorkflowSuspensionKind.WaitForTrigger, "{\"TriggerIds\":[]}", DateTimeOffset.UtcNow.AddMinutes(1)))),
            TokenProbe.Type => probe,
            _ => null
        };
        var workflowId = world.AddRaw(System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "door", blockTypeId = "test.wait", position = new { x = 0, y = 0 }, configuration = new { } },
                new { id = "probe", blockTypeId = TokenProbe.Type, position = new { x = 0, y = 0 }, configuration = new { } }
            },
            edges = new object[]
            {
                new { id = "a", sourceNodeId = "start", targetNodeId = "door", mappings = new { } },
                new { id = "b", sourceNodeId = "door", targetNodeId = "probe", mappings = new { } }
            }
        }));
        var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        world.PassTime(TimeSpan.FromMinutes(3));
        var waker = world.Waker(world.Resumer(engine));
        (await waker.TakeOnceAsync(stopping)).Should().Be(1, "the pass answers for its take, not for the walk");
        await probe.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (world, waker, probe, workflowId);
    }

    private static DurableSleepWorld World()
    {
        var world = new DurableSleepWorld { ParkWhenAtLeast = TimeSpan.FromSeconds(1) };
        world.SuspensionSeam = world.Seam;
        return world;
    }
}
