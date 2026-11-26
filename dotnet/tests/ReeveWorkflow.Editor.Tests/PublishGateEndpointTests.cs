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
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class PublishGateEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Id = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly IWorkflowEditor editor = Substitute.For<IWorkflowEditor>();
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
        builder.Services.AddScoped(_ => editor);
        builder.Services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(db => db.UseSqlite(connection));
        })
        .AddWorkflowEditor();

        app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Owner.ToString())], "test"));
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

    [Theory]
    [InlineData("publish")]
    [InlineData("rollback")]
    public async Task A_version_that_does_not_compile_is_a_400_carrying_the_diagnostics(string door)
    {
        var refusal = new WorkflowDoesNotCompileException(
            [new CompilationDiagnostic(CompilationRules.BlockTypeUnknown, "Block type 'gone' is not registered.", "n1")]);
        editor.PublishAsync(Owner, Id, Arg.Any<CancellationToken>()).ThrowsAsync(refusal);
        editor.RollbackAsync(Owner, Id, 1, Arg.Any<CancellationToken>()).ThrowsAsync(refusal);

        using var response = await SendAsync(door);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var diagnostic = body.RootElement.GetProperty(WorkflowDoesNotCompileException.ProblemExtension)[0];
        diagnostic.GetProperty("ruleId").GetString().Should().Be(CompilationRules.BlockTypeUnknown);
        diagnostic.GetProperty("nodeId").GetString().Should().Be("n1");
    }

    [Theory]
    [InlineData("host-says-no")]
    [InlineData("host-validator-faulted")]
    public async Task A_rollback_the_host_validator_refused_is_a_400_carrying_the_diagnostics_as_a_refused_publish_is(string code)
    {
        editor.RollbackAsync(Owner, Id, 1, Arg.Any<CancellationToken>()).Returns(Unpublished() with
        {
            Diagnostics = [new CompilationDiagnostic(code, "The host's validator refused the copy.", "n1")],
        });

        using var response = await SendAsync("rollback");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var diagnostic = body.RootElement.GetProperty(WorkflowDoesNotCompileException.ProblemExtension)[0];
        (diagnostic.GetProperty("ruleId").GetString(), diagnostic.GetProperty("nodeId").GetString()).Should().Be((code, "n1"));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("publish")]
    [InlineData("rollback")]
    public async Task A_write_another_overtook_is_a_409(string door)
    {
        var conflict = new WorkflowDraftChangedException(Id);
        editor.CreateAsync(Owner, Arg.Any<CreateWorkflowRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(conflict);
        editor.UpdateAsync(Owner, Id, Arg.Any<UpdateWorkflowRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(conflict);
        editor.PublishAsync(Owner, Id, Arg.Any<CancellationToken>()).ThrowsAsync(conflict);
        editor.RollbackAsync(Owner, Id, 1, Arg.Any<CancellationToken>()).ThrowsAsync(conflict);

        using var response = await SendAsync(door);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("changed by another write");
        JsonDocument.Parse(problem).RootElement.TryGetProperty("code", out _).Should().BeFalse("a changed draft is not a revision mismatch");
    }

    [Fact]
    public async Task A_save_from_a_copy_another_write_moved_is_a_409()
    {
        editor.UpdateAsync(Owner, Id, Arg.Is<UpdateWorkflowRequest>(r => r.ExpectedRevision == 4), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkflowRevisionMismatchException(Id, 4));

        using var response = await client.PutAsJsonAsync($"/api/workflows/{Id}",
            new { name = "Ship", definitionJson = "{\"nodes\":[],\"edges\":[]}", expectedRevision = 4 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Contain("no longer at revision 4");
        body.RootElement.GetProperty("code").GetString().Should().Be(Core.Hosting.WorkflowEditConflictCodes.RevisionMismatch);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("rollback")]
    public async Task A_publish_inside_a_transaction_the_host_holds_is_a_500_problem(string door)
    {
        var refusal = new WorkflowPublishInHostTransactionException(Id);
        editor.PublishAsync(Owner, Id, Arg.Any<CancellationToken>()).ThrowsAsync(refusal);
        editor.RollbackAsync(Owner, Id, 1, Arg.Any<CancellationToken>()).ThrowsAsync(refusal);

        using var response = await SendAsync(door);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("detail").GetString().Should().Contain("inside a transaction the host commits");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task A_save_whose_publish_could_not_be_finished_is_a_503_carrying_the_saved_draft_not_the_fault(string door)
    {
        Answer(Unpublished() with { Fault = new InvalidOperationException("connection reset by db-7") });

        using var response = await SendAsync(door);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(5));
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("db-7");
        using var body = JsonDocument.Parse(text);
        (body.RootElement.GetProperty("versionId").GetGuid(), body.RootElement.GetProperty("versionNumber").GetInt32())
            .Should().Be((Draft, 2));
        body.RootElement.GetProperty("workflow").GetProperty("id").GetGuid().Should().Be(Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_save_that_did_what_it_asked_answers_with_the_workflow_whatever_its_fault(bool autoPublish, bool published)
    {
        Answer(Unpublished() with { VersionPublished = published, Fault = new InvalidOperationException("late") });

        using var response = await client.PutAsJsonAsync($"/api/workflows/{Id}",
            new { name = "Ship", definitionJson = "{\"nodes\":[],\"edges\":[]}", autoPublish });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("id").GetGuid().Should().Be(Id);
    }

    [Fact]
    public async Task A_save_deferred_by_the_hosts_transaction_answers_with_the_workflow()
    {
        Answer(Unpublished() with { PublishDeferred = true });

        using var response = await SendAsync("create");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private static readonly Guid Draft = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static WorkflowEditResult Unpublished() => new(
        new ReeveWorkflow.Abstractions.WorkflowResponse(
            Id, "Ship", null, null, "{}", null, null, false, null, null, null, Owner,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, Draft, DraftVersionNumber: 2),
        Draft, 2, VersionPublished: false);

    private void Answer(WorkflowEditResult result)
    {
        editor.CreateAsync(Owner, Arg.Any<CreateWorkflowRequest>(), Arg.Any<CancellationToken>()).Returns(result);
        editor.UpdateAsync(Owner, Id, Arg.Any<UpdateWorkflowRequest>(), Arg.Any<CancellationToken>()).Returns(result);
    }

    private Task<HttpResponseMessage> SendAsync(string door) => door switch
    {
        "create" => client.PostAsJsonAsync("/api/workflows", new { name = "Ship", definitionJson = "{\"nodes\":[],\"edges\":[]}" }),
        "update" => client.PutAsJsonAsync($"/api/workflows/{Id}", new { name = "Ship", definitionJson = "{\"nodes\":[],\"edges\":[]}" }),
        "publish" => client.PostAsync($"/api/workflows/{Id}/publish", null),
        "rollback" => client.PostAsync($"/api/workflows/{Id}/versions/1/rollback", null),
        _ => throw new ArgumentOutOfRangeException(nameof(door)),
    };
}
