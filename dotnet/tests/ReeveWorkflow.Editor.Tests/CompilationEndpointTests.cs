using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class CompilationEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-25T12:00:00Z");

    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string ValidB = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{}}]}""";
    private const string Unknown = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"gone.block","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n1","mappings":{}}]}""";
    private const string AsksTheHost = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"noop","configuration":{"v":"expr:${host.q}"},"position":{"x":0,"y":0}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""";
    private const string AsksTheResolver = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"r","blockTypeId":"resolved","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"r","mappings":{}}]}""";

    private readonly HostCheck check = new();
    private readonly ThrowingResolver resolver = new();
    private readonly FlakyStore store = new();
    private readonly string database = $"Data Source=compilation-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection keepAlive = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        keepAlive = new SqliteConnection(database);
        await keepAlive.OpenAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Critical);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new PortlessBlock("noop"));
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new ResolvedBlock());
        builder.Services.AddSingleton<INodePortResolver>(resolver);
        builder.Services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(db => db.UseSqlite(database));
                options.Seams.ExpressionNamespaces.Add(HostNamespaceRegistration.For(check));
                options.Seams.CompilationStore.Use(sp =>
                    store.Over(new EfWorkflowCompilationStore(sp.GetRequiredService<IServiceScopeFactory>())));
            })
            .AddWorkflowEngine()
            .AddWorkflowEditor();

        app = builder.Build();
        app.Use((context, next) =>
        {
            var user = context.Request.Headers.TryGetValue("X-User", out var named) ? named.ToString() : Owner.ToString();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], "test"));
            return next(context);
        });
        app.MapWorkflowApi("api/workflows").AllowAnonymous();
        app.MapWorkflowEditorApi("api/workflows").AllowAnonymous();

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        await app.StartAsync();
        client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task The_listing_names_the_callers_current_versions_that_do_not_compile_and_no_other_users()
    {
        var mine = await SeedAsync(Owner, Unknown);
        await SeedAsync(Owner, Valid);
        await SeedAsync(Other, Unknown);
        using (var draftOnly = await client.PostAsJsonAsync("/api/workflows", new { name = "wf", definitionJson = Unknown }))
            draftOnly.StatusCode.Should().Be(HttpStatusCode.Created, "a first version that does not compile is kept as the draft");

        using var body = await GetJsonAsync("/api/workflows/compilation/invalid");

        var invalid = body.RootElement.GetProperty("invalid");
        invalid.GetArrayLength().Should().Be(1, "the other user's broken workflow is not the caller's");
        var entry = invalid[0];
        (entry.GetProperty("workflowId").GetGuid(), entry.GetProperty("workflowVersionId").GetGuid(), entry.GetProperty("isValid").GetBoolean())
            .Should().Be((mine.Workflow, mine.Version, false));
        entry.GetProperty("diagnostics")[0].GetProperty("ruleId").GetString().Should().Be(CompilationRules.BlockTypeUnknown);
        body.RootElement.GetProperty("totalCount").GetInt32().Should().Be(2, "a workflow with only a draft has no current version");
        body.RootElement.GetProperty("failed").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_listing_is_paged_in_workflow_id_order_and_takes_at_most_fifty()
    {
        var seeded = new List<Guid>();
        for (var i = 0; i < 3; i++)
            seeded.Add((await SeedAsync(Owner, Unknown)).Workflow);
        seeded.Sort((a, b) => string.CompareOrdinal(a.ToString().ToUpperInvariant(), b.ToString().ToUpperInvariant()));

        using var first = await GetJsonAsync("/api/workflows/compilation/invalid?skip=0&take=2");
        using var second = await GetJsonAsync("/api/workflows/compilation/invalid?skip=2&take=2");
        using var wide = await GetJsonAsync("/api/workflows/compilation/invalid?take=1000");
        using var defaulted = await GetJsonAsync("/api/workflows/compilation/invalid");
        using var clamped = await GetJsonAsync("/api/workflows/compilation/invalid?skip=-5&take=0");

        WorkflowsOf(first).Concat(WorkflowsOf(second)).Should().Equal(seeded);
        first.RootElement.GetProperty("totalCount").GetInt32().Should().Be(3);
        wide.RootElement.GetProperty("take").GetInt32().Should().Be(50, "a cold page compiles each version on it");
        (defaulted.RootElement.GetProperty("skip").GetInt32(), defaulted.RootElement.GetProperty("take").GetInt32()).Should().Be((0, 20));
        (clamped.RootElement.GetProperty("skip").GetInt32(), clamped.RootElement.GetProperty("take").GetInt32())
            .Should().Be((0, 1), "the page echoes what it read, after clamping");
    }

    [Fact]
    public async Task A_version_whose_compile_cannot_be_answered_is_listed_under_failed_not_invalid()
    {
        var faulting = await SeedAsync(Owner, AsksTheHost);
        check.Fault = new InvalidOperationException("check down");

        using var body = await GetJsonAsync("/api/workflows/compilation/invalid");

        body.RootElement.GetProperty("invalid").GetArrayLength().Should().Be(0);
        var failed = body.RootElement.GetProperty("failed");
        (failed[0].GetProperty("workflowId").GetGuid(), failed[0].GetProperty("workflowVersionId").GetGuid())
            .Should().Be((faulting.Workflow, faulting.Version));
    }

    [Fact]
    public async Task A_version_whose_verdict_the_store_did_not_save_is_listed_under_unsaved()
    {
        var saved = await SeedAsync(Owner, Unknown);
        using (var first = await GetJsonAsync("/api/workflows/compilation/invalid"))
            first.RootElement.GetProperty("unsaved").GetArrayLength().Should().Be(0, "the store saved its verdict");
        await WithDbAsync(async db =>
            (await db.WorkflowCompilations.CountAsync(r => r.WorkflowVersionId == saved.Version)).Should().Be(1));
        var seeded = await SeedAsync(Owner, Unknown);
        store.FailsSaves = true;

        using var body = await GetJsonAsync("/api/workflows/compilation/invalid");

        var unsaved = body.RootElement.GetProperty("unsaved");
        unsaved.GetArrayLength().Should().Be(1, "the first version's verdict is answered from what was saved");
        (unsaved[0].GetProperty("workflowId").GetGuid(), unsaved[0].GetProperty("workflowVersionId").GetGuid())
            .Should().Be((seeded.Workflow, seeded.Version));
        body.RootElement.GetProperty("invalid").GetArrayLength().Should().Be(2, "the verdict is answered although it was not saved");
    }

    [Fact]
    public async Task A_save_stored_as_the_draft_answers_with_the_workflow_and_the_drafts_verdict_gives_its_diagnostics()
    {
        var created = await CreateAsync(Valid);

        using var saved = await client.PutAsJsonAsync($"/api/workflows/{created}", new { name = "wf", definitionJson = Unknown });
        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        using var workflow = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
        var draft = workflow.RootElement.GetProperty("draftVersionId").GetGuid();

        using var verdict = await GetJsonAsync($"/api/workflows/{draft}/compilation");

        (verdict.RootElement.GetProperty("workflowId").GetGuid(), verdict.RootElement.GetProperty("isValid").GetBoolean())
            .Should().Be((created, false));
        verdict.RootElement.GetProperty("diagnostics")[0].GetProperty("ruleId").GetString()
            .Should().Be(CompilationRules.BlockTypeUnknown);
    }

    [Fact]
    public async Task A_version_of_another_users_workflow_is_not_found_as_one_that_does_not_exist_is()
    {
        var theirs = await SeedAsync(Other, Valid);

        using var foreign = await client.GetAsync($"/api/workflows/{theirs.Version}/compilation");
        using var absent = await client.GetAsync($"/api/workflows/{Guid.NewGuid()}/compilation");
        using var asOwner = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/{theirs.Version}/compilation");
        asOwner.Headers.Add("X-User", Other.ToString());
        using var own = await client.SendAsync(asOwner);

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync()).Should().Be(await absent.Content.ReadAsStringAsync());
        own.StatusCode.Should().Be(HttpStatusCode.OK, "its owner reads it");
    }

    [Fact]
    public async Task A_verdict_that_cannot_be_answered_is_a_503_with_retry_after_and_not_the_faults_message()
    {
        var seeded = await SeedAsync(Owner, AsksTheHost);
        check.Fault = new InvalidOperationException("check down");

        using var response = await client.GetAsync($"/api/workflows/{seeded.Version}/compilation");

        await ShouldBeUnansweredAsync(response);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("resolver")]
    public async Task A_publish_whose_compile_cannot_be_answered_is_a_503_with_retry_after_never_a_400(string fault)
    {
        var created = await CreateAsync(Valid);
        Break(fault);
        using (var draft = await client.PutAsJsonAsync($"/api/workflows/{created}",
                   new { name = "wf", definitionJson = fault == "check" ? AsksTheHost : AsksTheResolver, autoPublish = false }))
            draft.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await client.PostAsync($"/api/workflows/{created}/publish", null);

        await ShouldBeUnansweredAsync(response);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("resolver")]
    public async Task A_rollback_whose_compile_cannot_be_answered_is_a_503_with_retry_after(string fault)
    {
        var created = await CreateAsync(fault == "check" ? AsksTheHost : AsksTheResolver);
        using (var second = await client.PutAsJsonAsync($"/api/workflows/{created}", new { name = "wf", definitionJson = ValidB }))
            second.StatusCode.Should().Be(HttpStatusCode.OK);
        Break(fault);

        using var response = await client.PostAsync($"/api/workflows/{created}/versions/1/rollback", null);

        await ShouldBeUnansweredAsync(response);
    }

    [Fact]
    public async Task A_create_whose_compile_cannot_be_answered_is_a_503_carrying_the_saved_workflow_and_its_draft_publishes_later()
    {
        Break("resolver");

        using var response = await client.PostAsJsonAsync("/api/workflows", new { name = "wf", definitionJson = AsksTheResolver });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("resolver down");
        using var body = JsonDocument.Parse(text);
        var workflow = body.RootElement.GetProperty("workflow");
        var id = workflow.GetProperty("id").GetGuid();
        workflow.GetProperty("currentVersionId").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("versionId").GetGuid().Should().Be(workflow.GetProperty("draftVersionId").GetGuid());
        body.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        (body.RootElement.GetProperty("type").GetString(), body.RootElement.GetProperty("title").GetString())
            .Should().Be(("https://tools.ietf.org/html/rfc9110#section-15.6.4", "Service Unavailable"));

        resolver.Throws = false;
        using var publish = await client.PostAsync($"/api/workflows/{id}/publish", null);
        publish.StatusCode.Should().Be(HttpStatusCode.OK, "the draft the body names is the save, and it publishes once the compile answers");
    }

    [Fact]
    public async Task An_update_that_asked_to_publish_is_a_503_when_its_compile_cannot_be_answered_and_a_draft_save_is_a_200()
    {
        var created = await CreateAsync(Valid);
        Break("check");

        using var published = await client.PutAsJsonAsync($"/api/workflows/{created}", new { name = "wf", definitionJson = AsksTheHost });
        using var drafted = await client.PutAsJsonAsync($"/api/workflows/{created}", new { name = "wf", definitionJson = AsksTheHost.Replace("host.q", "host.r"), autoPublish = false });

        published.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var body = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("workflow").GetProperty("id").GetGuid().Should().Be(created);
        drafted.StatusCode.Should().Be(HttpStatusCode.OK, "a draft save did what it was asked; its diagnostics are the verdict read's");
    }

    [Fact]
    public async Task A_publish_that_does_not_compile_carries_its_diagnostics_as_a_top_level_member_and_no_extensions()
    {
        var created = await CreateAsync(Valid);
        using (await client.PutAsJsonAsync($"/api/workflows/{created}", new { name = "wf", definitionJson = Unknown, autoPublish = false }))
        {
        }

        using var response = await client.PostAsync($"/api/workflows/{created}/publish", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("type", "title", "status", "detail", "diagnostics");
        ShouldBeBadRequestProblem(body);
        body.RootElement.GetProperty("diagnostics")[0].GetProperty("ruleId").GetString().Should().Be(CompilationRules.BlockTypeUnknown);
    }

    [Fact]
    public async Task A_publish_refused_by_the_reader_is_a_400_with_no_diagnostics()
    {
        var seeded = await SeedAsync(Owner, Valid);
        await DraftAsync(seeded.Workflow, Unreadable);

        using var response = await client.PostAsync($"/api/workflows/{seeded.Workflow}/publish", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("diagnostics").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_start_with_no_current_version_is_a_400_whose_diagnostics_are_empty()
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new { name = "wf", definitionJson = Unknown });
        using var workflow = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = workflow.RootElement.GetProperty("id").GetGuid();

        using var response = await client.PostAsync($"/api/workflows/{id}/execute", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("diagnostics").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("detail").GetString().Should().Contain("no published version");
        ShouldBeBadRequestProblem(body);
    }

    [Fact]
    public async Task A_rollback_to_a_version_that_does_not_compile_is_a_400_problem_with_its_type_title_and_diagnostics()
    {
        var seeded = await SeedAsync(Owner, Unknown);
        using (var second = await client.PutAsJsonAsync($"/api/workflows/{seeded.Workflow}", new { name = "wf", definitionJson = Valid }))
            second.StatusCode.Should().Be(HttpStatusCode.OK);

        using var response = await client.PostAsync($"/api/workflows/{seeded.Workflow}/versions/1/rollback", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        ShouldBeBadRequestProblem(body);
        body.RootElement.GetProperty("diagnostics")[0].GetProperty("ruleId").GetString().Should().Be(CompilationRules.BlockTypeUnknown);
    }

    [Fact]
    public async Task A_rename_asked_to_publish_a_repeated_draft_the_reader_refuses_is_refused_whole_and_without_publishing_it_is_stored()
    {
        var seeded = await SeedAsync(Owner, Valid);
        await DraftAsync(seeded.Workflow, Unreadable);

        using var refused = await client.PutAsJsonAsync($"/api/workflows/{seeded.Workflow}", new { name = "renamed", definitionJson = Unreadable });
        var nameAfterRefusal = await NameAsync(seeded.Workflow);
        using var drafted = await client.PutAsJsonAsync($"/api/workflows/{seeded.Workflow}",
            new { name = "renamed", definitionJson = Unreadable, autoPublish = false });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync());
        nameAfterRefusal.Should().Be("wf", "a 400 means nothing was stored");
        drafted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await NameAsync(seeded.Workflow)).Should().Be("renamed");
    }

    private const string Unreadable = """{"nodes":[{"id":"a","blockTypeId":"noop","config":{},"position":{"x":0,"y":0}}],"edges":[]}""";

    private static void ShouldBeBadRequestProblem(JsonDocument body)
    {
        (body.RootElement.GetProperty("type").GetString(), body.RootElement.GetProperty("title").GetString(),
                body.RootElement.GetProperty("status").GetInt32())
            .Should().Be(("https://tools.ietf.org/html/rfc9110#section-15.5.1", "Bad Request", 400));
    }

    private async Task DraftAsync(Guid workflowId, string definitionJson) =>
        await WithDbAsync(async db =>
        {
            var workflow = await db.Workflows.SingleAsync(w => w.Id == workflowId);
            var draft = new WorkflowVersion(Guid.NewGuid(), workflowId, 2, definitionJson, null, null, Owner, T0);
            db.WorkflowVersions.Add(draft);
            await db.SaveChangesAsync();
            workflow.SetDraftVersion(draft, T0);
            await db.SaveChangesAsync();
        });

    private async Task<string> NameAsync(Guid workflowId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return (await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == workflowId)).Name;
    }

    private void Break(string fault)
    {
        if (fault == "check")
            check.Fault = new InvalidOperationException("check down");
        else
            resolver.Throws = true;
    }

    private static async Task ShouldBeUnansweredAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("down", "a host's fault is logged, not served");
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("detail").GetString().Should().Contain("could not be answered");
    }

    private static IEnumerable<Guid> WorkflowsOf(JsonDocument report) =>
        report.RootElement.GetProperty("invalid").EnumerateArray().Select(e => e.GetProperty("workflowId").GetGuid());

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await client.GetAsync(path);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private async Task<Guid> CreateAsync(string definitionJson)
    {
        using var response = await client.PostAsJsonAsync("/api/workflows", new { name = "wf", definitionJson });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty("currentVersionId").ValueKind.Should().NotBe(JsonValueKind.Null, "the fixture's first version compiles");
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task WithDbAsync(Func<WorkflowDbContext, Task> work)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<WorkflowDbContext>());
    }

    private async Task<(Guid Workflow, Guid Version)> SeedAsync(Guid owner, string definitionJson)
    {
        var workflow = new Workflow(Guid.NewGuid(), "wf", null, owner, T0);
        var version = new WorkflowVersion(Guid.NewGuid(), workflow.Id, 1, definitionJson, null, null, owner, T0, isPublished: true);
        await WithDbAsync(async db =>
        {
            db.Workflows.Add(workflow);
            db.WorkflowVersions.Add(version);
            await db.SaveChangesAsync();
            workflow.SetCurrentVersion(version, T0);
            await db.SaveChangesAsync();
        });
        return (workflow.Id, version.Id);
    }

    private sealed class FlakyStore : IWorkflowCompilationStore
    {
        private IWorkflowCompilationStore inner = null!;

        public bool FailsSaves { get; set; }

        public FlakyStore Over(IWorkflowCompilationStore store)
        {
            inner = store;
            return this;
        }

        public Task<CompilationVerdict?> FindAsync(
            Guid workflowVersionId, string definitionDigest, string dependencyHash, CancellationToken cancellationToken) =>
            inner.FindAsync(workflowVersionId, definitionDigest, dependencyHash, cancellationToken);

        public Task SaveAsync(CompilationVerdict verdict, CancellationToken cancellationToken) =>
            FailsSaves ? throw new InvalidOperationException("store down") : inner.SaveAsync(verdict, cancellationToken);

        public Task<int> DeleteStaleAsync(
            IReadOnlyCollection<CompilationKey> current, TimeSpan olderThan, CancellationToken cancellationToken) =>
            inner.DeleteStaleAsync(current, olderThan, cancellationToken);
    }

    private sealed class HostCheck : IExpressionNamespaceStaticCheck
    {
        public Exception? Fault { get; set; }

        public string Namespace => "host";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            Fault is { } fault ? throw fault : new(NamespacePathCheck.Yes, "String");
    }

    private sealed class ResolvedBlock : IWorkflowBlockFactory
    {
        public string BlockTypeId => "resolved";
        public string DisplayName => "resolved";
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public bool PortsAreResolvedElsewhere => true;
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private sealed class ThrowingResolver : INodePortResolver
    {
        public bool Throws { get; set; }

        public string BlockTypeId => "resolved";

        public Task<NodePortResolution> ResolveAsync(NodePortResolutionRequest request, CancellationToken cancellationToken) =>
            Throws
                ? throw new InvalidOperationException("resolver down")
                : Task.FromResult(NodePortResolution.Resolved(null, CanvasSignature.Empty));
    }
}
