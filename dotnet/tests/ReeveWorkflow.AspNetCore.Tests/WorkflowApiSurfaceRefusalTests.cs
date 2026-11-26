using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ReeveWorkflow.AspNetCore.Tests;

public sealed class WorkflowApiSurfaceRefusalTests
{
    private sealed record Thing(string Name);

    private sealed record OtherThing(int Count);

    private static Thing Handler() => new("x");

    private static Thing Named(Guid id) => new(id.ToString());

    private static Thing Filtered(string? status, int skip = 0) => new($"{status}{skip}");

    private static Thing Posted(OtherThing payload) => new(payload.Count.ToString());

    private static WorkflowApiSurface.Surface Describe(Action<IEndpointRouteBuilder> map)
    {
        var app = WebApplication.CreateBuilder().Build();
        map(app);
        return WorkflowApiSurface.Describe(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints), "probe");
    }

    [Fact]
    public void One_status_answering_with_two_shapes_is_refused()
    {
        var describe = () => Describe(app => app
            .MapGet("/probe", Handler)
            .Produces<Thing>()
            .Produces<OtherThing>(StatusCodes.Status200OK));

        describe.Should().Throw<InvalidOperationException>()
            .WithMessage("*200*",
                because: "the client would be generated against whichever declaration the metadata "
                         + "happened to list first, and nothing would say which");
    }

    [Fact]
    public void Two_endpoints_sharing_one_operation_name_are_refused()
    {
        var describe = () => Describe(app =>
        {
            app.MapGet("/probe/a", Handler).Produces<Thing>();
            app.MapGet("/probe/b", Handler).Produces<Thing>();
        });

        describe.Should().Throw<InvalidOperationException>()
            .WithMessage("*handler*",
                because: "two methods collapsing into one leaves the second URL unreachable, which is "
                         + "worse than a method that calls the wrong one");
    }

    [Fact]
    public void A_body_inferred_without_an_attribute_is_the_body_and_not_a_query_parameter()
    {
        var surface = Describe(app => app
            .MapPost("/probe", Posted)
            .Accepts<OtherThing>(isOptional: false, "application/json")
            .Produces<Thing>());

        var endpoint = surface.Endpoints.Single();
        endpoint.RequestBody?.Type.Should().Be("#OtherThing");
        endpoint.QueryParameters.Should().BeEmpty(
            because: "a minimal API infers a complex type as the body with no attribute at all, and a "
                     + "client that also sent it in the query string would put [object Object] there");
    }

    [Fact]
    public void A_nullable_query_parameter_is_optional_even_with_no_default()
    {
        var surface = Describe(app => app.MapGet("/probe", Filtered).Produces<Thing>());

        var byName = surface.Endpoints.Single().QueryParameters.ToDictionary(p => p.Name, p => p.Required);
        byName["status"].Should().BeFalse(
            because: "an absent nullable parameter binds to null, so requiring it would make a caller "
                     + "send a value in order to omit one");
        byName["skip"].Should().BeFalse(because: "it has a default");
    }

    [Fact]
    public void A_route_parameter_stays_required_and_out_of_the_query()
    {
        var surface = Describe(app => app.MapGet("/probe/{id:guid}", Named).Produces<Thing>());

        var endpoint = surface.Endpoints.Single();
        endpoint.PathParameters.Single().Should().Be(
            new WorkflowApiSurface.ParameterDescription("id", "uuid", true));
        endpoint.QueryParameters.Should().BeEmpty();
    }
}
