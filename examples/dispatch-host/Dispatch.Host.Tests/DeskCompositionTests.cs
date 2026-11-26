using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

public sealed class DeskCompositionTests
{
    [Fact]
    public void The_desk_answers_the_verdict_store_from_its_root_provider_with_scopes_validated()
    {
        using var desk = Build("--environment=Development");

        desk.Services.GetRequiredService<IWorkflowCompilationStore>().Should().BeOfType<EfWorkflowCompilationStore>();
    }

    [Fact]
    public void The_desks_migrations_cover_the_whole_workflow_model()
    {
        using var desk = Build();
        using var scope = desk.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.HasPendingModelChanges().Should().BeFalse(
            "a model change with no migration generated for it would leave the database without it");
    }

    [Fact]
    public void The_desk_registers_Shipment_by_record_as_a_host_type()
    {
        using var desk = Build();

        var shipment = desk.Services.GetRequiredService<IWorkflowTypeRegistry>().Current.Types.Single(t => t.Name == "Shipment");
        (shipment.Owner, desk.Services.GetRequiredService<IWorkflowTypeRegistry>().Generation).Should().Be(("host", 0L));
    }

    [Fact]
    public void The_validation_corpus_unregistered_type_cases_name_nothing_the_desk_registers()
    {
        using var desk = Build();
        var vocabulary = desk.Services.GetRequiredService<IWorkflowTypeRegistry>().Current;
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "contracts", "workflow-validation-corpus.json")))
            root = Path.GetDirectoryName(root) ?? throw new FileNotFoundException("contracts/workflow-validation-corpus.json");
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "contracts", "workflow-validation-corpus.json")));

        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("name").GetString()!.EndsWith("type-unregistered-corpus", StringComparison.Ordinal)).ToList();
        var types = cases.SelectMany(c => TypesIn(c)).Distinct().ToList();

        cases.Should().HaveCount(3);
        types.Should().NotBeEmpty();
        foreach (var type in types)
        {
            var refusal = vocabulary.RefusalOf(PortTerm.Parse(type, PortTermGrammar.Document));
            refusal.Should().NotBeNull($"'{type}' must stay unregistered on this host");
            refusal!.Code.Should().Be("port-type-unregistered", $"'{type}' must stay unregistered on this host");
        }

        vocabulary.Types.Select(t => t.Name).Should().Contain("Shipment").And.NotContain(types);
    }

    [Fact]
    public async Task The_desk_registers_a_codec_that_protects_and_reveals_under_the_workflow_that_protected_it()
    {
        using var desk = Build();
        desk.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository = new MemoryKeyRing();
        var codec = desk.Services.GetRequiredService<IWorkflowSecretCodec>();
        var mine = new WorkflowSecretSite(Guid.NewGuid(), "", "http", "Headers", "Authorization");
        var other = mine with { WorkflowId = Guid.NewGuid() };

        var stored = await codec.ProtectAsync(mine, "Bearer s3cr3t", CancellationToken.None);
        var again = await codec.ProtectAsync(mine, stored, CancellationToken.None);

        codec.Classify(stored).Should().Be(WorkflowSecretForm.Protected);
        codec.Classify("Bearer s3cr3t").Should().Be(WorkflowSecretForm.Plain);
        stored.Should().NotContain("s3cr3t");
        (await codec.RevealAsync(mine, stored, CancellationToken.None)).Should().Be("Bearer s3cr3t");
        (await codec.RevealAsync(mine, again, CancellationToken.None)).Should().Be(stored, "ciphertext offered for protection is protected as text, never kept as given");
        var foreign = async () => await codec.RevealAsync(other, stored, CancellationToken.None);
        await foreign.Should().ThrowAsync<WorkflowSecretUnresolvedException>();
    }

    [Fact]
    public async Task A_reveal_that_fails_logs_its_cause_and_neither_the_value_nor_the_ciphertext()
    {
        using var desk = Build();
        desk.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository = new MemoryKeyRing();
        var lines = new List<string>();
        desk.Services.GetRequiredService<ILoggerFactory>().AddProvider(new ListLoggerProvider(lines));
        var codec = desk.Services.GetRequiredService<IWorkflowSecretCodec>();
        var mine = new WorkflowSecretSite(Guid.NewGuid(), "", "http", "Headers", "Authorization");
        var stored = await codec.ProtectAsync(mine, "Bearer s3cr3t", CancellationToken.None);

        var foreign = async () => await codec.RevealAsync(mine with { WorkflowId = Guid.NewGuid() }, stored, CancellationToken.None);
        await foreign.Should().ThrowAsync<WorkflowSecretUnresolvedException>();

        lines.Should().ContainSingle(line => line.Contains("could not be revealed")).Which.Should().Contain("payload was invalid");
        var ciphertext = stored["enc:".Length..];
        lines.Should().NotContain(line => line.Contains("s3cr3t") || line.Contains(stored) || line.Contains(ciphertext));
    }

    private sealed class ListLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(lines);
        public void Dispose() { }

        private sealed class Sink(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Add(formatter(state, exception) + (exception?.ToString() ?? ""));
        }
    }

    private sealed class MemoryKeyRing : IXmlRepository
    {
        private readonly List<XElement> keys = [];
        public IReadOnlyCollection<XElement> GetAllElements() => keys.ToList();
        public void StoreElement(XElement element, string friendlyName) => keys.Add(element);
    }

    private static IEnumerable<string> TypesIn(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("type") && property.Value.ValueKind == JsonValueKind.String)
                        yield return property.Value.GetString()!;
                    else if (property.Name is "InputPorts" or "OutputPorts" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        using var inner = JsonDocument.Parse(property.Value.GetString()!);
                        foreach (var type in TypesIn(inner.RootElement).ToList())
                            yield return type;
                    }
                    else
                        foreach (var type in TypesIn(property.Value))
                            yield return type;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var type in TypesIn(item))
                        yield return type;
                break;
        }
    }

    private static WebApplication Build(params string[] args) => DispatchHost.Build(
        new DispatchHostOptions
        {
            PostgresConnectionString = "Host=127.0.0.1;Port=1;Database=desk;Username=desk;Password=desk",
            MigrateOnStartup = false,
        },
        args);
}
