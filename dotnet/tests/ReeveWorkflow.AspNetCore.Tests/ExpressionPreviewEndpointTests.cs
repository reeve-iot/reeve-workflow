using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.AspNetCore.Tests;

public sealed class ExpressionPreviewEndpointTests
{
    public sealed record Item(string Name);

    public static TheoryData<string> Values() => new() { "map", "record", "enum-map", "nan-map", "ip-address" };

    private static object Make(string name) => name switch
    {
        "map" => new Dictionary<string, object?> { ["b"] = 1, ["a"] = new List<object?> { "x" } },
        "record" => new Item("a"),
        "enum-map" => new Dictionary<string, object?> { ["d"] = DayOfWeek.Friday },
        "nan-map" => new Dictionary<string, object?> { ["x"] = double.NaN },
        _ => IPAddress.Parse("10.0.0.1"),
    };

    [Theory]
    [MemberData(nameof(Values))]
    public async Task An_object_is_no_result_and_is_answered_as_the_rule_that_refuses_it(string name)
    {
        var (status, body) = await PreviewAsync(Context(Make(name)), """{"expression":"${probe.x}","contextScope":"scope"}""");

        status.Should().Be(StatusCodes.Status200OK);
        using var answer = JsonDocument.Parse(body);
        var root = answer.RootElement;
        root.GetProperty("ok").GetBoolean().Should().BeFalse();
        root.GetProperty("error").GetProperty("ruleId").GetString().Should().Be("expression-type-mismatch");
        root.GetProperty("error").GetProperty("message").GetString().Should().Contain("Json");
    }

    [Fact]
    public async Task A_well_typed_expression_answers_its_term_and_its_value()
    {
        var (status, body) = await PreviewAsync(Context(), """{"expression":"2 * 2.5","contextScope":"scope"}""");

        status.Should().Be(StatusCodes.Status200OK);
        using var answer = JsonDocument.Parse(body);
        var root = answer.RootElement;
        root.GetProperty("ok").GetBoolean().Should().BeTrue();
        root.GetProperty("type").GetString().Should().Be("Number");
        root.GetProperty("value").GetDouble().Should().Be(5);
        root.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("${parameter.x}")]
    [InlineData("${parameter.x} * 2.5")]
    [InlineData("${previous.n.x}")]
    [InlineData("${previous.n.x} + 1")]
    public async Task A_read_of_a_node_output_or_a_parameter_is_out_of_scope(string expression)
    {
        var (status, body) = await PreviewAsync(Context(), $$"""{"expression":"{{expression}}","contextScope":"scope"}""");

        status.Should().Be(StatusCodes.Status200OK);
        using var answer = JsonDocument.Parse(body);
        var root = answer.RootElement;
        root.GetProperty("ok").GetBoolean().Should().BeFalse();
        root.GetProperty("type").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("ruleId").GetString().Should().Be("expression-out-of-scope");
    }

    [Fact]
    public async Task A_refused_expression_answers_the_rule_it_breaks_and_a_failing_one_the_runtime_code()
    {
        var (_, refused) = await PreviewAsync(Context(), """{"expression":"1 + true","contextScope":"scope"}""");
        var (_, failed) = await PreviewAsync(Context(), """{"expression":"1 % 0","contextScope":"scope"}""");

        using var first = JsonDocument.Parse(refused);
        first.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        first.RootElement.GetProperty("error").GetProperty("ruleId").GetString().Should().Be("expression-type-mismatch");
        using var second = JsonDocument.Parse(failed);
        second.RootElement.GetProperty("error").GetProperty("ruleId").GetString().Should().Be("expression-runtime-error");
    }

    [Theory]
    [InlineData("{\"expression\":", "The request body could not be read: reading stopped at $.expression (line 1, byte 15).")]
    [InlineData("{\"expression\":5}", "The request body could not be read: reading stopped at $.expression (line 1, byte 16).")]
    public async Task A_body_that_is_not_valid_JSON_is_a_400_problem_with_a_fixed_detail(string body, string detail)
    {
        var (status, text) = await PreviewAsync(Context(), body);

        status.Should().Be(StatusCodes.Status400BadRequest, because: text);
        using var problem = JsonDocument.Parse(text);
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
        problem.RootElement.GetProperty("detail").GetString().Should().Be(detail);
        text.Should().NotContain("ReeveWorkflow.").And.NotContain(".NET");
    }

    private static ExpressionContext Context(object? probed = null) => new ExpressionContext.Builder()
        .Namespace(new Probe(probed), 1, new ProbeCheck()).Build();

    private sealed class Probe(object? value) : IExpressionNamespaceResolver
    {
        public string Namespace => "probe";

        public ResolveResult Resolve(string[] segments) => ResolveResult.Resolved(value);
    }

    private sealed class ProbeCheck : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "probe";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(NamespacePathCheck.Yes, "Json?");
    }

    private static async Task<(int Status, string Body)> PreviewAsync(ExpressionContext value, string? rawBody = null)
    {
        var builder = WebApplication.CreateBuilder();
        var previews = new ExpressionPreviewService();
        previews.SetContext("scope", value);
        builder.Services.AddSingleton(previews);
        var app = builder.Build();
        app.MapWorkflowExpressionApi("/api/expressions").AllowAnonymous();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText!.EndsWith("preview", StringComparison.Ordinal));

        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody());
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        var request = Encoding.UTF8.GetBytes(
            rawBody ?? """{"expression":"1","contextScope":"scope"}""");
        context.Request.Body = new MemoryStream(request);
        context.Request.ContentLength = request.Length;
        var response = new MemoryStream();
        context.Response.Body = response;
        await endpoint.RequestDelegate!(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(response.ToArray()));
    }

    private sealed class HasBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
