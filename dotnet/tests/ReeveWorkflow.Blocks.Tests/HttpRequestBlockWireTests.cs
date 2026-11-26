using FluentAssertions;
using ReeveWorkflow.Abstractions;
using Xunit;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class HttpRequestBlockWireTests
{
    [Fact]
    public async Task A_cookie_a_response_sets_is_not_sent_by_a_later_run_and_an_authors_Cookie_goes_out_as_written()
    {
        using var server = new RawHttpServer(number => number == 1 ? "Set-Cookie: s=1; Path=/\r\n" : "");

        await Send(server.Url);
        await Send(server.Url);
        await Send(server.Url, ("Cookie", "a=2"));

        var requests = server.Requests;
        requests.Should().HaveCount(3);
        CookieLines(requests[1]).Should().BeEmpty("a cookie one run received must not reach another run");
        CookieLines(requests[2]).Should().Equal("Cookie: a=2");
    }

    [Fact]
    public async Task An_empty_value_goes_out_as_an_empty_field()
    {
        using var server = new RawHttpServer();

        await Send(server.Url, ("X-Empty", ""), ("Accept", ""));

        server.Requests.Single().Split("\r\n").Should().Contain(["X-Empty: ", "Accept: "]);
    }

    private static string[] CookieLines(string request) =>
        request.Split("\r\n").Where(line => line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static async Task Send(string url, params (string Name, string Value)[] headers)
    {
        var inputs = new Dictionary<string, object?>
        {
            ["Url"] = url,
            ["Headers"] = headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.Ordinal),
        };
        var result = await new HttpRequestBlock().ExecuteAsync(inputs, Context(), CancellationToken.None);
        result["StatusCode"].Should().Be(200L);
    }

    private static WorkflowBlockContext Context() => new()
    {
        ExecutionId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
        Services = new EmptyServices(),
    };

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
