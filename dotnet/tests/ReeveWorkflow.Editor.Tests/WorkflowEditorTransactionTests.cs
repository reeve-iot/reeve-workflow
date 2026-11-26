using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class WorkflowEditorTransactionTests
{
    private const string Definition = "{\"nodes\":[],\"edges\":[]}";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Create_and_update_hand_the_transaction_the_metadata_they_will_store()
    {
        var recorder = new IntentRecorder();
        using var db = new SqliteEditorDatabase(options => options.Transaction.Use(
            sp => new IntentAwareTransaction(sp.GetRequiredService<WorkflowDbContext>(), recorder), ServiceLifetime.Scoped));

        var created = await db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition, HostMetadata: new Dictionary<string, string> { ["folderId"] = "a" })));
        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Definition, HostMetadata: new Dictionary<string, string> { ["team"] = "b" })));

        recorder.Intents.Select(i => i.WorkflowId).Should().Equal(null, created.Workflow.Id);
        recorder.Intents.Select(i => i.HostMetadata).Should().BeEquivalentTo(new IReadOnlyDictionary<string, string>[]
        {
            new Dictionary<string, string> { ["folderId"] = "a" }, new Dictionary<string, string> { ["team"] = "b" },
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task An_empty_or_absent_map_reaches_the_transaction_as_the_null_that_is_stored()
    {
        var recorder = new IntentRecorder();
        using var db = new SqliteEditorDatabase(options => options.Transaction.Use(
            sp => new IntentAwareTransaction(sp.GetRequiredService<WorkflowDbContext>(), recorder), ServiceLifetime.Scoped));

        var created = await db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition, HostMetadata: new Dictionary<string, string>())));
        await db.EditAsync(editor => editor.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Definition)));

        recorder.Intents.Select(i => i.HostMetadata).Should().AllSatisfy(metadata => metadata.Should().BeNull());
    }

    [Fact]
    public async Task A_write_the_metadata_refuses_never_reaches_the_transaction()
    {
        var recorder = new IntentRecorder();
        using var db = new SqliteEditorDatabase(options => options.Transaction.Use(
            sp => new IntentAwareTransaction(sp.GetRequiredService<WorkflowDbContext>(), recorder), ServiceLifetime.Scoped));

        var refused = await Record.ExceptionAsync(() => db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition, HostMetadata: new Dictionary<string, string> { ["1bad"] = "a" }))));

        refused.Should().BeOfType<InvalidWorkflowEditRequestException>().Which.Code.Should().Be("host-metadata-invalid");
        recorder.Intents.Should().BeEmpty();
    }

    [Theory]
    [InlineData("7", null)]
    [InlineData("1", null)]
    [InlineData(null, "2")]
    [InlineData("Sometimes", null)]
    public async Task A_concurrency_setting_is_read_by_its_name_only(string? mode, string? overflow)
    {
        using var db = new SqliteEditorDatabase();

        var refused = await Record.ExceptionAsync(() => db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition, Mode: mode, OverflowBehavior: overflow))));

        refused.Should().BeOfType<InvalidWorkflowEditRequestException>()
            .Which.Message.Should().Contain(mode is null ? "is not an overflow behaviour" : "is not a workflow mode");
    }

    [Fact]
    public async Task A_concurrency_name_is_read_in_any_case()
    {
        using var db = new SqliteEditorDatabase();

        var created = await db.EditAsync(editor => editor.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, Definition, Mode: "queued", OverflowBehavior: "LOG")));

        created.Workflow.Mode.Should().Be(nameof(WorkflowMode.Queued));
        created.Workflow.OverflowBehavior.Should().Be(nameof(WorkflowOverflowBehavior.Log));
    }

    [Fact]
    public async Task Both_members_of_the_default_join_the_host_transaction_alike_and_neither_commits_it()
    {
        using var db = new SqliteEditorDatabase();

        foreach (var withIntent in new[] { false, true })
        {
            var stillOpen = await db.ReadAsync(async context =>
            {
                var seam = new WorkflowContextEditorTransaction(context);
                await using var host = await context.Database.BeginTransactionAsync();
                seam.JoinsAnotherTransaction.Should().BeTrue();

                await using (var scope = withIntent
                    ? await seam.BeginAsync(new WorkflowWriteIntent(null, null))
                    : await seam.BeginAsync())
                {
                    await scope.CommitAsync();
                }

                return context.Database.CurrentTransaction is not null;
            });

            stillOpen.Should().BeTrue($"the host's transaction is still open after a scope that joined it (intent: {withIntent})");
        }
    }

    private sealed class IntentRecorder
    {
        public List<WorkflowWriteIntent> Intents { get; } = [];
    }

    private sealed class IntentAwareTransaction(WorkflowDbContext context, IntentRecorder recorder)
        : IWorkflowEditorTransaction
    {
        private readonly WorkflowContextEditorTransaction inner = new(context);

        public bool JoinsAnotherTransaction => false;

        public Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default) =>
            inner.BeginAsync(cancellationToken);

        public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default)
        {
            recorder.Intents.Add(intent);
            return inner.BeginAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task The_default_commits_its_own_transaction_so_a_host_with_no_opinion_needs_none()
    {
        using var db = new SqliteEditorDatabase();

        var created = await db.EditAsync(editor =>
            editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        (await db.ReadAsync(context => context.Workflows.AnyAsync(w => w.Id == created.Workflow.Id)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task A_host_transaction_that_never_commits_leaves_no_row_behind()
    {
        using var db = new SqliteEditorDatabase(editor =>
            editor.Transaction.Use<RollingBackTransaction>(ServiceLifetime.Scoped));

        var created = await db.EditAsync(e =>
            e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        (await db.ReadAsync(context => context.Workflows.AnyAsync(w => w.Id == created.Workflow.Id)))
            .Should().BeFalse(
                because: "the write went into the scope the seam handed it and nothing else committed; "
                         + "a library that opened one of its own would have made this row durable");
    }

    [Fact]
    public async Task A_write_inside_a_transaction_the_host_already_opened_joins_it_rather_than_failing()
    {
        using var db = new SqliteEditorDatabase();

        var id = await db.InScopeAsync(async services =>
        {
            var context = services.GetRequiredService<WorkflowDbContext>();
            await using var outer = await context.Database.BeginTransactionAsync();

            var created = await services.GetRequiredService<IWorkflowEditor>()
                .CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition));

            await outer.RollbackAsync();
            return created.Workflow.Id;
        });

        (await db.ReadAsync(context => context.Workflows.AnyAsync(w => w.Id == id)))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_delete_that_fails_after_purging_the_run_rows_leaves_them_where_they_were()
    {
        using var db = new SqliteEditorDatabase(editor =>
            editor.Transaction.Use<RollingBackTransaction>(ServiceLifetime.Scoped));
        var created = await db.EditAsync(e =>
            e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        await db.InScopeAsync(async services =>
        {
            var context = services.GetRequiredService<WorkflowDbContext>();
            var now = SqliteEditorDatabase.Clock.GetUtcNow();
            var seeded = new Core.Model.Workflow(created.Workflow.Id, "Ship", null, Owner, now);
            var seededVersion = new Core.Model.WorkflowVersion(
                Guid.NewGuid(), created.Workflow.Id, 1, Definition, null, null, Owner, now, isPublished: true);
            context.Workflows.Add(seeded);
            await context.SaveChangesAsync();
            context.WorkflowVersions.Add(seededVersion);
            seeded.SetCurrentVersion(seededVersion, now);
            context.WorkflowExecutionStates.Add(new Core.Model.WorkflowExecutionState(
                Guid.NewGuid(), created.Workflow.Id, workflowVersion: 1,
                nameof(Core.Hosting.WorkflowRunStateStatus.Completed), "{}", null, null,
                SqliteEditorDatabase.Clock.GetUtcNow()));
            return await context.SaveChangesAsync();
        });

        await db.EditAsync(e => e.DeleteAsync(Owner, created.Workflow.Id));

        (await db.ReadAsync(context => context.Workflows.AnyAsync(w => w.Id == created.Workflow.Id)))
            .Should().BeTrue();
        (await db.ReadAsync(context => context.WorkflowExecutionStates
            .CountAsync(e => e.WorkflowId == created.Workflow.Id))).Should().Be(1,
            because: "ExecuteDelete issues its own statement, and a purge outside the seam's transaction "
                     + "would have taken the run row even as the workflow it belonged to survived");
    }

    private sealed class RollingBackTransaction(WorkflowDbContext context) : IWorkflowEditorTransaction
    {
        public bool JoinsAnotherTransaction => true;

        public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default) =>
            BeginAsync(cancellationToken);

        public async Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default) =>
            new Scope(await context.Database.BeginTransactionAsync(cancellationToken));

        private sealed class Scope(
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) : IWorkflowEditorTransactionScope
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}

public sealed class WorkflowEditorStagingTests
{
    private const string Definition = "{\"nodes\":[],\"edges\":[]}";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task A_write_the_seam_refuses_stages_nothing_the_host_could_commit_for_it()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(e =>
            e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition)));

        using var refusing = new SqliteEditorDatabase(o => o.Transaction.Use<RefusingTransaction>(ServiceLifetime.Scoped));
        await Seed(refusing, created.Workflow.Id);

        var wrote = await refusing.InScopeAsync(async services =>
        {
            var editor = services.GetRequiredService<IWorkflowEditor>();
            try
            {
                await editor.UpdateAsync(Owner, created.Workflow.Id,
                    new UpdateWorkflowRequest("RENAMED BY A REFUSED WRITE", null, "{\"nodes\":[{\"id\":\"a\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}"));
            }
            catch (InvalidOperationException)
            {
            }

            return await services.GetRequiredService<WorkflowDbContext>().SaveChangesAsync();
        });

        wrote.Should().Be(0);
        (await refusing.ReadAsync(c => c.Workflows.SingleAsync(w => w.Id == created.Workflow.Id)))
            .Name.Should().Be("Ship");
    }

    [Fact]
    public async Task A_rollback_leaves_the_context_describing_the_rows_that_are_really_there()
    {
        using var db = new SqliteEditorDatabase();

        var (id, found) = await db.InScopeAsync(async services =>
        {
            var context = services.GetRequiredService<WorkflowDbContext>();
            await using var outer = await context.Database.BeginTransactionAsync();
            var created = await services.GetRequiredService<IWorkflowEditor>()
                .CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Definition));
            await outer.RollbackAsync();
            context.ChangeTracker.Clear();
            return (created.Workflow.Id, await context.Workflows.AnyAsync(w => w.Id == created.Workflow.Id));
        });

        found.Should().BeFalse();
        (await db.ReadAsync(c => c.Workflows.AnyAsync(w => w.Id == id))).Should().BeFalse();
    }

    [Fact]
    public async Task The_default_scope_forgets_a_write_it_rolled_back()
    {
        using var db = new SqliteEditorDatabase();

        var (id, tracked) = await db.InScopeAsync(async services =>
        {
            var context = services.GetRequiredService<WorkflowDbContext>();
            var editor = services.GetRequiredService<IWorkflowEditor>();
            var workflowId = Guid.NewGuid();
            try
            {
                await editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, "[]"));
            }
            catch (InvalidWorkflowDefinitionException)
            {
            }

            return (workflowId, context.ChangeTracker.Entries().Count());
        });

        tracked.Should().Be(0, because: "a refused write leaves nothing for the host's next save to carry");
        (await db.ReadAsync(c => c.Workflows.AnyAsync(w => w.Id == id))).Should().BeFalse();
    }

    [Fact]
    public async Task A_run_that_starts_while_the_delete_is_in_flight_is_refused_rather_than_destroyed()
    {
        using var db = new SqliteEditorDatabase(o =>
            o.Transaction.Use<StartsARunTransaction>(ServiceLifetime.Scoped));
        var id = Guid.NewGuid();
        await Seed(db, id, withVersion: true);

        var delete = () => db.EditAsync(e => e.DeleteAsync(Owner, id));

        await delete.Should().ThrowAsync<WorkflowHasActiveExecutionsException>();
        (await db.ReadAsync(c => c.Workflows.AnyAsync(w => w.Id == id))).Should().BeTrue();
        (await db.ReadAsync(c => c.WorkflowExecutionStates.CountAsync(e => e.WorkflowId == id)))
            .Should().Be(1, because: "the run the guard did not see must survive the purge that follows it");
    }

    [Fact]
    public async Task Two_writers_allocating_one_version_number_get_a_conflict_and_not_a_fault()
    {
        using var db = new SqliteEditorDatabase();
        var id = Guid.NewGuid();
        await Seed(db, id, withVersion: true);

        var update = () => db.InScopeAsync(async services =>
        {
            var context = services.GetRequiredService<WorkflowDbContext>();

            void TakeTheNumber(object? sender, SavingChangesEventArgs args)
            {
                context.SavingChanges -= TakeTheNumber;
                var pending = context.ChangeTracker.Entries<Core.Model.WorkflowVersion>()
                    .Single(e => e.State == EntityState.Added).Entity;
                context.Database.ExecuteSqlRaw(
                    "insert into workflow_versions (id, workflow_id, version_number, definition_json, "
                    + "created_by_user_id, created_at_utc, is_published) values ({0}, {1}, {2}, {3}, {4}, {5}, 0)",
                    Guid.NewGuid(), id, pending.VersionNumber, "{}", Owner, SqliteEditorDatabase.Clock.GetUtcNow());
            }

            context.SavingChanges += TakeTheNumber;
            return await services.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, id,
                new UpdateWorkflowRequest("Ship", null, "{\"nodes\":[{\"id\":\"a\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}"));
        });

        (await update.Should().ThrowAsync<WorkflowVersionConflictException>())
            .Which.VersionNumber.Should().Be(2);
    }

    private static Task<int> Seed(SqliteEditorDatabase db, Guid workflowId, bool withVersion = false) =>
        db.InScopeAsync(async services =>
        {
            var context = services.GetRequiredService<WorkflowDbContext>();
            var now = SqliteEditorDatabase.Clock.GetUtcNow();
            var workflow = new Core.Model.Workflow(
                workflowId, "Ship", null, Owner, now);
            context.Workflows.Add(workflow);
            await context.SaveChangesAsync();

            if (withVersion)
            {
                var version = new Core.Model.WorkflowVersion(
                    Guid.NewGuid(), workflowId, 1, Definition, null, null, Owner, now, isPublished: true);
                context.WorkflowVersions.Add(version);
                workflow.SetCurrentVersion(version, now);
            }

            return await context.SaveChangesAsync();
        });

    private sealed class RefusingTransaction : IWorkflowEditorTransaction
    {
        public bool JoinsAnotherTransaction => false;

        public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default) =>
            BeginAsync(cancellationToken);

        public Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("this host refuses the write");
    }

    private sealed class StartsARunTransaction(WorkflowDbContext context) : IWorkflowEditorTransaction
    {
        public bool JoinsAnotherTransaction => context.Database.CurrentTransaction is not null;

        public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default) =>
            BeginAsync(cancellationToken);

        public async Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default)
        {
            var workflowId = await context.Workflows.Select(w => w.Id).FirstAsync(cancellationToken);
            context.WorkflowExecutionStates.Add(new Core.Model.WorkflowExecutionState(
                Guid.NewGuid(), workflowId, 1, nameof(Core.Hosting.WorkflowRunStateStatus.Running),
                "{}", null, null, SqliteEditorDatabase.Clock.GetUtcNow()));
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();

            return await new WorkflowContextEditorTransaction(context).BeginAsync(cancellationToken);
        }
    }
}
