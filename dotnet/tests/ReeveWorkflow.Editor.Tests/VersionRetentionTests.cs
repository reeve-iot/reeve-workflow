using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.History;

namespace ReeveWorkflow.Editor.Tests;

public sealed class VersionRetentionTests
{
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly FakeTimeProvider Later = new(SqliteEditorDatabase.Clock.GetUtcNow().AddDays(2));

    [Fact]
    public async Task A_superseded_version_no_run_references_is_deleted_and_no_revision_moves()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var id = await ShipAsync(db);
        await OpenRunAsync(db, id, 1);
        var revision = await RevisionAsync(db, id);
        var logger = new ListLogger<WorkflowVersionRetentionService>();

        await Retention(db, logger).CleanupUnderLeaderAsync(default);

        (await NumbersAsync(db, id)).Should().Equal(1, 3, 4);
        var row = await RowAsync(db, id);
        (row.CurrentVersion!.VersionNumber, row.DraftVersion!.VersionNumber).Should().Be((3, 4));
        (await RunsAsync(db, id)).Should().Be(1);
        row.Revision.Should().Be(revision, "a version that is neither current nor draft is not what a promote compares");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task The_newest_superseded_versions_up_to_the_count_stay_until_they_pass_the_cutoff()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var id = await ShipAsync(db, versions: 4);

        await Retention(db, maxVersions: 1, clock: SqliteEditorDatabase.Clock).CleanupUnderLeaderAsync(default);
        var byCount = await NumbersAsync(db, id);
        await Retention(db, maxVersions: 1).CleanupUnderLeaderAsync(default);

