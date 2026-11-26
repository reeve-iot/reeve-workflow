using System.Data.Common;
using System.Transactions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class CompilationSweepTests : IDisposable
{
    private readonly SqliteWorkflowDatabase database = new(onFile: true);

    public void Dispose() => database.Dispose();

    private WorkflowCompilationService Service(IWorkflowBlocks blocks) =>
        new(blocks, database.Services.GetRequiredService<IWorkflowRunScopeFactory>());

    [Fact]
    public async Task Every_workflows_current_version_is_listed_once_and_no_other_version()
    {
        var withHistory = Seed(published: 2, draft: false);
        var withDraft = Seed(published: 1, draft: true);
        Seed(published: 0, draft: true);
        Seed(published: 0, draft: false);
        var plain = Seed(published: 1, draft: false);

        var listed = await ToListAsync(new EfCurrentWorkflowVersions(ScopeFactory(), pageSize: 2).ReadAsync(default));

        listed.Select(v => v.Version.Id).Should().BeEquivalentTo([withHistory.Current, withDraft.Current, plain.Current]);
        listed.Should().OnlyHaveUniqueItems(v => v.Version.Id);
        listed.Should().OnlyContain(v => v.WorkflowId == v.Version.WorkflowId);
    }

    [Fact]
    public async Task A_listed_version_carries_its_three_documents()
    {
        var seeded = Seed(published: 1, draft: false, inputs: """[{"name":"a","type":"Int"}]""");

        var listed = (await ToListAsync(new EfCurrentWorkflowVersions(ScopeFactory()).ReadAsync(default))).Single();

        listed.WorkflowId.Should().Be(seeded.Workflow);
        listed.Version.Id.Should().Be(seeded.Current!.Value);
        listed.Version.DefinitionJson.Should().Be(Definition("noop"));
        listed.Version.InputParametersJson.Should().Be("""[{"name":"a","type":"Int"}]""");
        listed.Version.OutputParametersJson.Should().BeNull();
    }

    [Fact]
    public async Task The_listing_reads_outside_a_transaction_scope_its_caller_opened()
    {
        var commands = new AmbientCommands();
        using var watched = new SqliteWorkflowDatabase(onFile: true, context: options => options.AddInterceptors(commands));
        SeedInto(watched, published: 1);
        commands.Reset();

        List<CurrentWorkflowVersion> listed;
        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            listed = await ToListAsync(new EfCurrentWorkflowVersions(watched.Services.GetRequiredService<IServiceScopeFactory>()).ReadAsync(default));

        listed.Should().ContainSingle();
        commands.Commands.Should().Be(1);
        commands.InsideAnAmbientTransaction.Should().Be(0);
    }

    [Fact]
    public async Task Two_workflows_naming_one_current_version_are_listed_for_each_and_swept_once()
    {
        var owner = Seed(published: 1, draft: false);
        Guid borrower;
        using (var scope = database.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var other = new Workflow(Guid.NewGuid(), "borrower", null, Guid.NewGuid(), DateTimeOffset.UtcNow);
            db.Workflows.Add(other);
            db.SaveChanges();
            other.SetCurrentVersion(db.WorkflowVersions.Single(v => v.Id == owner.Current), DateTimeOffset.UtcNow);
            db.SaveChanges();
            borrower = other.Id;
        }

        var store = database.Services.GetRequiredService<IWorkflowCompilationStore>();
        var sweeper = new WorkflowCompilationSweeper(Service(new Blocks()), store);
        var report = await sweeper.SweepAsync(new EfCurrentWorkflowVersions(ScopeFactory()).ReadAsync(default), default);

        report.Current.Should().ContainSingle().Which.WorkflowVersionId.Should().Be(owner.Current!.Value);
        report.Shared.Should().ContainSingle().Which.WorkflowIds.Should().BeEquivalentTo([owner.Workflow, borrower]);
        report.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_draft_published_between_the_listing_and_the_retention_keeps_its_verdict()
    {
        var seeded = Seed(published: 1, draft: true);
        var store = database.Services.GetRequiredService<IWorkflowCompilationStore>();
        var service = Service(new Blocks());
        var draft = CompilationKey.Of(await service.EnsureAsync(Read(seeded.Draft!.Value), null, default));
        await AgeEveryRowAsync(DateTimeOffset.UtcNow.AddDays(-90));
        var sweeper = new WorkflowCompilationSweeper(service, store) { Retention = TimeSpan.FromDays(30) };
        var report = await sweeper.SweepAsync(new EfCurrentWorkflowVersions(ScopeFactory()).ReadAsync(default), default);

        PublishDraft(seeded.Workflow);
        var deleted = await sweeper.DeleteStaleAsync(report, default);

        deleted.Should().Be(0);
        (await store.FindAsync(draft.WorkflowVersionId, draft.DefinitionDigest, draft.DependencyHash, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_workflow_first_published_behind_the_listings_page_is_left_to_the_next_sweep()
    {
        var ids = await InTheDatabasesOrderAsync([.. Enumerable.Range(0, 3).Select(_ => Seed(published: 0, draft: true).Workflow)]);
        PublishDraft(ids[1]);
        PublishDraft(ids[2]);
        var source = new EfCurrentWorkflowVersions(ScopeFactory(), pageSize: 1);

        var listed = new List<Guid>();
        await using (var reading = source.ReadAsync(default).GetAsyncEnumerator())
        {
            (await reading.MoveNextAsync()).Should().BeTrue();
            listed.Add(reading.Current.WorkflowId);
            PublishDraft(ids[0]);
            while (await reading.MoveNextAsync())
                listed.Add(reading.Current.WorkflowId);
        }

        listed.Should().Equal(ids[1], ids[2]);
        (await ToListAsync(source.ReadAsync(default))).Select(v => v.WorkflowId).Should().Equal(ids);
    }

    [Fact]
    public async Task A_replica_on_another_catalogue_deletes_this_replicas_old_current_row_which_costs_a_recompile()
    {
        var seeded = Seed(published: 1, draft: false);
        var store = database.Services.GetRequiredService<IWorkflowCompilationStore>();
        var before = Service(new Blocks());
        var after = Service(new Blocks(noopHasAPort: true));
        var mine = await before.EnsureAsync(Read(seeded.Current!.Value), null, default);
        await AgeEveryRowAsync(DateTimeOffset.UtcNow.AddDays(-90));

        var theirs = new WorkflowCompilationSweeper(after, store) { Retention = TimeSpan.FromDays(30) };
        var deleted = await theirs.DeleteStaleAsync(
            await theirs.SweepAsync(new EfCurrentWorkflowVersions(ScopeFactory()).ReadAsync(default), default), default);

        deleted.Should().Be(1);
        (await store.FindAsync(mine.WorkflowVersionId, mine.DefinitionDigest, mine.DependencyHash, default)).Should().BeNull();
        var recompiled = await Service(new Blocks())
            .EnsureAsync(Read(seeded.Current!.Value), null, default);
        recompiled.Should().BeEquivalentTo(mine, options => options.Excluding(v => v.CompiledAtUtc));
        (await store.FindAsync(mine.WorkflowVersionId, mine.DefinitionDigest, mine.DependencyHash, default)).Should().NotBeNull();
    }

    [Fact]
    public void A_page_size_that_is_not_positive_is_refused()
    {
        var building = () => new EfCurrentWorkflowVersions(ScopeFactory(), pageSize: 0);

        building.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("pageSize");
    }

    [Fact]
    public async Task A_sweep_and_its_retention_keep_each_current_row_and_delete_what_is_no_versions_current_verdict()
    {
        var a = Seed(published: 1, draft: false, block: "noop");
        var b = Seed(published: 1, draft: false, block: "other");
        var store = database.Services.GetRequiredService<IWorkflowCompilationStore>();
        var sweeper = new WorkflowCompilationSweeper(Service(new Blocks()), store)
        {
            Retention = TimeSpan.FromDays(1),
        };
        var source = new EfCurrentWorkflowVersions(ScopeFactory());
        var first = await sweeper.SweepAsync(source.ReadAsync(default), default);
        first.Current.Select(k => k.DependencyHash).Distinct().Should().HaveCount(2);

        var old = DateTimeOffset.UtcNow.AddDays(-90);
        var gone = Guid.NewGuid();
        await store.SaveAsync(Row(a.Current, "digest-earlier", "hash-earlier", old), default);
        await store.SaveAsync(Row(gone, "digest-a", "hash-a", old), default);
        await AgeEveryRowAsync(old);

        var report = await sweeper.SweepAsync(source.ReadAsync(default), default);
        var deleted = await sweeper.DeleteStaleAsync(report, default);

        deleted.Should().Be(2, "a's row under an earlier key, and the row of a version that does not exist");
        (await KeysAsync()).Should().BeEquivalentTo(first.Current);
        report.Current.Should().Equal(first.Current);
    }

    private (Guid Workflow, Guid? Current, Guid? Draft) Seed(int published, bool draft, string block = "noop", string? inputs = null) =>
        SeedInto(database, published, draft, block, inputs);

    private static (Guid Workflow, Guid? Current, Guid? Draft) SeedInto(
        SqliteWorkflowDatabase database, int published, bool draft = false, string block = "noop", string? inputs = null)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(Guid.NewGuid(), "sweep", null, Guid.NewGuid(), now);
        db.Workflows.Add(workflow);
        db.SaveChanges();

        WorkflowVersion? current = null;
        for (var number = 1; number <= published; number++)
        {
            current = new WorkflowVersion(Guid.NewGuid(), workflow.Id, number, Definition(block), inputs, null, Guid.NewGuid(), now, isPublished: true);
            db.WorkflowVersions.Add(current);
        }

        WorkflowVersion? drafted = null;
        if (draft)
        {
            drafted = new WorkflowVersion(Guid.NewGuid(), workflow.Id, published + 1, Definition(block), inputs, null, Guid.NewGuid(), now);
            db.WorkflowVersions.Add(drafted);
            workflow.SetDraftVersion(drafted, now);
        }

        workflow.SetCurrentVersion(current, now);
        db.SaveChanges();
        return (workflow.Id, current?.Id, drafted?.Id);
    }

    private void PublishDraft(Guid workflowId)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = db.Workflows.Include(w => w.DraftVersion).Single(w => w.Id == workflowId);
        var draft = workflow.DraftVersion!;
        draft.Publish(DateTimeOffset.UtcNow);
        workflow.SetCurrentVersion(draft, DateTimeOffset.UtcNow);
        workflow.SetDraftVersion(null, DateTimeOffset.UtcNow);
        db.SaveChanges();
    }

    private WorkflowVersion Read(Guid versionId)
    {
        using var scope = database.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowVersions.AsNoTracking().Single(v => v.Id == versionId);
    }

    private async Task<Guid[]> InTheDatabasesOrderAsync(Guid[] ids)
    {
        using var scope = database.Services.CreateScope();
        return [.. await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows
            .Where(w => ids.Contains(w.Id)).OrderBy(w => w.Id).Select(w => w.Id).ToListAsync()];
    }

    private static string Definition(string block) =>
        $$"""{"nodes":[{"id":"start","blockTypeId":"{{WorkflowGraphHelpers.WorkflowInputBlockTypeId}}","position":{"x":0,"y":0},"configuration":{} },{"id":"n","blockTypeId":"{{block}}","position":{"x":0,"y":0},"configuration":{} }],"edges":[]}""";

    private static CompilationVerdict Row(Guid? versionId, string digest, string hash, DateTimeOffset at) =>
        new(versionId!.Value, digest, hash, true, [], [], DependencyFingerprint.CompilerVersion, at);

    private async Task AgeEveryRowAsync(DateTimeOffset at)
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        foreach (var row in await db.WorkflowCompilations.ToListAsync())
            row.CompiledAtUtc = at;
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<CompilationKey>> KeysAsync()
    {
        using var scope = database.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowCompilations
            .Select(r => new CompilationKey(r.WorkflowVersionId, r.DefinitionDigest, r.DependencyHash)).ToListAsync();
    }

    private IServiceScopeFactory ScopeFactory() => database.Services.GetRequiredService<IServiceScopeFactory>();

    private static async Task<List<CurrentWorkflowVersion>> ToListAsync(IAsyncEnumerable<CurrentWorkflowVersion> versions)
    {
        var list = new List<CurrentWorkflowVersion>();
        await foreach (var version in versions)
            list.Add(version);
        return list;
    }

    private sealed class Blocks(bool noopHasAPort = false) : IWorkflowBlocks
    {
        private readonly Dictionary<string, IWorkflowBlockFactory> byId = new(StringComparer.OrdinalIgnoreCase)
        {
            ["noop"] = new Block("noop", noopHasAPort),
            ["other"] = new Block("other"),
        };

        public IWorkflowBlockFactory? Find(string blockTypeId) => byId.GetValueOrDefault(blockTypeId);

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => byId.Values;
    }

    private sealed class Block(string id, bool hasAPort = false) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => id;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => hasAPort ? [new BlockPortDefinition("x", "x", PortValueType.Int, false)] : [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class AmbientCommands : DbCommandInterceptor
    {
        public int Commands;
        public int InsideAnAmbientTransaction;

        public void Reset()
        {
            Interlocked.Exchange(ref Commands, 0);
            Interlocked.Exchange(ref InsideAnAmbientTransaction, 0);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Commands);
            if (Transaction.Current is not null)
                Interlocked.Increment(ref InsideAnAmbientTransaction);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
