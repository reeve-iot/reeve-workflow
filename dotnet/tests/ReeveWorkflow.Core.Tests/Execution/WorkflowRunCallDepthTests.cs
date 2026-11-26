using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class WorkflowRunCallDepthTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_run_asked_for_past_the_call_depth_limit_is_refused_before_anything_of_it_exists()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        using var engine = world.Engine();

        var act = async () =>
        {
            using (WorkflowRunPinScope.Push(new WorkflowRunPin { CallDepth = WorkflowRunAdmission.MaxWorkflowCallDepth + 1 }))
                await engine.ExecuteAsync(world.WorkflowId, null, null);
        };

        var refused = await act.Should().ThrowAsync<WorkflowCallDepthExceededException>();
        refused.Which.WorkflowId.Should().Be(world.WorkflowId);
        refused.Which.CallDepth.Should().Be(WorkflowRunAdmission.MaxWorkflowCallDepth + 1);
        world.Journal.Logs.Should().BeEmpty();
        world.RunState.LastBegun.Should().BeNull();
        world.HoldEntries.Should().Be(0);
    }

    [Fact]
    public async Task A_run_at_the_limit_runs_and_walks_at_the_depth_it_was_handed()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        using var engine = world.Engine();

        using (WorkflowRunPinScope.Push(new WorkflowRunPin { CallDepth = WorkflowRunAdmission.MaxWorkflowCallDepth }))
            await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        world.HoldCallDepths.Should().Equal([WorkflowRunAdmission.MaxWorkflowCallDepth],
            "a sub-workflow started from its blocks is handed one more than this");
        WorkflowCallDepthScope.Current.Should().Be(0, "the depth is the run's, and gone once it has ended");
    }

    [Fact]
    public async Task A_run_that_parks_writes_its_call_depth_into_the_wait()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        using var engine = world.Engine(suspensions: suspensions);

        using (WorkflowRunPinScope.Push(new WorkflowRunPin { CallDepth = 4 }))
            await engine.ExecuteAsync(world.WorkflowId, null, null).WaitAsync(Patience);

        PayloadOf(suspensions.Suspended.Should().ContainSingle().Subject).CallDepth.Should().Be(4);
    }

    [Fact]
    public async Task A_resumed_segment_walks_at_the_call_depth_its_wait_was_parked_at_and_parks_again_at_it()
    {
        var world = new RunLifecycleWorld { ParkAtHold = true };
        var suspensions = new RunLifecycleWorld.RecordingSuspensions();
        var parked = world.Parked(callDepth: 7);

        (await world.Resumer(suspensions: suspensions).ResumeFromSuspensionAsync(parked, CancellationToken.None)
            .WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);

        world.HoldCallDepths.Should().Equal([7], "a resumed segment calls further down from where it was, not from 0");
        PayloadOf(suspensions.Suspended.Should().ContainSingle().Subject).CallDepth.Should().Be(7);
        WorkflowCallDepthScope.Current.Should().Be(0);
    }

    [Fact]
    public async Task A_wait_parked_before_5_2_0_resumes_at_depth_zero()
    {
        var world = new RunLifecycleWorld();
        world.Release.SetResult();
        var parked = world.Parked(callDepth: 3);
        var oldPayload = System.Text.RegularExpressions.Regex.Replace(
            parked.StatePayloadJson, ",?\"callDepth\":3", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        oldPayload.Should().NotContainEquivalentOf("callDepth");
        parked = new ResumeContext
        {
            SuspensionId = parked.SuspensionId, ExecutionLogId = parked.ExecutionLogId, WorkflowId = parked.WorkflowId,
            BlockId = parked.BlockId, Kind = parked.Kind, OutcomeJson = parked.OutcomeJson,
            StatePayloadJson = oldPayload, SuspendedAtUtc = parked.SuspendedAtUtc
        };

        await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience);

        world.HoldCallDepths.Should().Equal([0]);
    }

    private static WorkflowSuspensionStatePayload PayloadOf(SuspendRequest request) =>
        JsonSerializer.Deserialize<WorkflowSuspensionStatePayload>(request.StatePayloadJson, WorkflowGraphHelpers.JsonOptions)!;
}
