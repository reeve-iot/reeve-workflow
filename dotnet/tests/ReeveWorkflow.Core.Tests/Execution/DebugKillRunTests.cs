using System.Collections.Concurrent;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class DebugKillRunTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_kill_during_a_node_ends_the_run_cancelled_and_does_not_try_it_again(bool nodeRethrowsAsFailure)
    {
        var world = new RunLifecycleWorld(retryPolicyJson: """{"maxAttempts":3}""") { HoldWrapsItsCancellation = nodeRethrowsAsFailure };
        var debug = new KillableDebugSessions();
        using var engine = world.Engine(debugSessions: debug);

        Task run;
        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { DebugSessionId = Guid.NewGuid() }))
            run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        debug.Hook.Kill();
        if (nodeRethrowsAsFailure)
            await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<WorkflowNodeExecutionException>();
        else
            await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();

        world.HoldEntries.Should().Be(1, "a killed run is not walked again");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        world.RunState.Completions.Should().ContainSingle().Which.Status.Should().Be(WorkflowRunStateStatus.Cancelled);
        debug.Released.Should().ContainSingle().Which.Label.Should().Be(WorkflowRunStatusLabels.Cancelled);
    }

    [Fact]
    public async Task A_converted_cancellation_under_continue_does_not_dispatch_the_next_node()
    {
        const string graph = """
            {"nodes":[
              {"id":"start","blockTypeId":"WorkflowInput"},
              {"id":"hold","blockTypeId":"Hold","configuration":{"DurationMs":60000},
               "errorPolicy":{"policy":"ContinueWithDefault"}},
              {"id":"tick","blockTypeId":"Tick"}],
             "edges":[
              {"id":"e1","sourceNodeId":"start","targetNodeId":"hold","mappings":{}},
              {"id":"e2","sourceNodeId":"hold","targetNodeId":"tick","mappings":{}}]}
            """;
        var world = new RunLifecycleWorld(definitionJson: graph) { HoldWrapsItsCancellation = true };
        var debug = new KillableDebugSessions();
        using var engine = world.Engine(debugSessions: debug);

        Task run;
        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { DebugSessionId = Guid.NewGuid() }))
            run = engine.ExecuteAsync(world.WorkflowId, null, null);
        await world.Entered.Task.WaitAsync(Patience);
        debug.Hook.Kill();

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
        world.TickEntries.Should().Be(0, "the kill must stop the walk before dispatching another node");
        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task A_run_nobody_killed_is_unaffected_by_the_hook_it_carries()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var debug = new KillableDebugSessions();
        using var engine = world.Engine(debugSessions: debug);

        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation { DebugSessionId = Guid.NewGuid() }))
            await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.Journal.Logs.Values.Should().ContainSingle().Which.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        debug.Released.Should().ContainSingle().Which.Label.Should().Be(WorkflowRunStatusLabels.Succeeded);
    }


    private sealed class KillableDebugSessions : IWorkflowDebugSessions
    {
        public KillableHook Hook { get; } = new();
        public ConcurrentQueue<(Guid ExecutionId, string Label)> Released { get; } = new();

        public Task BindToRunAsync(Guid debugSessionId, Guid executionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task MarkRunningAsync(Guid debugSessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public IWorkflowDebugHook? HookFor(Guid executionId) => Hook;

        public Task ReleaseHookAsync(Guid executionId, string runStatus, CancellationToken cancellationToken = default)
        {
            Released.Enqueue((executionId, runStatus));
            return Task.CompletedTask;
        }
    }

    private sealed class KillableHook : IWorkflowDebugHook
    {
        private readonly CancellationTokenSource kill = new();

        public CancellationToken KillToken => kill.Token;

        public void Kill() => kill.Cancel();

        public Task OnNodeAsync(string nodeId, IReadOnlyDictionary<string, object?> inputs,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> allOutputs, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task OnNodeCompletedAsync(string nodeId, IReadOnlyDictionary<string, object?> outputs,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
