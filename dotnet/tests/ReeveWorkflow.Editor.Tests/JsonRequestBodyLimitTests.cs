using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class JsonRequestBodyLimitTests : IAsyncLifetime
{
    private SqliteConnection connection = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Critical);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 300);
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new PortlessBlock("noop"));
        builder.Services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(db => db.UseSqlite(connection));
        })
        .AddWorkflowEngine()
        .AddWorkflowEditor();

        app = builder.Build();
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        app.UseExceptionHandler(handler => handler.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        }));
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
            return next(context);
        });
        app.MapWorkflowEditorApi("api/workflows").AllowAnonymous();
        app.MapWorkflowApi("api/read").AllowAnonymous();
        app.MapWorkflowExpressionApi("api/expressions").AllowAnonymous();

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
    [InlineData("POST", "/api/workflows")]
    [InlineData("PUT", "/api/workflows/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/workflows/analysis")]
    [InlineData("POST", "/api/read/import/validate")]
    [InlineData("POST", "/api/read/00000000-0000-0000-0000-000000000001/execute")]
    [InlineData("POST", "/api/expressions/preview")]
    public async Task A_body_over_the_servers_limit_is_a_413_under_a_host_exception_handler(string verb, string url)
    {
        var body = $$"""{"name":"{{new string('x', 2048)}}"}""";
        using var request = new HttpRequestMessage(new HttpMethod(verb), url)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, because: await response.Content.ReadAsStringAsync());
    }
}
