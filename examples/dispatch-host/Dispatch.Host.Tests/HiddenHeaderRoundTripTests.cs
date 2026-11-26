using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class HiddenHeaderRoundTripTests(ClusterBackends backends)
{
    private const string Secret = "acct-8841-plain";

    [Fact]
    public async Task A_hidden_header_is_stored_protected_read_as_stars_kept_by_an_untouched_save_and_sent_in_clear_by_a_run()
    {
        var postgres = await backends.CreateDatabaseAsync("dispatch_hidden_header");
        await using var target = await CaptureTarget.StartAsync();
        await using var desk = await DispatchReplica.StartAsync(new DispatchReplicaSettings
        {
            ReplicaId = "desk-hidden-header", PostgresConnectionString = postgres, HasPeerReplicas = false,
            StaticRoot = Path.GetTempPath(),
        });
        await FreightDesk.WarmAsync(postgres, desk);

        var created = await desk.EditAsync(HttpMethod.Post, "/api/workflows", new
        {
            name = "Hidden header", definitionJson = Definition(target.Url, $"protect:{Secret}"),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, because: created.Body);
        AccountHeader(created.Json.GetProperty("definitionJson").GetString()!).Should().Be("***", because: "the create result is a read, and a read masks");
        var workflowId = created.Json.GetProperty("id").GetGuid();

        var read = await desk.EditAsync(HttpMethod.Get, $"/api/workflows/{workflowId}");
        var served = read.Json.GetProperty("definitionJson").GetString()!;
        AccountHeader(served).Should().Be("***", because: read.Body);

        var stored = await StoredAccountHeaderAsync(postgres, workflowId);
        stored.Should().StartWith("enc:").And.NotContain(Secret, because: "the desk's codec protects what it stores");

        var resaved = await desk.EditAsync(HttpMethod.Put, $"/api/workflows/{workflowId}", new { name = "Hidden header", definitionJson = served });
        resaved.StatusCode.Should().Be(HttpStatusCode.OK, because: resaved.Body);
        (await StoredAccountHeaderAsync(postgres, workflowId)).Should().Be(stored,
            because: "the stars keep the stored entry; a save that protected them again would store a new ciphertext");

        var run = await desk.ExecuteAsync(workflowId);

        run.Status.Should().Be(HttpStatusCode.OK, because: desk.Console);
        target.Received.Should().Equal(Secret);

        var recorded = await RecordedTextOfLatestRunAsync(desk, workflowId);
        var step = JsonNode.Parse(recorded)!["steps"]!.AsArray().Single(entry => entry!["nodeId"]!.GetValue<string>() == "call")!;
        JsonNode.Parse(step["inputsJson"]!.GetValue<string>())!["Headers"]!["X-Account"]!.GetValue<string>().Should().Be("***",
            because: "the run recorded the node's inputs, and a hidden header is stars there");
        recorded.Should().NotContain(Secret).And.NotContain(stored, because: "no recorded input holds a revealed value or a ciphertext");

        var foreign = await desk.EditAsync(HttpMethod.Post, "/api/workflows", new
        {
            name = "Pasted ciphertext", definitionJson = Definition(target.Url, stored),
        });

        foreign.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: foreign.Body);
        foreign.Json.GetProperty("code").GetString().Should().Be("secret-value-unmatched", because: foreign.Body);
        foreign.Body.Should().NotContain(stored, because: "a refusal names the site, never the value");

        var other = await desk.EditAsync(HttpMethod.Post, "/api/workflows", new
        {
            name = "Has its own", definitionJson = Definition(target.Url, "protect:another-account"),
        });
        other.StatusCode.Should().Be(HttpStatusCode.Created, because: other.Body);
        var otherId = other.Json.GetProperty("id").GetGuid();
        var otherStored = await StoredAccountHeaderAsync(postgres, otherId);

        var pasted = await desk.EditAsync(HttpMethod.Put, $"/api/workflows/{otherId}", new
        {
            name = "Has its own", definitionJson = Definition(target.Url, stored),
        });

        pasted.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: pasted.Body);
        pasted.Json.GetProperty("code").GetString().Should().Be("secret-value-unmatched", because: pasted.Body);
        (await StoredAccountHeaderAsync(postgres, otherId)).Should().Be(otherStored,
            because: "only the ciphertext this workflow stored at the site is accepted, not another workflow's");
    }

    private static async Task<string> RecordedTextOfLatestRunAsync(DispatchReplica desk, Guid workflowId)
    {
        var list = await desk.EditAsync(HttpMethod.Get, $"/api/workflows/{workflowId}/executions?take=1");
        var executionId = list.Json.GetProperty("items")[0].GetProperty("id").GetGuid();
        var detail = await desk.EditAsync(HttpMethod.Get, $"/api/workflows/{workflowId}/executions/{executionId}");
        return detail.Body;
    }

    private static string Definition(string url, string account) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = "WorkflowInput", position = new { x = 0, y = 0 }, configuration = new { } },
            new
            {
                id = "call", blockTypeId = "HttpRequest", position = new { x = 240, y = 0 },
                configuration = new Dictionary<string, object> { ["Url"] = url, ["Headers"] = new Dictionary<string, string> { ["X-Account"] = account } },
            },
        },
        edges = new[] { new { id = "e", sourceNodeId = "start", targetNodeId = "call", mappings = new { } } },
    });

    private static string AccountHeader(string definitionJson) =>
        JsonNode.Parse(definitionJson)!["nodes"]!.AsArray()
            .Single(node => node!["id"]!.GetValue<string>() == "call")!["configuration"]!["Headers"]!["X-Account"]!.GetValue<string>();

    private static async Task<string> StoredAccountHeaderAsync(string postgres, Guid workflowId)
    {
        using var container = FreightDesk.Container(postgres);
        using var scope = container.CreateScope();
        var definition = await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowVersions.AsNoTracking()
            .Where(version => version.WorkflowId == workflowId).OrderByDescending(version => version.VersionNumber)
            .Select(version => version.DefinitionJson).FirstAsync();
        return AccountHeader(definition);
    }

    private sealed class CaptureTarget : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly List<string> received = [];

        private CaptureTarget(WebApplication app) => this.app = app;

        public string Url => app.Urls.First();

        public IReadOnlyList<string> Received
        {
            get { lock (received) return [.. received]; }
        }

        public static async Task<CaptureTarget> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            var app = builder.Build();
            var target = new CaptureTarget(app);
            app.Run(context =>
            {
                lock (target.received) target.received.Add(context.Request.Headers["X-Account"].ToString());
                return context.Response.WriteAsync("ok");
            });
            await app.StartAsync();
            return target;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
