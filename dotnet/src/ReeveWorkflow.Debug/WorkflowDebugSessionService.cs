using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReeveWorkflow.EntityFrameworkCore;

using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Debug;

/// <summary>Per-execution debug hooks (zero-cost when no session is attached) and per-session pause
/// signals. The paused engine lives on exactly one replica, but control/breakpoint calls can land on any replica behind a non-affinity LB, so both are ALSO forwarded over pub/sub; a receiver never fabricates state for a session it does not locally own.</summary>
public sealed class WorkflowDebugSessionService : IWorkflowDebugSessionService, IDisposable
{
    private readonly ConcurrentDictionary<Guid, IWorkflowDebugHook> _hooksByExecution = new();

    private readonly ConcurrentDictionary<Guid, WorkflowDebugHook> _hooksBySession = new();

    private readonly AckWaiters _acks = new();

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<WorkflowDebugCommand>> _pauseTcs = new();

    private readonly ConcurrentDictionary<Guid, string> _endingOutcomes = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWorkflowDebugNotifier _notifier;
    private readonly IWorkflowMetrics _domainMetrics;
    private readonly IWorkflowAuditSink _auditSink;
    private readonly IWorkflowDebugBlobStore _blobs;
    private readonly ILogger<WorkflowDebugSessionService> _logger;
    private readonly WorkflowDebugOptions _debugOptions;

    private readonly TimeProvider _clock;

    internal DateTimeOffset UtcNow => _clock.GetUtcNow();

    private readonly IWorkflowClusterSignal? _clusterSignal;
    private readonly string _commandChannel;
    private readonly string _breakpointChannel;
    private readonly string _ackChannel;
    private IDisposable? _controlSubscription;
    private IDisposable? _ackSubscription;
    private IDisposable? _breakpointSubscription;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int MaxSnapshotBytes = 1024 * 1024;

    public WorkflowDebugSessionService(
        IServiceScopeFactory scopeFactory,
        IWorkflowDebugNotifier notifier,
        IWorkflowMetrics domainMetrics,
        IWorkflowAuditSink auditSink,
        IWorkflowDebugBlobStore blobs,
        IOptions<WorkflowDebugOptions> debugOptions,
        ILogger<WorkflowDebugSessionService> logger,
        TimeProvider? clock = null,
        IWorkflowClusterSignal? clusterSignal = null,
        WorkflowClusterSignalChannels? channels = null)
    {
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _domainMetrics = domainMetrics;
        _auditSink = auditSink;
        _blobs = blobs;
        _debugOptions = new WorkflowDebugOptions
        {
            SnapshotRetentionDays = Math.Clamp(debugOptions.Value.SnapshotRetentionDays, 1, 365),
            OrphanedSessionTimeoutSeconds = Math.Clamp(debugOptions.Value.OrphanedSessionTimeoutSeconds, 30, 3600),
            CommandAckTimeoutSeconds = Math.Clamp(debugOptions.Value.CommandAckTimeoutSeconds, 1, 60),
            KillWaitTimeoutSeconds = Math.Clamp(debugOptions.Value.KillWaitTimeoutSeconds, 1, 600)
        };
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        var names = channels ?? new WorkflowClusterSignalChannels();
        _clusterSignal = clusterSignal;
        _commandChannel = names.DebugCommands;
        _breakpointChannel = names.DebugBreakpoints;
        _ackChannel = names.DebugCommandAcks;

        _controlSubscription = _clusterSignal?.Subscribe(_commandChannel, async (sessionId, payload, _) =>
        {
            if (WorkflowDebugSignalPayload.TryReadCommand(payload, out var command)
                && ApplyControlMessage(sessionId, command))
                await PublishAckAsync(sessionId, command).ConfigureAwait(false);
        });

        _ackSubscription = _clusterSignal?.Subscribe(_ackChannel, (sessionId, payload, _) =>
        {
            if (WorkflowDebugSignalPayload.TryReadCommand(payload, out var command))
                _acks.Complete(sessionId, command);
            return Task.CompletedTask;
        });

        _breakpointSubscription = _clusterSignal?.Subscribe(_breakpointChannel, (executionLogId, payload, _) =>
        {
            if (WorkflowDebugSignalPayload.TryReadNodeIds(payload, out var nodeIds))
                ApplyBreakpointMessage(executionLogId, nodeIds);
            return Task.CompletedTask;
        });
    }

    private static string SessionBlobKey(Guid sessionId) => sessionId.ToString();
    private static string SnapshotBlobKey(Guid sessionId, Guid snapshotId) =>
        $"{sessionId}/snapshots/{snapshotId}";

