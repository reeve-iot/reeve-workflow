using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class UnreadableDefinitionTests
{
    private const string Start = """{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}""";

    private static readonly BlockShape Box = Shape("box", subCanvasNames: ["Body"]);

    private static readonly BlockShape Filter = new(
        "filter",
        [new PortShape("Collection", PortValueType.OfDeclaration("Collection<$T>"), true)],
        [new PortShape("Filtered", PortValueType.OfDeclaration("Collection<$T>"), false)],
        [], default, new Dictionary<string, IReadOnlyList<PortShape>>(), new Dictionary<string, IReadOnlyList<PortShape>>(), Source: null,
        TypeParameters: [new WorkflowTypeParameter("T")]);

    private static IBlockShapeSource Shapes() => Catalogue(Shape("a", inputs: [("x", PortValueType.String, false)]), Box, Filter);

    private static string Root(string edge)
    {
        var root = JsonNode.Parse("""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"a","blockTypeId":"a","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""")!.AsObject();
        root["edges"] = JsonNode.Parse($"[{edge}]");
        return root.ToJsonString();
    }

    private static string InBody(string edge)
    {
        var root = JsonNode.Parse("""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"box","blockTypeId":"box","position":{"x":0,"y":0},"configuration":{},"subCanvases":{"Body":{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"a","blockTypeId":"a","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}}}],"edges":[{"id":"r","sourceNodeId":"start","targetNodeId":"box","mappings":{}}]}""")!.AsObject();
        root["nodes"]![1]!["subCanvases"]!["Body"]!["edges"] = JsonNode.Parse($"[{edge}]");
        return root.ToJsonString();
    }

    private static string EdgeWith(string mappings, string id = "\"e\"", string source = "\"start\"", string target = "\"a\"") =>
        $$"""{"id":{{id}},"sourceNodeId":{{source}},"targetNodeId":{{target}},"mappings":{{mappings}}}""";

    public static TheoryData<string, string> MalformedMappings() => new()
    {
        { "[1]", "/mappings" },
        { """[{"sourceOutput":5,"targetInput":"x"}]""", "/mappings" },
        { """[{"sourceOutput":"o","targetInput":[]}]""", "/mappings" },
        { """{"x":{"type":5}}""", "/mappings/x/type" },
        { """{"x":{"sourceOutputName":true}}""", "/mappings/x/sourceOutputName" },
        { """{"not":"an array"}""", "/mappings/not" },
        { "\"not\"", "/mappings" },
    };

    private static async Task<CompilationVerdict> ThroughTheService(string definitionJson)
    {
        var world = new CompilationServiceTests.ServiceWorld([Shape("a", inputs: [("x", PortValueType.String, false)]), Box, Filter]);
        var version = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, definitionJson, null, null, Guid.NewGuid(), CompilationServiceTests.T0);
        return await world.Service.EnsureAsync(version, null, CancellationToken.None);
    }

    private static CompilationResult ThroughTheReader(string definitionJson) =>
        StoredWorkflowDocument.Read(definitionJson, out var fault) is { } document
            ? WorkflowCompiler.Compile(document, Shapes(), CanvasSignature.Empty)
            : CompilationResult.Refused([StoredWorkflowDocument.Unreadable(fault!)]);

    private static CompilationResult ThroughTheModel(string definitionJson) =>
        WorkflowCompiler.Compile(
            WorkflowDefinitionParse.FromText(definitionJson)!,
            Shapes(), CanvasSignature.Empty);

    private static async Task<IReadOnlyList<IReadOnlyList<CompilationDiagnostic>>> AllThreeDoors(string definitionJson, bool deserialiserRefuses = false)
    {
        var verdict = await ThroughTheService(definitionJson);
        verdict.IsValid.Should().BeFalse();
        var reader = ThroughTheReader(definitionJson);
        reader.Succeeded.Should().BeFalse();
        if (deserialiserRefuses)
        {
            var read = () => WorkflowDefinitionParse.FromText(definitionJson);
            read.Should().Throw<JsonException>();
            return [verdict.Diagnostics, reader.Diagnostics];
        }

        var model = ThroughTheModel(definitionJson);
        model.Succeeded.Should().BeFalse();
        return [verdict.Diagnostics, reader.Diagnostics, model.Diagnostics];
    }

    [Theory]
    [MemberData(nameof(MalformedMappings))]
    public async Task A_malformed_mapping_on_the_root_is_one_unreadable_refusal_naming_where_it_is(string mappings, string location)
    {
        foreach (var diagnostics in await AllThreeDoors(Root(EdgeWith(mappings))))
        {
            var refusal = diagnostics.Should().ContainSingle().Which;
            refusal.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
            refusal.Message.Should().Contain($"at '/edges/0{location}'");
        }
    }

    [Theory]
    [MemberData(nameof(MalformedMappings))]
    public async Task A_malformed_mapping_inside_a_sub_canvas_is_refused_naming_the_node_that_holds_it(string mappings, string location)
    {
        foreach (var diagnostics in await AllThreeDoors(InBody(EdgeWith(mappings))))
        {
            var refusal = diagnostics.Should().ContainSingle().Which;
            refusal.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
            refusal.Message.Should().Contain($"at '/nodes/1/subCanvases/Body/edges/0{location}'");
            refusal.NodeId.Should().Be("box");
        }
    }

    [Theory]
    [InlineData("sourceNodeId")]
    [InlineData("targetNodeId")]
    [InlineData("id")]
    public async Task A_connection_stored_with_a_null_end_is_refused_rather_than_thrown(string field)
    {
        var edge = field switch
        {
            "id" => EdgeWith("null", id: "null"),
            "sourceNodeId" => EdgeWith("null", source: "null"),
            _ => EdgeWith("null", target: "null"),
        };

        foreach (var text in new[] { Root(edge), InBody(edge) })
        {
            foreach (var diagnostics in await AllThreeDoors(text, deserialiserRefuses: field != "id"))
            {
                var refusal = diagnostics.Should().ContainSingle().Which;
                refusal.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
                refusal.Message.Should().Contain($"/edges/0/{field}'");
            }
        }
    }

    [Fact]
    public async Task A_stored_cycle_through_a_generic_port_is_refused_as_a_cycle()
    {
        const string text = $$"""{"nodes":[{{Start}},{"id":"f","blockTypeId":"filter","position":{"x":0,"y":0},"configuration":{} }],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"f","mappings":{} },{"id":"e2","sourceNodeId":"f","targetNodeId":"f","mappings":{"Collection":{"type":"direct","sourceOutputName":"Filtered"} } }]}""";

        foreach (var diagnostics in await AllThreeDoors(text))
            diagnostics.Should().Contain(d => d.RuleId == CompilationRules.Cycle).And.NotContain(d => d.RuleId == CompilationRules.DefinitionUnreadable);
    }

    [Theory]
    [InlineData("{\"nodes\":[{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", "/nodes/1/id")]
    [InlineData("{\"nodes\":[{\"id\":\"box\",\"subCanvases\":{\"Body\":{\"nodes\":[{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}},{\"id\":\"twin\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}},\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", "/nodes/0/subCanvases/Body/nodes/1/id")]
    public void A_duplicate_node_id_is_refused_at_the_stored_reader(string text, string pointer)
    {
        StoredWorkflowDocument.Read(text, out var fault).Should().BeNull();
        fault.Should().NotBeNull();
        fault!.Rule.Should().Be(StoredDocumentRules.DuplicateNodeId);
        fault.NodeId.Should().Be("twin");
        fault.Location.Should().Be(pointer);
    }

    [Fact]
    public async Task A_stored_lone_surrogate_is_refused_through_the_service_whichever_document_holds_it()
    {
        var world = new CompilationServiceTests.ServiceWorld();
        var definition = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, """{"nodes":[{"id":"\ud800","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""",
            null, null, Guid.NewGuid(), CompilationServiceTests.T0);
        var parameters = new WorkflowVersion(Guid.NewGuid(), Guid.NewGuid(), 1, $$"""{"nodes":[{{Start}}],"edges":[]}""",
            """[{"name":"\ud800","type":"Int"}]""", null, Guid.NewGuid(), CompilationServiceTests.T0);

        (await world.Service.EnsureAsync(definition, null, CancellationToken.None)).Diagnostics
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        (await world.Service.EnsureAsync(parameters, null, CancellationToken.None)).Diagnostics
            .Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.SignatureUnreadable);
    }
}

