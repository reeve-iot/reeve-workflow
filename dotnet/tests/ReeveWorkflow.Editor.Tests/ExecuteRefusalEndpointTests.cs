using System.Net;
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
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class ExecuteRefusalEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-24T12:00:00Z");

    private readonly HostCheck check = new();
    private readonly StartHost startHost = new();
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
                options.Seams.ExpressionNamespaces.Add(HostNamespaceRegistration.For(check));
                options.Seams.StartAdmission.UseInstance(startHost);
            })
            .AddWorkflowEngine();

        app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Owner.ToString())], "test"));
            return next(context);
        });
        app.MapWorkflowApi("api/workflows").AllowAnonymous();

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
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task A_start_whose_version_does_not_compile_is_a_400_carrying_the_diagnostics_and_leaves_its_row()
    {
        var (id, version) = await SeedAsync(Graph("gone", "gone.block", configuration: null));

        using var response = await client.PostAsync($"/api/workflows/{id}/execute", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var diagnostic = body.RootElement.GetProperty(WorkflowDoesNotCompileException.ProblemExtension)[0];
        diagnostic.GetProperty("ruleId").GetString().Should().Be(CompilationRules.BlockTypeUnknown);
        diagnostic.GetProperty("nodeId").GetString().Should().Be("gone");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var row = await db.WorkflowExecutionLogs.AsNoTracking().SingleAsync(l => l.WorkflowId == id);
        (row.WorkflowVersionId, row.Status, row.FailureReason)
            .Should().Be((version, WorkflowExecutionStatus.Failed, WorkflowFailureReasons.DidNotCompile));
        (await db.WorkflowExecutionLogEntries.AsNoTracking().Where(e => e.ExecutionLogId == row.Id).Select(e => e.NodeId).ToListAsync())
            .Should().Equal("gone");
    }

    [Fact]
    public async Task A_start_whose_arguments_do_not_read_as_the_parameters_is_a_400_carrying_the_findings_and_leaves_its_row()
    {
        var (id, version) = await SeedAsync(Graph("x", "noop", configuration: null), """[{"name":"Count","type":"Int","isRequired":true}]""");
        using var content = new StringContent("""{"inputParameters":{"Count":"seven","Stranger":1}}""", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync($"/api/workflows/{id}/execute", content);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        text.Should().NotContain("seven");
        using var body = JsonDocument.Parse(text);
        body.RootElement.GetProperty(WorkflowStartFindingsException.ProblemExtension).EnumerateArray()
            .Select(d => d.GetProperty("ruleId").GetString()).Should()
            .BeEquivalentTo([WorkflowStartArgumentCodes.TypeMismatch, WorkflowStartArgumentCodes.Unknown]);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var row = await db.WorkflowExecutionLogs.AsNoTracking().SingleAsync(l => l.WorkflowId == id);
        (row.WorkflowVersionId, row.Status, row.FailureReason)
            .Should().Be((version, WorkflowExecutionStatus.Failed, WorkflowFailureReasons.StartArgumentsInvalid));
    }

    [Fact]
    public async Task A_start_whose_compile_cannot_be_answered_is_a_503_with_retry_after_and_leaves_its_row()
    {
        check.Fault = new InvalidOperationException("check down");
        var (id, _) = await SeedAsync(Graph("x", "noop", configuration: new { v = "expr:${host.q}" }));

        using var response = await client.PostAsync($"/api/workflows/{id}/execute", null);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        (await response.Content.ReadAsStringAsync()).Should().NotContain("check down", "a host's fault is logged, not served");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        (await db.WorkflowExecutionLogs.AsNoTracking().SingleAsync(l => l.WorkflowId == id)).FailureReason
            .Should().Be(WorkflowFailureReasons.CompilationUnanswered);
    }

    [Theory]
    [InlineData("{\"debugMode\":\"yes\"}", "The request body could not be read: reading stopped at $.debugMode (line 1, byte 19).")]
    [InlineData("{\"inputParameters\":", "The request body could not be read: reading stopped at $.inputParameters (line 1, byte 20).")]
    [InlineData("{\"inputParameters\":{\"Count\":1,\"Count\":2}}", "The request body could not be read: reading stopped at $.inputParameters.Count (line 1, byte 40).")]
    [InlineData("{\"debugMode\":true,\"debugMode\":false}", "The request body could not be read: reading stopped at $.debugMode (line 1, byte 31).")]
    [InlineData("{\"inputParameters\":{\"Doc\":{\"a\":{\"k\":1,\"k\":2}}}}", "The request body could not be read: reading stopped at $.inputParameters.Doc (line 1, byte 46).")]
    public async Task A_body_that_cannot_be_bound_is_a_400_in_the_compile_problem_shape_and_asks_the_host_nothing(
        string body, string detail)
    {
        var (id, _) = await SeedAsync(Graph("x", "noop", configuration: null));
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync($"/api/workflows/{id}/execute", content);

        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: text);
        using var problem = JsonDocument.Parse(text);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        problem.RootElement.GetProperty("diagnostics").GetArrayLength().Should().Be(0);
        problem.RootElement.GetProperty("detail").GetString().Should().Be(detail);
        text.Should().NotContain("ReeveWorkflow.").And.NotContain(".NET");
        startHost.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_start_the_host_admits_runs_and_the_host_was_asked_once_as_a_manual_start()
    {
        var (id, version) = await SeedAsync(Graph("x", "noop", configuration: null));

        using var response = await client.PostAsync($"/api/workflows/{id}/execute", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        startHost.Asked.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new WorkflowStartRequest(id, version, WorkflowStartKind.Manual));
    }

    [Fact]
    public async Task A_start_the_host_refuses_is_a_403_carrying_its_message_and_code_and_leaves_no_row()
    {
        startHost.Decision = WorkflowStartDecision.Refuse("workflow-disabled", "This workflow is disabled.");
        var (id, _) = await SeedAsync(Graph("x", "noop", configuration: null));

        using var response = await client.PostAsync($"/api/workflows/{id}/execute", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Be("This workflow is disabled.");
        body.RootElement.GetProperty(WorkflowStartRefusedException.ProblemExtension).GetString().Should().Be("workflow-disabled");
        startHost.Asked.Should().ContainSingle();

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        (await db.WorkflowExecutionLogs.AsNoTracking().AnyAsync(l => l.WorkflowId == id)).Should().BeFalse();
    }

    [Fact]
    public async Task A_host_that_cannot_be_asked_is_a_503_with_retry_after_and_its_fault_is_not_served()
    {
        startHost.Fault = new InvalidOperationException("entitlements db down");
        var (id, _) = await SeedAsync(Graph("x", "noop", configuration: null));

        using var response = await client.PostAsync($"/api/workflows/{id}/execute", null);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        (await response.Content.ReadAsStringAsync()).Should().NotContain("entitlements");
    }

    private async Task<(Guid Workflow, Guid Version)> SeedAsync(string definitionJson, string? inputParametersJson = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(Guid.NewGuid(), "wf", null, Owner, T0);
        var version = new WorkflowVersion(Guid.NewGuid(), workflow.Id, 1, definitionJson, inputParametersJson, null, Owner, T0, isPublished: true);
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();
        workflow.SetCurrentVersion(version, T0);
        await db.SaveChangesAsync();
        return (workflow.Id, version.Id);
    }

    private static readonly object position = new { x = 0, y = 0 };

    private static string Graph(string id, string blockTypeId, object? configuration) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId, position, configuration = new { } },
            new { id, blockTypeId, position, configuration = configuration ?? new { } }
        },
        edges = new object[] { new { id = "e", sourceNodeId = "start", targetNodeId = id, mappings = new { } } }
    });

    private sealed class StartHost : IWorkflowStartAdmission
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<WorkflowStartRequest> asked = new();

        public IReadOnlyCollection<WorkflowStartRequest> Asked => [.. asked];

        public WorkflowStartDecision Decision { get; set; } = WorkflowStartDecision.Admit;

        public Exception? Fault { get; set; }

        public ValueTask<WorkflowStartDecision> DecideAsync(WorkflowStartRequest request, CancellationToken cancellationToken)
        {
            asked.Enqueue(request);
            return Fault is { } fault ? throw fault : ValueTask.FromResult(Decision);
        }
    }

    private sealed class HostCheck : IExpressionNamespaceStaticCheck
    {
        public Exception? Fault { get; set; }

        public string Namespace => "host";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            Fault is { } fault ? throw fault : new(NamespacePathCheck.Yes, "String");
    }
}