    private static async Task WriteSessionBlobAsync(
        IWorkflowDebugBlobStore storage,
        Guid sessionId,
        string breakpointsJson,
        string? currentPauseNodeId,
        CancellationToken ct)
    {
        var blob = new WorkflowDebugSessionBlob(breakpointsJson, currentPauseNodeId);
        var json = JsonSerializer.Serialize(blob, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await storage.PutBytesAsync(SessionBlobKey(sessionId), bytes, ct).ConfigureAwait(false);
    }

    private static async Task<WorkflowDebugSessionBlob?> ReadSessionBlobAsync(
        IWorkflowDebugBlobStore storage,
        Guid sessionId,
        CancellationToken ct)
    {
        var bytes = await storage.GetBytesAsync(SessionBlobKey(sessionId), ct).ConfigureAwait(false);
        if (bytes is null) return null;
        return JsonSerializer.Deserialize<WorkflowDebugSessionBlob>(bytes, JsonOptions);
    }

    private static async Task WriteSnapshotBlobAsync(
        IWorkflowDebugBlobStore storage,
        WorkflowDebugSnapshot snapshot,
        string inputsJson,
        string? outputsJson,
        string variablesJson,
        string expressionEvalsJson,
        CancellationToken ct)
    {
        var blob = new WorkflowDebugSnapshotBlob(inputsJson, outputsJson, variablesJson, expressionEvalsJson);
        var json = JsonSerializer.Serialize(blob, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await storage.PutBytesAsync(SnapshotBlobKey(snapshot.DebugSessionId, snapshot.Id), bytes, ct)
            .ConfigureAwait(false);
    }

    private static async Task<WorkflowDebugSnapshotBlob?> ReadSnapshotBlobAsync(
        IWorkflowDebugBlobStore storage,
        Guid sessionId,
        Guid snapshotId,
        CancellationToken ct)
    {
        var bytes = await storage.GetBytesAsync(SnapshotBlobKey(sessionId, snapshotId), ct).ConfigureAwait(false);
        if (bytes is null) return null;
        return JsonSerializer.Deserialize<WorkflowDebugSnapshotBlob>(bytes, JsonOptions);
    }

    public async Task<WorkflowDebugSessionResponse> CreateAsync(Guid workflowId, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var session = new WorkflowDebugSession(Guid.NewGuid(), workflowId, ownerUserId, _clock.GetUtcNow());

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        if (!await db.Workflows.AnyAsync(w => w.Id == workflowId && w.CreatedByUserId == ownerUserId, cancellationToken).ConfigureAwait(false))
        {
            throw new WorkflowNotFoundException(workflowId);
        }

        db.WorkflowDebugSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var storage = _blobs;
        await WriteSessionBlobAsync(storage, session.Id, "[]", null, cancellationToken).ConfigureAwait(false);

        await _auditSink.WriteAsync(new WorkflowAuditEvent(
            WorkflowAuditEventKinds.DebugSessionStarted, ownerUserId, workflowId,
            new { workflowId, sessionId = session.Id }), CancellationToken.None).ConfigureAwait(false);

        return WorkflowDebugSessionMapper.ToResponse(session, new WorkflowDebugSessionBlob("[]", null));
    }

    public async Task<WorkflowDebugSessionResponse> BindToExecutionAsync(Guid sessionId, Guid executionLogId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var session = await db.WorkflowDebugSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Debug session {sessionId} not found.");

        session.BindToExecution(executionLogId, _clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var storage = _blobs;
        var blob = await ReadSessionBlobAsync(storage, sessionId, cancellationToken).ConfigureAwait(false);
        var breakpoints = ParseBreakpoints(blob?.BreakpointsJson ?? "[]");

        var hook = new WorkflowDebugHook(session.Id, executionLogId, breakpoints, this, _notifier, _domainMetrics, _logger);
        RegisterExecutionHook(executionLogId, hook);

        return WorkflowDebugSessionMapper.ToResponse(session, blob);
    }

    public async Task MarkRunningAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var session = await db.WorkflowDebugSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Debug session {sessionId} not found.");

        session.Transition(WorkflowDebugSessionStatus.Running, _clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetBreakpointsAsync(Guid sessionId, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var session = await db.WorkflowDebugSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Debug session {sessionId} not found.");

        var now = _clock.GetUtcNow();
        session.TouchActivity(now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var storage = _blobs;
        var existingBlob = await ReadSessionBlobAsync(storage, sessionId, cancellationToken).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(nodeIds, JsonOptions);
        await WriteSessionBlobAsync(storage, sessionId, json, existingBlob?.CurrentPauseNodeId, cancellationToken)
            .ConfigureAwait(false);

        var executionLogId = session.WorkflowExecutionLogId;
        ApplyBreakpointsLocally(executionLogId, nodeIds);

        await PublishBreakpointsAsync(executionLogId, nodeIds).ConfigureAwait(false);

        await _auditSink.WriteAsync(new WorkflowAuditEvent(
            WorkflowAuditEventKinds.DebugBreakpointSet, session.OwnerUserId, sessionId,
            new { sessionId, nodeIds }), CancellationToken.None).ConfigureAwait(false);
    }

    public Task<WorkflowDebugCommandResult> StepAsync(Guid sessionId) => SendCommandAsync(sessionId, WorkflowDebugCommand.Step);
    public Task<WorkflowDebugCommandResult> StepIntoAsync(Guid sessionId) => SendCommandAsync(sessionId, WorkflowDebugCommand.StepInto);
    public Task<WorkflowDebugCommandResult> StepOverAsync(Guid sessionId) => SendCommandAsync(sessionId, WorkflowDebugCommand.StepOver);

    public async Task<WorkflowDebugCommandResult> ResumeAsync(Guid sessionId, Guid? actorUserId = null)
    {
        var result = await SendCommandAsync(sessionId, WorkflowDebugCommand.Resume).ConfigureAwait(false);
        if (result.Succeeded)
            await WriteResumedAuditAsync(sessionId, actorUserId).ConfigureAwait(false);
        return result;
    }

    public async Task<WorkflowDebugCommandResult> KillAsync(Guid sessionId, Guid? actorUserId = null, string? reason = null)
    {
        var result = await SendCommandAsync(sessionId, WorkflowDebugCommand.Kill).ConfigureAwait(false);
        if (result.Succeeded)
            await WriteKilledAuditAsync(sessionId, actorUserId, reason).ConfigureAwait(false);
        return result;
    }

    public async Task<WorkflowDebugSessionResponse?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var session = await db.WorkflowDebugSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (session is null) return null;

        var storage = _blobs;
        var blob = await ReadSessionBlobAsync(storage, sessionId, cancellationToken).ConfigureAwait(false);
        return WorkflowDebugSessionMapper.ToResponse(session, blob);
    }

    public async Task<IReadOnlyList<WorkflowDebugSnapshotResponse>> GetSnapshotsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var snapshots = await db.WorkflowDebugSnapshots
            .AsNoTracking()
            .Where(s => s.DebugSessionId == sessionId)
            .OrderBy(s => s.CapturedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshots.Count == 0) return [];

        var storage = _blobs;
        var results = new List<WorkflowDebugSnapshotResponse>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var blob = await ReadSnapshotBlobAsync(storage, sessionId, snapshot.Id, cancellationToken)
                .ConfigureAwait(false);
            results.Add(WorkflowDebugSessionMapper.ToResponse(snapshot, blob));
        }
        return results;
    }

    public async Task<WorkflowDebugCommandResult> DeleteAsync(
        Guid sessionId, bool force = false, CancellationToken cancellationToken = default)
    {
        var status = await GetSessionStatusAsync(sessionId).ConfigureAwait(false);
        if (status is null)
            return new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.NotFound);

        if (status is WorkflowDebugSessionStatus.Bound or WorkflowDebugSessionStatus.Running or WorkflowDebugSessionStatus.Paused)
        {
            var kill = await SendCommandAsync(sessionId, WorkflowDebugCommand.Kill).ConfigureAwait(false);
            switch (kill.Outcome)
            {
                case WorkflowDebugCommandOutcome.NotFound:
                    return kill;

                case WorkflowDebugCommandOutcome.WrongState:
                    break;

                case WorkflowDebugCommandOutcome.Applied:
                case WorkflowDebugCommandOutcome.Unconfirmed:
                    if (kill.Outcome == WorkflowDebugCommandOutcome.Unconfirmed &&
                        await RunHasDurableEndingAsync(sessionId, cancellationToken).ConfigureAwait(false))
                        break;
                    var ended = await WaitForEndAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    if (ended is null)
                        break;
                    if (!ended.Value && !force &&
                        !await RunHasDurableEndingAsync(sessionId, cancellationToken).ConfigureAwait(false))
                        return new WorkflowDebugCommandResult(
                            kill.Succeeded ? WorkflowDebugCommandOutcome.NotEnded : kill.Outcome, kill.Status);
                    break;

                case WorkflowDebugCommandOutcome.NotHeld:
                    if (!force && !await RunHasDurableEndingAsync(sessionId, cancellationToken).ConfigureAwait(false))
                        return kill;
                    break;

                default:
                    if (!force)
                        return kill;
                    break;
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var session = await db.WorkflowDebugSessions
            .Include(s => s.Snapshots)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (session is null) return new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.Applied);

        if (session.WorkflowExecutionLogId.HasValue)
        {
            UnregisterExecutionHook(session.WorkflowExecutionLogId.Value);
        }

        var storage = _blobs;
        foreach (var snapshot in session.Snapshots)
        {
            await storage.DeleteAsync(SnapshotBlobKey(sessionId, snapshot.Id), cancellationToken)
                .ConfigureAwait(false);
        }
        await storage.DeleteAsync(SessionBlobKey(sessionId), cancellationToken).ConfigureAwait(false);

        db.WorkflowDebugSessions.Remove(session);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _endingOutcomes.TryRemove(sessionId, out _);
        return new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.Applied);
    }

    public IWorkflowDebugHook? GetHookForExecution(Guid executionLogId)
    {
        _hooksByExecution.TryGetValue(executionLogId, out var hook);
        return hook;
    }

    public void RegisterExecutionHook(Guid executionLogId, IWorkflowDebugHook hook)
    {
        _hooksByExecution[executionLogId] = hook;
        if (hook is WorkflowDebugHook watching)
            _hooksBySession[watching.SessionId] = watching;
    }

    public void UnregisterExecutionHook(Guid executionLogId)
    {
        if (_hooksByExecution.TryRemove(executionLogId, out var hook) && hook is WorkflowDebugHook watching)
            _hooksBySession.TryRemove(new KeyValuePair<Guid, WorkflowDebugHook>(watching.SessionId, watching));
    }

    /// <inheritdoc />
    public async Task EndSessionForExecutionAsync(
        Guid executionLogId, string runStatus, CancellationToken cancellationToken = default)
    {
        var watchedSessionId = (_hooksByExecution.TryGetValue(executionLogId, out var live)
            ? live as WorkflowDebugHook
            : null)?.SessionId;
        UnregisterExecutionHook(executionLogId);

        Guid sessionId;
        WorkflowDebugSessionStatus status;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var found = await db.WorkflowDebugSessions
                .AsNoTracking()
                .Where(s => s.WorkflowExecutionLogId == executionLogId)
                .Select(s => new { s.Id, s.Status })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (found is null)
            {
                if (watchedSessionId is { } deleted) _endingOutcomes.TryRemove(deleted, out _);
                return;
            }
            sessionId = found.Id;
            status = found.Status;
            if (runStatus == WorkflowRunStatusLabels.Cancelled && (live as WorkflowDebugHook)?.KillRequested == true)
                DeclareEndingOutcome(sessionId, WorkflowDebugSessionOutcomes.Killed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up the debug session for execution {ExecutionLogId}.", executionLogId);
            return;
        }

        var terminal = TerminalStatusFor(runStatus, status);

        await UpdateSessionStateAsync(sessionId, terminal, currentPauseNodeId: null).ConfigureAwait(false);
    }

    private static WorkflowDebugSessionStatus TerminalStatusFor(string runStatus, WorkflowDebugSessionStatus status)
    {
        if (runStatus == WorkflowRunStatusLabels.Cancelled)
            return WorkflowDebugSessionStatus.Cancelled;
        if (runStatus == WorkflowRunStatusLabels.Succeeded)
            return status == WorkflowDebugSessionStatus.Running ? WorkflowDebugSessionStatus.Completed : WorkflowDebugSessionStatus.Cancelled;
        return status is WorkflowDebugSessionStatus.Bound or WorkflowDebugSessionStatus.Running or WorkflowDebugSessionStatus.Paused
            ? WorkflowDebugSessionStatus.Failed
            : WorkflowDebugSessionStatus.Cancelled;
    }

    /// <summary>Awaits a TCS until the UI sends a command (step/resume/kill). If none arrives within
    /// <c>Debug:OrphanedSessionTimeoutSeconds</c> the session is abandoned: the run is killed via <see cref="OperationCanceledException"/>.</summary>
    public async Task<WorkflowDebugCommand> WaitForCommandAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var tcs = _pauseTcs.GetOrAdd(sessionId, _ =>
            new TaskCompletionSource<WorkflowDebugCommand>(TaskCreationOptions.RunContinuationsAsynchronously));

        var orphanTimeout = TimeSpan.FromSeconds(_debugOptions.OrphanedSessionTimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(orphanTimeout, _clock);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var reg = linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token));

