using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ReeveWorkflow.AspNetCore;
using ReeveWorkflow.AspNetCore.Tests;

namespace ReeveWorkflow.Editor.Tests;

public sealed class WorkflowEditorApiSurfaceTests
{
    internal const string EditorPrefix = "/api/workflows";

    internal const string Source = "ReeveWorkflow.Editor.WorkflowEditorApiEndpoints";

    internal static IReadOnlyList<Endpoint> Mapped()
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapWorkflowEditorApi(EditorPrefix).AllowAnonymous();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
    }

    private static WorkflowApiSurface.Surface Describe() =>
        WorkflowApiSurface.Describe(Source, Mapped(), EditorPrefix.TrimStart('/'));

    private static string CommittedPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src",
            "ReeveWorkflow.Editor", "workflow-editor-api-surface.json"));

    [Fact]
    public void The_committed_surface_describes_the_endpoints_this_package_maps()
    {
        var expected = WorkflowApiSurface.ToJson(Describe());
        var path = CommittedPath();
        var actual = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

        if (actual != expected)
            File.WriteAllText(path + ".actual", expected);

        actual.Should().Be(expected,
            because: $"the client is generated from this document; regenerated output is at {path}.actual");
    }

    [Fact]
    public void Every_mapped_endpoint_reaches_the_document()
    {
        var mapped = Mapped()
            .OfType<RouteEndpoint>()
            .Select(e => (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Describe().Endpoints.Select(e => e.Path).Order(StringComparer.Ordinal).Should().Equal(mapped,
            because: "an endpoint the prefix filter drops is one the generated client never learns about");
    }

    [Fact]
    public void Every_endpoint_declares_what_it_answers_with()
    {
        Describe().Endpoints.Should().OnlyContain(e => e.Responses.Count > 0,
            because: "a minimal API carries no response metadata by itself; an endpoint that declares none "
                     + "is generated as returning nothing, whatever it really returns");
    }

    [Fact]
    public void No_operation_name_collides_with_one_the_executor_publishes()
    {
        var executorApp = WebApplication.CreateBuilder().Build();
        executorApp.MapWorkflowApi("/api/workflows").AllowAnonymous();
        executorApp.MapWorkflowBlockApi("/api/workflow-blocks").AllowAnonymous();
        executorApp.MapWorkflowExpressionApi("/api/expressions").AllowAnonymous();
        var executor = WorkflowApiSurface.Describe(
            ((IEndpointRouteBuilder)executorApp).DataSources.SelectMany(d => d.Endpoints),
            "api/workflows", "api/workflow-blocks", "api/expressions");

        executor.Endpoints.Should().HaveCountGreaterThan(10,
            because: "a corpus that shrank to one endpoint would make the assertion below pass by default");

        Describe().Endpoints.Select(e => e.Operation).Should()
            .NotIntersectWith(executor.Endpoints.Select(e => e.Operation),
                because: "both documents generate methods onto one client, and a shared name would make "
                         + "one of the two operations unreachable rather than merely ambiguous");
    }
}
