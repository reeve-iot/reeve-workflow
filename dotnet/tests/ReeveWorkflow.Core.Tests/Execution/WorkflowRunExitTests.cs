using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunExitTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_run_that_parks_is_not_traced_as_failed()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WorkflowActivitySource.Default.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "workflow.execute"
                    && activity.GetTagItem("workflow.id") as string == world.WorkflowId.ToString())
                    spans.Enqueue(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        using var engine = world.Engine(suspensions: new RunLifecycleWorld.RecordingSuspensions());

        await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended, "setup: parked");
        var span = spans.Should().ContainSingle().Subject;
        span.GetTagItem("workflow.result").Should().Be(WorkflowRunStatusLabels.Succeeded);
        span.Status.Should().NotBe(ActivityStatusCode.Error, "a run parked on a wait has not failed");
    }

    [Fact]
    public async Task A_debugged_run_that_parks_releases_its_session_as_succeeded()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var debug = new RecordingDebugSessions();
        using var engine = world.Engine(suspensions: new RunLifecycleWorld.RecordingSuspensions(), debugSessions: debug);

        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { DebugSessionId = Guid.NewGuid() }))
            await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.RunState.Row(world.RunState.LastBegun!.Value).Status.Should().Be(WorkflowRunStateStatus.Suspended, "setup: parked");
        debug.Released.Should().ContainSingle().Which.Label.Should().Be(WorkflowRunStatusLabels.Succeeded);
    }

    [Fact]
    public async Task A_debugged_run_whose_block_throws_releases_its_session_with_the_failed_label()
    {
        var world = new RunLifecycleWorld { HoldThrows = () => new InvalidOperationException("the block failed") };
        var debug = new RecordingDebugSessions();
        using var engine = world.Engine(debugSessions: debug);

        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { DebugSessionId = Guid.NewGuid() }))
        {
            await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
                .Should().ThrowAsync<WorkflowNodeExecutionException>();
        }

        debug.Released.Should().ContainSingle().Which.Label.Should().Be(WorkflowRunStatusLabels.Failed);
    }

    [Fact]
    public async Task A_run_whose_debug_session_was_bound_but_could_not_be_marked_running_releases_it()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var debug = new RecordingDebugSessions { MarkRunningThrows = true };
        using var engine = world.Engine(debugSessions: debug);

        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { DebugSessionId = Guid.NewGuid() }))
        {
            await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
                .Should().ThrowAsync<InvalidOperationException>();
        }

        var bound = debug.Bound.Should().ContainSingle("setup: the session was bound to the run").Subject;
        debug.Released.Should().ContainSingle().Which.Should().Be((bound, WorkflowRunStatusLabels.Failed));
    }

    [Fact]
    public async Task A_workflow_forgotten_after_admission_took_its_lease_gives_its_cluster_lock_back()
    {
        var world = new RunLifecycleWorld();
        var locks = new CountingLocks();
        WorkflowEngine? engine = null;
        using var built = world.Engine(locks: locks, metrics: new ForgetsOnStart(() => engine!.ForgetWorkflow(world.WorkflowId)));
        engine = built;

        await engine.Awaiting(e => e.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience))
            .Should().ThrowAsync<ObjectDisposedException>();

        locks.Taken.Should().Be(1, "setup: the run took the lock");
        locks.Released.Should().Be(1);
    }

    private sealed class ForgetsOnStart(Action forget) : IWorkflowMetrics
    {
        public void RecordExecutionStarted() => forget();
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugSession(string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class RecordingDebugSessions : IWorkflowDebugSessions
    {
        public bool MarkRunningThrows { get; init; }
        public ConcurrentQueue<Guid> Bound { get; } = new();
        public ConcurrentQueue<(Guid ExecutionId, string Label)> Released { get; } = new();

        public Task BindToRunAsync(Guid debugSessionId, Guid executionId, CancellationToken cancellationToken)
        {
            Bound.Enqueue(executionId);
            return Task.CompletedTask;
        }

        public Task MarkRunningAsync(Guid debugSessionId, CancellationToken cancellationToken) =>
            MarkRunningThrows
                ? throw new InvalidOperationException("The debug session store blipped.")
                : Task.CompletedTask;

        public IWorkflowDebugHook? HookFor(Guid executionId) => new NoHook();

        public Task ReleaseHookAsync(Guid executionId, string runStatus, CancellationToken cancellationToken = default)
        {
            Released.Enqueue((executionId, runStatus));
            return Task.CompletedTask;
        }

        private sealed class NoHook : IWorkflowDebugHook
        {
            public CancellationToken KillToken => CancellationToken.None;

            public Task OnNodeAsync(string nodeId, IReadOnlyDictionary<string, object?> inputs,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> allOutputs, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task OnNodeCompletedAsync(string nodeId, IReadOnlyDictionary<string, object?> outputs,
                CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }

    private sealed class CountingLocks : IWorkflowRunLockProvider
    {
        public int Taken;
        public int Released;

        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Taken);
            return Task.FromResult<IWorkflowRunLock?>(new Held(this));
        }

        private sealed class Held(CountingLocks owner) : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Released);
                return ValueTask.CompletedTask;
            }
        }
    }
}
