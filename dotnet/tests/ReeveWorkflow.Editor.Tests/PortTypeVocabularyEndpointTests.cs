using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class PortTypeVocabularyEndpointTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static void HostAdditions(WorkflowPortTypeRegistrations types) => types
        .Add(new WorkflowTypeRecord("BondedDepot", typeof(string)) { WidensTo = ["Depot"] })
        .Add(new WorkflowTypeRecord("Depot", typeof(string))
        {
            Aliases = ["store"],
            Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String, WorkflowLiteralFormat.Uuid),
            Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Comparable, WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable },
            JsonTypeInfo = JsonSerializerOptions.Default.GetTypeInfo(typeof(string)),
        })
        .Add(new WorkflowTypeRecord("Carrier", typeof(string)))
        .Add(new WorkflowTypeRecord("Flag", typeof(bool)) { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Boolean), JsonTypeInfo = JsonSerializerOptions.Default.GetTypeInfo(typeof(bool)), Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Equatable } })
        .Add(new WorkflowTypeRecord("Handle", typeof(string)) { WidensTo = ["Gauge<Boolean>"] })
        .Add(new WorkflowTypeConstructorRecord("Gauge", typeof(string))
        {
            AllowedArguments = ["Boolean", "Number"],
            Literal = new WorkflowLiteralForm(WorkflowLiteralKind.Json),
            JsonTypeInfo = JsonSerializerOptions.Default.GetTypeInfo(typeof(string)),
            Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Equatable },
        });

    private static async Task<WebApplication> StartAsync(
        Action<WorkflowPortTypeRegistrations>? additions, bool signedIn = true, IWorkflowSecretCodec? codec = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        if (codec is not null)
            builder.Services.AddSingleton(codec);
        builder.Logging.SetMinimumLevel(LogLevel.Critical);
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        builder.Services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(db => db.UseSqlite($"Data Source=vocabulary-{Guid.NewGuid():N};Mode=Memory;Cache=Shared"));
                additions?.Invoke(options.Seams.PortTypes);
            })
            .AddWorkflowEngine()
            .AddWorkflowEditor();

        var app = builder.Build();
        if (signedIn)
        {
            app.Use((context, next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Owner.ToString())], "test"));
                return next(context);
            });
        }

        app.MapWorkflowEditorApi("api/workflows").AllowAnonymous();
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(WebApplication app, string path)
    {
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var response = await client.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static JsonElement Entry(JsonDocument vocabulary, string name) =>
        vocabulary.RootElement.GetProperty("types").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);

    private static string[] Strings(JsonElement entry, string property) =>
        [.. entry.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    private static JsonElement Constructor(JsonDocument vocabulary, string name) =>
        vocabulary.RootElement.GetProperty("constructors").EnumerateArray().Single(t => t.GetProperty("name").GetString() == name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_vocabulary_says_whether_the_host_registered_a_secret_codec(bool registered)
    {
        await using var app = await StartAsync(null, codec: registered ? new NoCodec() : null);

        var (status, body) = await GetAsync(app, "/api/workflows/port-types");

        status.Should().Be(HttpStatusCode.OK);
        using var vocabulary = JsonDocument.Parse(body);
        vocabulary.RootElement.GetProperty("secretCodec").GetBoolean().Should().Be(registered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_serving_its_own_route_says_whether_it_registered_a_secret_codec(bool registered)
    {
        await using var app = await StartAsync(null, codec: registered ? new NoCodec() : null);

        var own = PortTypeVocabularyResponse.From(app.Services.GetRequiredService<CompilationOptions>().Vocabulary, app.Services);

        own.SecretCodec.Should().Be(registered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_container_that_cannot_say_what_is_registered_is_asked_for_the_codec(bool registered)
    {
        await using var app = await StartAsync(null);

        var own = PortTypeVocabularyResponse.From(
            app.Services.GetRequiredService<CompilationOptions>().Vocabulary, new BareContainer(registered ? new NoCodec() : null));

        own.SecretCodec.Should().Be(registered);
    }

    private sealed class BareContainer(IWorkflowSecretCodec? codec) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IWorkflowSecretCodec) ? codec : null;
    }

    private sealed class NoCodec : IWorkflowSecretCodec
    {
        public WorkflowSecretForm Classify(string value) => WorkflowSecretForm.Plain;
        public ValueTask<string> ProtectAsync(WorkflowSecretSite site, string plaintext, CancellationToken cancellationToken) => ValueTask.FromResult(plaintext);
        public ValueTask AdmitReferencesAsync(WorkflowSecretSave save, IReadOnlyList<string> bound, IReadOnlyList<string> unbound, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<string> RevealAsync(WorkflowSecretSite site, string stored, CancellationToken cancellationToken) => ValueTask.FromResult(stored);
    }

    [Fact]
    public async Task The_vocabulary_carries_the_builtins_and_whatever_the_host_registered()
    {
        await using var app = await StartAsync(HostAdditions);

        var (status, body) = await GetAsync(app, "/api/workflows/port-types");

        status.Should().Be(HttpStatusCode.OK);
        using var vocabulary = JsonDocument.Parse(body);
        Strings(Entry(vocabulary, "Boolean"), "aliases").Should().Contain("bool");
        Strings(Entry(vocabulary, "Int"), "widensTo").Should().Equal("Number");
        Strings(Entry(vocabulary, "Number"), "widensTo").Should().BeEmpty("the library has one row, Int to Number");
        Strings(Entry(vocabulary, "BondedDepot"), "widensTo").Should().Equal("Depot");
        Strings(Entry(vocabulary, "Depot"), "aliases").Should().Equal("store");
        Strings(Entry(vocabulary, "Handle"), "widensTo").Should().Equal(["Gauge<Boolean>"], "a row into a constructor term is published as the term");
        (Strings(Entry(vocabulary, "Carrier"), "aliases"), Strings(Entry(vocabulary, "Carrier"), "widensTo"))
            .Should().BeEquivalentTo((Array.Empty<string>(), Array.Empty<string>()), "a registered atom is in the vocabulary with nothing of its own");
    }

    private static string Literal(JsonElement entry) => entry.GetProperty("literal") is { ValueKind: JsonValueKind.Object } form
        ? form.GetProperty("gradedAs").GetString() + (form.GetProperty("format").ValueKind == JsonValueKind.Null ? "" : "/" + form.GetProperty("format").GetString())
        : "none";

    [Fact]
    public async Task Every_type_and_constructor_publishes_how_a_literal_of_it_is_graded()
    {
        await using var app = await StartAsync(HostAdditions);

        var (_, body) = await GetAsync(app, "/api/workflows/port-types");

        using var vocabulary = JsonDocument.Parse(body);
        new[] { "Boolean", "Int", "Number", "String", "DateTime", "Json", "Depot", "Flag", "Carrier", "BondedDepot" }
            .Select(name => $"{name}={Literal(Entry(vocabulary, name))}")
            .Should().Equal("Boolean=Boolean", "Int=Int", "Number=Number", "String=String", "DateTime=String/DateTime", "Json=Json",
                "Depot=String/Uuid", "Flag=Boolean", "Carrier=none", "BondedDepot=none");
        (Literal(Constructor(vocabulary, "Gauge")), Literal(Constructor(vocabulary, "Collection"))).Should().Be(("Json", "none"));
    }

    [Fact]
    public async Task Every_type_and_constructor_names_its_owner_and_a_constructor_its_variance_and_allowed_arguments()
    {
        await using var app = await StartAsync(HostAdditions);

        var (_, body) = await GetAsync(app, "/api/workflows/port-types");

        using var vocabulary = JsonDocument.Parse(body);
        Entry(vocabulary, "Json").GetProperty("owner").GetString().Should().Be("library");
        Entry(vocabulary, "Depot").GetProperty("owner").GetString().Should().Be("host");
        var collection = Constructor(vocabulary, "Collection");
        (collection.GetProperty("owner").GetString(), collection.GetProperty("variance").GetString(), collection.GetProperty("allowedArguments").ValueKind)
            .Should().Be(("library", "covariant", JsonValueKind.Null));
        var gauge = Constructor(vocabulary, "Gauge");
        (gauge.GetProperty("owner").GetString(), gauge.GetProperty("variance").GetString(), Strings(gauge, "allowedArguments"))
            .Should().BeEquivalentTo(("host", "invariant", new[] { "Boolean", "Number" }));
    }

    [Fact]
    public async Task Map_is_listed_as_a_library_constructor_over_a_fixed_String_key_and_a_value_it_follows()
    {
        await using var app = await StartAsync(HostAdditions);

        var (_, body) = await GetAsync(app, "/api/workflows/port-types");

        using var vocabulary = JsonDocument.Parse(body);
        var map = Constructor(vocabulary, "Map");
        (map.GetProperty("owner").GetString(), map.GetProperty("variance").GetString(), map.GetProperty("arity").GetInt32(), map.GetProperty("key").GetString())
            .Should().Be(("library", "covariant", 2, "String"));
        (map.GetProperty("allowedArguments").ValueKind, Strings(map, "implements"), Literal(map)).Should().BeEquivalentTo((JsonValueKind.Null, Array.Empty<string>(), "none"));
        var other = new[] { "Collection", "Gauge" }.Select(name => Constructor(vocabulary, name)).ToList();
        other.Select(c => c.GetProperty("arity").GetInt32()).Should().OnlyContain(a => a == 1);
        other.Select(c => c.GetProperty("key").ValueKind).Should().OnlyContain(k => k == JsonValueKind.Null);
    }

    [Fact]
    public async Task The_capability_vocabulary_is_published_and_every_type_and_constructor_lists_what_it_implements()
    {
        await using var app = await StartAsync(HostAdditions);

        var (_, body) = await GetAsync(app, "/api/workflows/port-types");

        using var vocabulary = JsonDocument.Parse(body);
        vocabulary.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(c => (c.GetProperty("name").GetString(), c.GetProperty("associated").ValueKind == JsonValueKind.Null ? "" : Strings(c, "associated").Single()))
            .Should().Equal(("Iterable", "Item"), ("Numeric", ""), ("Comparable", ""), ("Equatable", ""), ("JsonSerializable", ""));
        Strings(Entry(vocabulary, "Int"), "implements").Should().Equal("Numeric", "Comparable", "Equatable", "JsonSerializable");
        Strings(Entry(vocabulary, "String"), "implements").Should().Equal("Comparable", "Equatable", "JsonSerializable");
        Strings(Entry(vocabulary, "Json"), "implements").Should().Equal("JsonSerializable");
        Strings(Entry(vocabulary, "Depot"), "implements").Should().Equal("Comparable", "Equatable", "JsonSerializable");
        Strings(Entry(vocabulary, "Flag"), "implements").Should().Equal("Equatable");
        Strings(Entry(vocabulary, "Carrier"), "implements").Should().BeEmpty("a record that claims nothing implements nothing");
        Strings(Constructor(vocabulary, "Collection"), "implements").Should().Equal("Iterable");
        Strings(Constructor(vocabulary, "Gauge"), "implements").Should().Equal("Equatable");
    }

    [Fact]
    public async Task The_generation_is_the_registrys_and_moves_when_a_plugin_registers_and_unregisters()
    {
        await using var app = await StartAsync(HostAdditions);
        var registry = app.Services.GetRequiredService<IWorkflowTypeRegistry>();

        async Task<long> GenerationAsync()
        {
            var (_, body) = await GetAsync(app, "/api/workflows/port-types");
            using var vocabulary = JsonDocument.Parse(body);
            return vocabulary.RootElement.GetProperty("generation").GetInt64();
        }

        var atStartup = await GenerationAsync();
        registry.Register("plugin:bt", [new WorkflowTypeRecord("Beacon", typeof(string))]).Accepted.Should().BeTrue();
        var registered = await GenerationAsync();
        var (_, whileLoaded) = await GetAsync(app, "/api/workflows/port-types");
        registry.Unregister("plugin:bt").Should().BeTrue();
        var unregistered = await GenerationAsync();

        (atStartup, registered, unregistered).Should().Be((0L, 1L, 2L));
        using var loaded = JsonDocument.Parse(whileLoaded);
        Entry(loaded, "Beacon").GetProperty("owner").GetString().Should().Be("plugin:bt");
    }

    [Fact]
    public async Task A_host_that_adds_nothing_publishes_exactly_the_library_built_ins()
    {
        await using var app = await StartAsync(additions: null);

        var (_, body) = await GetAsync(app, "/api/workflows/port-types");

        using var vocabulary = JsonDocument.Parse(body);
        vocabulary.RootElement.GetProperty("types").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().Equal(PortTypeWideningTable.Builtin.KnownTypes.Select(t => t.Name));
        vocabulary.RootElement.GetProperty("constructors").EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().Equal("Collection", "Map");
        vocabulary.RootElement.GetProperty("generation").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task A_host_serving_its_own_route_builds_the_same_response_from_its_vocabulary()
    {
        await using var app = await StartAsync(HostAdditions);

        var (_, body) = await GetAsync(app, "/api/workflows/port-types");
        var own = PortTypeVocabularyResponse.From(app.Services.GetRequiredService<CompilationOptions>().Vocabulary, app.Services);

        JsonNode.DeepEquals(JsonNode.Parse(body), JsonSerializer.SerializeToNode(own, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task The_vocabulary_answers_a_request_that_names_no_user()
    {
        await using var app = await StartAsync(HostAdditions, signedIn: false);

        var (vocabulary, _) = await GetAsync(app, "/api/workflows/port-types");
        var (listing, _) = await GetAsync(app, "/api/workflows/compilation/invalid");

        (vocabulary, listing).Should().Be((HttpStatusCode.OK, HttpStatusCode.Unauthorized));
    }

    private static string CommittedPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "packages", "adapters", "src",
            "port-type-vocabulary.wire.json"));

    [Fact]
    public async Task The_committed_wire_vocabulary_is_what_the_endpoint_writes_with_the_compilers_answers()
    {
        await using var app = await StartAsync(HostAdditions);
        var (_, body) = await GetAsync(app, "/api/workflows/port-types");
        var vocabularyState = app.Services.GetRequiredService<CompilationOptions>().Vocabulary;
        var table = vocabularyState.Table;

        var response = JsonNode.Parse(body)!;
        var names = SampleNames(response);
        var file = new JsonObject
        {
            ["$note"] = "Written by PortTypeVocabularyEndpointTests (Editor.Tests); read by portTypeVocabulary.test.ts.",
            ["response"] = response,
            ["names"] = new JsonArray([.. names.Select(n => JsonValue.Create(n))]),
            ["canonical"] = new JsonArray([.. names.Select(n => JsonValue.Create(table.Canonicalize(PortValueType.Of(n)).Name))]),
            ["implements"] = new JsonArray([.. names.Select(n => ImplementsOf(vocabularyState, n))]),
        };
        var expected = file.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";

        var path = CommittedPath();
        var actual = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        if (actual != expected)
            await File.WriteAllTextAsync(path + ".actual", expected);

        actual.Should().Be(expected, because: $"the endpoint's response and the compiler's answers are at {path}.actual");
    }

    private static JsonNode? ImplementsOf(WorkflowTypeVocabulary vocabulary, string name)
    {
        try
        {
            return new JsonArray([.. vocabulary.CapabilitiesOf(PortTerm.Parse(name, PortTermGrammar.Document)).Select(c => JsonValue.Create(PortCapabilities.NameOf(c)))]);
        }
        catch (ArgumentException exception) when (exception.Message.StartsWith("port-type-unregistered", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static List<string> SampleNames(JsonNode response)
    {
        var published = response["types"]!.AsArray()
            .SelectMany(t => new[] { t!["name"]!.GetValue<string>() }.Concat(t["aliases"]!.AsArray().Select(a => a!.GetValue<string>())))
            .Append("Mystery")
            .Append("Any")
            .Append("Float")
            .ToList();
        var atoms = published.Where(n => !n.Contains('<', StringComparison.Ordinal)).ToList();
        return [.. published
            .Concat(atoms.Select(n => n.ToUpperInvariant()))
            .Concat(published.Select(n => $"Collection<{n}>"))
            .Concat(["Collection<Collection<Int>>", "Collection<Collection<Number>>", "Collection<Collection<BondedDepot>>", "Collection<Collection<Depot>>"])
            .Concat(["Int?", "Number?", "Collection<Int>?", "Gauge<Boolean>", "Gauge<Number>", "Gauge<Boolean>?", "Collection<Gauge<Boolean>>", "Collection<Int?>", "Handle?"])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
    }
}
