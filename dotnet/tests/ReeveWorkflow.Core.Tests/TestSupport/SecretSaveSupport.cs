using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Tests.TestSupport;

internal sealed class FakeSecretCodec : IWorkflowSecretCodec
{
    public List<(WorkflowSecretSite Site, string Plaintext)> Protections { get; } = [];

    public List<(WorkflowSecretSave Save, string[] Bound, string[] Unbound)> Admissions { get; } = [];

    public string? RefuseProtect { get; set; }

    public string? RefuseAdmission { get; set; }

    public HashSet<string> RefusedReferences { get; } = [];

    public Func<Task>? WhileAsking { get; set; }

    public static string Ciphertext(Guid workflowId, string plaintext) =>
        $"enc:{workflowId:N}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext))}";

    public WorkflowSecretForm Classify(string value) =>
        value.StartsWith("enc:", StringComparison.Ordinal) ? WorkflowSecretForm.Protected
        : value.StartsWith("ref:", StringComparison.Ordinal) ? WorkflowSecretForm.Reference
        : WorkflowSecretForm.Plain;

    public async ValueTask<string> ProtectAsync(WorkflowSecretSite site, string plaintext, CancellationToken cancellationToken)
    {
        Protections.Add((site, plaintext));
        if (WhileAsking is { } meanwhile)
            await meanwhile();
        return RefuseProtect is { } reason
            ? throw new WorkflowSecretRefusedException(reason)
            : Ciphertext(site.WorkflowId, plaintext);
    }

    public async ValueTask AdmitReferencesAsync(
        WorkflowSecretSave save, IReadOnlyList<string> bound, IReadOnlyList<string> unbound, CancellationToken cancellationToken)
    {
        Admissions.Add((save, [.. bound], [.. unbound]));
        if (WhileAsking is { } meanwhile)
            await meanwhile();
        if (RefuseAdmission is { } reason)
            throw new WorkflowSecretRefusedException(reason);
        if (bound.Any(RefusedReferences.Contains))
            throw new WorkflowSecretRefusedException("not yours");
    }

    public ValueTask<string> RevealAsync(WorkflowSecretSite site, string stored, CancellationToken cancellationToken) =>
        throw new WorkflowSecretUnresolvedException();
}

internal sealed class SecretTestBlock(string id, bool container = false) : IWorkflowBlockFactory
{
    public string BlockTypeId => id;
    public string DisplayName => id;
    public string Description => string.Empty;
    public string Category => "test";

    public IReadOnlyCollection<BlockPortDefinition> Inputs { get; } = container
        ? []
        :
        [
            new BlockPortDefinition("Url", "Url", PortValueType.String, false),
            new BlockPortDefinition("Headers", "Headers", PortValueType.MapOf(PortValueType.String), false) { SecretValues = true },
            new BlockPortDefinition("Tags", "Tags", PortValueType.MapOf(PortValueType.String), false),
        ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
    public IReadOnlyCollection<string> SubCanvasNames { get; } = container ? ["Body"] : [];
    public bool OnlyDrivesItsSubCanvases => container;
    public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
}

internal sealed class SecretTestBlocks : IWorkflowBlocks
{
    public static SecretTestBlock Http { get; } = new("http");

    public static SecretTestBlock Box { get; } = new("box", container: true);

    public IWorkflowBlockFactory? Find(string blockTypeId) =>
        string.Equals(blockTypeId, "http", StringComparison.OrdinalIgnoreCase) ? Http
        : string.Equals(blockTypeId, "box", StringComparison.OrdinalIgnoreCase) ? Box
        : null;

    public bool ContainsPrivilegedBlock(string? definitionJson) => false;

    public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [Http, Box];
}

internal static class SecretDocs
{
    public static object Http(string id, Dictionary<string, string> headers, string url = "https://example.test") => new
    {
        id, blockTypeId = "http", position = new { x = 0, y = 0 },
        configuration = new Dictionary<string, object> { ["Url"] = url, ["Headers"] = headers },
    };

    public static string Flow(Dictionary<string, string> headers, string url = "https://example.test") => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = "WorkflowInput", position = new { x = 0, y = 0 }, configuration = new { } },
            Http("h", headers, url),
        },
        edges = new[] { new { id = "e", sourceNodeId = "start", targetNodeId = "h", mappings = new { } } },
    });

    public static string EdgeFlow(Dictionary<string, string> literal, Dictionary<string, string>? configuration = null, string edgeId = "e1") =>
        JsonSerializer.Serialize(new
        {
            nodes = new object[] { Http("s", new()), Http("h", configuration ?? new()) },
            edges = new[]
            {
                new
                {
                    id = edgeId, sourceNodeId = "s", targetNodeId = "h",
                    mappings = new Dictionary<string, object> { ["Headers"] = new { type = "literal", literalValue = literal } },
                },
            },
        });

    public static Dictionary<string, string> EdgeEntries(string definition, string edgeId = "e1")
    {
        var edge = JsonNode.Parse(definition)!["edges"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == edgeId)!;
        return edge["mappings"]!["Headers"]!["literalValue"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
    }

    public static object Box(string id, params object[] inner) => new
    {
        id, blockTypeId = "box", position = new { x = 0, y = 0 }, configuration = new { },
        subCanvases = new Dictionary<string, object> { ["Body"] = new { nodes = inner, edges = Array.Empty<object>() } },
    };

    public static string Doc(params object[] nodes) => JsonSerializer.Serialize(new { nodes, edges = Array.Empty<object>() });

    public static Dictionary<string, string> Entries(string definition, string nodeId, string? box = null, string input = "Headers")
    {
        var canvas = JsonNode.Parse(definition)!;
        if (box is not null)
            canvas = canvas["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == box)!["subCanvases"]!["Body"]!;
        var node = canvas["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == nodeId)!;
        return node["configuration"]![input]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>());
    }
}
