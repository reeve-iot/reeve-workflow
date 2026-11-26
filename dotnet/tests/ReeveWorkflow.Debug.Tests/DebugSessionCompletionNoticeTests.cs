using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug.Tests;

public class DebugSessionCompletionNoticeTests
{
    [Theory]
    [InlineData(WorkflowDebugSessionStatus.Completed)]
    [InlineData(WorkflowDebugSessionStatus.Failed)]
    [InlineData(WorkflowDebugSessionStatus.Cancelled)]
    public async Task A_session_reaching_a_status_it_does_not_leave_is_pushed_to_the_host(
        WorkflowDebugSessionStatus terminal)
    {
        var notifier = new RecordingNotifier();
        var (service, sessionId, _) = await BuildAsync(notifier);

        await service.UpdateSessionStateAsync(sessionId, terminal, currentPauseNodeId: null);

        notifier.Completed.Should().ContainSingle().Which.Should().Be((sessionId, terminal.ToString()));
    }

    [Fact]
    public async Task A_session_that_merely_paused_is_not_reported_as_over()
    {
        var notifier = new RecordingNotifier();
        var (service, sessionId, _) = await BuildAsync(notifier);

        await service.UpdateSessionStateAsync(sessionId, WorkflowDebugSessionStatus.Paused, "node-2");

        notifier.Completed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowDebugSessionStatus.Completed, WorkflowDebugSessionOutcomes.Completed)]
    [InlineData(WorkflowDebugSessionStatus.Failed, WorkflowDebugSessionOutcomes.Failed)]
    [InlineData(WorkflowDebugSessionStatus.Cancelled, WorkflowDebugSessionOutcomes.Cancelled)]
    public async Task The_ordinary_end_of_a_session_is_counted_and_not_only_an_abandoned_one(
        WorkflowDebugSessionStatus terminal, string outcome)
    {
        var metrics = new RecordingMetrics();
        var (service, sessionId, _) = await BuildAsync(new RecordingNotifier(), metrics);

        await service.UpdateSessionStateAsync(sessionId, terminal, currentPauseNodeId: null);

        metrics.DebugSessions.Should().ContainSingle().Which.Should().Be(outcome);
    }

    [Theory]
    [InlineData(WorkflowRunStatusLabels.Succeeded, "Completed")]
    [InlineData(WorkflowRunStatusLabels.Failed, "Failed")]
    [InlineData(WorkflowRunStatusLabels.TimedOut, "Failed")]
    [InlineData(WorkflowRunStatusLabels.Cancelled, "Cancelled")]
    public async Task The_run_ending_ends_the_session_watching_it(string runStatus, string expected)
    {
        var notifier = new RecordingNotifier();
        var (service, sessionId, executionId) = await BoundAsync(notifier);

        await service.EndSessionForExecutionAsync(executionId, runStatus);

        notifier.Completed.Should().ContainSingle().Which.Should().Be((sessionId, expected));
    }

    [Fact]
    public async Task A_session_whose_run_failed_ends_failed_and_not_completed()
    {
        var notifier = new RecordingNotifier();
        var (service, sessionId, executionId) = await BoundAsync(notifier);
        IWorkflowDebugSessions sessions = new WorkflowDebugSessions(service);

        await sessions.ReleaseHookAsync(executionId, WorkflowRunStatusLabels.Failed);

        (await service.GetAsync(sessionId))!.Status.Should().Be(WorkflowDebugSessionStatus.Failed.ToString());
        notifier.Completed.Should().ContainSingle().Which.Should().Be((sessionId, WorkflowDebugSessionStatus.Failed.ToString()));
    }

    [Fact]
    public async Task A_run_nobody_was_watching_ends_without_a_word()
    {
        var notifier = new RecordingNotifier();
        var (service, _, _) = await BoundAsync(notifier);

        await service.EndSessionForExecutionAsync(Guid.NewGuid(), WorkflowRunStatusLabels.Succeeded);

        notifier.Completed.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkflowRunStatusLabels.Failed, WorkflowDebugSessionStatus.Failed)]
    [InlineData(WorkflowRunStatusLabels.TimedOut, WorkflowDebugSessionStatus.Failed)]
    [InlineData(WorkflowRunStatusLabels.Succeeded, WorkflowDebugSessionStatus.Cancelled)]
    public async Task A_session_whose_run_never_marked_it_running_ends_failed_when_the_run_failed_and_cancelled_otherwise(
        string runStatus, WorkflowDebugSessionStatus expected)
    {
        var notifier = new RecordingNotifier();
        var (service, sessionId, executionId) = await BuildAsync(notifier, bindToARun: true, leaveIdle: true);
        IWorkflowDebugSessions sessions = new WorkflowDebugSessions(service);
        await sessions.BindToRunAsync(sessionId, executionId!.Value, CancellationToken.None);

        await sessions.ReleaseHookAsync(executionId.Value, runStatus);

        sessions.HookFor(executionId.Value).Should().BeNull();
        notifier.Completed.Should().ContainSingle().Which.Should().Be((sessionId, expected.ToString()));
    }

    [Theory]
    [InlineData(WorkflowRunStatusLabels.TimedOut)]
    [InlineData(WorkflowRunStatusLabels.Failed)]
    public async Task A_session_paused_at_a_breakpoint_when_its_run_ends_is_ended_and_its_watcher_told(string runStatus)
    {
        var notifier = new RecordingNotifier();
        var (service, sessionId, executionId) = await BuildAsync(notifier, bindToARun: true, leaveIdle: true);
        await service.SetBreakpointsAsync(sessionId, ["n1"]);
        IWorkflowDebugSessions sessions = new WorkflowDebugSessions(service);
        await sessions.BindToRunAsync(sessionId, executionId!.Value, CancellationToken.None);
        await sessions.MarkRunningAsync(sessionId, CancellationToken.None);
        var hook = sessions.HookFor(executionId.Value);
        hook.Should().NotBeNull("setup: the bind registered a hook");

        using var runToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var pausing = async () => await hook!.OnNodeAsync(
            "n1", new Dictionary<string, object?>(), new Dictionary<string, IReadOnlyDictionary<string, object?>>(), runToken.Token);
        await pausing.Should().ThrowAsync<OperationCanceledException>("setup: the run's timeout fired while paused");
        (await service.GetAsync(sessionId))!.Status.Should().Be(WorkflowDebugSessionStatus.Paused.ToString(), "setup: paused");

        await sessions.ReleaseHookAsync(executionId.Value, runStatus);

        notifier.Completed.Should().ContainSingle().Which.Should().Be(
            (sessionId, WorkflowDebugSessionStatus.Failed.ToString()));
    }

    [Fact]
    public async Task A_session_its_client_walked_away_from_is_counted_once_and_as_abandoned()
    {
        var metrics = new RecordingMetrics();
        var clock = new FakeTimeProvider();
        var paused = new PauseSignallingNotifier();
        var (service, sessionId, executionId, hook, sessions) = await PausedAsync(
            metrics, new WorkflowDebugOptions { OrphanedSessionTimeoutSeconds = 30 }, paused, clock);

        var walking = Task.Run(async () => await hook.OnNodeAsync(
            "n1", new Dictionary<string, object?>(), new Dictionary<string, IReadOnlyDictionary<string, object?>>(),
            CancellationToken.None));
        await paused.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!walking.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await Task.WhenAny(walking, Task.Delay(20));
        }
        var walked = async () => await walking;
        await walked.Should().ThrowAsync<OperationCanceledException>("setup: nobody sent a command in time");
        await sessions.ReleaseHookAsync(executionId, WorkflowRunStatusLabels.Cancelled);

        metrics.DebugSessions.Should().ContainSingle().Which.Should().Be(WorkflowDebugSessionOutcomes.Abandoned);
    }

    [Fact]
    public async Task A_session_the_user_killed_is_counted_once_and_as_killed()
    {
        var metrics = new RecordingMetrics();
        var paused = new PauseSignallingNotifier();
        var (service, sessionId, executionId, hook, sessions) = await PausedAsync(metrics, notifier: paused);

        var walking = Task.Run(async () => await hook.OnNodeAsync(
            "n1", new Dictionary<string, object?>(), new Dictionary<string, IReadOnlyDictionary<string, object?>>(),
            CancellationToken.None));
        await KillOnceItIsParkedAsync(service, sessionId, paused, walking);
        var walked = async () => await walking;
        await walked.Should().ThrowAsync<OperationCanceledException>("setup: the kill unwinds the run");
        await sessions.ReleaseHookAsync(executionId, WorkflowRunStatusLabels.Cancelled);

        metrics.DebugSessions.Should().ContainSingle().Which.Should().Be(WorkflowDebugSessionOutcomes.Killed);
    }

    [Theory]
    [InlineData(WorkflowRunStatusLabels.Succeeded, WorkflowDebugSessionOutcomes.Completed)]
    [InlineData(WorkflowRunStatusLabels.Failed, WorkflowDebugSessionOutcomes.Failed)]
    [InlineData(WorkflowRunStatusLabels.TimedOut, WorkflowDebugSessionOutcomes.Failed)]
    public async Task A_session_still_walking_when_its_run_ends_is_counted_once_by_how_the_run_ended(string runStatus, string outcome)
    {
        var metrics = new RecordingMetrics();
        var (service, _, executionId) = await BuildAsync(new RecordingNotifier(), metrics, bindToARun: true);
        IWorkflowDebugSessions sessions = new WorkflowDebugSessions(service);

        await sessions.ReleaseHookAsync(executionId!.Value, runStatus);

        metrics.DebugSessions.Should().ContainSingle().Which.Should().Be(outcome);
    }

    [Theory]
    [InlineData(WorkflowRunStatusLabels.TimedOut)]
    [InlineData(WorkflowRunStatusLabels.Failed)]
    public async Task A_session_paused_when_its_run_fails_is_counted_once_and_as_failed(string runStatus)
    {
        var metrics = new RecordingMetrics();
        var (service, sessionId, executionId, hook, sessions) = await PausedAsync(metrics);

        using var runToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var walking = async () => await hook.OnNodeAsync(
            "n1", new Dictionary<string, object?>(), new Dictionary<string, IReadOnlyDictionary<string, object?>>(),
            runToken.Token);
        await walking.Should().ThrowAsync<OperationCanceledException>("setup: the run's timeout fired while paused");
        await sessions.ReleaseHookAsync(executionId, runStatus);

        metrics.DebugSessions.Should().ContainSingle().Which.Should().Be(WorkflowDebugSessionOutcomes.Failed);
    }

    [Fact]
    public async Task A_session_whose_run_was_cancelled_is_counted_once_and_as_cancelled()
    {
        var metrics = new RecordingMetrics();
        var (service, _, executionId) = await BuildAsync(new RecordingNotifier(), metrics, bindToARun: true);
        IWorkflowDebugSessions sessions = new WorkflowDebugSessions(service);

        await sessions.ReleaseHookAsync(executionId!.Value, WorkflowRunStatusLabels.Cancelled);

        metrics.DebugSessions.Should().ContainSingle().Which.Should().Be(WorkflowDebugSessionOutcomes.Cancelled);
    }

    private static async Task<(WorkflowDebugSessionService Service, Guid SessionId, Guid ExecutionId,
        IWorkflowDebugHook Hook, IWorkflowDebugSessions Sessions)> PausedAsync(
        IWorkflowMetrics metrics, WorkflowDebugOptions? debugOptions = null, IWorkflowDebugNotifier? notifier = null,
        TimeProvider? clock = null)
    {
        var (service, sessionId, executionId) = await BuildAsync(
            notifier ?? new RecordingNotifier(), metrics, bindToARun: true, leaveIdle: true, debugOptions: debugOptions,
            clock: clock);
        await service.SetBreakpointsAsync(sessionId, ["n1"]);
        IWorkflowDebugSessions sessions = new WorkflowDebugSessions(service);
        await sessions.BindToRunAsync(sessionId, executionId!.Value, CancellationToken.None);
        await sessions.MarkRunningAsync(sessionId, CancellationToken.None);
        var hook = sessions.HookFor(executionId.Value);
        hook.Should().NotBeNull("setup: the bind registered a hook");
        return (service, sessionId, executionId.Value, hook!, sessions);
    }

    private static async Task KillOnceItIsParkedAsync(
        WorkflowDebugSessionService service, Guid sessionId, PauseSignallingNotifier paused, Task walking)
    {
        await paused.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!walking.IsCompleted && DateTimeOffset.UtcNow < deadline)
        {
            await service.KillAsync(sessionId);
            await Task.WhenAny(walking, Task.Delay(20));
        }
    }

    private sealed class PauseSignallingNotifier : IWorkflowDebugNotifier
    {
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NotifyPausedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
        {
            Paused.TrySetResult();
            return Task.CompletedTask;
        }

        public Task NotifyResumedAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifySnapshotCapturedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCompletedAsync(Guid sessionId, string status, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static async Task<(WorkflowDebugSessionService Service, Guid SessionId, Guid ExecutionId)> BoundAsync(
        IWorkflowDebugNotifier notifier)
    {
        var (service, sessionId, executionId) = await BuildAsync(notifier, bindToARun: true);
        return (service, sessionId, executionId!.Value);
    }

    private static async Task<(WorkflowDebugSessionService Service, Guid SessionId, Guid? ExecutionId)> BuildAsync(
        IWorkflowDebugNotifier notifier, IWorkflowMetrics? metrics = null, bool bindToARun = false, bool leaveIdle = false,
        WorkflowDebugOptions? debugOptions = null, TimeProvider? clock = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(connection));
        });
        var provider = services.BuildServiceProvider();

        var db = provider.GetRequiredService<WorkflowDbContext>();
        await db.Database.EnsureCreatedAsync();
        var workflow = new Workflow(
            id: Guid.NewGuid(), name: "ends", description: null, createdByUserId: Guid.NewGuid(), createdAtUtc: DateTimeOffset.UtcNow);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        Guid? executionId = null;
        if (bindToARun)
        {
            var version = new WorkflowVersion(
                Guid.NewGuid(), workflow.Id, 1, "{}", null, null, Guid.NewGuid(), DateTimeOffset.UtcNow);
            db.WorkflowVersions.Add(version);
            var log = new WorkflowExecutionLog(
                Guid.NewGuid(), null, workflow.Id, version.Id, null,
                WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
            db.WorkflowExecutionLogs.Add(log);
            await db.SaveChangesAsync();
            executionId = log.Id;
        }

        var session = new WorkflowDebugSession(
            Guid.NewGuid(), workflow.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        if (!leaveIdle)
        {
            if (executionId is { } bound) session.BindToExecution(bound, DateTimeOffset.UtcNow);
            else session.Transition(WorkflowDebugSessionStatus.Bound, DateTimeOffset.UtcNow);
            session.Transition(WorkflowDebugSessionStatus.Running, DateTimeOffset.UtcNow);
        }
        db.WorkflowDebugSessions.Add(session);
        await db.SaveChangesAsync();

        var service = new WorkflowDebugSessionService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            notifier,
            metrics ?? NullWorkflowMetrics.Instance,
            new LoggingWorkflowAuditSink(NullLogger<LoggingWorkflowAuditSink>.Instance),
            new InMemoryBlobs(),
            Options.Create(debugOptions ?? new WorkflowDebugOptions()),
            NullLogger<WorkflowDebugSessionService>.Instance,
            clock);

        return (service, session.Id, executionId);
    }

    private sealed class RecordingNotifier : IWorkflowDebugNotifier
    {
        public List<(Guid SessionId, string Status)> Completed { get; } = [];

        public Task NotifyPausedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyResumedAsync(Guid sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifySnapshotCapturedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCompletedAsync(Guid sessionId, string status, CancellationToken cancellationToken = default)
        {
            Completed.Add((sessionId, status));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMetrics : IWorkflowMetrics
    {
        public List<string> DebugSessions { get; } = [];

        public void RecordDebugSession(string outcome) => DebugSessions.Add(outcome);

        public void RecordExecutionStarted() { }
        public void RecordExecutionCompleted(string status, double durationSeconds, string? triggerType = null) { }
        public void RecordNodeError(string blockType, string policy, bool caught, string exceptionType) { }
        public void RecordSupersession(Guid workflowId, string mode, string outcome) { }
        public void RecordDebugPauseDuration(double durationSeconds) { }
        public void RecordSuspensionStarted(string kind) { }
        public void RecordSuspensionResolved(string kind, string outcome, double durationSeconds) { }
    }

    private sealed class InMemoryBlobs : IWorkflowDebugBlobStore
    {
        private readonly Dictionary<string, byte[]> blobs = [];

        public Task PutBytesAsync(string key, byte[] bytes, CancellationToken cancellationToken = default)
        {
            blobs[key] = bytes;
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(blobs.TryGetValue(key, out var bytes) ? bytes : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            blobs.Remove(key);
            return Task.CompletedTask;
        }
    }
}
