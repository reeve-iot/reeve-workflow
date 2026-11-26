using System.Collections.Concurrent;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class AdmissionDecidedSignalTests
{
    private const int Short = 1;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(WorkflowMode.Single)]
    [InlineData(WorkflowMode.Restart)]
    [InlineData(WorkflowMode.Queued)]
    [InlineData(WorkflowMode.Parallel)]
    public async Task An_admitted_run_hears_started_once_and_before_its_first_node(WorkflowMode mode)
    {
        var world = World();
        var workflowId = world.AddWorkflow(Short, mode, maxQueuedRuns: 1);
        using var engine = world.Engine();
        var heard = new Heard(world);

        await heard.RunAsync(engine, workflowId);

        heard.Decisions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            WorkflowId = workflowId, Kind = WorkflowAdmissionDecisionKind.Admitted, Outcome = WorkflowAdmissionOutcomes.Started
        });
        world.Observer.Timeline.First().Should().Be("decided",
            "a caller that starts the next fire on this signal must not race the run it was told about");
        world.Observer.Timeline.Should().Contain(mark => mark.StartsWith("started:"));
    }

    [Fact]
    public async Task Two_restart_fires_chained_on_the_signal_are_admitted_in_the_order_they_were_called()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Short, WorkflowMode.Restart, holdFirst: true);
        using var engine = world.Engine();
        var on = new Heard(world);
        var off = new Heard(world);

        var onRun = on.RunAsync(engine, workflowId, Fire(out var onTrigger));
        await on.Decided.Task.WaitAsync(Patience);
        var offRun = off.RunAsync(engine, workflowId, Fire(out var offTrigger));
        await off.Decided.Task.WaitAsync(Patience);
        world.Blocks.Release.TrySetResult();
        await Settle(onRun, offRun);

        on.Decisions.Should().ContainSingle().Which.Outcome.Should().Be(WorkflowAdmissionOutcomes.Started);
        off.Decisions.Should().ContainSingle().Which.Outcome.Should().Be(WorkflowAdmissionOutcomes.Restarted,
            "the second fire found the first one already holding the run");
        world.Journal.Runs.Values.Single(r => r.TriggerId == offTrigger).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        world.Journal.Runs.Values.Single(r => r.TriggerId == onTrigger).Status.Should().NotBe(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_queued_run_hears_queued_when_its_place_is_fixed_not_when_it_starts()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Short, WorkflowMode.Queued, maxConcurrentRuns: 1, holdFirst: true, maxQueuedRuns: 1);
        using var engine = world.Engine();
        var first = new Heard(world);
        var second = new Heard(world);

        var firstRun = first.RunAsync(engine, workflowId);
        await world.Blocks.HoldReached.Task.WaitAsync(Patience);
        var secondRun = second.RunAsync(engine, workflowId);
        var decision = await second.Decided.Task.WaitAsync(Patience);
        var startsWhileQueued = world.Observer.Timeline.Count(mark => mark.StartsWith("started:"));
        world.Blocks.Release.TrySetResult();
        await Settle(firstRun, secondRun);

        decision.Kind.Should().Be(WorkflowAdmissionDecisionKind.Queued);
        decision.Outcome.Should().Be(WorkflowAdmissionOutcomes.Queued);
        startsWhileQueued.Should().Be(1, "only the first run's hold had started when the second heard");
        second.Decisions.Should().ContainSingle("being promoted off the queue is not a second decision");
        world.Journal.Runs.Should().HaveCount(2);
    }

    public enum Refusal { SingleBusy, ParallelBudget, QueueDisabled, QueueDisabledErrors, SafeMode }

    [Theory]
    [InlineData(Refusal.SingleBusy, WorkflowAdmissionOutcomes.Skipped, "single_mode_already_running")]
    [InlineData(Refusal.ParallelBudget, WorkflowAdmissionOutcomes.Dropped, null)]
    [InlineData(Refusal.QueueDisabled, WorkflowAdmissionOutcomes.Dropped, "queue_disabled")]
    [InlineData(Refusal.QueueDisabledErrors, WorkflowAdmissionOutcomes.Errored, null)]
    [InlineData(Refusal.SafeMode, WorkflowAdmissionOutcomes.Skipped, "execution_suspended")]
    public async Task A_refused_run_hears_refused_once_with_the_coordinators_outcome(
        Refusal refusal, string outcome, string? reason)
    {
        var world = World();
        var (mode, overflow) = refusal switch
        {
            Refusal.SingleBusy => (WorkflowMode.Single, WorkflowOverflowBehavior.Drop),
            Refusal.ParallelBudget => (WorkflowMode.Parallel, WorkflowOverflowBehavior.Drop),
            Refusal.QueueDisabled => (WorkflowMode.Queued, WorkflowOverflowBehavior.Drop),
            Refusal.QueueDisabledErrors => (WorkflowMode.Queued, WorkflowOverflowBehavior.Error),
            _ => (WorkflowMode.Single, WorkflowOverflowBehavior.Drop)
        };
        var workflowId = world.AddWorkflow(Short, mode, maxConcurrentRuns: 1, holdFirst: true, overflow: overflow);
        using var engine = world.Engine();
        var holder = new Heard(world);
        var refused = new Heard(world);

        Task? holderRun = null;
        if (refusal == Refusal.SafeMode)
        {
            world.ExecutionSuspended = true;
        }
        else
        {
            holderRun = holder.RunAsync(engine, workflowId);
            await world.Blocks.HoldReached.Task.WaitAsync(Patience);
        }

        await refused.RunAsync(engine, workflowId);
        world.Blocks.Release.TrySetResult();
        if (holderRun is not null)
            await Settle(holderRun);

        var decision = refused.Decisions.Should().ContainSingle().Subject;
        decision.Kind.Should().Be(WorkflowAdmissionDecisionKind.Refused);
        decision.Outcome.Should().Be(outcome);
        if (reason is not null)
            decision.Reason.Should().Be(reason);
        if (refusal == Refusal.QueueDisabledErrors)
            decision.Exception.Should().BeOfType<WorkflowConcurrencyException>();
    }

    [Fact]
    public async Task Single_parked_on_a_sleep_is_heard_as_refused_with_that_reason()
    {
        var world = World();
        world.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
        world.SuspensionSeam = world.Suspensions;
        var workflowId = world.AddWorkflow(600_000, WorkflowMode.Single);
        using var engine = world.Engine();
        await engine.ExecuteAsync(workflowId, null, null);
        var refused = new Heard(world);

        await refused.RunAsync(engine, workflowId);

        refused.Decisions.Should().ContainSingle().Which.Reason.Should().Be("single_mode_parked_sleep");
    }

    [Fact]
    public async Task A_run_that_fails_before_admission_still_hears_failed_once()
    {
        var world = World();
        using var engine = world.Engine();
        var heard = new Heard(world);

        await heard.RunAsync(engine, Guid.NewGuid());

        var decision = heard.Decisions.Should().ContainSingle().Subject;
        decision.Kind.Should().Be(WorkflowAdmissionDecisionKind.Failed);
        decision.Outcome.Should().BeNull();
        decision.Exception.Should().BeOfType<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task A_call_cancelled_before_admission_still_hears_failed_once()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Short, WorkflowMode.Single);
        using var engine = world.Engine();
        var heard = new Heard(world);

        await heard.RunAsync(engine, workflowId, cancellationToken: new CancellationToken(canceled: true));

        var decision = heard.Decisions.Should().ContainSingle().Subject;
        decision.Kind.Should().Be(WorkflowAdmissionDecisionKind.Failed);
        decision.Exception.Should().BeAssignableTo<OperationCanceledException>();
        world.Journal.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_callback_that_throws_does_not_stop_the_run()
    {
        var world = World();
        var workflowId = world.AddWorkflow(Short, WorkflowMode.Single);
        using var engine = world.Engine();
        var calls = 0;

        using (WorkflowRunObservationScope.Push(new WorkflowRunObservation
        {
            OnAdmissionDecided = _ => { calls++; throw new InvalidOperationException("the host's bug"); }
        }))
        {
            await engine.ExecuteAsync(workflowId, null, null);
        }

        calls.Should().Be(1);
        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_child_run_started_inside_the_run_does_not_hear_the_parents_callback()
    {
        var world = World();
        var childId = world.AddWorkflow(Short, WorkflowMode.Parallel, maxConcurrentRuns: 5);
        var parentId = world.AddParentWorkflow();
        using var engine = world.Engine();
        world.Blocks.RunChild = () => engine.ExecuteAsync(childId, null, null);
        var heard = new Heard(world);

        await heard.RunAsync(engine, parentId);

        world.Journal.Runs.Values.Should().Contain(r => r.WorkflowId == childId, "the child did run");
        heard.Decisions.Should().ContainSingle().Which.WorkflowId.Should().Be(parentId);
    }

    [Fact]
    public async Task A_parked_sleep_resumed_later_does_not_hear_it_again()
    {
        var world = World();
        world.ParkWhenAtLeast = TimeSpan.FromMinutes(1);
        world.SuspensionSeam = world.Suspensions;
        var workflowId = world.AddWorkflow(600_000, WorkflowMode.Single);
        using var engine = world.Engine();
        var heard = new Heard(world);

        await heard.RunAsync(engine, workflowId);
        world.PassTime(TimeSpan.FromHours(1));
        (await world.Waker(world.Resumer(engine)).WakeAndWalkAsync()).Should().Be(1);

        world.Journal.Single(workflowId).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        heard.Decisions.Should().ContainSingle();
    }

    private static DurableSleepWorld World() => new() { ParkWhenAtLeast = null };

    private static WorkflowFire Fire(out Guid triggerId)
    {
        triggerId = Guid.NewGuid();
        return new WorkflowFire { TriggerId = triggerId };
    }

    private static async Task Settle(params Task[] runs)
    {
        foreach (var run in runs)
        {
            try { await run.WaitAsync(Patience); }
            catch (Exception ex) when (ex is not TimeoutException) { }
        }
    }

    private sealed class Heard(DurableSleepWorld world)
    {
        public ConcurrentQueue<WorkflowAdmissionDecision> Decisions { get; } = new();
        public TaskCompletionSource<WorkflowAdmissionDecision> Decided { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(
            WorkflowEngine engine, Guid workflowId, WorkflowFire? fire = null, CancellationToken cancellationToken = default) =>
            Task.Run(async () =>
            {
                using var scope = WorkflowRunObservationScope.Push(new WorkflowRunObservation
                {
                    OnAdmissionDecided = decision =>
                    {
                        world.Observer.Timeline.Enqueue("decided");
                        Decisions.Enqueue(decision);
                        Decided.TrySetResult(decision);
                    }
                });
                try
                {
                    await engine.ExecuteAsync(workflowId, fire, null, cancellationToken: cancellationToken);
                }
                catch (Exception ex) when (ex is WorkflowNotFoundException or OperationCanceledException
                    or WorkflowConcurrencyException)
                {
                }
            });
    }
}