        try
        {
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Debug session {SessionId} orphaned after {Timeout}s with no client command. Killing the run.",
                    sessionId, _debugOptions.OrphanedSessionTimeoutSeconds);
                DeclareEndingOutcome(sessionId, WorkflowDebugSessionOutcomes.Abandoned);
                await UpdateSessionStateAsync(sessionId, WorkflowDebugSessionStatus.Cancelled, null).ConfigureAwait(false);
                throw new OperationCanceledException($"Debug session {sessionId} abandoned (client disconnected).", cancellationToken);
            }
        }
        finally
        {
            _pauseTcs.TryRemove(new KeyValuePair<Guid, TaskCompletionSource<WorkflowDebugCommand>>(sessionId, tcs));
        }
    }

    private async Task<WorkflowDebugCommandResult> SendCommandAsync(Guid sessionId, WorkflowDebugCommand command)
    {
        var status = await GetSessionStatusAsync(sessionId).ConfigureAwait(false);
        if (status is null)
            return new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.NotFound);
        if (!Admits(status.Value, command))
            return new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.WrongState, status);

        if (ApplyCommandLocally(sessionId, command))
            return new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.Applied, status);

        if (_clusterSignal is null)
        {
            var latest = await GetSessionStatusAsync(sessionId).ConfigureAwait(false);
            return latest is { } now && !Admits(now, command)
                ? new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.WrongState, now)
                : new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.NotHeld, latest);
        }

        var confirmed = _acks.Register(sessionId, command);
        try
        {
            await PublishControlAsync(sessionId, command).ConfigureAwait(false);
            await confirmed.Task
                .WaitAsync(TimeSpan.FromSeconds(_debugOptions.CommandAckTimeoutSeconds), _clock)
                .ConfigureAwait(false);
            return new WorkflowDebugCommandResult(WorkflowDebugCommandOutcome.Applied, status);
        }
        catch (TimeoutException)
        {
            var latest = await GetSessionStatusAsync(sessionId).ConfigureAwait(false);
            return new WorkflowDebugCommandResult(
                WorkflowDebugCommandOutcome.Unconfirmed, latest);
        }
        finally
        {
            _acks.Unregister(sessionId, command, confirmed);
        }
    }

    private static bool Admits(WorkflowDebugSessionStatus status, WorkflowDebugCommand command) =>
        command == WorkflowDebugCommand.Kill
            ? status is WorkflowDebugSessionStatus.Bound or WorkflowDebugSessionStatus.Running or WorkflowDebugSessionStatus.Paused
            : status is WorkflowDebugSessionStatus.Paused;

    private bool ApplyCommandLocally(Guid sessionId, WorkflowDebugCommand command)
    {
        if (command == WorkflowDebugCommand.Kill)
            return _hooksBySession.TryGetValue(sessionId, out var hook) && hook.RequestKill();

        return _pauseTcs.TryGetValue(sessionId, out var tcs) && tcs.TrySetResult(command);
    }

    private Task PublishControlAsync(Guid sessionId, WorkflowDebugCommand command)
        => _clusterSignal?.PublishAsync(
            _commandChannel, sessionId, WorkflowDebugSignalPayload.Write(command), CancellationToken.None)
           ?? Task.CompletedTask;

    private Task PublishAckAsync(Guid sessionId, WorkflowDebugCommand command)
        => _clusterSignal?.PublishAsync(
            _ackChannel, sessionId, WorkflowDebugSignalPayload.Write(command), CancellationToken.None)
           ?? Task.CompletedTask;

    private async Task<bool?> WaitForEndAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow().AddSeconds(_debugOptions.KillWaitTimeoutSeconds);
        while (true)
        {
            var status = await GetSessionStatusAsync(sessionId).ConfigureAwait(false);
            if (status is null)
                return null;
            if (status is WorkflowDebugSessionStatus.Completed or WorkflowDebugSessionStatus.Failed or WorkflowDebugSessionStatus.Cancelled)
                return true;
            if (_clock.GetUtcNow() >= deadline)
                return false;
            await Task.Delay(TimeSpan.FromMilliseconds(50), _clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> RunHasDurableEndingAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var session = await db.WorkflowDebugSessions.AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => new { session.WorkflowId, session.WorkflowExecutionLogId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (session?.WorkflowExecutionLogId is not { } executionLogId) return true;

        var status = await db.WorkflowExecutionLogs.AsNoTracking()
            .Where(log => log.Id == executionLogId)
            .Select(log => (WorkflowExecutionStatus?)log.Status)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (status is null or WorkflowExecutionStatus.Succeeded or WorkflowExecutionStatus.Failed
            or WorkflowExecutionStatus.Cancelled or WorkflowExecutionStatus.TimedOut)
            return true;

        var terminalRow = await db.WorkflowExecutionStates.AsNoTracking()
            .Where(row => row.WorkflowId == session.WorkflowId &&
                (row.Status == nameof(WorkflowRunStateStatus.Completed) ||
                 row.Status == nameof(WorkflowRunStateStatus.Failed) ||
                 row.Status == nameof(WorkflowRunStateStatus.Cancelled)))
            .Select(row => new WorkflowRunStateRow
            {
                ExecutionId = row.ExecutionId,
                WorkflowId = row.WorkflowId,
                Status = row.Status == nameof(WorkflowRunStateStatus.Completed)
                    ? WorkflowRunStateStatus.Completed
                    : row.Status == nameof(WorkflowRunStateStatus.Failed)
                        ? WorkflowRunStateStatus.Failed
                        : WorkflowRunStateStatus.Cancelled,
                VariablesJson = row.VariablesJson
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return terminalRow is not null && WorkflowRunCheckpoint.Of(terminalRow)?.JournalExecutionId == executionLogId;
    }

    /// <summary>Receiver core for a peer's control message: applies the command only if this replica owns the
    /// paused engine, never fabricating a phantom TCS. Own broadcasts are dropped by the signal.</summary>
    public bool ApplyControlMessage(Guid sessionId, WorkflowDebugCommand command)
        => ApplyCommandLocally(sessionId, command);

    internal void PreparePause(Guid sessionId) =>
        _pauseTcs.GetOrAdd(sessionId, _ =>
            new TaskCompletionSource<WorkflowDebugCommand>(TaskCreationOptions.RunContinuationsAsynchronously));

    internal void AbandonPause(Guid sessionId) => _pauseTcs.TryRemove(sessionId, out _);

    private bool ApplyBreakpointsLocally(Guid? executionLogId, IReadOnlyList<string> nodeIds)
    {
        if (executionLogId.HasValue &&
            _hooksByExecution.TryGetValue(executionLogId.Value, out var hook) &&
            hook is WorkflowDebugHook typedHook)
        {
            typedHook.UpdateBreakpoints(nodeIds);
            return true;
        }
        return false;
    }

    private Task PublishBreakpointsAsync(Guid? executionLogId, IReadOnlyList<string> nodeIds)
        => executionLogId.HasValue && _clusterSignal is not null
            ? _clusterSignal.PublishAsync(
                _breakpointChannel, executionLogId.Value, WorkflowDebugSignalPayload.Write(nodeIds), CancellationToken.None)
            : Task.CompletedTask;

    /// <summary>Receiver core for a peer's breakpoint update: updates the live hook only if this replica owns
    /// the in-flight engine, never registering a phantom hook. Own broadcasts are dropped by the signal.</summary>
    public bool ApplyBreakpointMessage(Guid executionLogId, IReadOnlyList<string> nodeIds)
        => ApplyBreakpointsLocally(executionLogId, nodeIds);

    public void Dispose()
    {
        _controlSubscription?.Dispose();
        _ackSubscription?.Dispose();
        _breakpointSubscription?.Dispose();
    }

    public async Task PersistSnapshotAsync(
        WorkflowDebugSnapshot snapshot,
        string inputsJson,
        string? outputsJson,
        string variablesJson,
        string expressionEvalsJson)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            db.WorkflowDebugSnapshots.Add(snapshot);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var storage = _blobs;
            await WriteSnapshotBlobAsync(storage, snapshot, inputsJson, outputsJson, variablesJson, expressionEvalsJson, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist debug snapshot for session {SessionId}, node {NodeId}.",
                snapshot.DebugSessionId, snapshot.NodeId);
        }
    }

    internal async Task<WorkflowDebugSessionStatus?> GetSessionStatusAsync(Guid sessionId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var session = await db.WorkflowDebugSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId)
            .ConfigureAwait(false);
        return session?.Status;
    }

    internal void DeclareEndingOutcome(Guid sessionId, string outcome) => _endingOutcomes[sessionId] = outcome;

    public async Task UpdateSessionStateAsync(
        Guid sessionId,
        WorkflowDebugSessionStatus? newStatus,
        string? currentPauseNodeId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

            var session = await db.WorkflowDebugSessions
                .FirstOrDefaultAsync(s => s.Id == sessionId)
                .ConfigureAwait(false);

            if (session is null)
            {
                _endingOutcomes.TryRemove(sessionId, out _);
                return;
            }

            if (newStatus.HasValue && session.Status is WorkflowDebugSessionStatus.Completed or WorkflowDebugSessionStatus.Failed or WorkflowDebugSessionStatus.Cancelled)
                return;

            var now = _clock.GetUtcNow();

            void ApplyStatus()
            {
                if (newStatus.HasValue)
                {
                    session.Transition(newStatus.Value, now);
                }
                else
                {
                    session.TouchActivity(now);
                }
            }

            var storage = _blobs;
            var existingBlob = await ReadSessionBlobAsync(storage, sessionId, CancellationToken.None).ConfigureAwait(false);
            var breakpointsJson = existingBlob?.BreakpointsJson ?? "[]";

            if (currentPauseNodeId is not null)
            {
                await WriteSessionBlobAsync(storage, sessionId, breakpointsJson, currentPauseNodeId, CancellationToken.None)
                    .ConfigureAwait(false);
                ApplyStatus();
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                ApplyStatus();
                await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                await WriteSessionBlobAsync(storage, sessionId, breakpointsJson, null, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (newStatus is WorkflowDebugSessionStatus.Completed or WorkflowDebugSessionStatus.Failed or WorkflowDebugSessionStatus.Cancelled)
            {
                _domainMetrics.RecordDebugSession(_endingOutcomes.TryRemove(sessionId, out var declared)
                    ? declared
                    : newStatus switch
                    {
                        WorkflowDebugSessionStatus.Completed => WorkflowDebugSessionOutcomes.Completed,
                        WorkflowDebugSessionStatus.Failed => WorkflowDebugSessionOutcomes.Failed,
                        _ => WorkflowDebugSessionOutcomes.Cancelled,
                    });

                await _notifier.NotifyCompletedAsync(sessionId, newStatus.Value.ToString(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update session state for {SessionId}.", sessionId);
        }
    }

    internal async Task WriteStepAuditAsync(Guid sessionId, Guid ownerUserId, string fromNodeId, string toNodeId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
                await _auditSink.WriteAsync(new WorkflowAuditEvent(
                WorkflowAuditEventKinds.DebugStep, ownerUserId, sessionId,
                new { sessionId, fromNodeId, toNodeId }), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write step audit for session {SessionId}.", sessionId);
        }
    }

    private async Task WriteResumedAuditAsync(Guid sessionId, Guid? actorUserId)
    {
        try
        {
            var resolvedActor = actorUserId ?? await ResolveSessionOwnerAsync(sessionId).ConfigureAwait(false);
            using var scope = _scopeFactory.CreateScope();
                await _auditSink.WriteAsync(new WorkflowAuditEvent(
                WorkflowAuditEventKinds.DebugResumed, resolvedActor, sessionId,
                new { sessionId, actorUserId = resolvedActor }), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write resumed audit for session {SessionId}.", sessionId);
        }
    }

    private async Task WriteKilledAuditAsync(Guid sessionId, Guid? actorUserId, string? reason)
    {
        try
        {
            var resolvedActor = actorUserId ?? await ResolveSessionOwnerAsync(sessionId).ConfigureAwait(false);
            using var scope = _scopeFactory.CreateScope();
                await _auditSink.WriteAsync(new WorkflowAuditEvent(
                WorkflowAuditEventKinds.DebugKilled, resolvedActor, sessionId,
                new { sessionId, actorUserId = resolvedActor, reason }), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write killed audit for session {SessionId}.", sessionId);
        }
    }

    private async Task<Guid?> ResolveSessionOwnerAsync(Guid sessionId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            return await db.WorkflowDebugSessions
                .AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => (Guid?)s.OwnerUserId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static HashSet<string> ParseBreakpoints(string json)
    {
        try
        {
            var nodeIds = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            return nodeIds is not null ? [.. nodeIds] : [];
        }
        catch
        {
            return [];
        }
    }

    public static string TruncateSnapshotJson(string json)
    {
        if (json.Length <= MaxSnapshotBytes)
            return json;

        var bytesDropped = json.Length - MaxSnapshotBytes;
        var truncated = new JsonObject
        {
            ["_truncated"] = true,
            ["_bytes_dropped"] = bytesDropped,
            ["_original_size_bytes"] = json.Length,
            ["_partial_preview"] = json[..MaxSnapshotBytes]
        };
        return truncated.ToJsonString();
    }

    private sealed class AckWaiters
    {
        private readonly object _gate = new();
        private readonly Dictionary<(Guid, WorkflowDebugCommand), List<TaskCompletionSource>> _waiting = [];

        public TaskCompletionSource Register(Guid sessionId, WorkflowDebugCommand command)
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (!_waiting.TryGetValue((sessionId, command), out var list))
                    _waiting[(sessionId, command)] = list = [];
                list.Add(waiter);
            }

            return waiter;
        }

        public void Unregister(Guid sessionId, WorkflowDebugCommand command, TaskCompletionSource waiter)
        {
            lock (_gate)
            {
                if (_waiting.TryGetValue((sessionId, command), out var list) && list.Remove(waiter) && list.Count == 0)
                    _waiting.Remove((sessionId, command));
            }
        }

        public void Complete(Guid sessionId, WorkflowDebugCommand command)
        {
            TaskCompletionSource[] released;
            lock (_gate)
                released = _waiting.TryGetValue((sessionId, command), out var list) ? [.. list] : [];
            foreach (var waiter in released)
                waiter.TrySetResult();
        }
    }
}

/// <summary>Per-execution debug hook holding the breakpoint set and step-mode state. Never constructed
/// when no debug session is attached — the zero-cost null check happens in the dispatcher before any call here.</summary>
public sealed class WorkflowDebugHook : IWorkflowDebugHook
{
    private const int DebugPayloadValueBudgetBytes = 16_384;
    private readonly Guid _sessionId;
    private readonly Guid _executionLogId;
    private volatile HashSet<string> _breakpoints;
    private readonly WorkflowDebugSessionService _service;
    private readonly IWorkflowDebugNotifier _notifier;
    private readonly IWorkflowMetrics _domainMetrics;
    private readonly ILogger _logger;

    private Guid _ownerUserId = Guid.Empty;

    private readonly CancellationTokenSource _kill = new();
    private volatile bool _killRequested;

    private volatile bool _singleStep;
    private string? _lastPausedNodeId;

    public WorkflowDebugHook(
        Guid sessionId,
        Guid executionLogId,
        HashSet<string> breakpoints,
        WorkflowDebugSessionService service,
        IWorkflowDebugNotifier notifier,
        IWorkflowMetrics domainMetrics,
        ILogger logger)
    {
        _sessionId = sessionId;
        _executionLogId = executionLogId;
        _breakpoints = breakpoints;
        _service = service;
        _notifier = notifier;
        _domainMetrics = domainMetrics;
        _logger = logger;
    }

    internal Guid SessionId => _sessionId;

    /// <inheritdoc />
    public CancellationToken KillToken => _kill.Token;

    internal bool KillRequested => _killRequested;

    internal bool RequestKill()
    {
        _killRequested = true;
        try
        {
            _kill.Cancel();
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex, "A cancellation callback threw while killing the run of session {SessionId}.", _sessionId);
        }

        return true;
    }

    internal void UpdateBreakpoints(IReadOnlyList<string> nodeIds)
    {
        _breakpoints = [.. nodeIds];
    }

    public async Task OnNodeAsync(
        string nodeId,
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> allOutputs,
        CancellationToken cancellationToken)
    {
        _kill.Token.ThrowIfCancellationRequested();
        var shouldPause = _singleStep || _breakpoints.Contains(nodeId);
        if (!shouldPause) return;

        var inputsJson = WorkflowDebugSessionService.TruncateSnapshotJson(SafeSerializeMap(inputs));
        var variablesJson = WorkflowDebugSessionService.TruncateSnapshotJson(SafeSerializeOutputMap(allOutputs));
        var snapshot = new WorkflowDebugSnapshot(
            Guid.NewGuid(), _sessionId, nodeId, _service.UtcNow);

        await _service.PersistSnapshotAsync(snapshot, inputsJson, null, variablesJson, "{}").ConfigureAwait(false);

        var currentStatus = await _service.GetSessionStatusAsync(_sessionId).ConfigureAwait(false);
        if (currentStatus == WorkflowDebugSessionStatus.Bound)
        {
            await _service.UpdateSessionStateAsync(_sessionId, WorkflowDebugSessionStatus.Running, null)
                .ConfigureAwait(false);
        }

        _service.PreparePause(_sessionId);
        try
        {
            await _service.UpdateSessionStateAsync(_sessionId, WorkflowDebugSessionStatus.Paused, nodeId).ConfigureAwait(false);

            var snapshotResponse = WorkflowDebugSessionMapper.ToResponse(snapshot, new WorkflowDebugSnapshotBlob(inputsJson, null, variablesJson, "{}"));
            var snapshotJson = JsonSerializer.Serialize(snapshotResponse);
            await _notifier.NotifyPausedAsync(_sessionId, snapshotJson, cancellationToken).ConfigureAwait(false);

            _singleStep = false;

            if (_lastPausedNodeId is not null && _ownerUserId != Guid.Empty)
            {
                await _service.WriteStepAuditAsync(_sessionId, _ownerUserId, _lastPausedNodeId, nodeId)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            _service.AbandonPause(_sessionId);
            throw;
        }

        var sw = Stopwatch.StartNew();
        WorkflowDebugCommand command;
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _kill.Token);
        try
        {
            command = await _service.WaitForCommandAsync(_sessionId, waiting.Token).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            _domainMetrics.RecordDebugPauseDuration(sw.Elapsed.TotalSeconds);
        }

        _lastPausedNodeId = nodeId;

        switch (command)
        {
            case WorkflowDebugCommand.Step:
            case WorkflowDebugCommand.StepInto:
            case WorkflowDebugCommand.StepOver:
                _singleStep = true;
                await _service.UpdateSessionStateAsync(_sessionId, WorkflowDebugSessionStatus.Running, null).ConfigureAwait(false);
                await _notifier.NotifyResumedAsync(_sessionId, cancellationToken).ConfigureAwait(false);
                break;

            case WorkflowDebugCommand.Resume:
                await _service.UpdateSessionStateAsync(_sessionId, WorkflowDebugSessionStatus.Running, null).ConfigureAwait(false);
                await _notifier.NotifyResumedAsync(_sessionId, cancellationToken).ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning("Unknown debug command {Command} for session {SessionId}.", command, _sessionId);
                break;
        }
    }

    public async Task OnNodeCompletedAsync(
        string nodeId,
        IReadOnlyDictionary<string, object?> outputs,
        CancellationToken cancellationToken)
    {
        if (!_singleStep && !_breakpoints.Contains(nodeId)) return;

        var outputsJson = WorkflowDebugSessionService.TruncateSnapshotJson(SafeSerializeMap(outputs));
        await _notifier.NotifySnapshotCapturedAsync(
            _sessionId,
            $"{{\"nodeId\":\"{nodeId}\",\"outputsJson\":{outputsJson}}}",
            cancellationToken).ConfigureAwait(false);
    }

    private static string SafeSerializeMap(IReadOnlyDictionary<string, object?> value)
    {
        try { return PayloadSanitizer.SerializeAndSanitize(value, DebugPayloadValueBudgetBytes) ?? "{}"; }
        catch { return "{}"; }
    }

    private static string SafeSerializeOutputMap(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> map)
    {
        try
        {
            var result = new JsonObject();
            foreach (var (nodeId, outputs) in map)
                result[nodeId] = PayloadSanitizer.SanitizeToObject(outputs, DebugPayloadValueBudgetBytes) ?? new JsonObject();
            return result.ToJsonString();
        }
        catch { return "{}"; }
    }
}
