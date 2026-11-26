using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Editor;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.History;
using Desk = Dispatch.Host.Tests.PublishGateOnPostgresTests.Desk;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class VersionRetentionOnPostgresTests(ClusterBackends backends)
{
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly TimeProvider Later = new Fixed(DateTimeOffset.UtcNow.AddDays(2));

    [Fact]
    public async Task Retention_vs_publish_a_publish_holding_the_workflow_keeps_the_draft_it_promotes_current()
    {
        await using var desk = await DeskAsync("retention_vs_publish");
        var id = await ShipAsync(desk, versions: 2, withDraft: false);
        Guid draft = default;
        PublishGateOnPostgresTests.HeldTransaction? publish = null;
        desk.Hooks.BeforeCommand(ReadsVersions, async () =>
        {
            draft = (await SaveAsync(desk, id, Text("d"))).VersionId!.Value;
            desk.Hooks.BeforeCommand(ReadsRuns, async () => publish = await desk.HoldAsync(
                "UPDATE automation.workflows SET current_version_id = @v, draft_version_id = NULL, revision = revision + 1 WHERE id = @w",
                "UPDATE automation.workflow_versions SET is_published = true, published_at_utc = now() WHERE id = @v AND NOT is_published",
                ("w", id), ("v", draft)));
        });
        var logger = new ListLogger();

        var sweep = Task.Run(() => Retention(desk.Services, logger).CleanupUnderLeaderAsync(default));
        (await WaitsOnLockAsync(desk, sweep)).Should().BeTrue("retention waits for the publish holding the workflow");
        await publish!.CommitAsync();
        await sweep.WaitAsync(TimeSpan.FromSeconds(60));
        await publish.DisposeAsync();

        desk.Hooks.Fired.Should().Be(2);
        var row = await desk.RowAsync(id);
        row.CurrentVersionId.Should().Be(draft);
        (await NumbersAsync(desk, id)).Should().Equal(2, 3);
        logger.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_holding_the_workflow_it_bumped_keeps_the_draft_it_writes_and_nothing_deadlocks()
    {
        await using var desk = await DeskAsync("retention_vs_save");
        var id = await ShipAsync(desk, versions: 2, withDraft: false);
        Guid draft = default;
        PublishGateOnPostgresTests.HeldTransaction? save = null;
        desk.Hooks.BeforeCommand(ReadsVersions, async () =>
        {
            draft = (await SaveAsync(desk, id, Text("d"))).VersionId!.Value;
            desk.Hooks.BeforeCommand(ReadsRuns, async () => save = await desk.HoldAsync(
                "UPDATE automation.workflows SET revision = revision + 1 WHERE id = @w", "SELECT 1", ("w", id)));
        });
        var logger = new ListLogger();

        var sweep = Task.Run(() => Retention(desk.Services, logger).CleanupUnderLeaderAsync(default));
        (await WaitsOnLockAsync(desk, sweep)).Should().BeTrue("retention waits for the save holding the workflow");
        await save!.RunAsync(
            "UPDATE automation.workflow_versions SET definition_json = @d::jsonb WHERE id = @v AND NOT is_published",
            ("d", Text("e")), ("v", draft));
        await save.CommitAsync();
        await sweep.WaitAsync(TimeSpan.FromSeconds(60));
        await save.DisposeAsync();

        var row = await desk.RowAsync(id);
        row.DraftVersionId.Should().Be(draft);
        row.DraftVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(Text("e")));
        logger.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_whose_opening_insert_has_not_committed_keeps_its_version_and_its_history()
    {
        await using var desk = await DeskAsync("retention_run_in_flight");
        var id = await ShipAsync(desk);
        var referenced = await VersionIdAsync(desk, id, 2);
        await using var run = await desk.HoldAsync(
            "INSERT INTO automation.workflow_execution_logs (id, workflow_id, workflow_version_id, status, started_at_utc, attempt_number) VALUES (@l, @w, @v, 0, now(), 1)",
            "SELECT 1", ("l", Guid.NewGuid()), ("w", id), ("v", referenced));
        var logger = new ListLogger();

        var sweep = Task.Run(() => Retention(desk.Services, logger).CleanupUnderLeaderAsync(default));
        (await WaitsOnLockAsync(desk, sweep)).Should().BeTrue("retention waits for the run's opening to commit");
        await run.CommitAsync();
        await sweep.WaitAsync(TimeSpan.FromSeconds(60));

        (await NumbersAsync(desk, id)).Should().Equal(2, 3, 4);
        (await RunsAsync(desk, id)).Should().Be(1);
        logger.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_opening_that_holds_its_version_before_the_workflows_row_finishes_and_is_kept()
    {
        await using var desk = await DeskAsync("retention_run_key_order");
        var id = await ShipAsync(desk);
        var referenced = await VersionIdAsync(desk, id, 2);
        await using var run = await desk.HoldAsync(
            "SELECT 1 FROM automation.workflow_versions WHERE id = @v FOR KEY SHARE", "SELECT 1", ("v", referenced));
        var logger = new ListLogger();

        var sweep = Task.Run(() => Retention(desk.Services, logger).CleanupUnderLeaderAsync(default));
        (await WaitsOnLockAsync(desk, sweep)).Should().BeTrue("retention waits on the version the opening holds");
        await run.RunAsync("SELECT 1 FROM automation.workflows WHERE id = @w FOR KEY SHARE", ("w", id)).WaitAsync(TimeSpan.FromSeconds(30));
        await run.RunAsync(
            "INSERT INTO automation.workflow_execution_logs (id, workflow_id, workflow_version_id, status, started_at_utc, attempt_number) VALUES (@l, @w, @v, 0, now(), 1)",
            ("l", Guid.NewGuid()), ("w", id), ("v", referenced));
        await run.CommitAsync();
        await sweep.WaitAsync(TimeSpan.FromSeconds(60));

        (await NumbersAsync(desk, id)).Should().Equal(2, 3, 4);
        (await RunsAsync(desk, id)).Should().Be(1);
        logger.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_opened_after_the_list_keeps_its_version_and_its_history()
    {
        await using var desk = await DeskAsync("retention_run_after_list");
        var id = await ShipAsync(desk);
        var referenced = await VersionIdAsync(desk, id, 2);
        desk.Hooks.BeforeCommand(LocksWorkflow, () => OpenRunAsync(desk, id, referenced));

        await Retention(desk.Services).CleanupUnderLeaderAsync(default);

        desk.Hooks.Fired.Should().Be(1);
        (await NumbersAsync(desk, id)).Should().Equal(2, 3, 4);
        (await RunsAsync(desk, id)).Should().Be(1);
    }

    [Fact]
    public async Task A_run_opening_on_a_version_retention_holds_fails_on_its_foreign_key_and_leaves_no_row()
    {
        await using var desk = await DeskAsync("retention_run_while_held");
        var id = await ShipAsync(desk);
        var candidate = await VersionIdAsync(desk, id, 2);
        Task? opening = null;
        desk.Hooks.BeforeCommand(Deletes, async () =>
        {
            opening = Task.Run(() => OpenRunAsync(desk, id, candidate));
            (await WaitsOnLockAsync(desk, opening)).Should().BeTrue("the opening waits on the candidate's row lock");
        });

        await Retention(desk.Services).CleanupUnderLeaderAsync(default);

        (await opening!.Awaiting(o => o).Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        (await NumbersAsync(desk, id)).Should().Equal(3, 4);
        (await RunsAsync(desk, id)).Should().Be(0);
    }

    [Fact]
    public async Task A_database_fault_in_one_workflows_delete_rolls_that_workflow_back_and_the_others_are_swept()
    {
        await using var desk = await DeskAsync("retention_one_fault");
        var first = await ShipAsync(desk);
        var second = await ShipAsync(desk);
        await using var faulting = await desk.WithInterceptorAsync(new FirstDeleteFails());
        var logger = new ListLogger();

        await Retention(faulting, logger).CleanupUnderLeaderAsync(default);

        var numbers = new[] { await NumbersAsync(desk, first), await NumbersAsync(desk, second) };
        numbers.Should().ContainSingle(n => n.SequenceEqual(new[] { 1, 2, 3, 4 }));
        numbers.Should().ContainSingle(n => n.SequenceEqual(new[] { 3, 4 }));
        var kept = numbers[0].Count == 4 ? first : second;
        logger.Faults.Should().ContainSingle().Which.Message.Should().Contain(kept.ToString());
    }

    [Theory]
    [InlineData("age")]
    [InlineData("count")]
    public async Task A_version_a_parked_parents_manifest_pins_stays_with_the_default_options(string route)
    {
        await using var desk = await DeskAsync("retention_pinned_" + route);
        var child = await ShipAsync(desk, versions: 2, withDraft: false);
        var pinned = await VersionIdAsync(desk, child, 2);
        await PinAsync(desk, child, pinned, "Suspended");
        var republishes = route == "age" ? 1 : 51;
        for (var n = 0; n < republishes; n++)
            await SaveAsync(desk, child, Text("n" + n), autoPublish: true);
        var clock = route == "age" ? new Fixed(DateTimeOffset.UtcNow.AddDays(367)) : TimeProvider.System;
        var logger = new ListLogger();

        await new WorkflowVersionRetentionService(desk.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkflowRetentionOptions()), clock, new SingleReplicaLeaderGate(), logger).CleanupUnderLeaderAsync(default);

        (await NumbersAsync(desk, child)).Should().Equal(Enumerable.Range(2, republishes + 1), "version 1 is unpinned and goes");
        logger.Faults.Should().BeEmpty();
    }

    [Fact]
    public async Task A_publish_that_read_before_a_sweep_promotes_after_it_and_the_sweep_moves_no_revision()
    {
        await using var desk = await DeskAsync("retention_rt09");
        var id = await ShipAsync(desk);
        var draft = (await desk.RowAsync(id)).DraftVersionId!.Value;
        var before = await desk.RevisionAsync(id);
        var logger = new ListLogger();
        desk.Hooks.BeforeCommand(sql => sql.Contains("SET current_version_id", StringComparison.Ordinal),
            () => Retention(desk.Services, logger).CleanupUnderLeaderAsync(default));

        var result = await desk.EditAsync(e => e.PublishAsync(Owner, id));

        desk.Hooks.Fired.Should().Be(1);
        result.VersionPublished.Should().BeTrue();
        (await desk.RowAsync(id)).CurrentVersionId.Should().Be(draft);
        (await desk.RevisionAsync(id)).Should().Be(before + 1);
        (await desk.NumbersAsync(id)).Should().Equal(3, 4);
        logger.Faults.Should().BeEmpty();
    }

    private static readonly string[] Ops = ["publish", "save", "newdraft", "auto", "rollback", "discard", "delete", "run"];

    private static readonly string[] Points =
    [
        "pointers", "versions", "runs", "pinned", "lock workflow", "lock candidates", "delete",
        "door begin 1", "door commit 1", "door begin 2", "at once",
    ];

    public static IEnumerable<object[]> OpsAndPoints() =>
        from op in Ops
        from point in Points
        where !(op == "run" && point.StartsWith("door", StringComparison.Ordinal)) && (point != "door begin 2" || op == "auto")
        select new object[] { op, point };

    [Theory]
    [MemberData(nameof(OpsAndPoints))]
    public async Task Every_door_and_a_runs_opening_at_each_point_of_a_sweep_hold(string op, string point)
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("RETENTION_WINDOW_ITERATIONS"), out var n) ? n : 3;
        await using var desk = await DeskAsync($"retention_w_{op}_{point.Replace(' ', '_')}");
        var random = new Random((op + point).Aggregate(17, (hash, c) => hash * 31 + c));
        var violations = new List<string>();

        for (var i = 0; i < iterations; i++)
        {
            var id = await ShipAsync(desk, withDraft: op != "newdraft");
            await OpenRunAsync(desk, id, await VersionIdAsync(desk, id, 1));
            var logger = new ListLogger();
            var retention = Retention(desk.Services, logger);
            var door = new Door(op, random.Next(2) == 0 ? 1 : 2, Text("t" + random.Next(1_000_000)), await VersionIdAsync(desk, id, 2));
            Task? detached = null;
            var met = false;

            switch (point)
            {
                case "at once":
                    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var (spinA, spinB) = (random.Next(4000), random.Next(4000));
                    var sweeping = Task.Run(async () => { await start.Task; Thread.SpinWait(spinA); await retention.CleanupUnderLeaderAsync(default); });
                    var dooring = Task.Run(async () => { await start.Task; Thread.SpinWait(spinB); await door.RunAsync(desk, id); });
                    start.SetResult();
                    await Task.WhenAll(sweeping, dooring).WaitAsync(TimeSpan.FromSeconds(60));
                    met = true;
                    break;
                case "door begin 1" or "door commit 1" or "door begin 2":
                    Func<Task> sweep = async () => { met = true; await retention.CleanupUnderLeaderAsync(default); };
                    if (point == "door commit 1")
                        desk.Hooks.AfterCommit(1, sweep);
                    else
                        desk.Hooks.BeforeBegin(point == "door begin 1" ? 1 : 2, sweep);
                    await door.RunAsync(desk, id).WaitAsync(TimeSpan.FromSeconds(60));
                    break;
                default:
                    var inside = point is "lock candidates" or "delete";
                    desk.Hooks.BeforeCommand(StepAt(point), async () =>
                    {
                        met = true;
                        if (!inside)
                        {
                            await door.RunAsync(desk, id);
                            return;
                        }
                        detached = Task.Run(() => door.RunAsync(desk, id));
                        if (!await WaitsOnLockAsync(desk, detached) && (op != "run" || point == "delete"))
                            violations.Add($"#{i} {op} at {point}: it never waited for retention's lock");
                    });
                    await retention.CleanupUnderLeaderAsync(default).WaitAsync(TimeSpan.FromSeconds(60));
                    if (detached is not null)
                        await detached.WaitAsync(TimeSpan.FromSeconds(60));
                    break;
            }
            desk.Hooks.Clear();

            if (!met)
                violations.Add($"#{i} {op} at {point}: the two never met");
            foreach (var violation in await ViolationsAsync(desk, id, door, logger))
                violations.Add($"#{i} {op} at {point}: {violation}");
            await desk.ExecuteAsync("""
                DELETE FROM automation.workflow_execution_logs;
                UPDATE automation.workflows SET current_version_id = NULL, draft_version_id = NULL;
                DELETE FROM automation.workflows;
                """);
        }

        violations.Should().BeEmpty();
    }

    private static Func<string, bool> StepAt(string point) => point switch
    {
        "pointers" => ReadsPointers,
        "versions" => ReadsVersions,
        "runs" => ReadsRuns,
        "pinned" => ReadsPins,
        "lock workflow" => LocksWorkflow,
        "lock candidates" => LocksCandidates,
        _ => Deletes,
    };

    private static readonly HashSet<Type> Answers =
    [
        typeof(WorkflowDraftChangedException), typeof(WorkflowNotFoundException), typeof(NoDraftVersionToPublishException),
        typeof(WorkflowVersionNotFoundException), typeof(CannotRollbackToActiveVersionException),
    ];

    private static async Task<List<string>> ViolationsAsync(Desk desk, Guid id, Door door, ListLogger logger)
    {
        var found = new List<string>();
        foreach (var (level, message, exception) in logger.Faults)
            found.Add($"retention logged {level}: {message} {exception?.Message}");
        if (door.Error is { } error && !(Answers.Contains(error.GetType())
            || door.Op == "run" && error is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } }))
            found.Add($"{door.Op} threw {error.GetType().Name}: {error.Message}");
        if (door.Result?.Fault is { } fault)
            found.Add($"{door.Op} faulted {fault.GetType().Name}: {fault.Message}");

        var row = await desk.ReadAsync(c => c.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
            .SingleOrDefaultAsync(w => w.Id == id));
        if (row is null)
            return door.Op == "delete" && door.Error is null ? found : [.. found, "the workflow is gone and no delete answered"];

        if (row.CurrentVersionId is null)
            found.Add("the workflow lost its current version");
        var dropsDraft = door.Error is null && (door.Op is "publish" or "rollback" or "discard"
            || door.Op is "auto" && door.Result!.VersionPublished);
        if (door.Op != "newdraft" && row.DraftVersionId is null && !dropsDraft)
            found.Add("the draft pointer is null and no door dropped it");
        if (door.Op is "save" or "newdraft" or "auto" && door.Result is { VersionId: { } saved }
            && saved != row.DraftVersionId && saved != row.CurrentVersionId)
            found.Add($"{door.Op} reported version {saved}, which is neither the draft nor current");

        var runs = await RunsAsync(desk, id);
        var expected = door.Op == "run" && door.Error is null ? 2 : 1;
        if (runs != expected)
            found.Add($"{runs} run rows, {expected} opened");
        if (door.Op == "run" && door.Error is not null && await desk.ReadAsync(c => c.WorkflowVersions.AnyAsync(v => v.Id == door.RunVersion)))
            found.Add("the run's opening failed and its version is still there");
        return found;
    }

    private sealed class Door(string op, int rollbackTo, string text, Guid runVersion)
    {
        public string Op => op;

        public Guid RunVersion => runVersion;

        public WorkflowEditResult? Result { get; private set; }

        public Exception? Error { get; private set; }

        public async Task RunAsync(Desk desk, Guid id)
        {
            try
            {
                switch (op)
                {
                    case "publish":
                        Result = await desk.EditAsync(e => e.PublishAsync(Owner, id));
                        break;
                    case "save" or "newdraft":
                        Result = await SaveAsync(desk, id, text);
                        break;
                    case "auto":
                        Result = await SaveAsync(desk, id, text, autoPublish: true);
                        break;
                    case "rollback":
                        Result = await desk.EditAsync(e => e.RollbackAsync(Owner, id, rollbackTo));
                        break;
                    case "discard":
                        var current = await desk.ReadAsync(c => c.Workflows.AsNoTracking().Where(w => w.Id == id)
                            .Select(w => w.CurrentVersion!.DefinitionJson).SingleAsync());
                        Result = await SaveAsync(desk, id, current);
                        break;
                    case "delete":
                        Result = await desk.EditAsync(e => e.DeleteAsync(Owner, id));
                        break;
                    default:
                        await OpenRunAsync(desk, id, runVersion);
                        break;
                }
            }
            catch (Exception error)
            {
                Error = error;
            }
        }
    }

    private static bool ReadsPointers(string sql) => sql.Contains("AS \"CurrentVersionId\"", StringComparison.Ordinal);

    private static bool ReadsVersions(string sql) =>
        sql.Contains("FROM automation.workflow_versions", StringComparison.Ordinal) && sql.Contains("version_number DESC", StringComparison.Ordinal);

    private static bool ReadsRuns(string sql) =>
        sql.StartsWith("SELECT DISTINCT", StringComparison.Ordinal) && sql.Contains("workflow_execution_logs", StringComparison.Ordinal);

    private static bool ReadsPins(string sql) => sql.Contains("FROM automation.workflow_executions_state", StringComparison.Ordinal);

    private static bool LocksWorkflow(string sql) => sql.EndsWith("FOR NO KEY UPDATE", StringComparison.Ordinal);

    private static bool LocksCandidates(string sql) =>
        sql.EndsWith(" FOR UPDATE", StringComparison.Ordinal) && sql.Contains("workflow_versions", StringComparison.Ordinal);

    private static bool Deletes(string sql) => sql.StartsWith("DELETE FROM automation.workflow_versions", StringComparison.Ordinal);

    private static string Text(string tag) =>
        $$$"""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"{{{tag}}}","blockTypeId":"noop","position":{"x":240,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"{{{tag}}}","mappings":{}}]}""";

    private async Task<Desk> DeskAsync(string database)
    {
        var desk = new Desk(await backends.CreateDatabaseAsync("dispatch_" + database));
        await using var scope = desk.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();
        return desk;
    }

    private static WorkflowVersionRetentionService Retention(IServiceProvider services, ListLogger? logger = null) =>
        new(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkflowRetentionOptions { Versions = new() { RetentionDays = 1 } }),
            Later, new SingleReplicaLeaderGate(), logger ?? new ListLogger());

    private static async Task<Guid> ShipAsync(Desk desk, int versions = 3, bool withDraft = true)
    {
        var id = (await desk.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)))).Workflow.Id;
        for (var v = 2; v <= versions; v++)
            await SaveAsync(desk, id, Text("v" + v), autoPublish: true);
        if (withDraft)
            await SaveAsync(desk, id, Text("draft"));
        return id;
    }

    private static Task<WorkflowEditResult> SaveAsync(Desk desk, Guid id, string definition, bool autoPublish = false) =>
        desk.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, definition, AutoPublish: autoPublish)));

    private static Task OpenRunAsync(Desk desk, Guid id, Guid versionId) =>
        desk.ReadAsync(context =>
        {
            context.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(
                Guid.NewGuid(), null, id, versionId, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow));
            return context.SaveChangesAsync();
        });

    private static async Task PinAsync(Desk desk, Guid child, Guid version, string status)
    {
        var parent = await ShipAsync(desk, versions: 1, withDraft: false);
        await desk.ReadAsync(context =>
        {
            context.WorkflowExecutionStates.Add(new WorkflowExecutionState(Guid.NewGuid(), parent, 1, status, "{}", "manual", null,
                DateTimeOffset.UtcNow, $$"""{"{{child}}":"{{version}}"}"""));
            return context.SaveChangesAsync();
        });
    }

    private static Task<Guid> VersionIdAsync(Desk desk, Guid id, int number) =>
        desk.ReadAsync(c => c.WorkflowVersions.AsNoTracking().Where(v => v.WorkflowId == id && v.VersionNumber == number)
            .Select(v => v.Id).SingleAsync());

    private static Task<List<int>> NumbersAsync(Desk desk, Guid id) => desk.NumbersAsync(id);

    private static Task<int> RunsAsync(Desk desk, Guid id) =>
        desk.ReadAsync(c => c.WorkflowExecutionLogs.CountAsync(e => e.WorkflowId == id));

    private static async Task<bool> WaitsOnLockAsync(Desk desk, Task done)
    {
        await using var connection = new NpgsqlConnection(desk.ConnectionString);
        await connection.OpenAsync();
        await using var look = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", connection);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !done.IsCompleted)
        {
            if ((long)(await look.ExecuteScalarAsync())! > 0)
                return true;
            await Task.Delay(5);
        }
        return false;
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FirstDeleteFails : DbCommandInterceptor
    {
        private int fired;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM automation.workflow_versions", StringComparison.Ordinal)
                && Interlocked.Exchange(ref fired, 1) == 0)
                command.CommandText = "SELECT 1 / 0";
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ListLogger : ILogger<WorkflowVersionRetentionService>
    {
        private readonly List<(LogLevel, string, Exception?)> entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Faults
        {
            get
            {
                lock (entries)
                    return entries.Where(e => e.Item1 >= LogLevel.Warning).ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries)
                entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
