using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;
using static ReeveWorkflow.EntityFrameworkCore.PostgreSql.PostgresJsonFunctions;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class PostgresJsonFunctionsOnPostgresTests(ClusterBackends backends)
{
    private const string Warnings = """{"a":{"b":1,"keep":true},"c":2,"list":["x","y"]}""";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Dictionary<string, string> Metadata = new() { ["folderId"] = "f1", ["color"] = "red" };

    [Fact]
    public async Task Delete_removes_a_key_from_a_string_column_and_from_host_metadata()
    {
        await using var fixture = await Fixture.StartAsync(backends, "delete");
        var warningKey = "c";
        var metadataKey = "folderId";

        await fixture.EditAsync(set => set
            .SetProperty(w => w.ImportWarningsJson, w => JsonbDelete(w.ImportWarningsJson, warningKey))
            .SetProperty(w => w.HostMetadata, w => JsonbDelete(w.HostMetadata, metadataKey)));

        var row = await fixture.ReadAsync();
        JsonIs(row.ImportWarningsJson, """{"a":{"b":1,"keep":true},"list":["x","y"]}""");
        Entries(row).Should().Equal(new Dictionary<string, string> { ["color"] = "red" }.OrderBy(pair => pair.Key));
    }

    [Fact]
    public async Task Delete_removes_a_string_element_from_an_array()
    {
        await using var fixture = await Fixture.StartAsync(backends, "delete_element");
        var path = new[] { "list" };
        var element = "x";

        await fixture.EditAsync(set => set.SetProperty(
            w => w.ImportWarningsJson, w => JsonbSet(w.ImportWarningsJson, path, JsonbDelete(JsonbObjectField(w.ImportWarningsJson, "list"), element)!)));

        JsonIs((await fixture.ReadAsync()).ImportWarningsJson, """{"a":{"b":1,"keep":true},"c":2,"list":["y"]}""");
    }

    [Fact]
    public async Task Concat_lays_a_captured_patch_over_a_string_column()
    {
        await using var fixture = await Fixture.StartAsync(backends, "concat");
        var patch = """{"c":3,"d":4}""";

        await fixture.EditAsync(set => set.SetProperty(w => w.ImportWarningsJson, w => JsonbConcat(w.ImportWarningsJson, patch)));

        JsonIs((await fixture.ReadAsync()).ImportWarningsJson, """{"a":{"b":1,"keep":true},"c":3,"d":4,"list":["x","y"]}""");
    }

    [Fact]
    public async Task Delete_path_removes_a_nested_value_and_leaves_its_siblings()
    {
        await using var fixture = await Fixture.StartAsync(backends, "delete_path");
        var path = new[] { "a", "b" };

        await fixture.EditAsync(set => set.SetProperty(w => w.ImportWarningsJson, w => JsonbDeletePath(w.ImportWarningsJson, path)));

        JsonIs((await fixture.ReadAsync()).ImportWarningsJson, """{"a":{"keep":true},"c":2,"list":["x","y"]}""");
    }

    [Fact]
    public async Task Set_places_a_captured_value_at_a_captured_path_and_keeps_everything_else()
    {
        await using var fixture = await Fixture.StartAsync(backends, "set");
        var path = new[] { "a", "b" };
        var value = "9";

        await fixture.EditAsync(set => set.SetProperty(w => w.ImportWarningsJson, w => JsonbSet(w.ImportWarningsJson, path, value)));

        JsonIs((await fixture.ReadAsync()).ImportWarningsJson, """{"a":{"b":9,"keep":true},"c":2,"list":["x","y"]}""");
    }

    [Fact]
    public async Task Null_if_empty_object_stores_no_document_when_the_last_key_is_removed_and_keeps_a_non_empty_one()
    {
        await using var fixture = await Fixture.StartAsync(backends, "null_if_empty");
        var other = await fixture.AddAsync("other", new Dictionary<string, string> { ["folderId"] = "f1" }, """{"c":2}""");

        var folderKey = "folderId";
        var warningKey = "c";

        await fixture.EditAllAsync(set => set
            .SetProperty(w => w.HostMetadata, w => NullIfEmptyObject(JsonbDelete(w.HostMetadata, folderKey)))
            .SetProperty(w => w.ImportWarningsJson, w => NullIfEmptyObject(JsonbDelete(w.ImportWarningsJson, warningKey))));

        var emptied = await fixture.ReadAsync(other);
        emptied.HostMetadata.Should().BeNull("removing the last key leaves nothing to store");
        emptied.ImportWarningsJson.Should().BeNull();
        var kept = await fixture.ReadAsync();
        Entries(kept).Should().Equal(new Dictionary<string, string> { ["color"] = "red" }.OrderBy(pair => pair.Key));
        kept.ImportWarningsJson.Should().NotBeNull();
    }

    [Fact]
    public async Task A_filtered_edit_changes_only_the_rows_the_predicate_names()
    {
        await using var fixture = await Fixture.StartAsync(backends, "filtered");
        var other = await fixture.AddAsync("other", new Dictionary<string, string> { ["folderId"] = "f2" }, Warnings);

        var key = "folderId";
        var folder = "f1";

        var changed = await fixture.Workflows(db => db.Workflows
            .Where(w => JsonbObjectFieldText(w.HostMetadata, key) == folder)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.HostMetadata, w => JsonbDelete(w.HostMetadata, key))));

        changed.Should().Be(1);
        Entries(await fixture.ReadAsync(other)).Select(pair => pair.Key).Should().Contain("folderId");
        Entries(await fixture.ReadAsync()).Select(pair => pair.Key).Should().NotContain("folderId");
    }

    [Fact]
    public async Task The_read_functions_answer_what_the_operators_would()
    {
        await using var fixture = await Fixture.StartAsync(backends, "reads");

        var field = "c";
        var missing = "nope";
        var folderKey = "folderId";
        var list = "list";
        var present = "x";
        var absent = "z";
        var contained = """{"a":{"keep":true}}""";
        var hostContained = """{"color":"red"}""";

        var row = await fixture.Workflows(async db => await db.Workflows.Where(w => w.Id == fixture.Id).Select(w => new
        {
            Field = JsonbObjectFieldText(w.ImportWarningsJson, field),
            MissingField = JsonbObjectFieldText(w.ImportWarningsJson, missing),
            HostField = JsonbObjectFieldText(w.HostMetadata, folderKey),
            Kind = JsonbTypeof(JsonbObjectField(w.ImportWarningsJson, list)),
            HostKind = JsonbTypeof(w.HostMetadata),
            HasX = JsonbContains(JsonbObjectField(w.ImportWarningsJson, list), ToJsonb(present)),
            HasZ = JsonbContains(JsonbObjectField(w.ImportWarningsJson, list), ToJsonb(absent)),
            HasLiteralX = JsonbContains(JsonbObjectField(w.ImportWarningsJson, "list"), ToJsonb("x")),
            Holds = JsonbContains(w.ImportWarningsJson, contained),
            LiteralHolds = JsonbContains(w.ImportWarningsJson, """{"c":2}"""),
            HostHolds = JsonbContains(w.HostMetadata, hostContained),
            Text = ToText(w.ImportWarningsJson)
        }).SingleAsync());

        row.Field.Should().Be("2");
        row.MissingField.Should().BeNull();
        row.HostField.Should().Be("f1");
        row.Kind.Should().Be("array");
        row.HostKind.Should().Be("object");
        row.HasX.Should().BeTrue();
        row.HasZ.Should().BeFalse();
        row.HasLiteralX.Should().BeTrue();
        row.LiteralHolds.Should().BeTrue();
        row.Holds.Should().BeTrue();
        row.HostHolds.Should().BeTrue();
        JsonNode.DeepEquals(JsonNode.Parse(row.Text!), JsonNode.Parse(Warnings)).Should().BeTrue();

        var matches = await fixture.Workflows(db => db.Workflows
            .Where(w => EF.Functions.ILike(ToText(w.ImportWarningsJson!),"%\"KEEP\"%")).Select(w => w.Id).ToListAsync());
        matches.Should().ContainSingle().Which.Should().Be(fixture.Id, "ILIKE over the cast text finds the key whatever its case");
    }

    [Fact]
    public async Task Comparing_a_member_with_null_selects_the_rows_that_lack_it_and_excludes_the_rest()
    {
        await using var fixture = await Fixture.StartAsync(backends, "null_comparisons");
        var jsonNull = await fixture.AddAsync("json_null", new Dictionary<string, string> { ["color"] = "red" }, """{"k":null}""");
        var valued = await fixture.AddAsync("valued", new Dictionary<string, string> { ["folderId"] = "f2" }, """{"k":"v"}""");
        var noMetadata = await fixture.AddAsync("no_metadata", new Dictionary<string, string>(), """{"k":"w"}""");
        var emptied = await fixture.AddAsync("emptied", new Dictionary<string, string> { ["folderId"] = "f1" }, "{}");
        var subject = fixture.Id;
        var key = "k";
        var folderKey = "folderId";

        async Task<Guid[]> Ids(System.Linq.Expressions.Expression<Func<Workflow, bool>> where) =>
            (await fixture.Workflows(db => db.Workflows.Where(where).Select(w => w.Id).ToArrayAsync())).Order().ToArray();
        Guid[] Of(params Guid[] ids) => ids.Order().ToArray();

        (await Ids(w => JsonbObjectFieldText(w.ImportWarningsJson, key) == null)).Should().Equal(Of(subject, jsonNull, emptied));
        (await Ids(w => JsonbObjectFieldText(w.ImportWarningsJson, key) != null)).Should().Equal(Of(valued, noMetadata));
        (await Ids(w => JsonbObjectFieldText(w.ImportWarningsJson, "k") == null)).Should().Equal(Of(subject, jsonNull, emptied));
        (await Ids(w => JsonbObjectFieldText(w.ImportWarningsJson, "k") != null)).Should().Equal(Of(valued, noMetadata));

        (await Ids(w => JsonbObjectField(w.ImportWarningsJson, key) == null)).Should().Equal(Of(subject, emptied));
        (await Ids(w => JsonbObjectField(w.ImportWarningsJson, key) != null)).Should().Equal(Of(jsonNull, valued, noMetadata));

        (await Ids(w => JsonbObjectFieldText(w.HostMetadata, folderKey) == null)).Should().Equal(Of(jsonNull, noMetadata));
        (await Ids(w => JsonbObjectFieldText(w.HostMetadata, folderKey) != null)).Should().Equal(Of(subject, valued, emptied));

        (await Ids(w => JsonbObjectFieldText(w.ImportWarningsJson, key) != "v")).Should().Equal(Of(subject, jsonNull, noMetadata, emptied));
        var defaulted = await fixture.Workflows(db => db.Workflows.Where(w => w.Id == subject || w.Id == jsonNull || w.Id == emptied || w.Id == valued)
            .Select(w => new { w.Id, Text = JsonbObjectFieldText(w.ImportWarningsJson, key) ?? "none" }).ToDictionaryAsync(row => row.Id, row => row.Text));
        defaulted.Should().Equal(new Dictionary<Guid, string> { [subject] = "none", [jsonNull] = "none", [emptied] = "none", [valued] = "v" });
        (await Ids(w => JsonbTypeof(JsonbObjectField(w.ImportWarningsJson, key)) != "null")).Should().Equal(Of(subject, valued, noMetadata, emptied),
            "a missing key is not a JSON null, so the typeof idiom now matches it; `->>` != null is the presence test");

        (await Ids(w => NullIfEmptyObject(w.ImportWarningsJson) == null)).Should().Equal(Of(emptied));
        (await Ids(w => NullIfEmptyObject(w.ImportWarningsJson) != null)).Should().Equal(Of(subject, jsonNull, valued, noMetadata));
        (await Ids(w => NullIfEmptyObject(w.HostMetadata) == null)).Should().Equal(Of(noMetadata));
        (await Ids(w => NullIfEmptyObject(w.HostMetadata) != null)).Should().Equal(Of(subject, jsonNull, valued, emptied));
    }

    [Fact]
    public async Task Strip_removes_keys_from_a_JsonDocument_column_and_touches_only_the_rows_that_had_them()
    {
        await using var fixture = await Fixture.StartAsync(backends, "document");
        await using var db = new DocumentContext(fixture.ConnectionString);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE exposure (id integer PRIMARY KEY, metadata_json jsonb);
            INSERT INTO exposure VALUES (1, '{{"isExposed":true,"label":"x","keep":1}}'), (2, '{{"keep":1}}'), (3, NULL)
            """);
        var first = "isExposed";
        var second = "label";

        var changed = await db.Rows
            .Where(r => JsonbDelete(JsonbDelete(r.Metadata, first), second) != r.Metadata)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Metadata, r => JsonbDelete(JsonbDelete(r.Metadata, first), second)));

        changed.Should().Be(1, "only the first row held either key");
        var rows = await db.Rows.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
        rows[0].Metadata!.RootElement.GetRawText().Should().NotContain("isExposed").And.NotContain("label").And.Contain("keep");
        rows[1].Metadata!.RootElement.GetRawText().Should().Contain("keep");
        rows[2].Metadata.Should().BeNull();
    }

    private static void JsonIs(string? actual, string expected)
    {
        actual.Should().NotBeNull();
        JsonNode.DeepEquals(JsonNode.Parse(actual!), JsonNode.Parse(expected)).Should().BeTrue($"{actual} should hold the same JSON as {expected}");
    }

    private static IEnumerable<KeyValuePair<string, string>> Entries(Workflow row) => row.HostMetadata!.OrderBy(pair => pair.Key);

    private sealed class DocumentRow
    {
        public int Id { get; set; }
        public JsonDocument? Metadata { get; set; }
    }

    private sealed class DocumentContext(string connectionString) : DbContext
    {
        public DbSet<DocumentRow> Rows => Set<DocumentRow>();

        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseNpgsql(connectionString);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddPostgresJsonFunctions();
            modelBuilder.Entity<DocumentRow>(row =>
            {
                row.ToTable("exposure");
                row.Property(r => r.Id).HasColumnName("id");
                row.Property(r => r.Metadata).HasColumnName("metadata_json").HasColumnType("jsonb");
            });
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;

        private Fixture(ServiceProvider services, Guid id, string connectionString)
        {
            this.services = services;
            Id = id;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public Guid Id { get; }

        public static async Task<Fixture> StartAsync(ClusterBackends backends, string name)
        {
            var connectionString = await backends.CreateDatabaseAsync($"dispatch_json_{name}");
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddWorkflows(workflows => workflows.ConfigureDbContext(context => context.UseNpgsql(connectionString))).AddPostgreSqlSchema();
            var services = collection.BuildServiceProvider();
            await using (var scope = services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();

            var fixture = new Fixture(services, Guid.NewGuid(), connectionString);
            await fixture.AddAsync("subject", Metadata, Warnings, fixture.Id);
            return fixture;
        }

        public async Task<Guid> AddAsync(string name, Dictionary<string, string> metadata, string warnings, Guid? id = null)
        {
            id ??= Guid.NewGuid();
            await Workflows(async db =>
            {
                var workflow = new Workflow(id.Value, name, null, Owner, DateTimeOffset.UtcNow, metadata);
                workflow.SetImportWarnings(warnings);
                db.Workflows.Add(workflow);
                return await db.SaveChangesAsync();
            });
            return id.Value;
        }

        public Task EditAsync(Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Workflow>> set) =>
            Workflows(db => db.Workflows.Where(w => w.Id == Id).ExecuteUpdateAsync(set));

        public Task EditAllAsync(Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<Workflow>> set) =>
            Workflows(db => db.Workflows.ExecuteUpdateAsync(set));

        public Task<Workflow> ReadAsync(Guid? id = null) =>
            Workflows(db => db.Workflows.AsNoTracking().SingleAsync(w => w.Id == (id ?? Id)));

        public async Task<T> Workflows<T>(Func<WorkflowDbContext, Task<T>> run)
        {
            await using var scope = services.CreateAsyncScope();
            return await run(scope.ServiceProvider.GetRequiredService<WorkflowDbContext>());
        }

        public ValueTask DisposeAsync() => services.DisposeAsync();
    }
}
