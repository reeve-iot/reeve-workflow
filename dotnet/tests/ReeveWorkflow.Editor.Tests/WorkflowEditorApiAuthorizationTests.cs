using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ReeveWorkflow.Editor.Tests;

public sealed class WorkflowEditorApiAuthorizationTests
{
    private static IReadOnlyList<Endpoint> Build(Action<RouteGroupBuilder>? state)
    {
        var app = WebApplication.CreateBuilder().Build();
        var group = app.MapWorkflowEditorApi("/api/workflows");
        state?.Invoke(group);

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
    }

    [Fact]
    public void Mapping_it_without_an_authorisation_intent_is_refused()
    {
        var build = () => Build(state: null);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*without an authorisation intent*",
                because: "a host that forgot to gate the write surface is not the party that pays for it");
    }

    [Fact]
    public void The_refusal_names_what_to_write_instead()
    {
        var build = () => Build(state: null);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*RequireAuthorization*").WithMessage("*AllowAnonymous*");
    }

    [Fact]
    public void A_policy_on_the_group_satisfies_it()
    {
        var build = () => Build(group => group.RequireAuthorization("workflows:write"));

        build.Should().NotThrow();
    }

    [Fact]
    public void Saying_anonymous_out_loud_satisfies_it_too()
    {
        var build = () => Build(group => group.AllowAnonymous());

        build.Should().NotThrow(
            because: "an open write surface is a decision a host is allowed to take; what is refused is "
                     + "taking it by omission");
    }

    [Fact]
    public void Every_endpoint_in_the_group_is_covered_by_the_one_statement()
    {
        var endpoints = Build(group => group.RequireAuthorization("workflows:write"));

        endpoints.Should().HaveCount(9, "five writes, the two compilation reads, the port-type vocabulary and the draft analysis");
        endpoints.Should().OnlyContain(e =>
            e.Metadata.Any(m => m is Microsoft.AspNetCore.Authorization.IAuthorizeData),
            because: "mounting one endpoint at a time is what lets a host gate eight of nine");
    }
}
