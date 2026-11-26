using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class EditorHostScopeTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

    private static string Text(int step) =>
        $$$"""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"u{{{step}}}","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"u{{{step}}}","mappings":{}}]}""";

    public static readonly string[] Setups =
    [
        "workflow and versions", "added version", "modified current version", "tracked draft, modified current version",
        "modified run log", "modified workflow", "added run state", "stale after a peer's publish, with a modified run log",
    ];

    public static readonly string[] Doors = ["settings", "write", "gate"];

    public static IEnumerable<object[]> Cases => from setup in Setups from door in Doors select new object[] { setup, door };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_door_keeps_every_entry_the_host_tracked_and_saves_what_it_staged(string setup, string door)
    {
        using var db = new SqliteEditorDatabase();
        var (id, current, draft) = await ShipAsync(db);
        var logId = Guid.NewGuid();
        await db.ReadAsync(c =>
        {
            c.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(logId, null, id, current, null, WorkflowExecutionStatus.Running, SqliteEditorDatabase.Clock.GetUtcNow()));
            return c.SaveChangesAsync();
        });

        await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            await StageAsync(db, context, setup, id, current, draft, logId);
            var staged = context.ChangeTracker.Entries().Select(e => new Staged(e.Entity, e.State, Changed(e))).ToList();
            var sent = door == "write" ? Text(9) : (await TextsAsync(db, id)).Definition;

            await sp.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, id,
                new UpdateWorkflowRequest("Renamed", null, sent, AutoPublish: door == "gate"));

            foreach (var entry in staged)
            {
                var tracked = context.Entry(entry.Entity);
                tracked.State.Should().Be(EntityState.Unchanged, $"{entry.Entity.GetType().Name} ({entry.State} before the door) is still tracked and saved");
                var stored = await tracked.GetDatabaseValuesAsync();
                stored.Should().NotBeNull();
                foreach (var property in tracked.Metadata.GetProperties().Where(p => !MovedByTheDoorsStatements.Contains(p.Name)))
                    tracked.CurrentValues[property].Should().Be(stored![property], $"{tracked.Metadata.ClrType.Name}.{property.Name} holds its row");
                foreach (var (name, value) in entry.Changed)
                    stored![name].Should().Be(value, $"the host's staged {tracked.Metadata.ClrType.Name}.{name} was saved");
            }

            return 0;
        });
    }

    [Fact]
    public async Task A_run_log_staged_for_a_version_whose_row_is_gone_fails_the_door_rather_than_vanish()
    {
        using var db = new SqliteEditorDatabase();
        var (id, _, draft) = await ShipAsync(db);

        await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            await context.WorkflowVersions.SingleAsync(v => v.Id == draft);
            context.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(Guid.NewGuid(), null, id, draft, null, WorkflowExecutionStatus.Running, SqliteEditorDatabase.Clock.GetUtcNow()));
            await db.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, Valid, AutoPublish: false)));
            await db.ReadAsync(c => c.WorkflowVersions.Where(v => v.Id == draft).ExecuteDeleteAsync());

            var door = () => sp.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, id, new UpdateWorkflowRequest("Renamed", null, Valid, AutoPublish: false));

            await door.Should().ThrowAsync<DbUpdateException>();
            return 0;
        });
        (await db.ReadAsync(c => c.Workflows.AsNoTracking().SingleAsync(w => w.Id == id))).Name.Should().Be("Ship");
    }

    [Fact]
    public async Task A_door_leaves_the_hosts_copies_of_another_workflow_as_it_read_them()
    {
        using var db = new SqliteEditorDatabase();
        var (id, _, _) = await ShipAsync(db);
        var (other, _, otherDraft) = await ShipAsync(db);

        var (workflow, draft) = await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            var copy = await context.Workflows.Include(w => w.DraftVersion).SingleAsync(w => w.Id == other);
            await db.EditAsync(e => e.UpdateAsync(Owner, other, new UpdateWorkflowRequest("Peer", null, Text(5), AutoPublish: false)));
            await sp.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, id, new UpdateWorkflowRequest("Renamed", null, Text(0), AutoPublish: false));
            return (copy.Name, copy.DraftVersion!.DefinitionJson);
        });

        (workflow, draft).Should().Be(("Ship", Text(0)));
        (await db.ReadAsync(c => c.WorkflowVersions.AsNoTracking().SingleAsync(v => v.Id == otherDraft))).DefinitionJson.Should().Be(Text(5));
    }

    public static readonly string[] Refusals = ["an unreadable definition", "an unknown mode"];

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public async Task A_door_that_refuses_in_its_own_transaction_clears_every_entry_the_host_tracked(string refusal)
    {
        using var db = new SqliteEditorDatabase();
        var (id, _, _) = await ShipAsync(db);
        var (other, otherCurrent, _) = await ShipAsync(db);
        var logId = Guid.NewGuid();
        await db.ReadAsync(c =>
        {
            c.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(logId, null, other, otherCurrent, null, WorkflowExecutionStatus.Running, SqliteEditorDatabase.Clock.GetUtcNow()));
            return c.SaveChangesAsync();
        });

        var saved = await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            await context.Workflows.Include(w => w.CurrentVersion).Include(w => w.DraftVersion).SingleAsync(w => w.Id == id);
            (await context.WorkflowExecutionLogs.SingleAsync(l => l.Id == logId)).Complete(WorkflowExecutionStatus.Succeeded, SqliteEditorDatabase.Clock.GetUtcNow());
            var request = refusal == "an unknown mode"
                ? new UpdateWorkflowRequest("Renamed", null, Valid, Mode: "Nope")
                : new UpdateWorkflowRequest("Renamed", null, "{not json");

            var door = () => sp.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, id, request);

            if (refusal == "an unknown mode")
                await door.Should().ThrowAsync<InvalidWorkflowEditRequestException>();
            else
                await door.Should().ThrowAsync<InvalidWorkflowDefinitionException>();
            context.ChangeTracker.Entries().Should().BeEmpty();
            return await context.SaveChangesAsync();
        });

        saved.Should().Be(0);
        (await db.ReadAsync(c => c.WorkflowExecutionLogs.AsNoTracking().SingleAsync(l => l.Id == logId))).Status.Should().Be(WorkflowExecutionStatus.Running);
    }

    public static IEnumerable<object[]> RefusalCases => Refusals.Select(r => new object[] { r });

    [Fact]
    public async Task A_delete_door_deletes_the_workflows_own_tracked_entries_and_saves_the_hosts_others()
    {
        using var db = new SqliteEditorDatabase();
        var (id, current, _) = await ShipAsync(db);
        var (other, otherCurrent, _) = await ShipAsync(db);
        var (ownLog, otherLog) = (Guid.NewGuid(), Guid.NewGuid());
        var now = SqliteEditorDatabase.Clock.GetUtcNow();
        await db.ReadAsync(c =>
        {
            c.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(ownLog, null, id, current, null, WorkflowExecutionStatus.Running, now));
            c.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(otherLog, null, other, otherCurrent, null, WorkflowExecutionStatus.Running, now));
            return c.SaveChangesAsync();
        });

        await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            var own = await context.WorkflowExecutionLogs.SingleAsync(l => l.Id == ownLog);
            own.Complete(WorkflowExecutionStatus.Succeeded, now);
            var others = await context.WorkflowExecutionLogs.SingleAsync(l => l.Id == otherLog);
            others.Complete(WorkflowExecutionStatus.Succeeded, now);

            await sp.GetRequiredService<IWorkflowEditor>().DeleteAsync(Owner, id);

            context.Entry(own).State.Should().Be(EntityState.Detached);
            context.Entry(others).State.Should().Be(EntityState.Unchanged);
            return 0;
        });

        (await db.ReadAsync(c => c.WorkflowExecutionLogs.AsNoTracking().Where(l => l.Id == ownLog).CountAsync())).Should().Be(0);
        (await db.ReadAsync(c => c.WorkflowExecutionLogs.AsNoTracking().SingleAsync(l => l.Id == otherLog))).Status.Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    public static readonly string[] PromotingDoors = ["gate", "publish", "rollback"];

    public static IEnumerable<object[]> PromotingDoorCases => PromotingDoors.Select(d => new object[] { d });

    [Theory]
    [MemberData(nameof(PromotingDoorCases))]
    public async Task What_a_doors_statements_set_reaches_the_hosts_copies_at_the_next_update_doors_read(string door)
    {
        using var db = new SqliteEditorDatabase();
        var (id, _, _) = await ShipAsync(db);
        var draftText = (await TextsAsync(db, id)).Definition;
        if (door == "rollback")
            await db.EditAsync(e => e.PublishAsync(Owner, id));

        await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            var editor = sp.GetRequiredService<IWorkflowEditor>();
            await context.Workflows.Include(w => w.CurrentVersion).Include(w => w.DraftVersion).SingleAsync(w => w.Id == id);
            var copies = context.ChangeTracker.Entries().ToList();

            _ = door switch
            {
                "gate" => await editor.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, draftText)),
                "publish" => await editor.PublishAsync(Owner, id),
                _ => await editor.RollbackAsync(Owner, id, 1),
            };

            var stale = new List<string>();
            foreach (var copy in copies)
            {
                var row = await copy.GetDatabaseValuesAsync();
                stale.AddRange(copy.Metadata.GetProperties().Where(p => !Equals(copy.CurrentValues[p], row![p]))
                    .Select(p => $"{copy.Metadata.ClrType.Name}.{p.Name}"));
            }
            stale.Should().NotBeEmpty().And.OnlyContain(name => MovedByTheDoorsStatements.Contains(name.Split('.', 2)[1]));

            await editor.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, (await TextsAsync(db, id)).Definition, AutoPublish: false));
            foreach (var copy in copies)
            {
                var row = await copy.GetDatabaseValuesAsync();
                foreach (var property in copy.Metadata.GetProperties())
                    copy.CurrentValues[property].Should().Be(row![property], $"{copy.Metadata.ClrType.Name}.{property.Name} was read again");
            }

            return 0;
        });
    }

    public static IEnumerable<object[]> StagedVersionColumns =>
        new[] { nameof(WorkflowVersion.VersionNumber), nameof(WorkflowVersion.CreatedByUserId), nameof(WorkflowVersion.CreatedAtUtc),
            nameof(WorkflowVersion.ChangeMessage), nameof(WorkflowVersion.IsPublished), nameof(WorkflowVersion.PublishedAtUtc) }
            .Select(name => new object[] { name });

    [Fact]
    public async Task The_staged_columns_are_every_column_of_a_version_but_its_keys_and_documents()
    {
        using var db = new SqliteEditorDatabase();
        var columns = await db.ReadAsync(c => Task.FromResult(c.Model.FindEntityType(typeof(WorkflowVersion))!.GetProperties()
            .Where(p => !p.IsKey() && !p.IsForeignKey() && !Documents.Contains(p.Name)).Select(p => p.Name).ToList()));

        columns.Should().BeEquivalentTo(StagedVersionColumns.Select(row => (string)row[0]));
    }

    [Theory]
    [MemberData(nameof(StagedVersionColumns))]
    public async Task A_write_into_the_draft_saves_what_the_host_staged_on_its_other_columns(string column)
    {
        using var db = new SqliteEditorDatabase();
        var (id, _, draft) = await ShipAsync(db);

        var staged = await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            var copy = context.Entry(await context.WorkflowVersions.SingleAsync(v => v.Id == draft));
            var property = copy.Property(column);
            property.CurrentValue = property.CurrentValue switch
            {
                int number => number + 40,
                Guid => Guid.NewGuid(),
                DateTimeOffset at => at.AddHours(3),
                bool flag => !flag,
                _ when property.Metadata.ClrType == typeof(DateTimeOffset?) => SqliteEditorDatabase.Clock.GetUtcNow().AddHours(5),
                _ => "staged by the host",
            };
            var value = property.CurrentValue;

            await sp.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, Text(9), AutoPublish: false));

            copy.State.Should().Be(EntityState.Unchanged);
            copy.Entity.DefinitionJson.Should().Be(Text(9));
            return value;
        });

        var row = await db.ReadAsync(c => c.WorkflowVersions.AsNoTracking().SingleAsync(v => v.Id == draft));
        row.DefinitionJson.Should().Be(Text(9));
        typeof(WorkflowVersion).GetProperty(column)!.GetValue(row).Should().Be(staged, $"the host's staged {column} was saved");
    }

    private static readonly HashSet<string> Documents =
        [nameof(WorkflowVersion.DefinitionJson), nameof(WorkflowVersion.InputParametersJson), nameof(WorkflowVersion.OutputParametersJson)];

    public static IEnumerable<object[]> TrackedCopyCases =>
        from door in new[] { "settings", "write", "gate", "delete" }
        from tracked in new[] { "", "workflow", "current", "draft", "workflow,current", "workflow,current,draft", "current,draft" }
        select new object[] { door, tracked };

    [Theory]
    [MemberData(nameof(TrackedCopyCases))]
    public async Task A_door_reads_each_unchanged_copy_once_and_nothing_more_on_a_fresh_scope(string door, string tracked)
    {
        var fresh = await CommandsOfAsync(door, "");
        var copies = tracked.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

        (await CommandsOfAsync(door, tracked)).Should().Be(fresh + copies, $"a {door} door on a scope tracking [{tracked}] reads each copy once");
    }

    private static async Task<int> CommandsOfAsync(string door, string tracked)
    {
        using var db = new SqliteEditorDatabase();
        var (id, current, draft) = await ShipAsync(db);
        var draftText = (await TextsAsync(db, id)).Definition;

        return await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            foreach (var copy in tracked.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                _ = copy switch
                {
                    "workflow" => (object)await context.Workflows.SingleAsync(w => w.Id == id),
                    "current" => await context.WorkflowVersions.SingleAsync(v => v.Id == current),
                    _ => await context.WorkflowVersions.SingleAsync(v => v.Id == draft),
                };
            }

            var editor = sp.GetRequiredService<IWorkflowEditor>();
            var before = db.Interleave.Commands.Executed;
            _ = door switch
            {
                "settings" => await editor.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Renamed", null, draftText, AutoPublish: false)),
                "write" => await editor.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, Text(9), AutoPublish: false)),
                "gate" => await editor.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Ship", null, draftText)),
                _ => await editor.DeleteAsync(Owner, id),
            };
            return db.Interleave.Commands.Executed - before;
        });
    }

    private static readonly HashSet<string> MovedByTheDoorsStatements =
        [nameof(Workflow.Revision), nameof(Workflow.CurrentVersionId), nameof(Workflow.DraftVersionId), nameof(Workflow.UpdatedAtUtc),
         nameof(WorkflowVersion.IsPublished), nameof(WorkflowVersion.PublishedAtUtc)];

    private sealed record Staged(object Entity, EntityState State, IReadOnlyList<(string Name, object? Value)> Changed);

    private static IReadOnlyList<(string, object?)> Changed(EntityEntry entry) => entry.State switch
    {
        EntityState.Added => entry.Metadata.GetProperties().Select(p => (p.Name, entry.CurrentValues[p])).ToList(),
        EntityState.Modified => entry.Properties.Where(p => p.IsModified).Select(p => (p.Metadata.Name, p.CurrentValue)).ToList(),
        _ => [],
    };

    private static async Task StageAsync(SqliteEditorDatabase db, WorkflowDbContext context, string setup, Guid id, Guid current, Guid draft, Guid logId)
    {
        var now = SqliteEditorDatabase.Clock.GetUtcNow();
        switch (setup)
        {
            case "workflow and versions":
                await context.Workflows.Include(w => w.CurrentVersion).Include(w => w.DraftVersion).SingleAsync(w => w.Id == id);
                break;
            case "added version":
                await context.Workflows.Include(w => w.CurrentVersion).SingleAsync(w => w.Id == id);
                context.WorkflowVersions.Add(new WorkflowVersion(Guid.NewGuid(), id, 9, Text(90), null, null, Owner, now));
                break;
            case "modified current version":
                (await context.WorkflowVersions.Include(v => v.Workflow).SingleAsync(v => v.Id == current)).UpdateDefinition(Text(77), null, null);
                break;
            case "tracked draft, modified current version":
                await context.WorkflowVersions.SingleAsync(v => v.Id == draft);
                (await context.WorkflowVersions.Include(v => v.Workflow).SingleAsync(v => v.Id == current)).UpdateDefinition(Text(78), null, null);
                break;
            case "modified run log":
                await context.Workflows.SingleAsync(w => w.Id == id);
                (await context.WorkflowExecutionLogs.SingleAsync(l => l.Id == logId)).Complete(WorkflowExecutionStatus.Succeeded, now);
                break;
            case "modified workflow":
                (await context.Workflows.Include(w => w.CurrentVersion).Include(w => w.DraftVersion).SingleAsync(w => w.Id == id)).SetWaitPollIntervalSeconds(7, now);
                break;
            case "added run state":
                await context.Workflows.SingleAsync(w => w.Id == id);
                context.WorkflowExecutionStates.Add(new WorkflowExecutionState(Guid.NewGuid(), id, 1, "Sleeping", "{}", "manual", null, now, null));
                break;
            default:
                await context.Workflows.Include(w => w.CurrentVersion).Include(w => w.DraftVersion).SingleAsync(w => w.Id == id);
                (await context.WorkflowExecutionLogs.SingleAsync(l => l.Id == logId)).Complete(WorkflowExecutionStatus.Succeeded, now);
                await db.EditAsync(e => e.PublishAsync(Owner, id));
                break;
        }
    }

    private static async Task<(Guid Id, Guid Current, Guid Draft)> ShipAsync(SqliteEditorDatabase db)
    {
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        var draft = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Text(0), AutoPublish: false)));
        return (created.Workflow.Id, created.VersionId!.Value, draft.VersionId!.Value);
    }

    private static Task<(string Definition, string? Inputs, string? Outputs)> TextsAsync(SqliteEditorDatabase db, Guid id) =>
        db.ReadAsync(async c =>
        {
            var w = await c.Workflows.AsNoTracking().Include(x => x.CurrentVersion).Include(x => x.DraftVersion).SingleAsync(x => x.Id == id);
            var v = w.DraftVersion ?? w.CurrentVersion!;
            return (v.DefinitionJson, v.InputParametersJson, v.OutputParametersJson);
        });
}
