using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Debug.Tests.DebugWorld;

namespace ReeveWorkflow.Debug.Tests;

public class DebugCommandAcrossReplicasTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_kill_sent_to_the_wrong_replica_cancels_the_run_where_it_is_and_the_sender_is_told(bool slowTransport)
    {
        await using var world = await DebugWorld.CreateAsync();
        if (slowTransport) world.Bus.Latency = TimeSpan.FromMilliseconds(50);
        var holder = world.AddReplica("holder");
        var sender = world.AddReplica("sender");
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Running);

        var result = await sender.Service.KillAsync(session.Id, reason: "user_initiated_kill");

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        await session.Run!.Walk.WaitAsync(Patience);
        session.Run.NodeTokenCancelled.Should().BeTrue();
        (await session.StatusAsync()).Should().Be(WorkflowDebugSessionStatus.Cancelled);
        sender.Audit.Kinds.Should().ContainSingle().Which.Should().Be(WorkflowAuditEventKinds.DebugKilled);
        holder.Audit.Kinds.Should().BeEmpty("the command is audited once, by the replica that took the request");
    }

    [Fact]
    public async Task A_kill_from_another_replica_that_lands_before_the_run_takes_its_token_still_ends_the_run()
    {
        await using var world = await DebugWorld.CreateAsync();
        var holder = world.AddReplica("holder");
        var sender = world.AddReplica("sender");
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Bound);

        var result = await sender.Service.KillAsync(session.Id);
        session.Run!.Start();

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        await session.Run.Walk.WaitAsync(Patience);
        session.Run.NodeTokenCancelled.Should().BeTrue();
        (await session.StatusAsync()).Should().Be(WorkflowDebugSessionStatus.Cancelled);
    }

    [Fact]
    public async Task A_resume_sent_to_the_wrong_replica_releases_the_pause_where_it_is()
    {
        await using var world = await DebugWorld.CreateAsync();
        var holder = world.AddReplica("holder");
        var sender = world.AddReplica("sender");
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Paused);

        var result = await sender.Service.ResumeAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        await session.Run!.Entered.Task.WaitAsync(Patience);
        sender.Audit.Kinds.Should().ContainSingle().Which.Should().Be(WorkflowAuditEventKinds.DebugResumed);
        holder.Audit.Kinds.Should().NotContain(WorkflowAuditEventKinds.DebugResumed);
        session.Run.Stop();
    }

    [Fact]
    public async Task A_command_nobody_confirms_is_unconfirmed_and_not_audited()
    {
        await using var world = await DebugWorld.CreateAsync();
        var holder = world.AddReplica("holder");
        var sender = world.AddReplica("sender");
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Running);
        holder.Endpoint!.Down = true;

        var result = await sender.Service.KillAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Unconfirmed);
        sender.Audit.Kinds.Should().BeEmpty();
        session.Run!.NodeTokenCancelled.Should().BeFalse();
        session.Run.Stop();
    }

    [Fact]
    public async Task A_replica_that_holds_nothing_does_not_confirm_a_command_it_could_not_apply()
    {
        await using var world = await DebugWorld.CreateAsync();
        var holder = world.AddReplica("holder");
        var bystander = world.AddReplica("bystander");
        var sender = world.AddReplica("sender");
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Running);
        holder.Endpoint!.Down = true;

        var result = await sender.Service.KillAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Unconfirmed);
        bystander.Endpoint!.Published.Should().BeEmpty("a replica that applied nothing says nothing");
        session.Run!.Stop();
    }

    [Fact]
    public async Task A_confirmation_that_is_lost_leaves_the_command_applied_and_the_answer_unconfirmed()
    {
        await using var world = await DebugWorld.CreateAsync();
        var holder = world.AddReplica("holder");
        var sender = world.AddReplica("sender");
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Running);
        world.Bus.DropAcks = true;

        var result = await sender.Service.KillAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Unconfirmed);
        await session.Run!.Walk.WaitAsync(Patience);
        (await session.StatusAsync()).Should().Be(WorkflowDebugSessionStatus.Cancelled, "the status is what settles it");
        sender.Audit.Kinds.Should().BeEmpty("a command nobody confirmed is not on the record as applied");
    }

    [Fact]
    public async Task A_deployment_with_no_cluster_signal_cannot_reach_a_run_in_another_process()
    {
        await using var world = await DebugWorld.CreateAsync();
        var holder = world.AddReplica("holder", onBus: false);
        var sender = world.AddReplica("sender", onBus: false);
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Running);

        var result = await sender.Service.KillAsync(session.Id);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.NotHeld);
        sender.Audit.Kinds.Should().BeEmpty();
        session.Run!.Stop();
    }

    [Fact]
    public async Task A_delete_on_the_wrong_replica_waits_for_the_run_that_is_elsewhere()
    {
        await using var world = await DebugWorld.CreateAsync();
        var holder = world.AddReplica("holder");
        var sender = world.AddReplica("sender");
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Running);

        var result = await sender.Service.DeleteAsync(session.Id).WaitAsync(Patience);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Applied);
        session.Run!.SessionExistedWhenReleased.Should().BeTrue("the run ended before the session went");
        (await sender.Service.GetAsync(session.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_delete_whose_holder_is_down_keeps_the_session()
    {
        await using var world = await DebugWorld.CreateAsync();
        var holder = world.AddReplica("holder");
        var sender = world.AddReplica("sender");
        var session = await world.SessionAsync(holder, WorkflowDebugSessionStatus.Running);
        holder.Endpoint!.Down = true;

        var result = await sender.Service.DeleteAsync(session.Id).WaitAsync(Patience);

        result.Outcome.Should().Be(WorkflowDebugCommandOutcome.Unconfirmed);
        (await sender.Service.GetAsync(session.Id)).Should().NotBeNull();
        session.Run!.Stop();
    }
}
