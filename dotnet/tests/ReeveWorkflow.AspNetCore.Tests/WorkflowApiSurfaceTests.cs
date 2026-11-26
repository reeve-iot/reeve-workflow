using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ReeveWorkflow.AspNetCore.Tests;

public sealed class WorkflowApiSurfaceTests
{
    private const string WorkflowPrefix = "/api/workflows";

    private const string BlockPrefix = "/api/workflow-blocks";

    private const string ExpressionPrefix = "/api/expressions";

    private static IReadOnlyList<Endpoint> Mapped()
    {
        var app = WebApplication.CreateBuilder().Build();
        app.MapWorkflowApi(WorkflowPrefix).AllowAnonymous();
        app.MapWorkflowBlockApi(BlockPrefix).AllowAnonymous();
        app.MapWorkflowExpressionApi(ExpressionPrefix).AllowAnonymous();

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
    }

    private static WorkflowApiSurface.Surface Describe() =>
        WorkflowApiSurface.Describe(Mapped(), WorkflowPrefix.TrimStart('/'), BlockPrefix.TrimStart('/'),
            ExpressionPrefix.TrimStart('/'));

    private static string CommittedPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src",
            "ReeveWorkflow.AspNetCore", "workflow-api-surface.json"));

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
            because: "an endpoint the prefix filter drops is one the generated client never learns about, "
                     + "and nothing else in this suite would notice its absence");
    }

    [Fact]
    public void Every_named_shape_is_described_in_the_same_document()
    {
        var surface = Describe();
        var described = surface.Types.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var named = surface.Endpoints
            .SelectMany(e => e.Responses.Select(r => r.Type)
                .Append(e.RequestBody?.Type)
                .Concat(e.PathParameters.Concat(e.QueryParameters).Select(p => p.Type)))
            .Concat(surface.Types.SelectMany(t => t.Properties.Select(p => p.Type)))
            .SelectMany(t => Regex.Matches(t ?? string.Empty, "#([A-Za-z0-9_]+)").Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        named.Should().OnlyContain(name => described.Contains(name),
            because: "a reference with no shape behind it generates a type name that does not exist, and "
                     + "the generator would emit code that cannot compile");
    }

    [Fact]
    public void Every_endpoint_declares_what_it_answers_with()
    {
        Describe().Endpoints.Should().OnlyContain(e => e.Responses.Count > 0,
            because: "a minimal API carries no response metadata by itself; an endpoint that declares none "
                     + "is generated as returning nothing, whatever it really returns");
    }
}
