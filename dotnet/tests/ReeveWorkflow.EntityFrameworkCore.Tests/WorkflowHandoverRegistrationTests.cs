using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class WorkflowHandoverRegistrationTests : IDisposable
{
    private const string Hold = "test.hold";
    private const string Pass = "test.pass";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string path = Path.Combine(Path.GetTempPath(), $"handover-{Guid.NewGuid():N}.db");
    private readonly Lifetime lifetime = new();
    private readonly HoldingBlocks blocks = new();

    public WorkflowHandoverRegistrationTests()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var wal = connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = path, Pooling = false, DefaultTimeout = 30
    }.ToString();

    [Theory]
    [InlineData(true, null, "Running", null)]
    [InlineData(false, null, "Cancelled", "desk-1")]
    [InlineData(false, true, "Running", null)]
    [InlineData(true, false, "Cancelled", "desk-1")]
    public async Task A_run_the_host_stops_is_handed_over_exactly_where_a_sweep_will_pick_it_up(
        bool recovery, bool? handsOver, string status, string? owner)
    {
        using var provider = Build(recovery, engine => engine.HandOverRunsOnStop = handsOver);
        var (workflowId, _) = SeedWorkflow(provider);
        using var caller = new CancellationTokenSource();

        var run = provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null, cancellationToken: caller.Token);
        await blocks.Entered.Task.WaitAsync(Patience);
        lifetime.StopApplication();
        await caller.CancelAsync();
        await run.Awaiting(r => r).Should().ThrowAsync<OperationCanceledException>();

        var row = StateRows(provider).Should().ContainSingle().Subject;
        row.Status.Should().Be(status, (handsOver ?? recovery)
            ? "a stop leaves the row for whichever sweep the host named"
            : "with no sweep nothing would ever pick a released row up, so it is ended as before");
        row.OwnedByReplicaId.Should().Be(owner, owner is null
            ? "a released row names nobody, or no sweep would know it was free"
            : "the run was ended rather than handed over, and an ending says which replica made it");
    }

    [Fact]
    public async Task A_running_row_is_renewed_at_the_interval_the_host_registered_while_its_block_runs()
    {
        var clock = new TimerClock(DateTimeOffset.UtcNow);
        using var provider = Build(recovery: false, engine => engine.RunHeartbeatInterval = TimeSpan.FromMilliseconds(50), clock: clock);
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var armedBeforeTheRun = clock.Armed;
        var (workflowId, _) = SeedWorkflow(provider);
        using var caller = new CancellationTokenSource();

        var run = engine.ExecuteAsync(workflowId, null, null, cancellationToken: caller.Token);
        await blocks.Entered.Task.WaitAsync(Patience);
        await clock.WaitForArmedAsync(armedBeforeTheRun + 2, Patience);

        clock.NextWait.Should().Be(TimeSpan.FromMilliseconds(50), "the beat waits the interval the host registered, not the default");
        await AssertABeatRenewsTheRowAsync(provider, clock);
        await caller.CancelAsync();
        await run.Awaiting(r => r).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_resumed_segment_is_renewed_at_the_interval_the_host_registered_too()
    {
        var clock = new TimerClock(DateTimeOffset.UtcNow);
        using var provider = Build(recovery: false, engine => engine.RunHeartbeatInterval = TimeSpan.FromMilliseconds(50), clock: clock);
        provider.GetRequiredService<IWorkflowEngine>();
        var resumer = provider.GetRequiredService<WorkflowRunResumer>();
        var armedBeforeTheRun = clock.Armed;
        var (workflowId, versionId) = SeedWorkflow(provider);
        var context = SeedParkedRun(provider, workflowId, versionId);
        using var caller = new CancellationTokenSource();

        var resume = resumer.ResumeFromSuspensionAsync(context, caller.Token);
        await blocks.Entered.Task.WaitAsync(Patience);
        await clock.WaitForArmedAsync(armedBeforeTheRun + 2, Patience);

        clock.NextWait.Should().Be(TimeSpan.FromMilliseconds(50), "the beat waits the interval the host registered, not the default");
        await AssertABeatRenewsTheRowAsync(provider, clock);
        await caller.CancelAsync();
        await resume.WaitAsync(Patience);
    }

    private async Task AssertABeatRenewsTheRowAsync(IServiceProvider provider, TimerClock clock)
    {
        var before = StateRows(provider).Single().Version;
        var armed = clock.Armed;

        clock.Advance(TimeSpan.FromMilliseconds(50));
        await clock.WaitForArmedAsync(armed, Patience);

        StateRows(provider).Single().Version.Should().BeGreaterThan(before, "the beat renewed the row");
    }

    [Fact]
    public async Task A_host_that_turned_the_heartbeat_off_leaves_a_running_row_as_the_block_found_it()
    {
        using var provider = Build(recovery: false, engine => engine.RunHeartbeatInterval = null);
        var (workflowId, _) = SeedWorkflow(provider);
        using var caller = new CancellationTokenSource();

        var run = provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null, cancellationToken: caller.Token);
        await blocks.Entered.Task.WaitAsync(Patience);
        var entered = StateRows(provider).Single().Version;
        await Task.Delay(500);
        var afterwards = StateRows(provider).Single().Version;
        await caller.CancelAsync();
        await run.Awaiting(r => r).Should().ThrowAsync<OperationCanceledException>();

        afterwards.Should().Be(entered);
    }

    [Fact]
    public async Task A_resumed_segment_the_host_stops_is_handed_over_by_the_registered_resumer()
    {
        using var provider = Build(recovery: true);
        var (workflowId, versionId) = SeedWorkflow(provider);
        var context = SeedParkedRun(provider, workflowId, versionId);
        using var caller = new CancellationTokenSource();

        var resume = provider.GetRequiredService<WorkflowRunResumer>().ResumeFromSuspensionAsync(context, caller.Token);
        await blocks.Entered.Task.WaitAsync(Patience);
        lifetime.StopApplication();
        await caller.CancelAsync();
        await resume;

        var row = StateRows(provider).Should().ContainSingle().Subject;
        row.Status.Should().Be("Running");
        row.OwnedByReplicaId.Should().BeNull();
    }

    [Fact]
    public async Task The_registered_resumer_leaves_a_parked_run_parked_while_the_registered_engine_holds_its_workflows_place()
    {
        using var provider = Build(recovery: false);
        var (workflowId, versionId) = SeedWorkflow(provider);
        var context = SeedParkedRun(provider, workflowId, versionId);
        using var caller = new CancellationTokenSource();

        var run = provider.GetRequiredService<IWorkflowEngine>().ExecuteAsync(workflowId, null, null, cancellationToken: caller.Token);
        await blocks.Entered.Task.WaitAsync(Patience);
        var outcome = await provider.GetRequiredService<WorkflowRunResumer>()
            .ResumeFromSuspensionAsync(context, CancellationToken.None).WaitAsync(Patience);
        await caller.CancelAsync();
        await run.Awaiting(r => r).Should().ThrowAsync<OperationCanceledException>();

        outcome.Should().Be(WorkflowResumeOutcome.Deferred);
        StateRows(provider).Should().ContainSingle(row => row.Status == "Suspended");
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public async Task The_registered_resumer_waits_for_the_write_before_a_block_exactly_where_the_registered_sweep_re_enters(
        bool recovery, bool handsOver, bool refused)
    {
        using var provider = Build(recovery, engine => engine.HandOverRunsOnStop = handsOver,
            store => new WritesFailBefore(store, "hold"));
        var (workflowId, versionId) = SeedWorkflow(provider);
        var context = SeedParkedRun(provider, workflowId, versionId);
        using var caller = new CancellationTokenSource();

        var resume = provider.GetRequiredService<WorkflowRunResumer>().ResumeFromSuspensionAsync(context, caller.Token);
        if (refused)
        {
            await resume.WaitAsync(Patience);
            blocks.Entered.Task.IsCompleted.Should().BeFalse();
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionLogs.AsNoTracking()
                .Single(log => log.Id == context.ExecutionLogId).ErrorMessage.Should().Contain("checkpoint_not_written");
        }
        else
        {
            await blocks.Entered.Task.WaitAsync(Patience);
            await caller.CancelAsync();
            await resume.WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task A_run_reaches_its_ending_without_the_synchronization_context_it_was_started_from()
    {
        using var provider = Build(recovery: false);
        var (workflowId, _) = SeedWorkflow(provider);
        using var caller = new CancellationTokenSource();

        var run = StartOnADeadContext(() => provider.GetRequiredService<IWorkflowEngine>()
            .ExecuteAsync(workflowId, null, null, cancellationToken: caller.Token));
        await blocks.Entered.Task.WaitAsync(Patience);
        await caller.CancelAsync();

        await run.Awaiting(r => r.WaitAsync(Patience)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_resumed_segment_reaches_its_ending_without_the_synchronization_context_it_was_started_from()
    {
        using var provider = Build(recovery: false);
        var (workflowId, versionId) = SeedWorkflow(provider);
        var context = SeedParkedRun(provider, workflowId, versionId);
        using var caller = new CancellationTokenSource();

        var resume = StartOnADeadContext(() => provider.GetRequiredService<WorkflowRunResumer>()
            .ResumeFromSuspensionAsync(context, caller.Token));
        await blocks.Entered.Task.WaitAsync(Patience);
        await caller.CancelAsync();

        await resume.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_resumed_segment_that_never_yields_ends_without_the_synchronization_context_it_was_started_from()
    {
        using var provider = Build(recovery: false, engine => engine.RunHeartbeatInterval = null, lockProvider: new SlowReleaseLocks());
        var (workflowId, versionId) = SeedWorkflow(provider, Pass);
        var context = SeedParkedRun(provider, workflowId, versionId);

        var resume = StartOnADeadContext(() => provider.GetRequiredService<WorkflowRunResumer>()
            .ResumeFromSuspensionAsync(context, CancellationToken.None));

        await resume.WaitAsync(Patience);
    }

    private static Task<T> StartOnADeadContext<T>(Func<Task<T>> start)
    {
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DeadContext());
        try { return start(); }
        finally { SynchronizationContext.SetSynchronizationContext(before); }
    }

    private ServiceProvider Build(
        bool recovery, Action<WorkflowEngineOptions>? engine = null, Func<IWorkflowRunStateStore, IWorkflowRunStateStore>? store = null,
        IWorkflowRunLockProvider? lockProvider = null, TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IWorkflowBlocks>(blocks);
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        if (clock is not null)
            services.AddSingleton(clock);
        if (lockProvider is not null)
            services.AddSingleton(lockProvider);

        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(context => context.UseSqlite(ConnectionString));
            options.Defaults.ReplicaId = "desk-1";
        }).AddWorkflowEngine(options =>
        {
            options.Recovery.Enabled = recovery;
            engine?.Invoke(options);
        });
        if (store is not null)
        {
            var registered = services.Last(descriptor => descriptor.ServiceType == typeof(IWorkflowRunStateStore));
            services.Remove(registered);
            services.AddSingleton(sp => store((IWorkflowRunStateStore)registered.ImplementationFactory!(sp)));
        }

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        return provider;
    }

    private static List<WorkflowExecutionState> StateRows(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionStates.AsNoTracking().ToList();
    }

    private static (Guid WorkflowId, Guid VersionId) SeedWorkflow(IServiceProvider provider, string secondBlock = Hold)
    {
        var definitionJson = $$$"""
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"hold","blockTypeId":"{{{secondBlock}}}","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"hold","mappings":{}}]}
            """;
        var workflowId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(workflowId, "wf", null, ownerId, now);
        db.Workflows.Add(workflow);
        db.SaveChanges();
        var version = new WorkflowVersion(Guid.NewGuid(), workflowId, 1, definitionJson, null, null, ownerId, now, isPublished: true);
        db.WorkflowVersions.Add(version);
        db.SaveChanges();
        workflow.SetCurrentVersion(version, now);
        db.SaveChanges();
        return (workflowId, version.Id);
    }

    private static ResumeContext SeedParkedRun(IServiceProvider provider, Guid workflowId, Guid versionId)
    {
        var now = DateTimeOffset.UtcNow;
        var log = new WorkflowExecutionLog(Guid.NewGuid(), null, workflowId, versionId, null, WorkflowExecutionStatus.Running, now);
        using (var scope = provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<IWorkflowRunJournal>().OpenRunAsync(log, default).GetAwaiter().GetResult();

        var store = provider.GetRequiredService<IWorkflowRunStateStore>();
        var stateId = store.BeginAsync(new WorkflowRunStateBegin
        {
            WorkflowId = workflowId, WorkflowVersion = 1, VariablesJson = "{}", OwnedByReplicaId = "desk-1"
        }, default).GetAwaiter().GetResult();
        store.TransitionAsync(new WorkflowRunStateTransition
        {
            ExecutionId = stateId, FromStatus = WorkflowRunStateStatus.Running, ToStatus = WorkflowRunStateStatus.Suspended,
            CurrentBlockPath = "start", VariablesJson = "{}", OwnedByReplicaId = "desk-1"
        }, default).GetAwaiter().GetResult();

        return new ResumeContext
        {
            SuspensionId = Guid.NewGuid(), ExecutionLogId = log.Id, WorkflowId = workflowId, BlockId = "start",
            Kind = WorkflowSuspensionKind.WaitTemplate, OutcomeJson = "{\"outcome\":\"satisfied\"}",
            StatePayloadJson = JsonSerializer.Serialize(new WorkflowSuspensionStatePayload
            {
                VariablesJson = "{}", PreviousOutputsJson = "{}", TriggerEnvelopeJson = "{}", ExecutionStateId = stateId
            }, WorkflowGraphHelpers.JsonOptions),
            SuspendedAtUtc = now
        };
    }

    public void Dispose()
    {
        lifetime.Dispose();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            File.Delete(file);
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }

    private sealed class DeadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    private sealed class HoldingBlocks : IWorkflowBlocks
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == Hold ? new HoldFactory(this) : blockTypeId == Pass ? new PassFactory() : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [new HoldFactory(this)];
    }

    private sealed class HoldFactory(HoldingBlocks blocks) : IWorkflowBlockFactory
    {
        public string BlockTypeId => Hold;
        public string DisplayName => "Hold";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new HoldBlock(blocks);
    }

    private sealed class SlowReleaseLocks : IWorkflowRunLockProvider
    {
        public Task<IWorkflowRunLock?> TryAcquireAsync(string lockKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IWorkflowRunLock?>(new SlowReleaseLock());

        private sealed class SlowReleaseLock : IWorkflowRunLock
        {
            public Task<bool> IsValidAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public async ValueTask DisposeAsync() => await Task.Run(() => { }).ConfigureAwait(false);
        }
    }

    private sealed class PassFactory : IWorkflowBlockFactory
    {
        public string BlockTypeId => Pass;
        public string DisplayName => "Pass";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => new PassBlock();
    }

    private sealed class PassBlock : IWorkflowBlockPlugin
    {
        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    private sealed class HoldBlock(HoldingBlocks blocks) : IWorkflowBlockPlugin
    {
        public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken)
        {
            blocks.Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, object?>();
        }
    }
}
