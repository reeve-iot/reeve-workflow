using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.Compilation;
using Xunit.Abstractions;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowDefinitionSerialisationTests(ITestOutputHelper output)
{
    public static TheoryData<string, string> OptionSets() => new()
    {
        { "default", "same" },
        { "camel", "same" },
        { "engine", "same" },
        { "web", "same" },
        { "numstring", "same" },
        { "enumcamel", "same" },
        { "enumnoint", "same" },
        { "whenwritingdefault", "same" },
        { "relaxed", "same" },
        { "indented", "same" },
        { "preserve", "refused" },
        { "dictcamel", "differs" },
        { "snake", "differs" },
    };

    private static JsonSerializerOptions Options(string name) => name switch
    {
        "default" => new(),
        "camel" => new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
        "engine" => WorkflowGraphHelpers.JsonOptions,
        "web" => new(JsonSerializerDefaults.Web),
        "dictcamel" => new() { DictionaryKeyPolicy = JsonNamingPolicy.CamelCase },
        "numstring" => new() { NumberHandling = JsonNumberHandling.WriteAsString },
        "preserve" => new() { ReferenceHandler = ReferenceHandler.Preserve },
        "enumcamel" => new() { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } },
        "whenwritingdefault" => new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault },
        "relaxed" => new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping },
        "indented" => new() { WriteIndented = true, IndentCharacter = '\t' },
        "snake" => new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower },
        "enumnoint" => new() { Converters = { new JsonStringEnumConverter(null, allowIntegerValues: false) } },
        _ => throw new ArgumentException(name),
    };

    [Fact]
    public void ToJson_writes_what_the_reader_reads_back_as_the_same_model()
    {
        var compared = 0;
        foreach (var (text, _) in StoredDocumentEngineFuzzTests.Corpus(8000, 20260924))
        {
            if (StoredWorkflowDocument.Read(text, out _) is not { } read)
                continue;

            var written = read.Definition.ToJson();
            var again = StoredWorkflowDocument.Read(written, out var fault);

            again.Should().NotBeNull($"{fault?.Describe()} in {written}");
            again!.Definition.ToJson().Should().Be(written);
            written.Should().Be(StoredWorkflowDocument.Write(read.Definition, out _), "ToJson is the library's one writer");
            compared++;
        }

        compared.Should().BeGreaterThan(1000, "the corpus must reach the writer, not only the lexer");
    }

    [Fact]
    public void ToJson_omits_absent_optional_members_that_the_reader_refuses_when_explicitly_null()
    {
        var model = new WorkflowDefinition
        {
            Nodes = [new WorkflowNodeDefinition
            {
                Id = "a", BlockTypeId = "x", Position = new WorkflowNodePosition { X = 1, Y = 2 },
                Config = new Dictionary<string, JsonElement>(),
            }],
            Edges = [],
        };

        var written = model.ToJson();
        using var document = JsonDocument.Parse(written);
        var node = document.RootElement.GetProperty("nodes")[0];
        var back = StoredWorkflowDocument.Read(written, out var fault);

        node.TryGetProperty("subCanvases", out _).Should().BeFalse();
        node.TryGetProperty("passthrough", out _).Should().BeFalse();
        node.TryGetProperty("outputPorts", out _).Should().BeFalse();
        node.TryGetProperty("errorPolicy", out _).Should().BeFalse();
        back.Should().NotBeNull(fault?.Describe());
    }

    [Fact]
    public void ToJson_refuses_a_string_JSON_cannot_hold_instead_of_repairing_it()
    {
        var model = new WorkflowDefinition { Nodes = [new WorkflowNodeDefinition { Id = "a\ud800", BlockTypeId = "x" }] };

        var write = () => model.ToJson();

        write.Should().Throw<InvalidWorkflowDefinitionException>().WithMessage("*unpaired surrogate*");
    }

    [Theory]
    [MemberData(nameof(OptionSets))]
    public void Each_option_set_writes_one_spelling_and_the_unsupported_ones_fail_as_ToJson_says(string options, string expected)
    {
        int same = 0, refused = 0, differs = 0;
        foreach (var (text, _) in StoredDocumentEngineFuzzTests.Corpus(8000, 20260924))
        {
            if (StoredWorkflowDocument.Read(text, out _) is not { } read)
                continue;

            string written;
            try
            {
                written = JsonSerializer.Serialize(read.Definition, Options(options));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                refused++;
                continue;
            }
            written.Should().NotMatchRegex("\"(config|connections|Config|Connections)\"\\s*:\\s*[\\[{n]",
                "no option set writes an unlisted key");
            if (StoredWorkflowDocument.Read(written, out _) is not { } back)
                refused++;
            else if (back.Definition.ToJson() != read.Definition.ToJson())
                differs++;
            else
                same++;
        }

        output.WriteLine($"{options}: same={same} refused={refused} differs={differs}");
        switch (expected)
        {
            case "same":
                (refused + differs).Should().Be(0);
                same.Should().BeGreaterThan(1000);
                break;
            case "refused":
                refused.Should().BeGreaterThan(0);
                break;
            default:
                differs.Should().BeGreaterThan(0);
                break;
        }
    }

    [Theory]
    [InlineData("numstring")]
    [InlineData("enumcamel")]
    public void A_position_and_a_policy_keep_their_form_under_options_that_would_change_it(string options)
    {
        var model = new WorkflowDefinition
        {
            Nodes = [new WorkflowNodeDefinition
            {
                Id = "a", BlockTypeId = "x", Position = new WorkflowNodePosition { X = 1.5, Y = -2 },
                ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault)
            }]
        };

        var node = JsonDocument.Parse(JsonSerializer.Serialize(model, Options(options))).RootElement.GetProperty("nodes")[0];

        node.GetProperty("position").GetRawText().Should().Be("""{"x":1.5,"y":-2}""");
        node.GetProperty("errorPolicy").GetProperty("policy").GetString().Should().Be("ContinueWithDefault");
    }
}
