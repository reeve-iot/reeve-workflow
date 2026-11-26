using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Tests.TestSupport;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class ImportDropsEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-24T12:00:00Z");

    private readonly FakeSecretCodec codec = new();
    private SqliteConnection connection = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        app = await StartHostAsync(connection, codec, configureServices: null);
        client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    private static async Task<WebApplication> StartHostAsync(SqliteConnection connection, FakeSecretCodec codec, Action<IServiceCollection>? configureServices)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.AddSingleton<IWorkflowBlockFactory>(SecretTestBlocks.Http);
        builder.Services.AddSingleton<IWorkflowSecretCodec>(codec);
        builder.Services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(db => db.UseSqlite(connection));
        })
        .AddWorkflowEditor();
        configureServices?.Invoke(builder.Services);

        var host = builder.Build();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        host.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner.ToString())], "test"));
            return next(context);
        });
        host.MapWorkflowEditorApi("api/workflows").AllowAnonymous();
        host.MapWorkflowApi("api/read").AllowAnonymous();

        await host.StartAsync();
        return host;
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await connection.DisposeAsync();
    }

    private static object Export(string definition) => new
    {
        schemaVersion = "1", exportedAtUtc = T0, name = "wf", description = (string?)null,
        definitionJson = definition, inputParametersJson = (string?)null, outputParametersJson = (string?)null,
    };

    [Fact]
    public async Task Import_validation_names_the_stars_and_the_foreign_protected_values_and_asks_the_codec_for_nothing()
    {
        var definition = SecretDocs.Flow(new()
        {
            ["X-Star"] = "***", ["X-Foreign"] = FakeSecretCodec.Ciphertext(Guid.NewGuid(), "x"),
            ["X-Custom"] = "protect:hunter2", ["Authorization"] = "Bearer t", ["X-Ref"] = "ref:vault:a#k",
        });

        using var response = await client.PostAsJsonAsync("/api/read/import/validate", Export(definition));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var dropped = JsonDocument.Parse(body).RootElement.GetProperty("droppedSecrets").EnumerateArray()
            .Select(d => (d.GetProperty("nodeId").GetString(), d.GetProperty("input").GetString(), d.GetProperty("key").GetString(), d.GetProperty("reason").GetString()))
            .ToList();
        dropped.Should().Equal(("h", "Headers", "X-Star", "masked"), ("h", "Headers", "X-Foreign", "foreign-protected"));
        codec.Protections.Should().BeEmpty();
        codec.Admissions.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_validation_of_a_definition_with_no_such_entry_reports_an_empty_list()
    {
        using var response = await client.PostAsJsonAsync("/api/read/import/validate", Export(SecretDocs.Flow(new() { ["Accept"] = "a" })));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        JsonDocument.Parse(body).RootElement.GetProperty("droppedSecrets").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_HTTP_create_that_declares_origin_Imported_is_refused_with_its_code_and_stores_nothing()
    {
        var definition = SecretDocs.Flow(new() { ["X-Star"] = "***", ["Accept"] = "a" });

        using var response = await client.PostAsJsonAsync("/api/workflows", new { name = "Ship", definitionJson = definition, origin = "Imported" });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(WorkflowSaveRefusalCodes.OriginNotAccepted);
        (await ListedWorkflowCountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("\"Edited\"")]
    [InlineData("null")]
    public async Task An_HTTP_create_that_declares_origin_Edited_or_none_is_an_edit_and_its_star_is_refused(string origin)
    {
        var definition = SecretDocs.Flow(new() { ["X-Star"] = "***", ["Accept"] = "a" });

        using var response = await Post(Create(definition, origin));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
    }

    [Fact]
    public async Task An_HTTP_create_that_declares_origin_Edited_stores_what_an_edit_stores()
    {
        using var response = await Post(Create(SecretDocs.Flow(new() { ["Accept"] = "a" }), "\"Edited\""));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("\"1\"")]
    [InlineData("\"imported\"")]
    [InlineData("\"EDITED\"")]
    [InlineData("\" Edited\"")]
    [InlineData("\"0\"")]
    [InlineData("\"Edited, Imported\"")]
    [InlineData("\"Elsewhere\"")]
    [InlineData("\"\"")]
    [InlineData("7")]
    [InlineData("true")]
    public async Task An_HTTP_create_whose_origin_is_not_the_exact_name_of_Edited_or_Imported_is_refused_and_stores_nothing(string origin)
    {
        using var response = await Post(Create(SecretDocs.Flow(new() { ["Accept"] = "a" }), origin));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, origin + " " + await response.Content.ReadAsStringAsync());
        (await ListedWorkflowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_HTTP_update_that_sends_an_origin_is_refused_as_an_unknown_member_because_an_update_has_none()
    {
        using var created = await Post(Create(SecretDocs.Flow(new() { ["Accept"] = "a" }), "null"));
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        using var response = await client.PutAsync($"/api/workflows/{id}", new StringContent(
            $"{{\"name\":\"Ship\",\"definitionJson\":{JsonSerializer.Serialize(SecretDocs.Flow(new() { ["Accept"] = "a" }))},\"origin\":\"Imported\"}}", Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        JsonDocument.Parse(body).RootElement.TryGetProperty("code", out _).Should().BeFalse(body);
        body.Should().Contain("origin");
    }

    [Theory]
    [InlineData("\"edited\"")]
    [InlineData("0")]
    [InlineData("\"imported\"")]
    [InlineData("1")]
    public async Task A_host_that_registers_a_global_enum_converter_does_not_widen_how_the_origin_is_spelled(string origin)
    {
        using var hostConnection = new SqliteConnection("Filename=:memory:");
        await hostConnection.OpenAsync();
        await using var host = await StartHostAsync(hostConnection, new FakeSecretCodec(), services =>
            services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter())));
        using var hostClient = new HttpClient { BaseAddress = new Uri(host.Urls.First()) };

        using var response = await hostClient.PostAsync("/api/workflows", new StringContent(
            Create(SecretDocs.Flow(new() { ["Accept"] = "a" }), origin), Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, origin + " " + body);
        JsonDocument.Parse(body).RootElement.TryGetProperty("code", out _).Should().BeFalse(origin + " " + body);
        await host.StopAsync();
    }

    private static string Create(string definition, string rawOrigin) =>
        $"{{\"name\":\"Ship\",\"definitionJson\":{JsonSerializer.Serialize(definition)},\"origin\":{rawOrigin}}}";

    private Task<HttpResponseMessage> Post(string json) =>
        client.PostAsync("/api/workflows", new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<int> ListedWorkflowCountAsync()
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows.CountAsync();
    }
}
