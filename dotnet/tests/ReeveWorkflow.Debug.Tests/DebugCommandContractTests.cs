using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Debug.Tests.DebugWorld;

namespace ReeveWorkflow.Debug.Tests;

public class DebugCommandContractTests
{
    private const WorkflowDebugSessionStatus Idle = WorkflowDebugSessionStatus.Idle;
    private const WorkflowDebugSessionStatus Bound = WorkflowDebugSessionStatus.Bound;
    private const WorkflowDebugSessionStatus Running = WorkflowDebugSessionStatus.Running;
    private const WorkflowDebugSessionStatus Paused = WorkflowDebugSessionStatus.Paused;
    private const WorkflowDebugSessionStatus Completed = WorkflowDebugSessionStatus.Completed;
    private const WorkflowDebugSessionStatus Cancelled = WorkflowDebugSessionStatus.Cancelled;

    private static Task<WorkflowDebugCommandResult> Send(Replica replica, Guid sessionId, WorkflowDebugCommand command) =>
        command switch
        {
            WorkflowDebugCommand.Step => replica.Service.StepAsync(sessionId),
            WorkflowDebugCommand.StepInto => replica.Service.StepIntoAsync(sessionId),
            WorkflowDebugCommand.StepOver => replica.Service.StepOverAsync(sessionId),
            WorkflowDebugCommand.Resume => replica.Service.ResumeAsync(sessionId),
            _ => replica.Service.KillAsync(sessionId, reason: "test")
        };

    public static TheoryData<WorkflowDebugSessionStatus, WorkflowDebugCommand> Refused()
    {
        var data = new TheoryData<WorkflowDebugSessionStatus, WorkflowDebugCommand>();
        foreach (var command in new[]
                 {
                     WorkflowDebugCommand.Step, WorkflowDebugCommand.StepInto, WorkflowDebugCommand.StepOver,
                     WorkflowDebugCommand.Resume
                 })
            foreach (var status in new[] { Idle, Bound, Running, Completed, Cancelled })
                data.Add(status, command);
        foreach (var status in new[] { Idle, Completed, Cancelled })
            data.Add(status, WorkflowDebugCommand.Kill);
        return data;
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_command_the_state_does_not_admit_is_refused_and_touches_nothing(
        WorkflowDebugSessionStatus status, WorkflowDebugCommand command)
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, status);

