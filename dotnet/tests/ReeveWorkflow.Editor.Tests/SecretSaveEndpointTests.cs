using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Tests.TestSupport;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class SecretSaveEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly FakeSecretCodec codec = new();
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
        builder.Services.AddSingleton<IWorkflowBlockFactory>(SecretTestBlocks.Http);
        builder.Services.AddSingleton<IWorkflowSecretCodec>(codec);
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
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner.ToString())], "test"));
            return next(context);
        });
        app.MapWorkflowEditorApi("api/workflows").AllowAnonymous();

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

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpResponseMessage response) =>
        (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());

    [Fact]
    public async Task A_create_with_a_star_and_no_stored_entry_answers_400_with_the_code()
    {
        using var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "Ship", definitionJson = SecretDocs.Flow(new() { ["X-Token"] = "***" }),
        });

        var (status, body) = await ReadAsync(response);
        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("code").GetString().Should().Be("secret-value-unmatched");
    }

    [Fact]
    public async Task An_update_the_host_refuses_a_reference_for_answers_400_with_the_code()
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new { name = "Ship", definitionJson = SecretDocs.Flow(new() { ["Accept"] = "x" }) });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        codec.RefuseAdmission = "not your secret";

        using var response = await client.PutAsJsonAsync($"/api/workflows/{id}", new
        {
            name = "Ship", definitionJson = SecretDocs.Flow(new() { ["X-A"] = "ref:vault:a#k" }),
        });

        var (status, body) = await ReadAsync(response);
        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("code").GetString().Should().Be("secret-refused");
        body.GetProperty("detail").GetString().Should().Contain("not your secret");
    }

    [Fact]
    public async Task A_rollback_the_host_refuses_references_for_answers_400_with_the_code_beside_the_diagnostics()
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new { name = "Ship", definitionJson = SecretDocs.Flow(new() { ["X-A"] = "ref:vault:a#k" }) });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var changed = await client.PutAsJsonAsync($"/api/workflows/{id}", new { name = "Ship", definitionJson = SecretDocs.Flow(new() { ["Accept"] = "x" }) });
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        codec.RefuseAdmission = "not your secret";

        using var response = await client.PostAsync($"/api/workflows/{id}/versions/1/rollback", null);

        var (status, body) = await ReadAsync(response);
        status.Should().Be(HttpStatusCode.BadRequest);
        body.GetProperty("code").GetString().Should().Be("secret-refused");
        body.GetProperty("diagnostics").GetArrayLength().Should().Be(0);
        body.GetProperty("detail").GetString().Should().Contain("not your secret");
    }
}
