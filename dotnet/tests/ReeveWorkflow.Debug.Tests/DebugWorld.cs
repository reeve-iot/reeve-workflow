using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug.Tests;

internal sealed class DebugWorld : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string databaseFile;
    private readonly ServiceProvider provider;
    private readonly InMemoryBlobs blobs = new();

    private DebugWorld(string databaseFile, ServiceProvider provider, Workflow workflow)
    {
        this.databaseFile = databaseFile;
        this.provider = provider;
        Workflow = workflow;
    }

    public Workflow Workflow { get; }

    public LoopbackBus Bus { get; } = new();

    public static async Task<DebugWorld> CreateAsync()
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"debug-world-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile, Pooling = false, DefaultTimeout = 30
        }.ToString();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(connectionString).AddInterceptors(NoSync.Instance));
        });
        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<WorkflowDbContext>();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        var workflow = new Workflow(
            id: Guid.NewGuid(), name: "debugged", description: null,
            createdByUserId: Guid.NewGuid(), createdAtUtc: DateTimeOffset.UtcNow);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        return new DebugWorld(databaseFile, provider, workflow);
    }

    public Replica AddReplica(string name, WorkflowDebugOptions? options = null, bool onBus = true)
    {
        var notifier = new RecordingNotifier();
        var audit = new RecordingAudit();
        var metrics = new RecordingMetrics();
        var endpoint = onBus ? Bus.Join(name) : null;
        var service = new WorkflowDebugSessionService(
            provider.GetRequiredService<IServiceScopeFactory>(), notifier, metrics, audit, blobs,
            Options.Create(options ?? new WorkflowDebugOptions { CommandAckTimeoutSeconds = 1, KillWaitTimeoutSeconds = 2 }),
            NullLogger<WorkflowDebugSessionService>.Instance,
            clusterSignal: endpoint);
        return new Replica(this, name, service, notifier, audit, metrics, endpoint);
    }

    public async Task<Session> SessionAsync(Replica holder, WorkflowDebugSessionStatus status)
    {
        var sessionId = Guid.NewGuid();
        Guid? executionId = null;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var now = DateTimeOffset.UtcNow;
            var session = new WorkflowDebugSession(sessionId, Workflow.Id, Guid.NewGuid(), now);
            if (status is not (WorkflowDebugSessionStatus.Idle or WorkflowDebugSessionStatus.Cancelled))
            {
                var version = new WorkflowVersion(Guid.NewGuid(), Workflow.Id, 1, "{}", null, null, Guid.NewGuid(), now);
                db.WorkflowVersions.Add(version);
                var log = new WorkflowExecutionLog(
                    Guid.NewGuid(), null, Workflow.Id, version.Id, null, WorkflowExecutionStatus.Running, now);
                db.WorkflowExecutionLogs.Add(log);
                await db.SaveChangesAsync();
                executionId = log.Id;
                if (status is WorkflowDebugSessionStatus.Completed)
                {
                    session.BindToExecution(log.Id, now);
                    session.Transition(WorkflowDebugSessionStatus.Running, now);
                    session.Transition(WorkflowDebugSessionStatus.Completed, now);
                }
            }
            else if (status is WorkflowDebugSessionStatus.Cancelled)
            {
                session.Transition(WorkflowDebugSessionStatus.Cancelled, now);
            }

            db.WorkflowDebugSessions.Add(session);
            await db.SaveChangesAsync();
        }

        if (status is WorkflowDebugSessionStatus.Paused)
            await holder.Service.SetBreakpointsAsync(sessionId, ["n1"]);

        FakeRun? run = null;
        if (status is WorkflowDebugSessionStatus.Bound or WorkflowDebugSessionStatus.Running or WorkflowDebugSessionStatus.Paused)
        {
            await holder.Sessions.BindToRunAsync(sessionId, executionId!.Value, CancellationToken.None);
            if (status is not WorkflowDebugSessionStatus.Bound)
                await holder.Sessions.MarkRunningAsync(sessionId, CancellationToken.None);
            run = new FakeRun(holder, sessionId, executionId!.Value, parkAtBreakpoint: status is WorkflowDebugSessionStatus.Paused);
            if (status is not WorkflowDebugSessionStatus.Bound)
            {
                run.Start();
                await (status is WorkflowDebugSessionStatus.Paused ? holder.Notifier.Paused.Task : run.Entered.Task)
                    .WaitAsync(Patience);
            }
        }

        return new Session(this, sessionId, executionId, run);
    }

    public async Task<WorkflowDebugSessionStatus?> StatusAsync(Guid sessionId)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return (await db.WorkflowDebugSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId))?.Status;
    }

    public async Task CompleteExecutionLogAsync(Guid executionLogId)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var log = await db.WorkflowExecutionLogs.FirstAsync(entry => entry.Id == executionLogId);
        log.Complete(WorkflowExecutionStatus.Cancelled, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(databaseFile)!, Path.GetFileName(databaseFile) + "*"))
            File.Delete(file);
    }

    internal sealed record Session(DebugWorld World, Guid Id, Guid? ExecutionId, FakeRun? Run)
    {
        public Task<WorkflowDebugSessionStatus?> StatusAsync() => World.StatusAsync(Id);
    }

    internal sealed class Replica(
        DebugWorld world, string name, WorkflowDebugSessionService service, RecordingNotifier notifier,
        RecordingAudit audit, RecordingMetrics metrics, LoopbackBus.Endpoint? endpoint)
    {
        public string Name { get; } = name;
        public DebugWorld World { get; } = world;
        public WorkflowDebugSessionService Service { get; } = service;
        public IWorkflowDebugSessions Sessions { get; } = new WorkflowDebugSessions(service);
        public RecordingNotifier Notifier { get; } = notifier;
        public RecordingAudit Audit { get; } = audit;
        public RecordingMetrics Metrics { get; } = metrics;
        public LoopbackBus.Endpoint? Endpoint { get; } = endpoint;
    }

    internal sealed class FakeRun
    {
        private readonly Replica holder;
        private readonly Guid sessionId;
        private readonly Guid executionId;
        private readonly bool parkAtBreakpoint;
        private readonly CancellationTokenSource externalStop = new();
        private CancellationTokenSource? nodeToken;

        public FakeRun(Replica holder, Guid sessionId, Guid executionId, bool parkAtBreakpoint)
        {
            this.holder = holder;
            this.sessionId = sessionId;
            this.executionId = executionId;
            this.parkAtBreakpoint = parkAtBreakpoint;
        }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? LetItEnd { get; set; }

        public bool IgnoresCancellation { get; set; }

        public bool SessionExistedWhenReleased { get; private set; }

        public bool NodeTokenCancelled => nodeToken?.IsCancellationRequested == true;

        public Task Walk { get; private set; } = Task.CompletedTask;

        public void Start() => Walk = Task.Run(WalkAsync);

        public void Stop() => externalStop.Cancel();

        private async Task WalkAsync()
        {
            var hook = holder.Sessions.HookFor(executionId)!;
            nodeToken = CancellationTokenSource.CreateLinkedTokenSource(externalStop.Token, hook.KillToken);
            var label = WorkflowRunStatusLabels.Succeeded;
            try
            {
                if (parkAtBreakpoint)
                {
                    await hook.OnNodeAsync(
                        "n1", new Dictionary<string, object?>(),
                        new Dictionary<string, IReadOnlyDictionary<string, object?>>(), nodeToken.Token);
                }

                Entered.TrySetResult();
                if (IgnoresCancellation)
                    await LetItEnd!.Task;
                else
                    await Task.Delay(Timeout.Infinite, nodeToken.Token);
                if (nodeToken.IsCancellationRequested)
                    label = WorkflowRunStatusLabels.Cancelled;
            }
            catch (OperationCanceledException)
            {
                label = WorkflowRunStatusLabels.Cancelled;
            }
            finally
            {
                Entered.TrySetResult();
                if (LetItEnd is { } gate)
                    await gate.Task;
                SessionExistedWhenReleased = await holder.Service.GetAsync(sessionId) is not null;
                await holder.Sessions.ReleaseHookAsync(executionId, label);
            }
        }
    }

    private sealed class NoSync : DbConnectionInterceptor
    {
        public static readonly NoSync Instance = new();

        private static async Task ApplyAsync(DbConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=OFF;";
            await command.ExecuteNonQueryAsync();
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            ApplyAsync(connection).GetAwaiter().GetResult();

        public override Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) =>
            ApplyAsync(connection);
    }

    internal sealed class LoopbackBus
    {
        private readonly List<Endpoint> endpoints = [];

        public bool DropAcks { get; set; }

        public TimeSpan? Latency { get; set; }

        public Endpoint Join(string name)
        {
            var endpoint = new Endpoint(this, name);
            lock (endpoints) endpoints.Add(endpoint);
            return endpoint;
        }

        internal sealed class Endpoint(LoopbackBus bus, string name) : IWorkflowClusterSignal
        {
            private readonly List<(string Channel, Func<Guid, string?, CancellationToken, Task> Handler)> handlers = [];

            public string Name { get; } = name;

            public bool Down { get; set; }

            public List<(string Channel, Guid Id, string? Payload)> Published { get; } = [];

            public async Task PublishAsync(
                string channel, Guid id, string? payload = null, CancellationToken cancellationToken = default)
            {
                lock (Published) Published.Add((channel, id, payload));
                if (bus.DropAcks && channel == new WorkflowClusterSignalChannels().DebugCommandAcks)
                    return;

                Endpoint[] peers;
                lock (bus.endpoints) peers = bus.endpoints.Where(e => e != this && !e.Down).ToArray();
                foreach (var peer in peers)
                {
                    (string Channel, Func<Guid, string?, CancellationToken, Task> Handler)[] heard;
                    lock (peer.handlers) heard = peer.handlers.Where(h => h.Channel == channel).ToArray();
                    foreach (var subscribed in heard)
                    {
                        if (bus.Latency is { } latency)
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(latency);
                                await subscribed.Handler(id, payload, CancellationToken.None);
                            });
                        else
                            await subscribed.Handler(id, payload, CancellationToken.None);
                    }
                }
            }

            public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
            {
                var entry = (channel, handler);
                lock (handlers) handlers.Add(entry);
                return new Unsubscribe(() => { lock (handlers) handlers.Remove(entry); });
            }

            private sealed class Unsubscribe(Action stop) : IDisposable
            {
                public void Dispose() => stop();
            }
        }
    }

    internal sealed class RecordingNotifier : IWorkflowDebugNotifier
    {
        public TaskCompletionSource Paused { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<Guid, Task>? OnPaused { get; set; }

        public List<(Guid SessionId, string Status)> Completed { get; } = [];

        public async Task NotifyPausedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default)
        {
            if (OnPaused is { } react) await react(sessionId);
            Paused.TrySetResult();
        }

        public Task NotifyResumedAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task NotifySnapshotCapturedAsync(Guid sessionId, string snapshotJson, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task NotifyCompletedAsync(Guid sessionId, string status, CancellationToken cancellationToken = default)
        {
            lock (Completed) Completed.Add((sessionId, status));
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingAudit : IWorkflowAuditSink
    {
        private readonly List<string> kinds = [];

        public IReadOnlyList<string> Kinds
        {
            get { lock (kinds) return kinds.ToArray(); }
        }

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (kinds) kinds.Add(auditEvent.Kind);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingMetrics : IWorkflowMetrics
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
        private readonly Dictionary<string, byte[]> store = [];

        public Task PutBytesAsync(string key, byte[] bytes, CancellationToken cancellationToken = default)
        {
            lock (store) store[key] = bytes;
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (store) return Task.FromResult(store.TryGetValue(key, out var bytes) ? bytes : null);
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            lock (store) store.Remove(key);
            return Task.CompletedTask;
        }
    }
}
