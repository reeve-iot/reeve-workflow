using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class HostTransactionTests : IDisposable
{
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string ValidB = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{}}]}""";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly string file = Path.Combine(Path.GetTempPath(), $"host-tx-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider provider;

    public HostTransactionTests()
    {
        var connectionString = $"Data Source={file};Pooling=False";
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddScoped(_ => new HostConnection(connectionString));
        services.AddSingleton<IWorkflowBlockFactory>(new PortlessBlock("noop"));
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(connectionString));
        }).AddWorkflowEditor(options => options.Transaction.Use<BorrowingSeam>(ServiceLifetime.Scoped));
        provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task A_create_inside_a_host_transaction_saves_the_draft_defers_the_publish_and_publishes_after_the_commit()
    {
        var created = await InHostTransactionAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));

        created.PublishDeferred.Should().BeTrue();
        created.VersionPublished.Should().BeFalse();
        created.Fault.Should().BeNull();
        created.Diagnostics.Should().BeEmpty();
        var row = await RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().BeNull();
        row.DraftVersionId.Should().Be(created.VersionId);
        row.Revision.Should().Be(0);

        var published = await EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        published.VersionPublished.Should().BeTrue();
        (await RowAsync(created.Workflow.Id)).CurrentVersionId.Should().Be(created.VersionId);
    }

    [Fact]
    public async Task A_default_save_inside_a_host_transaction_saves_the_draft_and_defers_the_publish_without_a_conflict()
    {
        var created = await EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));

        var saved = await InHostTransactionAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB)));

        saved.PublishDeferred.Should().BeTrue();
        saved.VersionPublished.Should().BeFalse();
        saved.Fault.Should().BeNull();
        var row = await RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersionId.Should().Be(saved.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(ValidB);
    }

    [Fact]
    public async Task A_default_save_equal_to_the_draft_defers_inside_a_host_transaction_and_publishes_outside_one()
    {
        var created = await EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        var deferred = await InHostTransactionAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB)));

        var again = await InHostTransactionAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB)));
        var outside = await EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB)));

        again.PublishDeferred.Should().BeTrue();
        again.VersionId.Should().Be(deferred.VersionId);
        outside.VersionPublished.Should().BeTrue();
        outside.VersionId.Should().Be(deferred.VersionId);
        (await RowAsync(created.Workflow.Id)).CurrentVersionId.Should().Be(deferred.VersionId);
    }

    [Fact]
    public async Task A_draft_save_inside_a_host_transaction_defers_nothing_and_compiles_nothing()
    {
        var created = await EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));

        var saved = await InHostTransactionAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB, AutoPublish: false)));

        saved.PublishDeferred.Should().BeFalse();
        saved.VersionPublished.Should().BeFalse();
        saved.Fault.Should().BeNull();
        (await RowAsync(created.Workflow.Id)).DraftVersionId.Should().Be(saved.VersionId);
    }

    [Fact]
    public async Task A_draft_save_inside_a_host_transaction_answers_with_the_workflow_as_that_transaction_left_it()
    {
        var created = await EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        await EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, ValidB, AutoPublish: false)));

        var saved = await InHostTransactionAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Renamed", null, """{"nodes":[{"id":"x","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", AutoPublish: false)));

        (saved.Workflow.Name, saved.Fault, saved.Diagnostics.Count).Should().Be(("Renamed", (Exception?)null, 0));
    }

    [Fact]
    public async Task Publish_and_rollback_inside_a_host_transaction_are_refused_and_change_nothing()
    {
        var created = await EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));
        await EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, ValidB)));
        await EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Valid, AutoPublish: false)));
        var before = await RowAsync(created.Workflow.Id);

        var publish = () => InHostTransactionAsync(e => e.PublishAsync(Owner, created.Workflow.Id));
        var rollback = () => InHostTransactionAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        await publish.Should().ThrowAsync<WorkflowPublishInHostTransactionException>();
        await rollback.Should().ThrowAsync<WorkflowPublishInHostTransactionException>();
        var after = await RowAsync(created.Workflow.Id);
        (after.CurrentVersionId, after.DraftVersionId, after.Revision)
            .Should().Be((before.CurrentVersionId, before.DraftVersionId, before.Revision));
    }

    [Fact]
    public async Task The_librarys_own_seam_says_it_joins_a_transaction_begun_on_its_context()
    {
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var seam = new WorkflowContextEditorTransaction(context);

        var before = seam.JoinsAnotherTransaction;
        await using var outer = await context.Database.BeginTransactionAsync();

        before.Should().BeFalse();
        seam.JoinsAnotherTransaction.Should().BeTrue();
    }

    public void Dispose()
    {
        provider.Dispose();
        File.Delete(file);
    }

    private async Task<T> EditAsync<T>(Func<IWorkflowEditor, Task<T>> work)
    {
        using var scope = provider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IWorkflowEditor>());
    }

    private async Task<T> InHostTransactionAsync<T>(Func<IWorkflowEditor, Task<T>> work)
    {
        using var scope = provider.CreateScope();
        var host = scope.ServiceProvider.GetRequiredService<HostConnection>();
        host.Transaction = host.Connection.BeginTransaction();
        try
        {
            var result = await work(scope.ServiceProvider.GetRequiredService<IWorkflowEditor>());
            host.Transaction.Commit();
            return result;
        }
        finally
        {
            host.Transaction.Dispose();
            host.Transaction = null;
        }
    }

    private async Task<Workflow> RowAsync(Guid workflowId)
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows.AsNoTracking()
            .Include(w => w.CurrentVersion).Include(w => w.DraftVersion).SingleAsync(w => w.Id == workflowId);
    }

    public sealed class HostConnection(string connectionString) : IDisposable
    {
        public SqliteConnection Connection { get; } = Open(connectionString);

        public SqliteTransaction? Transaction { get; set; }

        public void Dispose() => Connection.Dispose();

        private static SqliteConnection Open(string connectionString)
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection;
        }
    }

    public sealed class BorrowingSeam(WorkflowDbContext context, HostConnection host) : IWorkflowEditorTransaction
    {
        public bool JoinsAnotherTransaction => host.Transaction is not null;

        public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default) =>
            BeginAsync(cancellationToken);

        public async Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default)
        {
            if (host.Transaction is null)
                return await new WorkflowContextEditorTransaction(context).BeginAsync(cancellationToken);

            var own = context.Database.GetConnectionString();
            context.Database.SetDbConnection(host.Connection, contextOwnsConnection: false);
            await context.Database.UseTransactionAsync(host.Transaction, cancellationToken);
            return new Scope(context, own);
        }

        private sealed class Scope(WorkflowDbContext context, string? own) : IWorkflowEditorTransactionScope
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await context.Database.UseTransactionAsync(null);
                }
                finally
                {
                    context.Database.SetDbConnection(null, contextOwnsConnection: false);
                    context.Database.SetConnectionString(own);
                }
                context.ChangeTracker.Clear();
            }
        }
    }
}