        byCount.Should().Equal(3, 4, 5);
        (await NumbersAsync(db, id)).Should().Equal(4, 5);
    }

    [Fact]
    public async Task Retention_vs_publish_a_draft_made_after_the_pointer_read_and_published_before_the_delete_stays_current()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var id = await ShipAsync(db, versions: 2, withDraft: false);
        peers.Before(Step.Versions, () => SaveAsync(db, id, Text("d")));
        peers.Before(Step.Begin, () => db.EditAsync(e => e.PublishAsync(Owner, id)));
        var logger = new ListLogger<WorkflowVersionRetentionService>();

        await Retention(db, logger).CleanupUnderLeaderAsync(default);

        peers.Fired.Should().Equal(Step.Versions, Step.Begin);
        var row = await RowAsync(db, id);
        row.CurrentVersion!.VersionNumber.Should().Be(3);
        row.CurrentVersion.DefinitionJson.Should().Be(Text("d"));
        (await NumbersAsync(db, id)).Should().Equal(2, 3);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_draft_made_after_the_pointer_read_and_saved_into_before_the_delete_keeps_the_save()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var id = await ShipAsync(db, versions: 2, withDraft: false);
        peers.Before(Step.Versions, () => SaveAsync(db, id, Text("d")));
        peers.Before(Step.Begin, () => SaveAsync(db, id, Text("e")));

        await Retention(db).CleanupUnderLeaderAsync(default);

        var row = await RowAsync(db, id);
        row.DraftVersion!.VersionNumber.Should().Be(3);
        row.DraftVersion.DefinitionJson.Should().Be(Text("e"));
        row.CurrentVersion!.VersionNumber.Should().Be(2);
    }

    [Fact]
    public async Task A_run_opened_on_a_candidate_after_the_list_keeps_the_version_and_its_history()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var id = await ShipAsync(db);
        peers.Before(Step.Begin, () => OpenRunAsync(db, id, 2));

        await Retention(db).CleanupUnderLeaderAsync(default);

        (await NumbersAsync(db, id)).Should().Equal(2, 3, 4);
        (await RunsAsync(db, id)).Should().Be(1);
    }

    [Fact]
    public async Task A_fault_in_one_workflows_delete_leaves_that_workflow_whole_and_the_others_are_swept()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var first = await ShipAsync(db);
        var second = await ShipAsync(db);
        peers.FailBefore(Step.Delete);
        var logger = new ListLogger<WorkflowVersionRetentionService>();

        await Retention(db, logger).CleanupUnderLeaderAsync(default);

        var numbers = new[] { await NumbersAsync(db, first), await NumbersAsync(db, second) };
        numbers.Should().ContainSingle(n => n.SequenceEqual(new[] { 1, 2, 3, 4 }));
        numbers.Should().ContainSingle(n => n.SequenceEqual(new[] { 3, 4 }));
        var kept = numbers[0].Count == 4 ? first : second;
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Message.Should().Contain(kept.ToString());
    }

    [Fact]
    public async Task No_retention_transaction_is_open_between_two_workflows()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var first = await ShipAsync(db);
        var second = await ShipAsync(db);
        var third = await ShipAsync(db, versions: 1, withDraft: false);
        peers.Before(Step.Begin, () => SaveAsync(db, third, Text("x")), occurrence: 2);

        await Retention(db).CleanupUnderLeaderAsync(default);

        peers.Fired.Should().Equal(Step.Begin);
        (await RowAsync(db, third)).DraftVersion!.DefinitionJson.Should().Be(Text("x"));
        (await NumbersAsync(db, first)).Should().Equal(3, 4);
        (await NumbersAsync(db, second)).Should().Equal(3, 4);
    }

    [Fact]
    public async Task A_publish_that_read_before_a_sweep_and_promotes_after_it_is_not_told_the_workflow_moved()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var id = await ShipAsync(db);
        db.Interleave.BeforeBegin(1, () => Retention(db).CleanupUnderLeaderAsync(default));

        var published = await db.EditAsync(e => e.PublishAsync(Owner, id));

        published.VersionPublished.Should().BeTrue();
        (await NumbersAsync(db, id)).Should().Equal(3, 4);
    }

    [Theory]
    [InlineData("Pending", true)]
    [InlineData("Running", true)]
    [InlineData("Sleeping", true)]
    [InlineData("Waiting", true)]
    [InlineData("Suspended", true)]
    [InlineData("Completed", false)]
    [InlineData("Failed", false)]
    [InlineData("Cancelled", false)]
    public async Task A_version_a_live_runs_manifest_pins_stays_however_old_and_a_finished_runs_does_not(string status, bool kept)
    {
        using var db = Desk(new RetentionPeers());
        var (child, pinned) = await PinnedChildAsync(db, status);
        await SaveAsync(db, child, Text("n"), autoPublish: true);

        await Defaults(db, new FakeTimeProvider(SqliteEditorDatabase.Clock.GetUtcNow().AddDays(366))).CleanupUnderLeaderAsync(default);

        (await NumbersAsync(db, child)).Should().Equal(kept ? [2, 3] : new[] { 3 });
        (await db.ReadAsync(c => c.WorkflowVersions.AnyAsync(v => v.Id == pinned))).Should().Be(kept);
    }

    [Fact]
    public async Task A_version_a_live_runs_manifest_pins_stays_however_many_versions_follow_it()
    {
        using var db = Desk(new RetentionPeers());
        var (child, _) = await PinnedChildAsync(db, "Sleeping");
        for (var n = 0; n < 51; n++)
            await SaveAsync(db, child, Text("n" + n), autoPublish: true);

        await Defaults(db, SqliteEditorDatabase.Clock).CleanupUnderLeaderAsync(default);

        (await NumbersAsync(db, child)).Should().Equal(Enumerable.Range(2, 52), "version 1 is 51st of the superseded and unpinned");
    }

    [Fact]
    public async Task A_pin_that_commits_while_the_sweep_lists_candidates_keeps_its_version()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var id = await ShipAsync(db);
        var second = await VersionIdAsync(db, id, 2);
        peers.Before(Step.Pointers, () => PinAsync(db, id, second, "Sleeping"));

        await Retention(db).CleanupUnderLeaderAsync(default);

        peers.Fired.Should().Equal(Step.Pointers);
        (await NumbersAsync(db, id)).Should().Equal(2, 3, 4);
    }

    [Fact]
    public async Task A_pin_that_commits_after_the_pins_were_read_does_not_keep_its_version()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var id = await ShipAsync(db);
        var second = await VersionIdAsync(db, id, 2);
        peers.Before(Step.Begin, () => PinAsync(db, id, second, "Sleeping"));

        await Retention(db).CleanupUnderLeaderAsync(default);

        peers.Fired.Should().Equal(Step.Begin);
        (await NumbersAsync(db, id)).Should().Equal(3, 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_live_run_with_no_manifest_or_a_json_null_one_pins_nothing_and_the_sweep_goes_on(bool jsonNull)
    {
        using var db = Desk(new RetentionPeers());
        var id = await ShipAsync(db);
        await PinAsync(db, id, null, "Sleeping", manifest: jsonNull ? "null" : null, noManifest: !jsonNull);
        var logger = new ListLogger<WorkflowVersionRetentionService>();

        await Retention(db, logger).CleanupUnderLeaderAsync(default);

        (await NumbersAsync(db, id)).Should().Equal(3, 4);
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_manifest_that_does_not_read_as_one_refuses_the_sweep_before_any_delete()
    {
        using var db = Desk(new RetentionPeers());
        var first = await ShipAsync(db);
        var second = await ShipAsync(db);
        await PinAsync(db, first, null, "Sleeping", manifest: """{"not a workflow id":"not a version id"}""");
        var logger = new ListLogger<WorkflowVersionRetentionService>();

        await Retention(db, logger).CleanupUnderLeaderAsync(default);

        (await NumbersAsync(db, first)).Should().Equal(1, 2, 3, 4);
        (await NumbersAsync(db, second)).Should().Equal(1, 2, 3, 4);
        logger.Entries.Should().ContainSingle(e => e.Level >= LogLevel.Warning).Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task A_sweep_cancelled_in_a_workflows_delete_throws_the_cancellation_logs_no_fault_and_deletes_nothing()
    {
        var peers = new RetentionPeers();
        using var db = Desk(peers);
        var first = await ShipAsync(db);
        var second = await ShipAsync(db);
        using var stop = new CancellationTokenSource();
        peers.Before(Step.Begin, () => stop.CancelAsync());
        var logger = new ListLogger<WorkflowVersionRetentionService>();

        await FluentActions.Awaiting(() => Retention(db, logger).CleanupUnderLeaderAsync(stop.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        (await NumbersAsync(db, first)).Should().Equal(1, 2, 3, 4);
        (await NumbersAsync(db, second)).Should().Equal(1, 2, 3, 4);
    }

    public static IEnumerable<object[]> OpsAndPoints() =>
        from op in Ops
        from point in new[] { "pointers", "versions", "runs", "pinned", "begin", "committed", "door begin 1", "door commit 1", "door begin 2" }
        where !(op == "run" && point.StartsWith("door", StringComparison.Ordinal)) && (point != "door begin 2" || op == "auto")
        select new object[] { op, point };

    private static readonly string[] Ops = ["publish", "save", "newdraft", "auto", "rollback", "discard", "delete", "run"];

    [Theory]
    [MemberData(nameof(OpsAndPoints))]
    public async Task Every_door_and_a_runs_opening_at_each_point_of_a_sweep_hold(string op, string point)
    {
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("RETENTION_WINDOW_ITERATIONS"), out var n) ? n : 2;
        var random = new Random((op + point).Aggregate(17, (hash, c) => hash * 31 + c));
        var violations = new List<string>();

        for (var i = 0; i < iterations; i++)
        {
            var peers = new RetentionPeers();
            using var db = Desk(peers);
            var id = await ShipAsync(db, withDraft: op != "newdraft");
            await OpenRunAsync(db, id, 1);
            var logger = new ListLogger<WorkflowVersionRetentionService>();
            var retention = Retention(db, logger);
            var door = new Door(op, random.Next(2) == 0 ? 1 : 2, Text("t" + random.Next(1_000_000)), await VersionIdAsync(db, id, 2));

            var met = false;
            if (point.StartsWith("door", StringComparison.Ordinal))
            {
                Func<Task> sweep = async () => { met = true; await retention.CleanupUnderLeaderAsync(default); };
                if (point == "door commit 1")
                    db.Interleave.AfterCommit(1, sweep);
                else
                    db.Interleave.BeforeBegin(point == "door begin 1" ? 1 : 2, sweep);
                await door.RunAsync(db, id);
            }
            else
            {
                peers.Before(Enum.Parse<Step>(point, ignoreCase: true), () => door.RunAsync(db, id));
                await retention.CleanupUnderLeaderAsync(default);
                met = peers.Fired.Count == 1;
            }
            if (!met)
                violations.Add($"#{i} {op} at {point}: the two never met");

            foreach (var violation in await ViolationsAsync(db, id, door, logger))
                violations.Add($"#{i} {op} at {point}: {violation}");
        }

        violations.Should().BeEmpty();
    }

    private static readonly HashSet<Type> Answers =
    [
        typeof(WorkflowDraftChangedException), typeof(WorkflowNotFoundException), typeof(NoDraftVersionToPublishException),
        typeof(WorkflowVersionNotFoundException), typeof(CannotRollbackToActiveVersionException),
    ];

    private static async Task<List<string>> ViolationsAsync(
        SqliteEditorDatabase db, Guid id, Door door, ListLogger<WorkflowVersionRetentionService> logger)
    {
        var found = new List<string>();
        foreach (var entry in logger.Entries.Where(e => e.Level >= LogLevel.Warning))
            found.Add($"retention logged {entry.Level}: {entry.Message} {entry.Exception?.Message}");
        if (door.Error is { } error && !(Answers.Contains(error.GetType()) || door.Op == "run" && error is DbUpdateException))
            found.Add($"{door.Op} threw {error.GetType().Name}: {error.Message}");
        if (door.Result?.Fault is { } fault)
            found.Add($"{door.Op} faulted {fault.GetType().Name}: {fault.Message}");

        var row = await db.ReadAsync(c => c.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
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

        var runs = await db.ReadAsync(c => c.WorkflowExecutionLogs.AsNoTracking().Where(e => e.WorkflowId == id)
            .Select(e => e.WorkflowVersionId).ToListAsync());
        var expected = door.Op == "run" && door.Error is null ? 2 : 1;
        if (runs.Count != expected)
            found.Add($"{runs.Count} run rows, {expected} opened");
        if (door.Op == "run" && door.Error is not null && await db.ReadAsync(c => c.WorkflowVersions.AnyAsync(v => v.Id == door.RunVersion)))
            found.Add("the run's opening failed and its version is still there");
        return found;
    }

    private sealed class Door(string op, int rollbackTo, string text, Guid runVersion)
    {
        public string Op => op;

        public WorkflowEditResult? Result { get; private set; }

        public Exception? Error { get; private set; }

        public Guid RunVersion => runVersion;

        public async Task RunAsync(SqliteEditorDatabase db, Guid id)
        {
            try
            {
                switch (op)
                {
                    case "publish":
                        Result = await db.EditAsync(e => e.PublishAsync(Owner, id));
                        break;
                    case "save" or "newdraft":
                        Result = await SaveAsync(db, id, text);
                        break;
                    case "auto":
                        Result = await SaveAsync(db, id, text, autoPublish: true);
                        break;
                    case "rollback":
                        Result = await db.EditAsync(e => e.RollbackAsync(Owner, id, rollbackTo));
                        break;
                    case "discard":
                        var current = await db.ReadAsync(c => c.Workflows.AsNoTracking().Where(w => w.Id == id)
                            .Select(w => w.CurrentVersion!.DefinitionJson).SingleAsync());
                        Result = await SaveAsync(db, id, current);
                        break;
                    case "delete":
                        Result = await db.EditAsync(e => e.DeleteAsync(Owner, id));
                        break;
                    default:
                        await OpenRunAsync(db, id, runVersion);
                        break;
                }
            }
            catch (Exception error)
            {
                Error = error;
            }
        }
    }

    private static string Text(string tag) =>
        $$$"""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"{{{tag}}}","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"{{{tag}}}","mappings":{}}]}""";

    private static SqliteEditorDatabase Desk(RetentionPeers peers) =>
        new(configureHost: host => host.ConfigureDbContext(builder => builder.AddInterceptors(peers)));

    private static WorkflowVersionRetentionService Retention(
        SqliteEditorDatabase db, ILogger<WorkflowVersionRetentionService>? logger = null, int maxVersions = 50,
        TimeProvider? clock = null) =>
        new(db.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkflowRetentionOptions { Versions = new() { RetentionDays = 1, MaxVersionsPerWorkflow = maxVersions } }),
            clock ?? Later, new SingleReplicaLeaderGate(), logger ?? new ListLogger<WorkflowVersionRetentionService>());

    private static WorkflowVersionRetentionService Defaults(SqliteEditorDatabase db, TimeProvider clock) =>
        new(db.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new WorkflowRetentionOptions()), clock,
            new SingleReplicaLeaderGate(), new ListLogger<WorkflowVersionRetentionService>());

    private static async Task<(Guid Child, Guid Pinned)> PinnedChildAsync(SqliteEditorDatabase db, string status)
    {
        var child = await ShipAsync(db, versions: 2, withDraft: false);
        var pinned = await VersionIdAsync(db, child, 2);
        await PinAsync(db, child, pinned, status);
        return (child, pinned);
    }

    private static async Task PinAsync(
        SqliteEditorDatabase db, Guid child, Guid? version, string status, string? manifest = null, bool noManifest = false)
    {
        var parent = await ShipAsync(db, versions: 1, withDraft: false);
        await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            context.WorkflowExecutionStates.Add(new WorkflowExecutionState(Guid.NewGuid(), parent, 1, status, "{}", "manual", null,
                SqliteEditorDatabase.Clock.GetUtcNow(), noManifest ? null : manifest ?? $$"""{"{{child}}":"{{version}}"}"""));
            return await context.SaveChangesAsync();
        });
    }

    private static async Task<Guid> ShipAsync(SqliteEditorDatabase db, int versions = 3, bool withDraft = true)
    {
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        var id = created.Workflow.Id;
        for (var v = 2; v <= versions; v++)
            await SaveAsync(db, id, Text("v" + v), autoPublish: true);
        if (withDraft)
            await SaveAsync(db, id, Text("draft"));
        return id;
    }

    private static Task<WorkflowEditResult> SaveAsync(SqliteEditorDatabase db, Guid id, string definition, bool autoPublish = false) =>
        db.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, definition, AutoPublish: autoPublish)));

    private static async Task OpenRunAsync(SqliteEditorDatabase db, Guid id, int versionNumber) =>
        await OpenRunAsync(db, id, await VersionIdAsync(db, id, versionNumber));

    private static Task OpenRunAsync(SqliteEditorDatabase db, Guid id, Guid versionId) =>
        db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            context.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(
                Guid.NewGuid(), null, id, versionId, null, WorkflowExecutionStatus.Running, SqliteEditorDatabase.Clock.GetUtcNow()));
            return await context.SaveChangesAsync();
        });

    private static Task<Guid> VersionIdAsync(SqliteEditorDatabase db, Guid id, int number) =>
        db.ReadAsync(c => c.WorkflowVersions.AsNoTracking().Where(v => v.WorkflowId == id && v.VersionNumber == number)
            .Select(v => v.Id).SingleAsync());

    private static Task<List<int>> NumbersAsync(SqliteEditorDatabase db, Guid id) =>
        db.ReadAsync(c => c.WorkflowVersions.Where(v => v.WorkflowId == id).OrderBy(v => v.VersionNumber)
            .Select(v => v.VersionNumber).ToListAsync());

    private static Task<int> RunsAsync(SqliteEditorDatabase db, Guid id) =>
        db.ReadAsync(c => c.WorkflowExecutionLogs.CountAsync(e => e.WorkflowId == id));

    private static Task<int> RevisionAsync(SqliteEditorDatabase db, Guid id) =>
        db.ReadAsync(c => c.Workflows.Where(w => w.Id == id).Select(w => w.Revision).SingleAsync());

    private static Task<Workflow> RowAsync(SqliteEditorDatabase db, Guid id) =>
        db.ReadAsync(c => c.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
            .SingleAsync(w => w.Id == id));
}

