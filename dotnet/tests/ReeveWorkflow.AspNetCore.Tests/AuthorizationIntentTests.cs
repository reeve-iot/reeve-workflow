using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ReeveWorkflow.AspNetCore.Tests;

public sealed class AuthorizationIntentTests
{
    public static TheoryData<string, string> Mappings => new()
    {
        { nameof(WorkflowApiEndpoints.MapWorkflowApi), "workflow API" },
        { nameof(WorkflowBlockApiEndpoints.MapWorkflowBlockApi), "workflow block API" },
        { nameof(WorkflowExpressionApiEndpoints.MapWorkflowExpressionApi), "workflow expression API" },
    };

    private static RouteGroupBuilder Map(WebApplication app, string mapping) => mapping switch
    {
        nameof(WorkflowApiEndpoints.MapWorkflowApi) => app.MapWorkflowApi("/api/workflows"),
        nameof(WorkflowBlockApiEndpoints.MapWorkflowBlockApi) => app.MapWorkflowBlockApi("/api/workflow-blocks"),
        nameof(WorkflowExpressionApiEndpoints.MapWorkflowExpressionApi) =>
            app.MapWorkflowExpressionApi("/api/expressions"),
        _ => throw new ArgumentOutOfRangeException(nameof(mapping), mapping, null),
    };

    private static IReadOnlyList<Endpoint> Build(string mapping, Action<RouteGroupBuilder>? state)
    {
        var app = WebApplication.CreateBuilder().Build();
        var group = Map(app, mapping);
        state?.Invoke(group);

        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).ToList();
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Mapping_without_an_authorisation_intent_is_refused_and_names_the_api(string mapping, string api)
    {
        var build = () => Build(mapping, state: null);

        build.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains($"The {api} was mapped without an authorisation intent")
                        && e.Message.Contains(mapping)
                        && e.Message.Contains("RequireAuthorization")
                        && e.Message.Contains("AllowAnonymous"),
                because: "a host that forgot to gate the surface is told which call to fix and what to write");
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public void A_policy_on_the_group_satisfies_it(string mapping, string api)
    {
        _ = api;
        var endpoints = Build(mapping, group => group.RequireAuthorization("workflows:read"));

        endpoints.Should().NotBeEmpty().And.OnlyContain(
            e => e.Metadata.Any(m => m is IAuthorizeData),
            because: "the one statement on the group covers every endpoint in it");
    }

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Saying_anonymous_out_loud_satisfies_it_too(string mapping, string api)
    {
        _ = api;
        var endpoints = Build(mapping, group => group.AllowAnonymous());

        endpoints.Should().NotBeEmpty().And.OnlyContain(e => e.Metadata.Any(m => m is IAllowAnonymous));
    }
}
