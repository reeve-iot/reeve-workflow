using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowSuspensionWakerConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task B5_A_long_resumed_segment_does_not_delay_the_other_timers_that_are_due()
    {
        var slow = Parked(timeoutAtUtc: Now.AddSeconds(-2));
        var quick = Parked(timeoutAtUtc: Now.AddSeconds(-1));
        var release = new TaskCompletionSource();
        var store = new RecordingResumeStore { Candidates = [slow, quick] };
        var resumer = new BlockingResumer(slow.ExecutionLogId, release.Task);
        var waker = new WorkflowSuspensionWaker(store, resumer, NullLogger.Instance, ParkedTemplates.Compiler());

        var pass = waker.WakeOnceAsync(CancellationToken.None);

        var quickResumed = await Task.WhenAny(resumer.Entered(quick.ExecutionLogId), Task.Delay(TimeSpan.FromSeconds(1)));
        quickResumed.Should().BeSameAs(resumer.Entered(quick.ExecutionLogId),
            "the pass awaits each resumed segment in turn, so the second timer waits for the first run's blocks");

        release.SetResult();
        (await pass).Should().Be(2);
    }

    [Fact]
    public async Task B4_A_run_taken_by_a_pass_that_is_stopped_before_re_entering_it_goes_back_rather_than_being_lost()
    {
        var candidate = Parked(timeoutAtUtc: Now.AddSeconds(-1));
        var store = new RecordingResumeStore { Candidates = [candidate] };
        var resumer = new StoppedBeforeTakingResumer();
        var waker = new WorkflowSuspensionWaker(store, resumer, NullLogger.Instance, ParkedTemplates.Compiler());

        var pass = async () => await waker.WakeOnceAsync(CancellationToken.None);

        await pass.Should().NotThrowAsync("a stop is not an error of this row");
        store.Taken.Should().ContainSingle();
        store.Released.Should().ContainSingle().Which.Id.Should().Be(candidate.SuspensionId,
            "the row was resolved and the run was never re-entered, so nothing else will ever offer it again");
    }

    [Fact]
    public async Task B5_A_pass_with_every_worker_busy_takes_no_more_waits_and_leaves_them_parked()
    {
        var slow = Parked(Now.AddSeconds(-2));
        var due = Parked(Now.AddSeconds(-1));
        var release = new TaskCompletionSource();
        var store = new RecordingResumeStore { Candidates = [slow, due] };
        var resumer = new BlockingUntilStopped(slow.ExecutionLogId, release.Task);
        var waker = new WorkflowSuspensionWaker(
            store, resumer, NullLogger.Instance, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions { MaxConcurrentResumes = 1 });

        (await waker.TakeOnceAsync(CancellationToken.None).WaitAsync(Soon)).Should().Be(1);
        await resumer.SlowEntered.WaitAsync(Soon);

        store.Taken.Select(t => t.Id).Should().Equal([slow.SuspensionId], "no worker was free for the second row");
        release.SetResult();
        await waker.WaitForResumesAsync().WaitAsync(Soon);
    }

    [Fact]
    public async Task B5_A_pass_returns_once_it_has_taken_while_the_runs_it_took_go_on()
    {
        var slow = Parked(Now.AddSeconds(-2));
        var release = new TaskCompletionSource();
        var store = new RecordingResumeStore { Candidates = [slow] };
        var resumer = new BlockingUntilStopped(slow.ExecutionLogId, release.Task);
        var waker = new WorkflowSuspensionWaker(store, resumer, NullLogger.Instance, ParkedTemplates.Compiler());

        (await waker.TakeOnceAsync(CancellationToken.None).WaitAsync(Soon)).Should().Be(1,
            "the pass is over when its takes are, not when the segments it started are");
        await resumer.SlowEntered.WaitAsync(Soon);
        var draining = waker.WaitForResumesAsync();
        draining.IsCompleted.Should().BeFalse("the run it took is still in its segment");

        release.SetResult();
        await draining.WaitAsync(Soon);
    }

    [Fact]
    public async Task B5_A_later_pass_takes_the_row_the_busy_one_left_once_a_worker_is_free()
    {
        var slow = Parked(Now.AddSeconds(-2));
        var due = Parked(Now.AddSeconds(-1));
        var release = new TaskCompletionSource();
        var store = new RecordingResumeStore { Candidates = [slow, due] };
        var resumer = new BlockingUntilStopped(slow.ExecutionLogId, release.Task);
        var waker = new WorkflowSuspensionWaker(
            store, resumer, NullLogger.Instance, ParkedTemplates.Compiler(), new WorkflowSuspensionWakeOptions { MaxConcurrentResumes = 1 });

        await waker.TakeOnceAsync(CancellationToken.None).WaitAsync(Soon);
        (await waker.TakeOnceAsync(CancellationToken.None).WaitAsync(Soon)).Should().Be(0, "the one worker is busy");
        release.SetResult();
        await waker.WaitForResumesAsync().WaitAsync(Soon);

        (await waker.TakeOnceAsync(CancellationToken.None).WaitAsync(Soon)).Should().Be(1);
        await waker.WaitForResumesAsync().WaitAsync(Soon);
        store.Taken.Select(t => t.Id).Should().Equal([slow.SuspensionId, due.SuspensionId]);
    }

    [Fact]
    public async Task B5_A_take_is_not_handed_the_passs_token_so_a_stop_cannot_turn_a_committed_take_into_a_lost_row()
    {
        var candidate = Parked(Now.AddSeconds(-1));
        using var stopping = new CancellationTokenSource();
        var store = new RecordingResumeStore { Candidates = [candidate], StopLandsAfterTheTakeCommits = stopping };
        var waker = new WorkflowSuspensionWaker(store, new StoppedBeforeTakingResumer(), NullLogger.Instance, ParkedTemplates.Compiler());

        (await waker.WakeOnceAsync(stopping.Token)).Should().Be(0, "the stopped resume moved nothing");
        store.Taken.Should().ContainSingle();
        store.Released.Should().ContainSingle().Which.Id.Should().Be(candidate.SuspensionId,
            "the take committed and the run was never re-entered, so the row goes back");
    }

    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    private static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + Soon;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(because);
            await Task.Delay(5);
        }
    }

    private static WorkflowSuspensionCandidate Parked(DateTimeOffset timeoutAtUtc) =>
        new(
            SuspensionId: Guid.NewGuid(),
            ExecutionLogId: Guid.NewGuid(),
            WorkflowId: Guid.NewGuid(),
            BlockId: "wait-1",
            Kind: WorkflowSuspensionKind.WaitForTrigger,
            ConfigJson: "{}",
            StatePayloadJson: Payload,
            SuspendedAtUtc: Now.AddMinutes(-5),
            TimeoutAtUtc: timeoutAtUtc);

    private static readonly string Payload = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
    {
        VariablesJson = "{}",
        PreviousOutputsJson = "{}",
        TriggerEnvelopeJson = "{}",
        ExecutionStateId = null
    });

    private sealed class BlockingResumer(Guid slowExecutionLogId, Task release) : IWorkflowRunResumer
    {
        private readonly Dictionary<Guid, TaskCompletionSource> entered = [];
        private readonly object gate = new();

        public Task Entered(Guid executionLogId)
        {
            lock (gate)
            {
                if (!entered.TryGetValue(executionLogId, out var tcs))
                    entered[executionLogId] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return tcs.Task;
            }
        }

        public async Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken)
        {
            TaskCompletionSource tcs;
            lock (gate)
            {
                if (!entered.TryGetValue(ctx.ExecutionLogId, out tcs!))
                    entered[ctx.ExecutionLogId] = tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            tcs.TrySetResult();

            if (ctx.ExecutionLogId == slowExecutionLogId)
                await release;

            return WorkflowResumeOutcome.Resumed;
        }
    }

    private sealed class StoppedBeforeTakingResumer : IWorkflowRunResumer
    {
        public Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken) =>
            Task.FromResult(WorkflowResumeOutcome.Deferred);
    }

    private sealed class BlockingUntilStopped(Guid slowExecutionLogId, Task release) : IWorkflowRunResumer
    {
        private readonly TaskCompletionSource slowEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SlowEntered => slowEntered.Task;

        public async Task<WorkflowResumeOutcome> ResumeFromSuspensionAsync(ResumeContext ctx, CancellationToken cancellationToken)
        {
            if (ctx.ExecutionLogId == slowExecutionLogId)
            {
                slowEntered.TrySetResult();
                await release;
                return WorkflowResumeOutcome.Resumed;
            }

            return cancellationToken.IsCancellationRequested ? WorkflowResumeOutcome.Deferred : WorkflowResumeOutcome.Resumed;
        }
    }

    private sealed class RecordingResumeStore : IWorkflowSuspensionResumeStore
    {
        public IReadOnlyList<WorkflowSuspensionCandidate> Candidates { get; set; } = [];
        public List<(Guid Id, WorkflowSuspensionStatus Status)> Taken { get; } = [];
        public List<(Guid Id, WorkflowSuspensionStatus TakenAs)> Released { get; } = [];

        public CancellationTokenSource? StopLandsAfterTheTakeCommits { get; init; }

        public Task<WorkflowSuspensionScan> ScanAsync(int afterPosition, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowSuspensionScan(Now, Candidates, 0));

        public Task<bool> ReleaseAsync(Guid suspensionId, WorkflowSuspensionStatus takenAs, CancellationToken cancellationToken)
        {
            lock (Released) Released.Add((suspensionId, takenAs));
            return Task.FromResult(true);
        }

        public Task<ResumeContext?> TryResolveAsync(
            Guid suspensionId, WorkflowSuspensionStatus status, string outcomeJson,
            DateTimeOffset resolvedAtUtc, CancellationToken cancellationToken)
        {
            lock (Taken)
            {
                if (Taken.Any(t => t.Id == suspensionId))
                    return Task.FromResult<ResumeContext?>(null);
                Taken.Add((suspensionId, status));
            }

            if (StopLandsAfterTheTakeCommits is { } stop)
            {
                stop.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var candidate = Candidates.Single(c => c.SuspensionId == suspensionId);
            return Task.FromResult<ResumeContext?>(new ResumeContext
            {
                SuspensionId = candidate.SuspensionId,
                ExecutionLogId = candidate.ExecutionLogId,
                WorkflowId = candidate.WorkflowId,
                BlockId = candidate.BlockId,
                Kind = candidate.Kind,
                OutcomeJson = outcomeJson,
                StatePayloadJson = candidate.StatePayloadJson,
                SuspendedAtUtc = candidate.SuspendedAtUtc
            });
        }
    }
}
