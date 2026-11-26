using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using Xunit;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public sealed class ResumeStoppedAfterTheTakeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_resume_stopped_mid_walk_whose_row_a_peer_took_and_whose_store_is_closing_says_it_took_the_run()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        var parked = world.Parked();
        var resuming = world.Resumer(stopping.Token).ResumeFromSuspensionAsync(parked, stopping.Token);
        await world.Entered.Task.WaitAsync(Patience);

        world.RunState.TakeOver(world.ParkedStateId!.Value, "replica-2");
        world.RunState.ReadsCancel = true;
        await stopping.CancelAsync();

        (await resuming.WaitAsync(Patience)).Should().Be(WorkflowResumeOutcome.Resumed);
        world.HoldEntries.Should().Be(1);
    }

    [Fact]
    public async Task A_wait_whose_resume_was_stopped_after_its_run_was_taken_is_not_put_back()
    {
        var world = new RunLifecycleWorld();
        var parked = world.Parked();
        var store = new OneParkedWait(parked);
        var waker = new WorkflowSuspensionWaker(store, world.Resumer(), NullLogger.Instance, ParkedTemplates.Compiler());
        using var stopping = new CancellationTokenSource();

        (await waker.TakeOnceAsync(stopping.Token).WaitAsync(Patience)).Should().Be(1);
        await world.Entered.Task.WaitAsync(Patience);
        world.RunState.TakeOver(world.ParkedStateId!.Value, "replica-2");
        world.RunState.ReadsCancel = true;
        await stopping.CancelAsync();
        await waker.WaitForResumesAsync().WaitAsync(Patience);

        store.Released.Should().BeEmpty("the run was taken, so the wait it came back from stays resolved");
    }

    [Fact]
    public async Task A_wait_put_back_after_its_run_was_taken_is_refused_by_the_run_state_row()
    {
        var world = new RunLifecycleWorld();
        using var stopping = new CancellationTokenSource();
        var parked = world.Parked();
        var resumer = world.Resumer(stopping.Token);
        var resuming = resumer.ResumeFromSuspensionAsync(parked, stopping.Token);
        await world.Entered.Task.WaitAsync(Patience);
        world.RunState.TakeOver(world.ParkedStateId!.Value, "replica-2");
        await stopping.CancelAsync();
        await resuming.WaitAsync(Patience);
        world.Release.SetResult();

        (await world.Resumer().ResumeFromSuspensionAsync(parked, CancellationToken.None).WaitAsync(Patience))
            .Should().Be(WorkflowResumeOutcome.HeldElsewhere);
        world.HoldEntries.Should().Be(1, "the row is Running in the peer's name, past the wait");
    }

    private sealed class OneParkedWait(ResumeContext parked) : IWorkflowSuspensionResumeStore
    {
        private readonly Lock gate = new();
        private bool taken;

        public List<(Guid Id, WorkflowSuspensionStatus TakenAs)> Released { get; } = [];

        private WorkflowSuspensionCandidate Candidate => new(
            SuspensionId: parked.SuspensionId,
            ExecutionLogId: parked.ExecutionLogId,
            WorkflowId: parked.WorkflowId,
            BlockId: parked.BlockId,
            Kind: parked.Kind,
            ConfigJson: "{}",
            StatePayloadJson: parked.StatePayloadJson,
            SuspendedAtUtc: parked.SuspendedAtUtc,
            TimeoutAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken)
        {
            lock (gate)
                return Task.FromResult(new WorkflowSuspensionScan(
                    DateTimeOffset.UtcNow, taken ? [] : [Candidate], 0));
        }

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson, DateTimeOffset resolvedAtUtc,
            CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (taken || suspensionId != parked.SuspensionId)
                    return Task.FromResult<ResumeContext?>(null);
                taken = true;
            }

            return Task.FromResult<ResumeContext?>(new ResumeContext
            {
                SuspensionId = parked.SuspensionId,
                ExecutionLogId = parked.ExecutionLogId,
                WorkflowId = parked.WorkflowId,
                BlockId = parked.BlockId,
                Kind = parked.Kind,
                OutcomeJson = outcomeJson,
                StatePayloadJson = parked.StatePayloadJson,
                SuspendedAtUtc = parked.SuspendedAtUtc
            });
        }

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (!taken)
                    return Task.FromResult(false);
                taken = false;
                Released.Add((suspensionId, takenAs));
                return Task.FromResult(true);
            }
        }
    }
}
