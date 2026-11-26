using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class WorkflowEditorRefusalTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string Definition = "{\"nodes\":[],\"edges\":[]}";

    private SqliteConnection connection = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    private bool authenticate = true;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
        builder.Services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(db => db.UseSqlite(connection));
        })
        .AddWorkflowEditor();

        app = builder.Build();
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Owner.ToString())],
                authenticate ? "test" : null));
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

    [Theory]
    [InlineData("{\"name\":\"  \",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\"}", "required")]
    [InlineData("{\"name\":\"Ship\"}", "no document (rule L1, at '')")]
    [InlineData("{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\",\"maxConcurrentRuns\":0}", "MaxConcurrentRuns must be >= 1")]
    [InlineData("{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\",\"mode\":\"Paralell\"}", "not a workflow mode")]
    [InlineData("{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"connections\\\":[]}\"}", "(rule stored-key-unknown, at '/connections')")]
    [InlineData("{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\",\"inputParametersJson\":\"[{\\\"type\\\":\\\"Int\\\"}]\"}", "declared input parameters cannot be read")]
    public async Task A_body_the_library_will_not_store_is_a_problem_document_and_not_a_fault(string body, string says)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/workflows", content);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        text.Should().Contain(says);
        text.Should().NotContain("(Parameter '",
            because: "a problem body naming an internal parameter tells a caller about the code and not "
                     + "about what it sent");
        text.Should().NotContain("   at ");
    }

    [Theory]
    [InlineData("inputParametersJson", "declared input parameters cannot be read")]
    [InlineData("outputParametersJson", "declared output parameters cannot be read")]
    public async Task An_import_whose_parameter_list_the_reader_refuses_is_a_problem_document(string field, string says)
    {
        var body = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1", ["exportedAtUtc"] = DateTimeOffset.UtcNow, ["name"] = "Ship",
            ["description"] = null, ["definitionJson"] = Definition, [field] = """[{"type":"Int"}]""",
        };

        using var response = await client.PostAsJsonAsync("/api/read/import/validate", body);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        text.Should().Contain(says);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"1\",\"exportedAtUtc\":\"2026-10-04T00:00:00Z\",\"name\":\"Ship\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"exportedAtUtc\":\"2026-10-04T00:00:00Z\",\"name\":\"Ship\",\"definitionJson\":null}")]
    public async Task An_import_without_a_definition_is_a_400_in_the_publish_refusal_shape(string file)
    {
        using var content = new StringContent(file, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/read/import/validate", content);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        text.Should().Contain("definition-unreadable").And.Contain("no document");
        text.Should().NotContain("(Parameter '");
    }

    [Fact]
    public async Task An_import_with_an_L4_unreadable_definition_is_a_400_with_its_finding()
    {
        var body = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1", ["exportedAtUtc"] = DateTimeOffset.UtcNow, ["name"] = "Ship",
            ["description"] = null, ["definitionJson"] = """{"nodes":[{"id":"\ud800"}]}""",
        };

        using var response = await client.PostAsJsonAsync("/api/read/import/validate", body);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        text.Should().Contain("definition-unreadable").And.Contain("rule L4");
    }

    [Theory]
    [InlineData("{\"nodes\":[{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}")]
    [InlineData("{\"nodes\":[{\"id\":\"box\",\"subCanvases\":{\"Body\":{\"nodes\":[{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}},\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}")]
    public async Task An_import_with_a_duplicate_node_id_is_a_named_problem_document(string definition)
    {
        var body = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1", ["exportedAtUtc"] = DateTimeOffset.UtcNow, ["name"] = "Ship",
            ["description"] = null, ["definitionJson"] = definition,
        };

        using var response = await client.PostAsJsonAsync("/api/read/import/validate", body);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        text.Should().Contain("duplicate-node-id").And.Contain("twin");
        text.Should().NotContain("ArgumentException").And.NotContain("ToDictionary");
    }

    public static TheoryData<string> RefusedRowSettings => new()
    {
        "\"executionTimeoutSeconds\":0",
        "\"executionTimeoutSeconds\":-3",
        "\"executionTimeoutSeconds\":4294968",
        "\"retryPolicyJson\":\"{\\\"maxAttempts\\\":\"",
        "\"retryPolicyJson\":\"{\\\"backoffStrategy\\\":\\\"Exponential\\\"}\"",
        "\"retryPolicyJson\":\"{\\\"maxAttempts\\\":0}\"",
        "\"retryPolicyJson\":\"{\\\"initialDelayMs\\\":0}\"",
    };

    private static string RowBody(string extra) =>
        "{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\"," + extra + "}";

    [Theory]
    [MemberData(nameof(RefusedRowSettings))]
    public async Task A_create_carrying_a_row_setting_the_run_would_read_as_something_else_is_a_400_and_stores_nothing(string setting)
    {
        using var content = new StringContent(RowBody(setting), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/workflows", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
        (await CountWorkflowsAsync()).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(RefusedRowSettings))]
    public async Task An_update_carrying_a_row_setting_the_run_would_read_as_something_else_is_a_400(string setting)
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new { name = "Ship", definitionJson = Definition });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using var content = new StringContent(RowBody(setting), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PutAsync(created.Headers.Location!.ToString(), content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [MemberData(nameof(RefusedRowSettings))]
    public async Task An_import_carrying_a_row_setting_the_run_would_read_as_something_else_is_a_400(string setting)
    {
        var file = "{\"schemaVersion\":\"1\",\"exportedAtUtc\":\"2026-10-04T00:00:00Z\",\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\"," + setting + "}";
        using var content = new StringContent(file, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/read/import/validate", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_import_the_compile_refuses_is_a_400_with_the_diagnostics_publish_answers()
    {
        var definition = "{\"nodes\":[{\"id\":\"a\",\"blockTypeId\":\"" + new string('B', 101)
            + "\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}";
        var body = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1", ["exportedAtUtc"] = DateTimeOffset.UtcNow, ["name"] = "Ship",
            ["description"] = null, ["definitionJson"] = definition,
        };

        using var response = await client.PostAsJsonAsync("/api/read/import/validate", body);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        text.Should().Contain("\"diagnostics\"").And.Contain("\"ruleId\":\"block-type-id-too-long\"")
            .And.Contain("\"ruleId\":\"no-start-node\"");
    }

    [Fact]
    public async Task A_create_carrying_isEnabled_is_a_400_and_stores_nothing()
    {
        using var content = new StringContent(RowBody("\"isEnabled\":false"), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/workflows", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
        (await CountWorkflowsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_update_carrying_isEnabled_is_a_400()
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new { name = "Ship", definitionJson = Definition });
        using var content = new StringContent(RowBody("\"isEnabled\":false"), System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PutAsync(created.Headers.Location!.ToString(), content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
    }

    public static TheoryData<string, string, string, bool, string> UnreadableBodies()
    {
        var data = new TheoryData<string, string, string, bool, string>();
        foreach (var (verb, url, declaresDiagnostics) in new[]
                 {
                     ("POST", "/api/read/import/validate", true),
                     ("POST", "/api/workflows", false),
                     ("PUT", "/api/workflows/00000000-0000-0000-0000-000000000001", false),
                     ("POST", "/api/workflows/analysis", false),
                 })
        {
            data.Add(verb, url, "{\"isEnabled\":false}", declaresDiagnostics,
                "The request body could not be read: reading stopped at $.isEnabled (line 1, byte 14).");
            data.Add(verb, url, "{\"name\":", declaresDiagnostics, "The request body could not be read: reading stopped at $.name (line 1, byte 9).");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(UnreadableBodies))]
    public async Task A_body_that_cannot_be_bound_is_a_400_in_the_shape_the_endpoint_declares(
        string verb, string url, string body, bool declaresDiagnostics, string detail)
    {
        using var request = new HttpRequestMessage(new HttpMethod(verb), url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        using var document = System.Text.Json.JsonDocument.Parse(text);
        document.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        document.RootElement.GetProperty("detail").GetString().Should().Be(detail);
        text.Should().NotContain("ReeveWorkflow.").And.NotContain(".NET");
        document.RootElement.TryGetProperty("diagnostics", out var diagnostics).Should().Be(declaresDiagnostics, because: text);
        if (declaresDiagnostics)
            diagnostics.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_import_file_carrying_isEnabled_is_a_400()
    {
        var file = "{\"schemaVersion\":\"1\",\"exportedAtUtc\":\"2026-10-04T00:00:00Z\",\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\",\"isEnabled\":false}";
        using var content = new StringContent(file, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/read/import/validate", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"a","blockTypeId":"WorkflowInput","configuration":{"errorPolicy":{"policy":"Skip"}},"position":{"x":0,"y":0}}],"edges":[]}""", "remove the configuration key")]
    [InlineData("""{"nodes":[{"id":"a","blockTypeId":"WorkflowInput","errorPolicy":{"policy":"Skip","routeToErrorPort":true},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "a routeToErrorPort key no run reads")]
    public async Task An_import_holding_an_error_policy_no_run_reads_is_a_problem_document_saying_how_to_fix_it(string definition, string says)
    {
        var body = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "1", ["exportedAtUtc"] = DateTimeOffset.UtcNow, ["name"] = "Ship",
            ["description"] = null, ["definitionJson"] = definition,
        };

        using var response = await client.PostAsJsonAsync("/api/read/import/validate", body);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        text.Should().Contain(says);
    }

    [Fact]
    public async Task A_principal_nobody_authenticated_writes_nothing_however_it_names_itself()
    {
        authenticate = false;

        using var response = await client.PostAsJsonAsync("/api/workflows",
            new { name = "Ship", definitionJson = Definition });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await CountWorkflowsAsync()).Should().Be(0,
            because: "a claim on an identity nobody authenticated is a claim any middleware can add");
    }

    [Fact]
    public async Task A_create_answers_with_a_location_a_caller_can_follow()
    {
        using var response = await client.PostAsJsonAsync("/api/workflows",
            new { name = "Ship", definitionJson = Definition });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().StartWith("/api/workflows/");
    }

    [Fact]
    public async Task A_non_ascii_prefix_does_not_lose_the_answer_to_a_row_that_was_written()
    {
        using var second = new SqliteConnection("Filename=:memory:");
        await second.OpenAsync();
        await using var tenant = await TenantHostAsync(second);
        using var http = new HttpClient { BaseAddress = new Uri(tenant.Urls.First()) };

        using var response = await http.PostAsJsonAsync("/api/tenants/gda%C5%84sk/workflows",
            new { name = "Ship", definitionJson = Definition });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            because: "the row is committed before the header is written, so a header the server refuses "
                     + "loses the answer to a workflow that exists");
        response.Headers.Location!.OriginalString.Should().StartWith("/api/tenants/gda%C5%84sk/workflows/");
        await tenant.StopAsync();
    }

    private async Task<WebApplication> TenantHostAsync(SqliteConnection sqlite)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(db => db.UseSqlite(sqlite));
        })
        .AddWorkflowEditor();

        var tenant = builder.Build();
        using (var scope = tenant.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        tenant.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Owner.ToString())], "test"));
            return next(context);
        });
        tenant.MapWorkflowEditorApi("api/tenants/{tenantId}/workflows").AllowAnonymous();
        await tenant.StartAsync();
        return tenant;
    }

    private async Task<int> CountWorkflowsAsync()
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows.CountAsync();
    }
}

public sealed class WorkflowEditorStartupRefusalTests
{
    [Fact]
    public async Task A_host_that_states_no_intent_does_not_start()
    {
        await using var host = await BuildAsync(state: null);

        var start = () => host.App.StartAsync();

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*without an authorisation intent*",
                because: "composed lazily, the same refusal answers 500 to every route in the process "
                         + "instead, and the first thing that notices is production traffic");
    }

    [Fact]
    public async Task A_host_that_states_one_starts()
    {
        await using var host = await BuildAsync(group => group.AllowAnonymous());

        var start = () => host.App.StartAsync();

        await start.Should().NotThrowAsync();
        await host.App.StopAsync();
    }

    private static async Task<Host> BuildAsync(Action<Microsoft.AspNetCore.Routing.RouteGroupBuilder>? state)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(db => db.UseSqlite(connection));
        })
        .AddWorkflowEditor();

        var app = builder.Build();
        var group = app.MapWorkflowEditorApi("api/workflows");
        state?.Invoke(group);
        return new Host(app, connection);
    }

    private sealed record Host(WebApplication App, SqliteConnection Connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
