using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed class WorkflowDefinitionSecretsTests
{
    private const string Cipher = "enc:CIPHER-7f3a";
    private const string Reference = "ref:vault:db#pw";

    [Fact]
    public void A_Protected_and_a_name_rule_entry_read_stars_a_reference_and_any_other_entry_as_written_in_a_configuration_and_an_edge_literal()
    {
        var headers = Headers(("Accept", "text/plain"), ("X-Custom", Cipher), ("Authorization", "plain"), ("Authorization-Backup", Reference), ("X-Ref", Reference));
        var text = Definition(onNode: headers, onEdge: headers);

        var masked = Mask(text, withCodec: true);

        Literals(masked).Should().AllSatisfy(literal => literal.Should().Equal(
            ("Accept", "text/plain"), ("X-Custom", "***"), ("Authorization", "***"), ("Authorization-Backup", Reference), ("X-Ref", Reference)));
    }

    [Fact]
    public void Only_the_value_of_a_masked_entry_changes_in_the_text()
    {
        var text = "{ \"nodes\" : [ {\"id\":\"h\",\"blockTypeId\":\"Secrets\",\"position\":{\"x\":1.50,\"y\":0},\n \"configuration\":{ \"Headers\" : {\"Authorization\":\"plain\" , \"Accept\":\"a\"}}} ],\"edges\":[] }";

        Mask(text, withCodec: false).Should().Be(text.Replace("\"plain\"", "\"***\"", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entry_whose_key_holds_a_tilde_or_a_slash_is_masked_and_the_rest_of_the_text_is_untouched()
    {
        var text = Definition(onNode: Headers(("X~Api-Token", "plain"), ("Auth/Token", "plain"), ("Accept", "a")), onEdge: Headers(("X~1-Token", "plain"), ("Auth/~0-Token", "plain")));

        var masked = Mask(text, withCodec: false);

        masked.Should().Be(text.Replace("\"plain\"", "\"***\"", StringComparison.Ordinal))
            .And.NotContain("plain");
        var literals = Literals(masked);
        literals[0].Should().Equal(("X~Api-Token", "***"), ("Auth/Token", "***"), ("Accept", "a"));
        literals[1].Should().Equal(("X~1-Token", "***"), ("Auth/~0-Token", "***"));
    }

    [Fact]
    public void A_masked_value_with_escaped_characters_is_replaced_whole_and_the_key_bytes_are_kept()
    {
        var text = "{\"nodes\":[{\"id\":\"h\",\"blockTypeId\":\"Secrets\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{\"Headers\":{\"Authorization\":\"p\\\"l\\\\ain\\u00e9\",\"Au\\u0074horization-Backup\":\"ref:vault:db#pw\",\"Accept\":\"a\"}}}],\"edges\":[]}";

        var masked = Mask(text, withCodec: true);

        masked.Should().Contain("\"Authorization\":\"***\"").And.Contain("\"Au\\u0074horization-Backup\":\"ref:vault:db#pw\"").And.Contain("\"Accept\":\"a\"");
        masked.Should().NotContain("ain");
        JsonDocument.Parse(masked).RootElement.GetProperty("edges").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Text_the_lexer_refuses_is_served_as_stored_even_when_it_holds_a_name_rule_entry()
    {
        var text = "{\"nodes\":[{\"id\":\"h\",\"blockTypeId\":\"Secrets\",\"position\":{\"x\":0,\"x\":1,\"y\":0},\"configuration\":{\"Headers\":{\"Authorization\":\"plain\"}}}],\"edges\":[]}";

        Mask(text, withCodec: true).Should().Be(text);
    }

    [Fact]
    public void A_node_on_a_sub_canvas_is_masked_as_one_on_the_root()
    {
        var node = Node("h", Headers(("Authorization", "plain")));
        var text = JsonSerializer.Serialize(new
        {
            nodes = new object[] { new { id = "box", blockTypeId = "Other", position = new { x = 0, y = 0 }, configuration = new { }, subCanvases = new { Body = new { nodes = new[] { node }, edges = Array.Empty<object>() } } } },
            edges = Array.Empty<object>(),
        });

        Mask(text, withCodec: true).Should().NotContain("plain").And.Contain("\"Authorization\":\"***\"");
    }

    [Fact]
    public void Two_nodes_of_one_id_on_different_canvases_are_masked_each_by_its_own_block()
    {
        var text = JsonSerializer.Serialize(new
        {
            nodes = new object[]
            {
                Node("n", Headers(("Authorization", "plain-root"))),
                new { id = "box", blockTypeId = "Other", position = new { x = 0, y = 0 }, configuration = new { }, subCanvases = new { Body = new { nodes = new[] { Node("n", Headers(("Authorization", "plain-sub")), "Plain") }, edges = Array.Empty<object>() } } },
            },
            edges = Array.Empty<object>(),
        });

        var masked = Mask(text, withCodec: true);

        masked.Should().NotContain("plain-root").And.Contain("plain-sub", "the inner node's block declares no secret-capable input");
    }

    [Fact]
    public void A_node_whose_block_is_not_registered_is_served_as_stored()
    {
        var text = JsonSerializer.Serialize(new
        {
            nodes = new object[] { Node("h", Headers(("Authorization", "plain"), ("X-Custom", Cipher)), "Gone") },
            edges = Array.Empty<object>(),
        });

        Mask(text, withCodec: true).Should().Be(text);
    }

    [Fact]
    public void An_input_that_does_not_declare_SecretValues_is_not_read_for_entries()
    {
        var text = JsonSerializer.Serialize(new
        {
            nodes = new object[] { new { id = "h", blockTypeId = "Secrets", position = new { x = 0, y = 0 }, configuration = new { Other = Headers(("Authorization", "plain"), ("X-Custom", Cipher)) } } },
            edges = Array.Empty<object>(),
        });

        Mask(text, withCodec: true).Should().Be(text);
    }

    [Fact]
    public void With_no_codec_a_name_rule_entry_reads_stars_and_no_text_is_taken_for_a_ciphertext()
    {
        var text = Definition(onNode: Headers(("Authorization", "plain"), ("X-Custom", Cipher), ("X-Ref", Reference)), onEdge: null);

        Literals(Mask(text, withCodec: false)).First().Should().Equal(("Authorization", "***"), ("X-Custom", Cipher), ("X-Ref", Reference));
    }

    [Fact]
    public void A_literal_that_is_not_an_object_of_text_is_left_as_stored()
    {
        var text = Definition(onNode: "expr:${previous.src.Out}", onEdge: new[] { "Authorization: Bearer x" });

        Mask(text, withCodec: true).Should().Be(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"nodes\":[],\"edges\":[]}")]
    public void Text_with_no_node_to_read_is_returned_as_it_was_written(string text) =>
        Mask(text, withCodec: true).Should().Be(text);

    private static string Mask(string text, bool withCodec) =>
        new WorkflowDefinitionSecrets(
            new RegisteredWorkflowBlocks([new Block("Secrets", secret: true), new Block("Plain", secret: false), new Block("Other", secret: false)]),
            withCodec ? new Codec() : null).Mask(text);

    private static Dictionary<string, string> Headers(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);

    private static object Node(string id, object headers, string blockTypeId = "Secrets") =>
        new { id, blockTypeId, position = new { x = 0, y = 0 }, configuration = new { Headers = headers } };

    private static string Definition(object? onNode, object? onEdge) => JsonSerializer.Serialize(new
    {
        nodes = new object[]
        {
            new { id = "start", blockTypeId = "WorkflowInput", position = new { x = 0, y = 0 }, configuration = new { } },
            Node("h", onNode ?? new { }),
            new { id = "e", blockTypeId = "Secrets", position = new { x = 0, y = 0 }, configuration = new { } },
        },
        edges = new object[]
        {
            new { id = "e1", sourceNodeId = "start", targetNodeId = "e", mappings = new { Headers = new { type = "literal", literalValue = onEdge ?? new { } } } },
        },
    });

    private static List<List<(string Key, string Value)>> Literals(string definition)
    {
        using var document = JsonDocument.Parse(definition);
        var found = new List<List<(string, string)>>();
        foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            if (node.GetProperty("configuration").TryGetProperty("Headers", out var headers) && headers.ValueKind == JsonValueKind.Object && headers.EnumerateObject().Any())
                found.Add([.. headers.EnumerateObject().Select(p => (p.Name, p.Value.GetString()!))]);
        }

        foreach (var edge in document.RootElement.GetProperty("edges").EnumerateArray())
        {
            var literal = edge.GetProperty("mappings").GetProperty("Headers").GetProperty("literalValue");
            if (literal.ValueKind == JsonValueKind.Object && literal.EnumerateObject().Any())
                found.Add([.. literal.EnumerateObject().Select(p => (p.Name, p.Value.GetString()!))]);
        }

        return found;
    }

    private sealed class Codec : IWorkflowSecretCodec
    {
        public WorkflowSecretForm Classify(string value) =>
            value.StartsWith("enc:", StringComparison.Ordinal) ? WorkflowSecretForm.Protected
            : value.StartsWith("ref:", StringComparison.Ordinal) ? WorkflowSecretForm.Reference
            : WorkflowSecretForm.Plain;

        public ValueTask<string> ProtectAsync(WorkflowSecretSite site, string plaintext, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AdmitReferencesAsync(
            WorkflowSecretSave save, IReadOnlyList<string> bound, IReadOnlyList<string> unbound, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<string> RevealAsync(WorkflowSecretSite site, string stored, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Block(string id, bool secret) : IWorkflowBlockFactory
    {
        public string BlockTypeId => id;
        public string DisplayName => id;
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [
            new("Headers", "Headers", PortValueType.MapOf(PortValueType.String), false) { SecretValues = secret },
            new("Other", "Other", PortValueType.MapOf(PortValueType.String), false),
        ];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
