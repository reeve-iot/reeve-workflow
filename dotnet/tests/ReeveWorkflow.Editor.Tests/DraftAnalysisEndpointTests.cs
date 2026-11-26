using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Blocks;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class DraftAnalysisEndpointTests : IAsyncLifetime
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Root = "/api/workflows";

    private static readonly Lazy<JsonElement> Contract = new(() =>
        JsonDocument.Parse(File.ReadAllText(ContractPath())).RootElement.Clone());

    private SqliteConnection keepAlive = null!;
    private WebApplication app = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        var database = $"Data Source=analysis-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        keepAlive = new SqliteConnection(database);
        await keepAlive.OpenAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Critical);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new FilterBlockFactory());
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new IterateOverBlockFactory());
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new ForLoopBlockFactory());
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new TextToJsonBlockFactory());
        builder.Services.AddSingleton<IWorkflowBlockFactory>(new ExpressionBlockFactory());
        builder.Services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(db => db.UseSqlite(database));
            })
            .AddWorkflowEngine()
            .AddWorkflowEditor();

        app = builder.Build();
        app.Use((context, next) =>
        {
            var user = context.Request.Headers.TryGetValue("X-User", out var named) ? named.ToString() : Owner.ToString();
            var authenticated = !context.Request.Headers.ContainsKey("X-Anonymous");
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user)], authenticated ? "test" : null));
            return next(context);
        });
        app.MapWorkflowEditorApi("api/workflows").AllowAnonymous();

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
        await keepAlive.DisposeAsync();
    }

    private static string ContractPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "contracts", "draft-analysis.json"));

    public static TheoryData<string> CaseNames() => new(Contract.Value.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Each_contract_case_is_answered_as_the_contract_says(string name)
    {
        var row = Contract.Value.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var request = row.GetProperty("request");
        var body = new JsonObject
        {
            ["revision"] = request.GetProperty("revision").GetInt64(),
            ["definitionJson"] = JsonNode.Parse(request.GetProperty("definition").GetRawText())!.ToJsonString(),
        };
        foreach (var (key, field) in new[] { ("inputParameters", "inputParametersJson"), ("outputParameters", "outputParametersJson") })
        {
            if (request.TryGetProperty(key, out var list))
                body[field] = JsonNode.Parse(list.GetRawText())!.ToJsonString();
        }

        using var response = await client.PostAsJsonAsync($"{Root}/analysis", body);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var actual = JsonDocument.Parse(text);
        Differences(row.GetProperty("response"), actual.RootElement).Should().BeEmpty($"the response was {text}");
    }

    [Fact]
    public void The_contract_has_the_cases_the_spec_asks_for()
    {
        var cases = Contract.Value.GetProperty("cases").EnumerateArray().ToList();
        var states = cases.SelectMany(c => c.GetProperty("response").GetProperty("nodes").EnumerateArray())
            .SelectMany(n => n.GetProperty("inputs").EnumerateArray().Concat(n.GetProperty("outputs").EnumerateArray()))
            .ToList();

        states.Select(p => p.GetProperty("state").GetString()).Distinct().Should().BeEquivalentTo("bound", "unbound", "refused");
        states.Should().Contain(p => Has(p, "expression"));
        states.Should().Contain(p => Has(p, "projection"));
        cases.Should().Contain(c => c.GetProperty("response").GetProperty("nodes").EnumerateArray()
            .Any(n => n.GetProperty("canvasPath").GetArrayLength() == 1));
        cases.Should().Contain(c => c.GetProperty("response").GetProperty("diagnostics").EnumerateArray()
            .Any(d => d.GetProperty("ruleId").GetString() == "definition-unreadable")
            && c.GetProperty("response").GetProperty("nodes").GetArrayLength() == 0);
    }

    private static bool Has(JsonElement element, string member) => element.TryGetProperty(member, out _);

    [Fact]
    public void A_port_has_no_declared_term_only_where_a_passthrough_entry_gives_its_name()
    {
        var cases = Contract.Value.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("response").GetProperty("nodes").GetArrayLength() > 0).ToList();
        var nullDeclared = 0;
        foreach (var row in cases)
        {
            var names = new Dictionary<string, HashSet<string>>();
            PassthroughNames(row.GetProperty("request").GetProperty("definition"), "", null, names);
            foreach (var node in row.GetProperty("response").GetProperty("nodes").EnumerateArray())
            {
                foreach (var port in node.GetProperty("inputs").EnumerateArray().Concat(node.GetProperty("outputs").EnumerateArray()))
                {
                    if (port.GetProperty("declared").ValueKind != JsonValueKind.Null)
                        continue;
                    nullDeclared++;
                    names[Key(node)].Should().Contain(port.GetProperty("name").GetString(),
                        $"{row.GetProperty("name")} {Key(node)}: a declared term is null only for a passthrough name");
                }
            }
        }

        nullDeclared.Should().BeGreaterThan(0, "the contract holds passthrough names");
    }

    private static void PassthroughNames(JsonElement canvas, string path, HashSet<string>? owner, Dictionary<string, HashSet<string>> into)
    {
        foreach (var node in canvas.GetProperty("nodes").EnumerateArray())
        {
            var id = node.GetProperty("id").GetString()!;
            var own = node.TryGetProperty("passthrough", out var list) ? list.EnumerateArray().Select(n => n.GetString()!).ToHashSet() : [];
            into[$"[{path}]{id}"] = node.GetProperty("blockTypeId").GetString() == "WorkflowInput" && owner is not null ? owner : own;
            if (!node.TryGetProperty("subCanvases", out var canvases))
                continue;
            foreach (var sub in canvases.EnumerateObject())
                PassthroughNames(sub.Value, path == "" ? $"{id}>{sub.Name}" : $"{path}/{id}>{sub.Name}", own, into);
        }
    }

    private static List<string> Differences(JsonElement expected, JsonElement actual)
    {
        var problems = new List<string>();
        foreach (var scalar in new[] { "revision", "registryGeneration" })
        {
            if (expected.GetProperty(scalar).GetInt64() != actual.GetProperty(scalar).GetInt64())
                problems.Add($"{scalar}: expected {expected.GetProperty(scalar)}, got {actual.GetProperty(scalar)}");
        }

        var wanted = expected.GetProperty("diagnostics").EnumerateArray().Select(Diagnostic).Order(StringComparer.Ordinal).ToList();
        var got = actual.GetProperty("diagnostics").EnumerateArray().Select(Diagnostic).Order(StringComparer.Ordinal).ToList();
        if (!wanted.SequenceEqual(got))
            problems.Add($"diagnostics: expected [{string.Join("; ", wanted)}], got [{string.Join("; ", got)}]");

        var wantedNodes = expected.GetProperty("nodes").EnumerateArray().ToDictionary(Key);
        var gotNodes = actual.GetProperty("nodes").EnumerateArray().ToDictionary(Key);
        foreach (var missing in wantedNodes.Keys.Except(gotNodes.Keys))
            problems.Add($"node {missing}: expected, not answered");
        foreach (var extra in gotNodes.Keys.Except(wantedNodes.Keys))
            problems.Add($"node {extra}: answered, not expected");
        foreach (var key in wantedNodes.Keys.Intersect(gotNodes.Keys))
        {
            var (want, have) = (wantedNodes[key], gotNodes[key]);
            if (!Same(want.GetProperty("typeArguments"), have.GetProperty("typeArguments")))
                problems.Add($"node {key} typeArguments: expected {want.GetProperty("typeArguments")}, got {have.GetProperty("typeArguments")}");
            foreach (var side in new[] { "inputs", "outputs" })
                problems.AddRange(PortDifferences($"node {key} {side}", want.GetProperty(side), have.GetProperty(side)));
        }

        return problems;
    }

    private static IEnumerable<string> PortDifferences(string where, JsonElement expected, JsonElement actual)
    {
        var wanted = expected.EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        var got = actual.EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        foreach (var name in wanted.Keys.Except(got.Keys))
            yield return $"{where} {name}: expected, not answered";
        foreach (var name in got.Keys.Except(wanted.Keys))
            yield return $"{where} {name}: answered, not expected";
        foreach (var name in wanted.Keys.Intersect(got.Keys).Where(n => !Same(wanted[n], got[n])))
            yield return $"{where} {name}: expected {wanted[name]}, got {got[name]}";
    }

    private static string CanvasText(JsonElement canvasPath) =>
        string.Join("/", canvasPath.EnumerateArray().Select(s => $"{s.GetProperty("nodeId").GetString()}>{s.GetProperty("branchName").GetString()}"));

    private static string Key(JsonElement node) => $"[{CanvasText(node.GetProperty("canvasPath"))}]{node.GetProperty("nodeId").GetString()}";

    private static string Diagnostic(JsonElement diagnostic) =>
        $"{diagnostic.GetProperty("ruleId").GetString()}@[{CanvasText(diagnostic.GetProperty("canvasPath"))}]"
        + $"{(diagnostic.GetProperty("nodeId").GetString() ?? "-")}.{(diagnostic.GetProperty("portName").GetString() ?? "-")}";

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
            return false;
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var left = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var right = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                return left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out var other) && Same(p.Value, other));
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => Same(pair.First, pair.Second));
            default:
                return a.GetRawText() == b.GetRawText();
        }
    }

    private const string Plain = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

    private async Task<HttpResponseMessage> PostAsync(object body, Guid? user = null, bool anonymous = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Root}/analysis") { Content = JsonContent.Create(body) };
        if (user is { } named)
            request.Headers.Add("X-User", named.ToString());
        if (anonymous)
            request.Headers.Add("X-Anonymous", "1");
        return await client.SendAsync(request);
    }

    private async Task<Guid> CreateAsync(Guid user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Root)
        {
            Content = JsonContent.Create(new { name = "wf", definitionJson = Plain }),
        };
        request.Headers.Add("X-User", user.ToString());
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return created.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<(int Workflows, int Versions)> CountsAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return (await db.Workflows.CountAsync(), await db.WorkflowVersions.CountAsync());
    }

    [Fact]
    public async Task A_workflow_the_caller_does_not_own_answers_as_update_answers_it()
    {
        var theirs = await CreateAsync(Other);

        using var foreign = await PostAsync(new { workflowId = theirs, revision = 1, definitionJson = Plain });
        using var absent = await PostAsync(new { workflowId = Guid.NewGuid(), revision = 1, definitionJson = Plain });
        using var update = await client.PutAsJsonAsync($"{Root}/{theirs}", new { name = "wf", definitionJson = Plain });
        using var theirOwn = await PostAsync(new { workflowId = theirs, revision = 1, definitionJson = Plain }, user: Other);

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync()).Should().Be(await update.Content.ReadAsStringAsync());
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await absent.Content.ReadAsStringAsync()).Should().Be(await update.Content.ReadAsStringAsync());
        theirOwn.StatusCode.Should().Be(HttpStatusCode.OK, "its owner analyses a draft of it");
    }

    [Fact]
    public async Task A_body_that_does_not_bind_is_the_problem_details_400()
    {
        using var unknownMember = await client.PostAsync($"{Root}/analysis",
            new StringContent("""{"revision":1,"definitionJson":"{}","isEnabled":false}""", System.Text.Encoding.UTF8, "application/json"));
        using var wrongType = await client.PostAsync($"{Root}/analysis",
            new StringContent("""{"revision":"one","definitionJson":"{}"}""", System.Text.Encoding.UTF8, "application/json"));
        using var empty = await client.PostAsync($"{Root}/analysis", new StringContent("", System.Text.Encoding.UTF8, "application/json"));

        foreach (var response in new[] { unknownMember, wrongType, empty })
        {
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
            using var problem = JsonDocument.Parse(text);
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
            problem.RootElement.GetProperty("detail").GetString().Should().StartWith(response == empty ? "A request body is required" : "The request body could not be read")
                .And.NotContain("ReeveWorkflow.");
        }
    }

    [Fact]
    public async Task A_caller_nobody_authenticated_is_a_401()
    {
        using var response = await PostAsync(new { revision = 1, definitionJson = Plain }, anonymous: true);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_analysis_writes_nothing_and_echoes_the_revision()
    {
        var owned = await CreateAsync(Owner);
        var before = await CountsAsync();

        using var response = await PostAsync(new { workflowId = owned, revision = 41, definitionJson = Plain });
        using var unnamed = await PostAsync(new { revision = 42, definitionJson = """{"nodes":[],"edges":[]}""" });

        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        answer.RootElement.GetProperty("revision").GetInt64().Should().Be(41);
        using var other = JsonDocument.Parse(await unnamed.Content.ReadAsStringAsync());
        other.RootElement.GetProperty("revision").GetInt64().Should().Be(42);
        (await CountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task An_absent_definition_is_unreadable_not_a_bad_request()
    {
        using var response = await PostAsync(new { revision = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        answer.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("ruleId").GetString())
            .Should().Equal("definition-unreadable");
        answer.RootElement.GetProperty("nodes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_parameter_list_that_does_not_read_answers_its_diagnostic_and_no_nodes()
    {
        using var response = await PostAsync(new { revision = 1, definitionJson = Plain, inputParametersJson = """[{"name":"n","type":"List<Int"}]""" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        answer.RootElement.GetProperty("diagnostics").GetArrayLength().Should().BeGreaterThan(0);
        answer.RootElement.GetProperty("nodes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_registry_generation_is_the_one_the_vocabulary_answers_and_moves_with_it()
    {
        var registry = app.Services.GetRequiredService<IWorkflowTypeRegistry>();

        async Task<(long Analysis, long Vocabulary)> GenerationsAsync()
        {
            using var analysis = await PostAsync(new { revision = 1, definitionJson = Plain });
            using var vocabulary = await client.GetAsync($"{Root}/port-types");
            using var a = JsonDocument.Parse(await analysis.Content.ReadAsStringAsync());
            using var v = JsonDocument.Parse(await vocabulary.Content.ReadAsStringAsync());
            return (a.RootElement.GetProperty("registryGeneration").GetInt64(), v.RootElement.GetProperty("generation").GetInt64());
        }

        var before = await GenerationsAsync();
        registry.Register("plugin:bt", [new WorkflowTypeRecord("Beacon", typeof(string))]).Accepted.Should().BeTrue();
        var after = await GenerationsAsync();

        (before.Analysis == before.Vocabulary, after.Analysis == after.Vocabulary, after.Analysis > before.Analysis)
            .Should().Be((true, true, true));
    }
}
