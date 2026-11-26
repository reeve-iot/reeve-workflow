using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.AspNetCore.Tests;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class SecretReadMaskingEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-24T12:00:00Z");

    private const string Plaintext = "S3CR3T-plain-91c";
    private const string CipherOne = "enc:CIPHER-7f3a";
    private const string CipherTwo = "enc:CIPHER-b2e9";
    private const string Reference = "ref:vault:db#pw";

    private static readonly string[] Proven =
    [
        "createWorkflow", "updateWorkflow", "publishWorkflow", "rollbackWorkflow",
        "getWorkflow", "getVersion", "diffVersions", "getExecution", "getExecutionReplay",
    ];

    private static readonly Dictionary<string, string> CarriesNeither = new(StringComparer.Ordinal)
    {
        ["analyzeDraft"] = "diagnostics and node ports; the draft is the caller's own request",
        ["validateImport"] = "the definition text the caller sent, returned as it came: the caller's own text",
        ["getInvalidCompilations"] = "diagnostics",
        ["getPortTypes"] = "the vocabulary",
        ["deleteWorkflow"] = "no body",
        ["getVersionCompilation"] = "diagnostics",
        ["previewExpression"] = "an expression's value",
        ["getBlockTypes"] = "the catalogue",
        ["getWorkflows"] = "summaries without documents",
        ["executeWorkflow"] = "the run's outputs, which are run-time data",
        ["getExecutions"] = "rows without steps",
        ["getVersions"] = "summaries without documents",
    };

    private readonly FakeCodec codec = new();
    private SqliteConnection connection = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new SecretHeadersBlock());
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new SecretBoxBlock());
        builder.Services.AddScoped<IWorkflowSecretCodec>(_ => codec);
        builder.Services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(db => db.UseSqlite(connection).ReplaceService<IModelCustomizer, InstantsAsNumbers>());
            })
            .AddWorkflowEditor()
            .AddWorkflowEngine(options => options.TraceMode = WorkflowTraceMode.Full);

        app = builder.Build();
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner.ToString())], "test"));
            return next(context);
        });
        app.MapWorkflowEditorApi("api/workflows").AllowAnonymous();
        app.MapWorkflowApi("api/read").AllowAnonymous();

        await app.StartAsync();
        client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public void Every_response_the_two_surfaces_list_is_proven_masked_or_named_as_carrying_neither_a_definition_nor_a_recorded_input()
    {
        var read = WebApplication.CreateBuilder().Build();
        read.MapWorkflowApi("api/workflows").AllowAnonymous();
        read.MapWorkflowBlockApi("api/workflow-blocks").AllowAnonymous();
        read.MapWorkflowExpressionApi("api/expressions").AllowAnonymous();
        var listed = WorkflowApiSurface.Describe(
                ((IEndpointRouteBuilder)read).DataSources.SelectMany(d => d.Endpoints), "api/workflows", "api/workflow-blocks", "api/expressions")
            .Endpoints.Concat(WorkflowApiSurface.Describe(
                WorkflowEditorApiSurfaceTests.Source, WorkflowEditorApiSurfaceTests.Mapped(), WorkflowEditorApiSurfaceTests.EditorPrefix.TrimStart('/')).Endpoints)
            .Select(e => e.Operation)
            .ToList();

        Proven.Concat(CarriesNeither.Keys).Should().BeEquivalentTo(
            listed, "every response of the surfaces is either proven here or named as carrying neither, and nothing else is");
    }

    public static TheoryData<string> ProvenOperations => new(Proven);

    [Theory]
    [MemberData(nameof(ProvenOperations))]
    public async Task A_response_that_carries_a_definition_or_a_recorded_input_masks_each_Protected_and_name_rule_entry(string operation)
    {
        switch (operation)
        {
            case "createWorkflow":
                await ProveDoor(HttpMethod.Post, "/api/workflows", withCipher: false);
                break;
            case "updateWorkflow":
                await ProveDoor(HttpMethod.Put, "/api/workflows/{id}", withCipher: false);
                break;
            case "publishWorkflow":
                await ProveSeeded(HttpMethod.Post, "/api/workflows/{id}/publish", [Definition(CipherOne)], draft: Definition(CipherTwo));
                break;
            case "rollbackWorkflow":
                await ProveSeeded(HttpMethod.Post, "/api/workflows/{id}/versions/1/rollback", [Definition(CipherOne), Definition(CipherTwo)]);
                break;
            case "getWorkflow":
                await ProveSeeded(HttpMethod.Get, "/api/read/{id}", [Definition(CipherOne)]);
                break;
            case "getVersion":
                await ProveSeeded(HttpMethod.Get, "/api/read/{id}/versions/1", [Definition(CipherOne), Definition(CipherTwo)]);
                break;
            case "diffVersions":
                await ProveDiff();
                break;
            case "getExecution":
            case "getExecutionReplay":
                await ProveRecordedInputs(replay: operation == "getExecutionReplay");
                break;
            default:
                throw new InvalidOperationException(operation);
        }
    }

    private async Task ProveDoor(HttpMethod method, string path, bool withCipher)
    {
        var id = method == HttpMethod.Put ? (await SeedAsync([Definition(null)])).Workflow : Guid.Empty;
        using var request = new HttpRequestMessage(method, path.Replace("{id}", id.ToString()))
        {
            Content = JsonContent.Create(new { name = "wf", definitionJson = Definition(withCipher ? CipherOne : null) }),
        };

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.Should().BeTrue(body);
        AssertMasked(body, WorkflowDefinitionOf(body), withCipher);
    }

    private async Task ProveSeeded(HttpMethod method, string path, string[] versions, string? draft = null)
    {
        var (id, _) = await SeedAsync(versions, draft);
        using var response = await client.SendAsync(new HttpRequestMessage(method, path.Replace("{id}", id.ToString())));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        AssertMasked(body, WorkflowDefinitionOf(body), withCipher: true);
    }

    private async Task ProveDiff()
    {
        var (id, _) = await SeedAsync([Definition(CipherOne), Definition(CipherTwo)]);

        using var response = await client.GetAsync($"/api/read/{id}/versions/1/diff/2");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        AssertNoneOf(body);
        using var diff = JsonDocument.Parse(body);
        var node = diff.RootElement.GetProperty("modifiedNodes").EnumerateArray().Should().ContainSingle(n => n.GetProperty("id").GetString() == "h").Subject;
        var change = node.GetProperty("configChanges").GetProperty("Headers");
        foreach (var side in new[] { "from", "to" })
            EntriesOf(JsonDocument.Parse(change.GetProperty(side).GetString()!).RootElement).Should().Contain("X-Custom", "***");
        var edges = diff.RootElement.GetProperty("addedEdges").EnumerateArray().Concat(diff.RootElement.GetProperty("removedEdges").EnumerateArray())
            .Where(e => e.GetProperty("targetNodeId").GetString() == "e").ToList();
        edges.Should().HaveCount(2, "a changed protected entry on an edge literal is a removed edge and an added one");
        foreach (var edge in edges)
        {
            var literal = JsonDocument.Parse(edge.GetProperty("mappingsJson").GetString()!).RootElement.GetProperty("Headers").GetProperty("literalValue");
            EntriesOf(literal).Should().Contain("X-Custom", "***");
        }
    }

    [Fact]
    public async Task Import_validation_answers_the_definition_text_it_was_sent_unmasked()
    {
        var export = new
        {
            schemaVersion = "1", exportedAtUtc = T0, name = "wf", description = (string?)null,
            definitionJson = Definition(CipherOne), inputParametersJson = (string?)null, outputParametersJson = (string?)null,
        };

        using var response = await client.PostAsJsonAsync("/api/read/import/validate", export);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var answered = EntriesOf(JsonDocument.Parse(WorkflowDefinitionOf(body)).RootElement.GetProperty("nodes").EnumerateArray()
            .Single(n => n.GetProperty("id").GetString() == "h").GetProperty("configuration").GetProperty("Headers"));
        answered["Authorization"].Should().Be(Plaintext);
        answered["X-Custom"].Should().Be(CipherOne);
    }

    [Fact]
    public async Task A_changed_protected_entry_on_a_sub_canvas_node_is_reported_with_stars_on_both_sides()
    {
        var (id, _) = await SeedAsync([BoxedDefinition(CipherOne), BoxedDefinition(CipherTwo)]);

        using var response = await client.GetAsync($"/api/read/{id}/versions/1/diff/2");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        AssertNoneOf(body);
        using var diff = JsonDocument.Parse(body);
        var node = diff.RootElement.GetProperty("modifiedNodes").EnumerateArray().Should().ContainSingle(n => n.GetProperty("id").GetString() == "n").Subject;
        var change = node.GetProperty("configChanges").GetProperty("Headers");
        foreach (var side in new[] { "from", "to" })
            EntriesOf(JsonDocument.Parse(change.GetProperty(side).GetString()!).RootElement).Should().Contain("X-Custom", "***");
    }

    private async Task ProveRecordedInputs(bool replay)
    {
        var (id, _) = await SeedAsync([Definition(CipherOne)]);
        using (var run = await client.PostAsync($"/api/read/{id}/execute", null))
            run.StatusCode.Should().Be(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        await using var scope = app.Services.CreateAsyncScope();
        var executionId = (await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionLogs
            .AsNoTracking().SingleAsync(l => l.WorkflowId == id)).Id;

        using var read = await client.GetAsync($"/api/read/{id}/executions/{executionId}" + (replay ? "/replay" : ""));
        var body = await read.Content.ReadAsStringAsync();
        read.StatusCode.Should().Be(HttpStatusCode.OK, body);

        AssertNoneOf(body);
        using var document = JsonDocument.Parse(body);
        var steps = replay ? document.RootElement : document.RootElement.GetProperty("steps");
        foreach (var nodeId in new[] { "h", "e" })
        {
            var step = steps.EnumerateArray().Should().ContainSingle(s => s.GetProperty("nodeId").GetString() == nodeId).Subject;
            AssertEntries(EntriesOf(JsonDocument.Parse(step.GetProperty("inputsJson").GetString()!).RootElement.GetProperty("Headers")), withCipher: true);
        }
    }

    private static string WorkflowDefinitionOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("definitionJson").GetString()!;

    private static void AssertNoneOf(string body) =>
        new[] { Plaintext, CipherOne, CipherTwo }.Where(secret => body.Contains(secret, StringComparison.Ordinal))
            .Should().BeEmpty("no response holds a planted plaintext or ciphertext");

    private static void AssertMasked(string body, string definitionJson, bool withCipher)
    {
        AssertNoneOf(body);
        using var definition = JsonDocument.Parse(definitionJson);
        var nodes = definition.RootElement.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("id").GetString()!);
        AssertEntries(EntriesOf(nodes["h"].GetProperty("configuration").GetProperty("Headers")), withCipher);

        var edge = definition.RootElement.GetProperty("edges").EnumerateArray().Single(e => e.GetProperty("targetNodeId").GetString() == "e");
        AssertEntries(EntriesOf(edge.GetProperty("mappings").GetProperty("Headers").GetProperty("literalValue")), withCipher);
    }

    private static void AssertEntries(Dictionary<string, string> entries, bool withCipher)
    {
        entries["Authorization"].Should().Be("***");
        entries["Accept"].Should().Be("text/plain");
        entries["X-Ref"].Should().Be(Reference, "a reference is shown as written");
        entries["Authorization-Backup"].Should().Be(Reference, "a reference under a name-rule key is still a reference");
        if (withCipher)
            entries["X-Custom"].Should().Be("***");
        else
            entries.Should().NotContainKey("X-Custom");
    }

    private static Dictionary<string, string> EntriesOf(JsonElement entries) =>
        entries.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    private static string Definition(string? cipher)
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "text/plain",
            ["Authorization"] = Plaintext,
            ["X-Ref"] = Reference,
            ["Authorization-Backup"] = Reference,
        };
        if (cipher is not null)
            headers["X-Custom"] = cipher;

        var position = new { x = 0, y = 0 };
        return JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position, configuration = new { } },
                new { id = "h", blockTypeId = SecretHeadersBlock.Id, position, configuration = new { Headers = headers } },
                new { id = "e", blockTypeId = SecretHeadersBlock.Id, position, configuration = new { } },
            },
            edges = new object[]
            {
                new { id = "e0", sourceNodeId = "start", targetNodeId = "h", mappings = new { } },
                new { id = "e1", sourceNodeId = "start", targetNodeId = "e", mappings = new { Headers = new { type = "literal", literalValue = headers } } },
            },
        });
    }

    private static string BoxedDefinition(string cipher)
    {
        var position = new { x = 0, y = 0 };
        var inner = new
        {
            nodes = new object[] { new { id = "n", blockTypeId = SecretHeadersBlock.Id, position, configuration = new { Headers = new Dictionary<string, string> { ["X-Custom"] = cipher, ["Accept"] = "text/plain" } } } },
            edges = Array.Empty<object>(),
        };
        return JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position, configuration = new { } },
                new { id = "box", blockTypeId = SecretBoxBlock.Id, position, configuration = new { }, subCanvases = new { Body = inner } },
            },
            edges = new object[] { new { id = "e0", sourceNodeId = "start", targetNodeId = "box", mappings = new { } } },
        });
    }

    private async Task<(Guid Workflow, Guid Version)> SeedAsync(string[] versions, string? draft = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(Guid.NewGuid(), "wf", null, Owner, T0);
        db.Workflows.Add(workflow);
        var made = versions.Select((text, i) => new WorkflowVersion(Guid.NewGuid(), workflow.Id, i + 1, text, null, null, Owner, T0, isPublished: true)).ToList();
        db.WorkflowVersions.AddRange(made);
        await db.SaveChangesAsync();
        workflow.SetCurrentVersion(made[^1], T0);
        if (draft is not null)
        {
            var next = new WorkflowVersion(Guid.NewGuid(), workflow.Id, made.Count + 1, draft, null, null, Owner, T0, isPublished: false);
            db.WorkflowVersions.Add(next);
            await db.SaveChangesAsync();
            workflow.SetDraftVersion(next, T0);
        }

        await db.SaveChangesAsync();
        return (workflow.Id, made[^1].Id);
    }

    private sealed class InstantsAsNumbers(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                if (property.GetValueConverter() is null)
                    property.SetValueConverter(new DateTimeOffsetToBinaryConverter());
            }
        }
    }

    private sealed class FakeCodec : IWorkflowSecretCodec
    {
        public WorkflowSecretForm Classify(string value) =>
            value.StartsWith("enc:", StringComparison.Ordinal) ? WorkflowSecretForm.Protected
            : value.StartsWith("ref:", StringComparison.Ordinal) ? WorkflowSecretForm.Reference
            : WorkflowSecretForm.Plain;

        public ValueTask<string> ProtectAsync(WorkflowSecretSite site, string plaintext, CancellationToken cancellationToken) =>
            ValueTask.FromResult("enc:" + plaintext);

        public ValueTask AdmitReferencesAsync(
            WorkflowSecretSave save, IReadOnlyList<string> bound, IReadOnlyList<string> unbound, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<string> RevealAsync(WorkflowSecretSite site, string stored, CancellationToken cancellationToken) =>
            ValueTask.FromResult("revealed");
    }

    private sealed class SecretHeadersBlock : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "SecretHeaders";
        public string BlockTypeId => Id;
        public string DisplayName => Id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [
            new("Headers", "Headers", PortValueType.MapOf(PortValueType.String), false) { SecretValues = true },
            new("Other", "Other", PortValueType.MapOf(PortValueType.String), false),
        ];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }

    private sealed class SecretBoxBlock : IWorkflowBlockFactory, IWorkflowBlockPlugin
    {
        public const string Id = "SecretBox";
        public string BlockTypeId => Id;
        public string DisplayName => Id;
        public string Description => "";
        public string Category => "test";
        public bool IsIdempotent => true;
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<string> SubCanvasNames => ["Body"];
        public IWorkflowBlockPlugin Create() => this;

        public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
            IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
    }
}
