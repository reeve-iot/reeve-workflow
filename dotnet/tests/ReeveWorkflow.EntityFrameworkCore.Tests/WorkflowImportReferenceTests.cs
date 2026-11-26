using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class WorkflowImportReferenceTests : IDisposable
{
    private readonly SqliteWorkflowDatabase database = new();

    public void Dispose() => database.Dispose();

    private const string Shipment = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string Depot = "5c4d1a2b-0000-4000-8000-0000000000aa";
    private const string SecondShipment = "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d";

    [Fact]
    public async Task An_id_is_reported_under_the_port_type_that_declared_it()
    {
        var result = await ValidateAsync(Node("declares", new()
        {
            ["shipment"] = Shipment,
            ["depot"] = Depot
        }));

        result.ReferenceIdsByPortType.Should().ContainKey("Shipment")
            .WhoseValue.Should().BeEquivalentTo([Shipment]);
        result.ReferenceIdsByPortType.Should().ContainKey("Depot")
            .WhoseValue.Should().BeEquivalentTo([Depot]);
    }

    [Fact]
    public async Task A_collection_port_reports_under_its_element_type()
    {
        var result = await ValidateAsync(Node("declares", new()
        {
            ["shipment"] = Shipment,
            ["manifest"] = $"[\"{SecondShipment}\"]"
        }));

        result.ReferenceIdsByPortType.Keys.Should().BeEquivalentTo(["Shipment"]);
        result.ReferenceIdsByPortType["Shipment"].Should().BeEquivalentTo([Shipment, SecondShipment]);
    }

    [Fact]
    public async Task A_nested_collection_port_reports_under_its_innermost_element_type()
    {
        var result = await ValidateAsync(Node("declares", new()
        {
            ["shipment"] = Depot, ["batches"] = $"[[\"{Shipment}\"],[\"{SecondShipment}\"]]"
        }));

        result.ReferenceIdsByPortType.Keys.Should().BeEquivalentTo(["Shipment"]);
        result.ReferenceIdsByPortType["Shipment"].Should().BeEquivalentTo([Depot, Shipment, SecondShipment]);
    }

    [Fact]
    public async Task A_map_port_reports_the_ids_of_its_members_under_its_value_type()
    {
        var result = await ValidateAsync(Node("declares", new()
        {
            ["shipment"] = Depot, ["registry"] = $"{{\"east\":\"{Shipment}\",\"west\":\"{SecondShipment}\"}}"
        }));

        result.ReferenceIdsByPortType.Keys.Should().BeEquivalentTo(["Shipment"]);
        result.ReferenceIdsByPortType["Shipment"].Should().BeEquivalentTo([Depot, Shipment, SecondShipment]);
    }

    [Fact]
    public async Task A_map_literal_in_a_mapping_and_a_map_nested_in_a_collection_report_their_ids_too()
    {
        const string template = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"declares","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n1","mappings":{
               "shipment":{"type":"literal","literalValue":"DEPOT"},
               "registry":{"type":"literal","literalValue":{"east":"SHIPMENT"}},
               "groups":{"type":"literal","literalValue":[{"g":["SECOND"]}]}}}]}
            """;

        var result = await ValidateAsync(template
            .Replace("DEPOT", Depot, StringComparison.Ordinal).Replace("SHIPMENT", Shipment, StringComparison.Ordinal)
            .Replace("SECOND", SecondShipment, StringComparison.Ordinal));

        result.ReferenceIdsByPortType.Keys.Should().BeEquivalentTo(["Shipment"]);
        result.ReferenceIdsByPortType["Shipment"].Should().BeEquivalentTo([Depot, Shipment, SecondShipment]);
    }

    [Fact]
    public async Task A_literal_on_a_port_declared_with_a_type_parameter_imports_and_reports_the_ids_of_a_map_of_it()
    {
        var onValue = await ValidateAsync(Node("generic", new() { ["Value"] = Shipment }));
        var onMap = await ValidateAsync(Node("generic", new() { ["Bag"] = $"{{\"east\":\"{Shipment}\"}}" }));

        onValue.MissingBlockTypeIds.Should().BeEmpty();
        onMap.MissingBlockTypeIds.Should().BeEmpty();
        onValue.ReferenceIdsByPortType.Should().ContainSingle().Which.Key.Should().Be("$T");
        onValue.ReferenceIdsByPortType["$T"].Should().BeEquivalentTo([Shipment]);
        onMap.ReferenceIdsByPortType.Should().ContainSingle().Which.Key.Should().Be("$T");
        onMap.ReferenceIdsByPortType["$T"].Should().BeEquivalentTo([Shipment]);
    }

    [Fact]
    public async Task A_nullable_map_port_reports_the_ids_of_its_members_under_its_value_type()
    {
        var result = await ValidateAsync(Node("declares", new()
        {
            ["shipment"] = Depot, ["maybe"] = $"{{\"east\":\"{Shipment}\"}}"
        }));

        result.ReferenceIdsByPortType.Keys.Should().BeEquivalentTo(["Shipment"]);
        result.ReferenceIdsByPortType["Shipment"].Should().BeEquivalentTo([Depot, Shipment]);
    }

    [Fact]
    public async Task A_port_carrying_no_id_reports_nothing()
    {
        var result = await ValidateAsync(Node("declares", new() { ["shipment"] = "not-a-guid" }));

        result.ReferenceIdsByPortType.Should().BeEmpty();
    }

    [Fact]
    public async Task A_block_this_host_does_not_have_is_still_the_first_thing_reported()
    {
        var result = await ValidateAsync(Node("absent", new() { ["shipment"] = Shipment }));

        result.MissingBlockTypeIds.Should().BeEquivalentTo(["absent"]);
        result.ReferenceIdsByPortType.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"declares","configuration":{"shipment":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"},"config":{"shipment":"9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d"},"position":{"x":0,"y":0}}],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n1","mappings":{}}]}""", "rule stored-key-unknown")]
    [InlineData("""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","BlockTypeId":"declares","blockTypeId":"x","configuration":{"shipment":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"},"position":{"x":0,"y":0}}],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n1","mappings":{}}]}""", "rule L5")]
    [InlineData("""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"declares","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n1","mappings":{"shipment":{"Type":"literal","LiteralValue":"3f2504e0-4f89-11d3-9a0c-0305e82c3301"}}}]}""", "rule L5")]
    [InlineData("""{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"declares","outputPorts":[{"name":"p","type":"CollectionInt"}],"position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n1","mappings":{}}]}""", "port-type")]
    [InlineData("""{"nodes":[{"id":"twin","position":{"x":0,"y":0},"configuration":{}},{"id":"twin","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "duplicate-node-id")]
    [InlineData("""{"nodes":[{"id":"box","subCanvases":{"Body":{"nodes":[{"id":"twin","position":{"x":0,"y":0},"configuration":{}},{"id":"twin","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "duplicate-node-id")]
    public async Task A_definition_the_one_reader_refuses_is_refused_at_import(string definitionJson, string because)
    {
        var validate = () => ValidateAsync(definitionJson);

        var diagnostics = (await validate.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics;
        var message = string.Join(" ", diagnostics.Select(d => d.Message));
        var rule = because switch
        {
            "port-type" => CompilationRules.PortTypeMalformed,
            "duplicate-node-id" => CompilationRules.DuplicateNodeId,
            "rule stored-key-unknown" => CompilationRules.StoredKeyUnknown,
            _ => CompilationRules.DefinitionUnreadable,
        };
        diagnostics.Should().Contain(d => d.RuleId == rule);
        message.Should().Contain(because == "port-type" ? "malformed type" : because);
        if (because == "duplicate-node-id")
            message.Should().Contain("twin");
    }

    [Fact]
    public async Task A_literal_mapping_under_edges_is_reported_and_under_the_unlisted_connections_refused()
    {
        const string template = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"declares","position":{"x":0,"y":0},"configuration":{}}],
             "KEY":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n1","mappings":{"shipment":{"type":"literal","literalValue":"SHIPMENT"}}}]}
            """;
        var edges = template.Replace("SHIPMENT", Shipment, StringComparison.Ordinal);

        var result = await ValidateAsync(edges.Replace("KEY", "edges", StringComparison.Ordinal));
        var unlisted = () => ValidateAsync(edges.Replace("KEY", "connections", StringComparison.Ordinal));

        result.ReferenceIdsByPortType.Should().ContainKey("Shipment").WhoseValue.Should().BeEquivalentTo([Shipment]);
        (await unlisted.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Should().Contain(d => d.RuleId == CompilationRules.StoredKeyUnknown).Which.Message
            .Should().Contain("unlisted key 'connections' on canvas (rule stored-key-unknown, at '/connections')");
    }

    [Fact]
    public async Task A_reference_inside_a_sub_canvas_and_a_missing_block_there_are_reported()
    {
        var result = await ValidateAsync("""
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"loop","blockTypeId":"declares","configuration":{"shipment":"SHIPMENT"},
              "subCanvases":{"Body":{"nodes":[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"x","blockTypeId":"absent","position":{"x":0,"y":0},"configuration":{}},{"id":"y","blockTypeId":"declares","configuration":{"shipment":"SHIPMENT","depot":"DEPOT"},"position":{"x":0,"y":0}}],"edges":[]}},"position":{"x":0,"y":0}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"loop","mappings":{}}]}
            """.Replace("SHIPMENT", Shipment, StringComparison.Ordinal).Replace("DEPOT", Depot, StringComparison.Ordinal));

        result.MissingBlockTypeIds.Should().BeEquivalentTo(["absent"]);
        result.ReferenceIdsByPortType.Keys.Should().BeEquivalentTo(["Shipment", "Depot"]);
    }

    [Fact]
    public async Task A_document_the_compile_refuses_is_refused_at_import_with_its_diagnostics()
    {
        const string definition = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
              {"id":"n1","blockTypeId":"declares","configuration":{"InputPorts":"[{\"name\":\"p\",\"type\":\"String\"}]"},"position":{"x":0,"y":0}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n1","mappings":{}}]}
            """;

        var validate = () => ValidateAsync(definition);

        (await validate.Should().ThrowAsync<ReeveWorkflow.Core.Compilation.WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Should().Contain(d => d.RuleId == "port-list-without-dynamic-ports" && d.NodeId == "n1");
    }

    [Fact]
    public async Task A_block_this_host_lacks_is_reported_and_alone_does_not_refuse_the_import()
    {
        var result = await ValidateAsync(Node("absent", new()));

        result.MissingBlockTypeIds.Should().BeEquivalentTo(["absent"]);
    }

    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo StringInfo =
        System.Text.Json.JsonSerializerOptions.Default.GetTypeInfo(typeof(string));

    private static readonly CompilationOptions HostVocabulary = new()
    {
        TypeRegistry = new WorkflowTypeRegistry(
        [
            new WorkflowTypeRecord("Shipment", typeof(string)) { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String), JsonTypeInfo = StringInfo },
            new WorkflowTypeRecord("Depot", typeof(string)) { Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String), JsonTypeInfo = StringInfo },
        ]),
    };

    private async Task<WorkflowImportValidationResult> ValidateAsync(
        string definitionJson, string? inputParametersJson = null, string? outputParametersJson = null)
    {
        using var scope = database.Services.CreateScope();
        var blocks = new OneBlock();
        var catalog = new WorkflowCatalog(
            scope.ServiceProvider.GetRequiredService<WorkflowDbContext>(), blocks,
            new ReeveWorkflow.Core.Compilation.WorkflowCompilationService(
                blocks, scope.ServiceProvider.GetRequiredService<ReeveWorkflow.Core.Hosting.IWorkflowRunScopeFactory>(), HostVocabulary),
            new ReeveWorkflow.Core.Hosting.WorkflowDefinitionSecrets(blocks, scope.ServiceProvider.GetService<ReeveWorkflow.Core.Hosting.IWorkflowSecretCodec>()));
        return await catalog.ValidateWorkflowImportAsync(definitionJson, inputParametersJson, outputParametersJson);
    }

    [Fact]
    public async Task A_document_wired_to_its_input_and_output_parameters_validates_with_the_lists_it_is_imported_with()
    {
        const string definition = """
            {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
              {"id":"end","blockTypeId":"WorkflowOutput","position":{"x":0,"y":0},"configuration":{}}],
             "edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"end","mappings":{"result":{"type":"direct","sourceOutputName":"p"}}}]}
            """;
        const string inputs = """[{"name":"p","type":"String","isRequired":true}]""";
        const string outputs = """[{"name":"result","type":"String","isRequired":true}]""";

        var declared = await ValidateAsync(definition, inputs, outputs);
        var omitted = () => ValidateAsync(definition);

        declared.MissingBlockTypeIds.Should().BeEmpty();
        (await omitted.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Should().Contain(d => d.RuleId == CompilationRules.WirePortUnknown);
    }

    [Fact]
    public async Task A_graph_without_a_start_node_is_refused_with_the_diagnostics_publish_answers()
    {
        var validate = () => ValidateAsync("""{"nodes":[{"id":"n1","blockTypeId":"absent","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""");

        (await validate.Should().ThrowAsync<WorkflowDoesNotCompileException>()).Which.Diagnostics
            .Select(d => d.RuleId).Should().Equal(CompilationRules.NoStartNode);
    }

    private static string Node(string blockTypeId, Dictionary<string, string> configuration)
    {
        var config = "{" + string.Join(",", configuration.Select(kv => $"\"{kv.Key}\":{(kv.Value.StartsWith('[') || kv.Value.StartsWith('{') ? kv.Value : $"\"{kv.Value}\"")}")) + "}";
        return $$"""
            {"nodes":[
              {"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{} },
              {"id":"n1","blockTypeId":"{{blockTypeId}}","configuration":{{config}},"position":{"x":0,"y":0} }
            ],"edges":[{"id":"e1","sourceNodeId":"start","targetNodeId":"n1","mappings":{} }]}
            """;
    }

    private sealed class OneBlock : IWorkflowBlocks, IWorkflowBlockFactory
    {
        public string BlockTypeId => "declares";
        public string DisplayName => "Declares";
        public string Description => "Declares ports over a vocabulary the library does not own.";
        public string Category => "test";

        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [
            new("shipment", "Shipment", PortValueType.Of("Shipment"), IsRequired: true),
            new("manifest", "Manifest", PortValueType.CollectionOf(PortValueType.Of("Shipment")), IsRequired: false),
            new("depot", "Depot", PortValueType.Of("Depot"), IsRequired: false),
            new("batches", "Batches", PortValueType.CollectionOf(PortValueType.CollectionOf(PortValueType.Of("Shipment"))), IsRequired: false),
            new("registry", "Registry", PortValueType.MapOf(PortValueType.Of("Shipment")), IsRequired: false),
            new("groups", "Groups", PortValueType.CollectionOf(PortValueType.MapOf(PortValueType.CollectionOf(PortValueType.Of("Shipment")))), IsRequired: false),
            new("maybe", "Maybe", PortValueType.OfDeclaration("Map<String,Shipment>?"), IsRequired: false)
        ];

        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];

        public IReadOnlyCollection<string> SubCanvasNames => ["Body"];

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();

        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            blockTypeId == BlockTypeId ? this : blockTypeId == GenericBlock.Id ? new GenericBlock() : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [this];
    }

    private sealed class GenericBlock : IWorkflowBlockFactory
    {
        public const string Id = "generic";
        public string BlockTypeId => Id;
        public string DisplayName => "Generic";
        public string Description => "Declares ports over a type parameter.";
        public string Category => "test";
        public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T")];

        public IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [
            new("Value", "Value", PortValueType.OfDeclaration("$T"), IsRequired: false),
            new("Bag", "Bag", PortValueType.OfDeclaration("Map<String,$T>"), IsRequired: false)
        ];

        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];

        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
