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
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class HostMetadataEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string Definition = "{\"nodes\":[],\"edges\":[]}";

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
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new PortlessBlock("noop"));
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

    private async Task<int> CountWorkflowsAsync()
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows.CountAsync();
    }

    private static Dictionary<string, string>? MetadataOf(JsonElement workflow) =>
        workflow.GetProperty("hostMetadata").ValueKind == JsonValueKind.Null
            ? null
            : workflow.GetProperty("hostMetadata").Deserialize<Dictionary<string, string>>();

    private async Task<JsonElement> ReadAsync(string url)
    {
        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Metadata_round_trips_through_create_update_and_the_read_response()
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "Ship", definitionJson = Definition, hostMetadata = new Dictionary<string, string> { ["folderId"] = "f1", ["team.a-b_c"] = "x" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var createdBody = (await created.Content.ReadFromJsonAsync<JsonElement>())!;
        var id = createdBody.GetProperty("id").GetGuid();
        var expected = new Dictionary<string, string> { ["folderId"] = "f1", ["team.a-b_c"] = "x" };

        MetadataOf(createdBody).Should().BeEquivalentTo(expected);
        MetadataOf(await ReadAsync($"/api/read/{id}")).Should().BeEquivalentTo(expected);

        using var updated = await client.PutAsJsonAsync($"/api/workflows/{id}", new
        {
            name = "Ship", definitionJson = Definition, hostMetadata = new Dictionary<string, string> { ["folderId"] = "f2" },
        });
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        MetadataOf((await updated.Content.ReadFromJsonAsync<JsonElement>())!).Should().BeEquivalentTo(new Dictionary<string, string> { ["folderId"] = "f2" });
        MetadataOf(await ReadAsync($"/api/read/{id}")).Should().BeEquivalentTo(new Dictionary<string, string> { ["folderId"] = "f2" });
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("")]
    public async Task An_update_with_an_empty_map_a_null_or_no_member_clears_the_metadata_and_reads_back_null(string member)
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "Ship", definitionJson = Definition, hostMetadata = new Dictionary<string, string> { ["a"] = "1" },
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        var body = "{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\""
            + (member.Length == 0 ? "" : $",\"hostMetadata\":{member}") + "}";

        using var updated = await client.PutAsync($"/api/workflows/{id}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        MetadataOf((await updated.Content.ReadFromJsonAsync<JsonElement>())!).Should().BeNull();
        MetadataOf(await ReadAsync($"/api/read/{id}")).Should().BeNull();
    }

    [Fact]
    public async Task A_create_with_an_empty_map_reads_back_null()
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "Ship", definitionJson = Definition, hostMetadata = new Dictionary<string, string>(),
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        MetadataOf(await ReadAsync($"/api/read/{id}")).Should().BeNull();
    }

    public static TheoryData<string> RefusedMaps()
    {
        var data = new TheoryData<string>();
        data.Add("{" + string.Join(",", Enumerable.Range(0, 17).Select(i => $"\"k{i}\":\"v\"")) + "}");
        data.Add("{\"1abc\":\"v\"}");
        data.Add("{\"a b\":\"v\"}");
        data.Add("{\"" + new string('a', 65) + "\":\"v\"}");
        data.Add("{\"note\":\"" + new string('x', 257) + "\"}");
        data.Add("{\"note\":\"line\\nbreak\"}");
        data.Add("{\"note\":null}");
        return data;
    }

    [Theory]
    [MemberData(nameof(RefusedMaps))]
    public async Task A_create_over_a_limit_is_a_400_with_the_code_and_stores_nothing(string map)
    {
        var body = "{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\",\"hostMetadata\":" + map + "}";

        using var response = await client.PostAsync("/api/workflows", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("code").GetString().Should().Be("host-metadata-invalid");
        (await CountWorkflowsAsync()).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(RefusedMaps))]
    public async Task An_update_over_a_limit_is_a_400_with_the_code_and_keeps_the_stored_metadata(string map)
    {
        using var created = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "Ship", definitionJson = Definition, hostMetadata = new Dictionary<string, string> { ["a"] = "1" },
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        var body = "{\"name\":\"Renamed\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\",\"hostMetadata\":" + map + "}";

        using var response = await client.PutAsync($"/api/workflows/{id}", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("code").GetString().Should().Be("host-metadata-invalid");
        var stored = await ReadAsync($"/api/read/{id}");
        stored.GetProperty("name").GetString().Should().Be("Ship");
        MetadataOf(stored).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });
    }

    [Fact]
    public async Task The_refusal_names_the_entry_it_refused()
    {
        var body = "{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\",\"hostMetadata\":{\"ok\":\"1\",\"note\":\"" + new string('x', 257) + "\"}}";

        using var response = await client.PostAsync("/api/workflows", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        (await response.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("detail").GetString().Should().Contain("'note'");
    }

    [Fact]
    public async Task A_request_carrying_a_folderId_is_a_400_on_create_and_update()
    {
        const string folder = ",\"folderId\":\"22222222-2222-2222-2222-222222222222\"";
        var create = "{\"name\":\"Ship\",\"definitionJson\":\"{\\\"nodes\\\":[],\\\"edges\\\":[]}\"" + folder + "}";
        using var refusedCreate = await client.PostAsync("/api/workflows", new StringContent(create, System.Text.Encoding.UTF8, "application/json"));
        using var created = await client.PostAsJsonAsync("/api/workflows", new { name = "Ship", definitionJson = Definition });

        using var refusedUpdate = await client.PutAsync(created.Headers.Location!.ToString(), new StringContent(create, System.Text.Encoding.UTF8, "application/json"));

        refusedCreate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        refusedUpdate.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountWorkflowsAsync()).Should().Be(1);
    }
}