        var result = await Send(replica, session.Id, command);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.WrongState);
        result.Status.Should().Be(status, "the answer names the status that refused it");
        replica.Audit.Kinds.Should().BeEmpty("nothing took effect, so nothing is on the record");
        session.Run?.NodeTokenCancelled.Should().NotBe(true, "a refused command leaves the run alone");
        (await session.StatusAsync()).Should().Be(status);
        session.Run?.Stop();
    }

    [Theory]
    [InlineData(WorkflowDebugCommand.Step)]
    [InlineData(WorkflowDebugCommand.Resume)]
    [InlineData(WorkflowDebugCommand.Kill)]
    public async Task A_command_for_a_session_that_does_not_exist_is_not_found(WorkflowDebugCommand command)
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");

        var result = await Send(replica, Guid.NewGuid(), command);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.NotFound);
        replica.Audit.Kinds.Should().BeEmpty();
    }

    [Fact]
    public async Task Kill_while_the_run_is_running_a_long_wait_cancels_it_and_the_session_ends_killed()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, Running);

        var result = await replica.Service.KillAsync(session.Id, reason: "user_initiated_kill");

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        await session.Run!.Walk.WaitAsync(Patience);
        session.Run.NodeTokenCancelled.Should().BeTrue("the wait the run was in is what the kill reached");
        (await session.StatusAsync()).Should().Be(Cancelled);
        replica.Metrics.DebugSessions.Should().ContainSingle().Which.Should().Be(WorkflowDebugSessionOutcomes.Killed);
        replica.Audit.Kinds.Where(k => k == WorkflowAuditEventKinds.DebugKilled).Should().ContainSingle();
    }

    [Fact]
    public async Task Kill_while_the_run_is_paused_cancels_it_and_the_session_ends_killed()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, Paused);

        var result = await replica.Service.KillAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        await session.Run!.Walk.WaitAsync(Patience);
        (await session.StatusAsync()).Should().Be(Cancelled);
        replica.Metrics.DebugSessions.Should().ContainSingle().Which.Should().Be(WorkflowDebugSessionOutcomes.Killed);
        replica.Audit.Kinds.Where(k => k == WorkflowAuditEventKinds.DebugKilled).Should().ContainSingle();
    }

    [Fact]
    public async Task Kill_while_the_session_is_bound_and_the_run_has_not_walked_yet_cancels_the_run_before_its_first_node()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, Bound);
        var hook = replica.Sessions.HookFor(session.ExecutionId!.Value)!;

        var result = await replica.Service.KillAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        hook.KillToken.IsCancellationRequested.Should().BeTrue("the runner links this token when it starts walking");
    }

    [Fact]
    public async Task Kill_cancels_the_real_debug_hook_before_its_next_node_even_without_a_breakpoint()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, Bound);
        var hook = replica.Sessions.HookFor(session.ExecutionId!.Value)!;

        hook.Should().BeOfType<WorkflowDebugHook>("the session service registers its real hook for this execution");
        (await replica.Service.KillAsync(session.Id)).Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);

        var nextNode = () => hook.OnNodeAsync(
            "next-node", new Dictionary<string, object?>(),
            new Dictionary<string, IReadOnlyDictionary<string, object?>>(), CancellationToken.None);

        await nextNode.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Resume_while_paused_releases_the_pause_and_is_audited_once()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, Paused);

        var result = await replica.Service.ResumeAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        await session.Run!.Entered.Task.WaitAsync(Patience);
        (await session.StatusAsync()).Should().Be(Running, "the run walked on past the pause");
        replica.Audit.Kinds.Where(k => k == WorkflowAuditEventKinds.DebugResumed).Should().ContainSingle();
        session.Run.Stop();
    }

    [Theory]
    [InlineData(WorkflowDebugCommand.Step)]
    [InlineData(WorkflowDebugCommand.StepInto)]
    [InlineData(WorkflowDebugCommand.StepOver)]
    public async Task A_step_while_paused_releases_the_pause(WorkflowDebugCommand command)
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, Paused);

        var result = await Send(replica, session.Id, command);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        await session.Run!.Entered.Task.WaitAsync(Patience);
        session.Run.Stop();
    }

    [Fact]
    public async Task A_command_sent_the_moment_the_pause_is_announced_lands()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        WorkflowDebugCommandResult? answered = null;
        replica.Notifier.OnPaused = async id => answered = await replica.Service.ResumeAsync(id);

        var session = await world.SessionAsync(replica, Paused);

        answered.Should().NotBeNull();
        answered!.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        await session.Run!.Entered.Task.WaitAsync(Patience);
        session.Run.Stop();
    }

    [Fact]
    public async Task A_kill_for_a_run_that_has_ended_but_whose_session_still_reads_running_is_not_held_and_not_audited()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a", onBus: false);
        var session = await world.SessionAsync(replica, Running);
        replica.Service.UnregisterExecutionHook(session.ExecutionId!.Value);

        var result = await replica.Service.KillAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.NotHeld);
        replica.Audit.Kinds.Should().BeEmpty();
        session.Run!.Stop();
    }

    [MachineSpeedFact]
    public async Task Deleting_a_running_session_waits_for_the_run_to_end_before_the_session_goes()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, Running);
        session.Run!.LetItEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deleting = replica.Service.DeleteAsync(session.Id);
        await Task.Delay(300);
        deleting.IsCompleted.Should().BeFalse("the run is still going, and the session is what its watcher holds");
        (await replica.Service.GetAsync(session.Id)).Should().NotBeNull();
        session.Run.LetItEnd.SetResult();
        var result = await deleting.WaitAsync(Patience);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        session.Run.SessionExistedWhenReleased.Should().BeTrue("the session outlived the run it was watching");
        (await replica.Service.GetAsync(session.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_session_whose_run_ignores_the_kill_keeps_it_and_says_so()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a", new WorkflowDebugOptions { KillWaitTimeoutSeconds = 1 });
        var session = await world.SessionAsync(replica, Running);
        session.Run!.LetItEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await replica.Service.DeleteAsync(session.Id).WaitAsync(Patience);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.NotEnded);
        (await replica.Service.GetAsync(session.Id)).Should().NotBeNull("deleting it would orphan a run that is still going");
        session.Run.LetItEnd.SetResult();
    }

    [Fact]
    public async Task Deleting_an_unheld_session_without_durable_ending_is_refused_unless_forced()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a", onBus: false);
        var session = await world.SessionAsync(replica, Running);
        replica.Service.UnregisterExecutionHook(session.ExecutionId!.Value);

        var refused = await replica.Service.DeleteAsync(session.Id);
        refused.Outcome.Should().Be(WorkflowDebugCommandOutcome.NotHeld);
        (await replica.Service.GetAsync(session.Id)).Should().NotBeNull();

        var result = await replica.Service.DeleteAsync(session.Id, force: true);
        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        (await replica.Service.GetAsync(session.Id)).Should().BeNull();
        session.Run!.Stop();
    }

    [Fact]
    public async Task Deleting_an_unheld_session_with_a_durable_ending_removes_its_orphaned_row()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a", onBus: false);
        var session = await world.SessionAsync(replica, Running);
        replica.Service.UnregisterExecutionHook(session.ExecutionId!.Value);
        await world.CompleteExecutionLogAsync(session.ExecutionId.Value);

        var result = await replica.Service.DeleteAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        (await replica.Service.GetAsync(session.Id)).Should().BeNull();
        session.Run!.Stop();
    }

    [Fact]
    public async Task Deleting_an_unconfirmed_session_with_a_durable_ending_removes_its_orphaned_row()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a", new WorkflowDebugOptions { CommandAckTimeoutSeconds = 1, KillWaitTimeoutSeconds = 1 });
        var session = await world.SessionAsync(replica, Running);
        replica.Service.UnregisterExecutionHook(session.ExecutionId!.Value);
        await world.CompleteExecutionLogAsync(session.ExecutionId.Value);

        var result = await replica.Service.DeleteAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        (await replica.Service.GetAsync(session.Id)).Should().BeNull();
        session.Run!.Stop();
    }

    [Theory]
    [InlineData(Idle)]
    [InlineData(Completed)]
    [InlineData(Cancelled)]
    public async Task Deleting_a_session_with_no_live_run_just_deletes_it(WorkflowDebugSessionStatus status)
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");
        var session = await world.SessionAsync(replica, status);

        var result = await replica.Service.DeleteAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        (await replica.Service.GetAsync(session.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_a_session_that_does_not_exist_says_so()
    {
        await using var world = await DebugWorld.CreateAsync();
        var replica = world.AddReplica("a");

        (await replica.Service.DeleteAsync(Guid.NewGuid())).Outcome.Should().Be(WorkflowDebugCommandOutcome.NotFound);
    }


}