internal enum Step
{
    Pointers,
    Versions,
    Runs,
    Pinned,
    Begin,
    Delete,
    Committed,
}

internal sealed class RetentionPeers : DbCommandInterceptor, IDbTransactionInterceptor
{
    private readonly Dictionary<Step, (int Occurrence, Func<Task> Peer)> armed = [];
    private readonly Dictionary<Step, int> seen = [];
    private Step? failing;
    private bool running;

    public List<Step> Fired { get; } = [];

    public void Before(Step step, Func<Task> peer, int occurrence = 1) =>
        armed[step] = (seen.GetValueOrDefault(step) + occurrence, peer);

    public void FailBefore(Step step) => failing = step;

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await OnAsync(StepOf(command.CommandText));
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await OnAsync(StepOf(command.CommandText));
        return result;
    }

    public async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        await OnAsync(Step.Begin);
        return result;
    }

    public async Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
        await OnAsync(Step.Committed);

    private static Step? StepOf(string sql)
    {
        if (SqlCommandText.WithoutLeadingComments(sql).StartsWith("DELETE FROM \"workflow_versions\"", StringComparison.Ordinal))
            return Step.Delete;
        if (!sql.StartsWith("SELECT", StringComparison.Ordinal))
            return null;
        if (sql.Contains("AS \"CurrentVersionId\"", StringComparison.Ordinal) && sql.Contains("LIMIT 1", StringComparison.Ordinal))
            return Step.Pointers;
        if (sql.Contains("FROM \"workflow_versions\"", StringComparison.Ordinal) && sql.Contains("ORDER BY \"w\".\"version_number\" DESC", StringComparison.Ordinal))
            return Step.Versions;
        if (sql.StartsWith("SELECT DISTINCT", StringComparison.Ordinal) && sql.Contains("FROM \"workflow_execution_logs\"", StringComparison.Ordinal))
            return Step.Runs;
        if (sql.Contains("FROM \"workflow_executions_state\"", StringComparison.Ordinal))
            return Step.Pinned;
        return null;
    }

    private async Task OnAsync(Step? step)
    {
        if (running || step is not { } at)
            return;
        if (failing == at)
        {
            failing = null;
            throw new InvalidOperationException("A fault injected before retention's " + at + ".");
        }
        seen[at] = seen.GetValueOrDefault(at) + 1;
        if (!armed.TryGetValue(at, out var peer) || peer.Occurrence != seen[at])
            return;
        armed.Remove(at);
        Fired.Add(at);
        running = true;
        try
        {
            await peer.Peer();
        }
        finally
        {
            running = false;
        }
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
