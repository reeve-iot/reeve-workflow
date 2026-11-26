using System.Collections;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class MapStartupRefusalTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Filename=:memory:");

    public Task InitializeAsync() => connection.OpenAsync();

    public async Task DisposeAsync() => await connection.DisposeAsync();

    private void AddLibrary(IServiceCollection services) =>
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(db => db.UseSqlite(connection));
        }).AddWorkflowEditor();

    private WebApplication NewApp()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Critical);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        AddLibrary(builder.Services);
        return builder.Build();
    }

    private IHost NewGenericHost(Action<IEndpointRouteBuilder> map) =>
        Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Critical))
            .ConfigureWebHost(web => web
                .UseKestrel()
                .UseUrls("http://127.0.0.1:0")
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    AddLibrary(services);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(map);
                }))
            .Build();

    public static TheoryData<string> Apis => new()
    {
        "workflow API", "workflow block API", "workflow expression API", "workflow editor API",
    };

    private static RouteGroupBuilder Map(IEndpointRouteBuilder routes, string api) => api switch
    {
        "workflow API" => routes.MapWorkflowApi("workflows"),
        "workflow block API" => routes.MapWorkflowBlockApi("workflow-blocks"),
        "workflow expression API" => routes.MapWorkflowExpressionApi("expressions"),
        "workflow editor API" => routes.MapWorkflowEditorApi("editor"),
        _ => throw new ArgumentOutOfRangeException(nameof(api), api, null),
    };

    private static async Task ShouldRefuseToStart(Func<Task> start, string api) =>
        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Where(e => e.Message.Contains($"The {api} was mapped without an authorisation intent"),
                because: "the deploy names the API, instead of every route answering 500 once traffic arrives");

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task The_application_itself_does_not_start_bare(string api)
    {
        await using var app = NewApp();
        Map(app, api);

        await ShouldRefuseToStart(() => app.StartAsync(), api);
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task A_parent_group_does_not_start_bare(string api)
    {
        await using var app = NewApp();
        app.MapGet("/health", () => "ok");
        Map(app.MapGroup("/api"), api);

        await ShouldRefuseToStart(() => app.StartAsync(), api);
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task UseEndpoints_on_the_application_does_not_start_bare(string api)
    {
        await using var app = NewApp();
        app.UseRouting();
        app.UseEndpoints(routes => Map(routes, api));

        await ShouldRefuseToStart(() => app.StartAsync(), api);
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task A_generic_host_with_UseEndpoints_does_not_start_bare(string api)
    {
        using var host = NewGenericHost(routes => Map(routes, api));

        await ShouldRefuseToStart(() => host.StartAsync(), api);
    }

    [Fact]
    public async Task A_parent_group_that_states_a_policy_starts()
    {
        await using var app = NewApp();
        app.MapGroup("/api").RequireAuthorization("workflows:read").MapWorkflowApi("workflows");

        await app.StartAsync();

        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).Should().NotBeEmpty();
        await app.StopAsync();
    }

    [Fact]
    public async Task A_generic_host_that_says_anonymous_starts()
    {
        using var host = NewGenericHost(routes =>
        {
            routes.MapWorkflowApi("workflows").AllowAnonymous();
            routes.MapWorkflowBlockApi("workflow-blocks").RequireAuthorization("workflows:read");
        });

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Several_mappings_share_one_check_and_the_bare_one_is_named()
    {
        await using var app = NewApp();
        app.MapWorkflowApi("a").AllowAnonymous();
        app.MapWorkflowBlockApi("b1").AllowAnonymous();
        app.MapGroup("/g").MapWorkflowBlockApi("b2");
        app.MapWorkflowExpressionApi("e").RequireAuthorization("p");

        CheckCount(app.Services).Should().Be(1, because: "the table is composed once however many APIs are mapped");
        await ShouldRefuseToStart(() => app.StartAsync(), "workflow block API");
    }

    [Fact]
    public async Task Two_hosts_in_one_process_keep_their_own_checks()
    {
        await using var open = NewApp();
        open.MapWorkflowBlockApi("blocks").AllowAnonymous();
        await using var bare = NewApp();
        bare.MapWorkflowBlockApi("blocks");

        ReferenceEquals(Registry(open.Services), Registry(bare.Services)).Should().BeFalse();
        await open.StartAsync();
        await ShouldRefuseToStart(() => bare.StartAsync(), "workflow block API");
        await open.StopAsync();
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task An_authorization_policy_object_in_the_metadata_is_an_intent(string api)
    {
        await using var app = NewApp();
        var policy = new AuthorizationPolicyBuilder().RequireRole("editor").Build();
        Map(app, api).WithMetadata(policy);

        await app.StartAsync();
        await app.StopAsync();
    }

    [Theory]
    [MemberData(nameof(Apis))]
    public async Task A_requirement_data_object_in_the_metadata_is_an_intent(string api)
    {
        await using var app = NewApp();
        Map(app, api).WithMetadata(new RoleRequirementData());

        await app.StartAsync();
        await app.StopAsync();
    }

    private sealed class RoleRequirementData : IAuthorizationRequirementData
    {
        public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new RolesAuthorizationRequirement(["editor"])];
    }

    private static int CheckCount(IServiceProvider services)
    {
        var type = typeof(WorkflowCompilationService).Assembly.GetType("ReeveWorkflow.Core.Hosting.WorkflowStartupChecks", true)!;
        var registry = services.GetRequiredService(type);
        return ((ICollection)type.GetField("checks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(registry)!).Count;
    }

    private static object Registry(IServiceProvider services) => services.GetRequiredService(
        typeof(WorkflowCompilationService).Assembly.GetType("ReeveWorkflow.Core.Hosting.WorkflowStartupChecks", true)!);
}
