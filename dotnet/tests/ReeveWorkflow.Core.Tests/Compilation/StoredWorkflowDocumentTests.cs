using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class StoredWorkflowDocumentTests
{
    [Theory]
    [InlineData("{\"nodes\":[{\"id\":\"a.b\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", "/nodes/0/id")]
    [InlineData("{\"nodes\":[{\"id\":\"n\",\"outputPorts\":[{\"name\":\"out.one\"}],\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}", "/nodes/0/outputPorts/0/name")]
    public void A_dotted_stored_name_is_refused_by_its_rule(string text, string pointer)
    {
        var fault = Refused(text);
        (fault.Rule, fault.Location).Should().Be(("dotted-name", pointer));
    }

    private static StoredDocumentFault Refused(string? text)
    {
        StoredWorkflowDocument.Read(text, out var fault).Should().BeNull(text);
        return fault!;
    }

    private static StoredWorkflowDocument Accepted(string text)
    {
        var document = StoredWorkflowDocument.Read(text, out var fault);
        fault.Should().BeNull(text);
        return document!;
    }

    private static string Nest(int containers) => new string('[', containers) + new string(']', containers);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("{")]
    [InlineData("{} {}")]
    [InlineData("""{"nodes":[],}""")]
    [InlineData("""/* c */ {}""")]
    [InlineData("""{"nodes":NaN,"edges":[]}""")]
    [InlineData("﻿{}")]
    [InlineData("'x'")]
    public void L1_text_that_is_not_one_JSON_value_is_unreadable_at_the_document(string? text)
    {
        var fault = Refused(text);

        (fault.Rule, fault.Location).Should().Be(("L1", ""));
    }

    [Fact]
    public void L2_sixty_four_nested_containers_read_and_sixty_five_do_not()
    {
        const string head = """{"nodes":[{"id":"a","position":{"x":0,"y":0},"configuration":{"k":""";
        const string tail = "}}],\"edges\":[]}";
        Accepted(head + Nest(60) + tail);
        var fault = Refused(head + Nest(61) + tail);

        fault.Rule.Should().Be("L2");
        fault.Location.Should().Be("/nodes/0/configuration/k" + string.Concat(Enumerable.Repeat("/0", 60)));
        fault.NodeId.Should().BeNull("a lexical fault is found before any node is read; its location names the place");
        var parse = () => JsonDocument.Parse(head + Nest(61) + tail);
        parse.Should().Throw<JsonException>("the reader's limit is the parser's default, not a second one");
    }

    private static string NestedCanvas(int levels, bool withPosition)
    {
        var inner = withPosition
            ? """{"nodes":[{"id":"z","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}"""
            : """{"nodes":[{"id":"z","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
        for (var i = 0; i < levels; i++)
            inner = "{\"nodes\":[{\"id\":\"n" + i + "\",\"blockTypeId\":\"x\",\"subCanvases\":{\"B\":" + inner + "},\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}";
        return inner;
    }

    [Fact]
    public void ToJson_round_trips_a_document_at_depth_sixty_three_with_an_absent_position()
    {
        var document = Accepted(NestedCanvas(15, withPosition: false));

        var written = document.Definition.ToJson();

        Accepted(written).Definition.Should().NotBeNull();
    }

    [Fact]
    public void ToJson_round_trips_a_document_already_at_depth_sixty_four()
    {
        var document = Accepted(NestedCanvas(15, withPosition: true));

        var written = document.Definition.ToJson();

        Accepted(written).Definition.Should().NotBeNull();
    }

    [Fact]
    public void ToJson_refuses_a_model_seventy_sub_canvases_deep()
    {
        var definition = new WorkflowDefinition();
        for (var i = 0; i < 70; i++)
            definition = new WorkflowDefinition
            {
                Nodes = [new WorkflowNodeDefinition { Id = "n" + i, BlockTypeId = "x", SubCanvases = new() { ["B"] = definition } }],
            };

        var write = () => definition.ToJson();

        write.Should().Throw<InvalidWorkflowDefinitionException>();
    }

    [Theory]
    [InlineData("""{"nodes":[],"nodes":[],"edges":[]}""", "/nodes")]
    [InlineData("""{"nodes":[{"id":"a","configuration":{"k":1,"k":2},"position":{"x":0,"y":0}}],"edges":[]}""", "/nodes/0/configuration/k")]
    [InlineData("""{"nodes":[{"id":"a","configuration":{"v":{"x":1,"x":1}},"position":{"x":0,"y":0}}],"edges":[]}""", "/nodes/0/configuration/v/x")]
    public void L3_a_second_property_of_one_name_is_unreadable_anywhere(string text, string location)
    {
        var fault = Refused(text);

        (fault.Rule, fault.Location).Should().Be(("L3", location));
    }

    [Fact]
    public void L3_two_configuration_keys_that_differ_only_in_case_are_the_blocks_and_read()
    {
        var document = Accepted("""{"nodes":[{"id":"a","configuration":{"DeviceId":1,"deviceId":2},"position":{"x":0,"y":0}}],"edges":[]}""");

        document.Definition.Nodes.Single().Config!.Keys.Should().Equal("DeviceId", "deviceId");
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"\ud800","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/id")]
    [InlineData("""{"nodes":[{"id":"\udc00","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/id")]
    [InlineData("""{"nodes":[{"id":"\udc00\ud800","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/id")]
    [InlineData("""{"nodes":[{"id":"a","configuration":{"k":["\ud83d"]},"position":{"x":0,"y":0}}],"edges":[]}""", "/nodes/0/configuration/k/0")]
    [InlineData("""{"nodes":[{"id":"a","configuration":{"\ud800":1},"position":{"x":0,"y":0}}],"edges":[]}""", "/nodes/0/configuration")]
    public void L4_an_escaped_unpaired_surrogate_is_unreadable(string text, string location)
    {
        var fault = Refused(text);

        (fault.Rule, fault.Location).Should().Be(("L4", location));
    }

    [Fact]
    public void L4_a_raw_unpaired_surrogate_is_unreadable_at_the_string_it_sits_in()
    {
        var fault = Refused("{\"nodes\":[{\"id\":\"a\ud800\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}");

        (fault.Rule, fault.Location).Should().Be(("L4", "/nodes/0/id"));
    }

    [Fact]
    public void L4_a_paired_surrogate_escaped_or_raw_reads()
    {
        Accepted("""{"nodes":[{"id":"😀","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""").Definition.Nodes.Single().Id.Should().Be("\U0001F600");
        Accepted("{\"nodes\":[{\"id\":\"\U0001F600\",\"position\":{\"x\":0,\"y\":0},\"configuration\":{}}],\"edges\":[]}").Definition.Nodes.Single().Id.Should().Be("\U0001F600");
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"a","blockTypeId":"Harmless","position":{"x":0,"y":0},"configuration":{}}],"Nodes":[{"id":"a","blockTypeId":"RunScript","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/Nodes", null)]
    [InlineData("""{"nodes":[{"id":"a","blockTypeId":"Harmless","BlockTypeId":"RunScript","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/BlockTypeId", "a")]
    public void L5_a_key_that_is_a_listed_key_in_another_case_is_a_fault_never_ignored(string text, string location, string? nodeId)
    {
        var fault = Refused(text);

        (fault.Rule, fault.Location, fault.NodeId).Should().Be(("L5", location, nodeId));
        var engine = JsonSerializer.Deserialize<WorkflowDefinition>(text, WorkflowGraphHelpers.JsonOptions)!;
        engine.Nodes.Single().BlockTypeId.Should().Be("RunScript", "this is what the engine would run");
    }

    [Fact]
    public void L5_the_same_variant_inside_a_sub_canvas_is_refused_with_its_path()
    {
        const string text = """{"nodes":[{"id":"loop","blockTypeId":"ForEach","subCanvases":{"Body":{"nodes":[{"id":"x","blockTypeId":"Harmless","position":{"x":0,"y":0},"configuration":{}}],"Nodes":[{"id":"x","blockTypeId":"RunScript","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";

        var fault = Refused(text);

        (fault.Rule, fault.Location, fault.NodeId).Should().Be(("L5", "/nodes/0/subCanvases/Body/Nodes", "loop"));
        fault.CanvasPath.Should().Equal(new CanvasPathSegment("loop", "Body"));
        JsonSerializer.Deserialize<WorkflowDefinition>(text, WorkflowGraphHelpers.JsonOptions)!
            .Nodes.Single().SubCanvases!["Body"].Nodes.Single().BlockTypeId.Should().Be("RunScript");
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"a","Position":{"x":1,"y":2},"configuration":{}}],"edges":[]}""", "/nodes/0/Position")]
    [InlineData("""{"nodes":[{"id":"a","position":{"X":1},"configuration":{}}],"edges":[]}""", "/nodes/0/position/X")]
    [InlineData("""{"edges":[{"id":"e","mappings":{"In":{"Type":"literal","literalValue":"x"}}}]}""", "/edges/0/mappings/In/Type")]
    [InlineData("""{"edges":[{"id":"e","mappings":{"In":{"type":"direct","SourceOutputName":"a"}}}]}""", "/edges/0/mappings/In/SourceOutputName")]
    [InlineData("""{"nodes":[{"id":"a","outputPorts":[{"name":"p","TYPE":"Int"}],"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/outputPorts/0/TYPE")]
    [InlineData("""{"nodes":[{"id":"a","errorPolicy":{"Policy":"Skip"},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/errorPolicy/Policy")]
    public void L5_holds_at_every_position_of_the_kind_table(string text, string location)
    {
        var fault = Refused(text);

        (fault.Rule, fault.Location).Should().Be(("L5", location));
    }

    [Fact]
    public void A_key_unknown_in_every_case_is_refused_at_its_first_position()
    {
        const string text = """{"meta":{"x":1},"nodes":[{"id":"a","blockTypeId":"b","name":"n","Label":"l"}],"NodesExtra":[{"blockTypeId":"RunScript"}]}""";

        var fault = Refused(text);
        (fault.Rule, fault.Location).Should().Be(("stored-key-unknown", "/meta"));
    }

    [Theory]
    [InlineData(typeof(WorkflowDefinition), "canvas")]
    [InlineData(typeof(WorkflowNodeDefinition), "node")]
    [InlineData(typeof(WorkflowNodePosition), "position")]
    [InlineData(typeof(WorkflowConnectionDefinition), "connection")]
    [InlineData(typeof(WorkflowOutputMappingDefinition), "output mapping")]
    [InlineData(typeof(SubCanvasOutputPortDefinition), "declared output")]
    [InlineData(typeof(NodeErrorConfig), "error policy")]
    public void Every_key_the_engine_binds_is_a_listed_key(Type model, string position)
    {
        var bound = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            .GetTypeInfo(model, WorkflowGraphHelpers.JsonOptions)!.Properties.Select(p => p.Name.ToLowerInvariant()).Order();
        var kind = StoredDocumentSchema.Objects(StoredDocumentSchema.Definition).Single(k => k.Name == position);
        var listed = kind.Fields.Select(f => f.Key).Select(k => k.ToLowerInvariant()).Order();
        StoredDocumentSchema.Models[model].Name.Should().Be(position);

        bound.Should().Equal(listed);
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"a","config":{},"position":{"x":0,"y":0}}],"edges":[]}""", "/nodes/0/config")]
    [InlineData("""{"nodes":[{"id":"a","CONFIG":null,"position":{"x":0,"y":0}}],"edges":[]}""", "/nodes/0/CONFIG")]
    [InlineData("""{"nodes":[{"id":"a","configuration":{"k":1},"config":null,"position":{"x":0,"y":0}}],"edges":[]}""", "/nodes/0/config")]
    [InlineData("""{"connections":[]}""", "/connections")]
    [InlineData("""{"nodes":[],"edges":[],"Connections":null}""", "/Connections")]
    [InlineData("""{"nodes":[{"id":"l","subCanvases":{"Body":{"connections":null}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/subCanvases/Body/connections")]
    public void A_key_the_grammar_does_not_list_is_unreadable_in_any_case_whatever_its_value(string text, string location)
    {
        var fault = Refused(text);

        (fault.Rule, fault.Location).Should().Be(("stored-key-unknown", location));
    }

    [Theory]
    [InlineData("[]", "")]
    [InlineData("null", "")]
    [InlineData("""{"nodes":{},"edges":[]}""", "/nodes")]
    [InlineData("""{"nodes":[null],"edges":[]}""", "/nodes/0")]
    [InlineData("""{"nodes":[{"id":5,"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/id")]
    [InlineData("""{"nodes":[{"id":null,"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/id")]
    [InlineData("""{"nodes":[{"id":"a","position":null,"configuration":{}}],"edges":[]}""", "/nodes/0/position")]
    [InlineData("""{"nodes":[{"id":"a","position":{"x":"1"},"configuration":{}}],"edges":[]}""", "/nodes/0/position/x")]
    [InlineData("""{"nodes":[{"id":"a","errorPolicy":{"policy":"skip"},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/errorPolicy/policy")]
    [InlineData("""{"nodes":[{"id":"a","errorPolicy":{"policy":3},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/errorPolicy/policy")]
    [InlineData("""{"nodes":[{"id":"a","errorPolicy":{"policy":"FailWorkflow, Skip"},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/errorPolicy/policy")]
    [InlineData("""{"nodes":[{"id":"a","passthrough":[null],"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/passthrough/0")]
    [InlineData("""{"nodes":[{"id":"a","subCanvases":{"Body":null},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/subCanvases/Body")]
    [InlineData("""{"nodes":[{"id":"a","outputPorts":[{"name":"p","sources":{"Then":5}}],"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "/nodes/0/outputPorts/0/sources/Then")]
    [InlineData("""{"edges":[{"id":"e","sourceNodeId":null,"mappings":{}}]}""", "/edges/0/sourceNodeId")]
    [InlineData("""{"edges":[{"id":"e","mappings":5}]}""", "/edges/0/mappings")]
    [InlineData("""{"edges":[{"id":"e","mappings":["x"]}]}""", "/edges/0/mappings")]
    [InlineData("""{"edges":[{"id":"e","mappings":{"In":"x"}}]}""", "/edges/0/mappings/In")]
    [InlineData("""{"edges":[{"id":"e","mappings":[{"sourceOutput":1}]}]}""", "/edges/0/mappings")]
    public void L7_a_listed_key_of_the_wrong_kind_is_unreadable(string text, string location)
    {
        var fault = Refused(text);

        (fault.Rule, fault.Location).Should().Be(("L7", location));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("\"true\"")]
    public void A_route_to_the_error_port_is_refused_under_its_own_rule_whatever_its_value(string value)
    {
        var fault = Refused($$"""{"nodes":[{"id":"a","errorPolicy":{"policy":"Skip","routeToErrorPort":{{value}} },"position":{"x":0,"y":0},"configuration":{} }],"edges":[]}""");

        (fault.Rule, fault.Location).Should().Be(("error-policy-route-unread", "/nodes/0/errorPolicy/routeToErrorPort"));
    }

    [Fact]
    public void L7_the_engine_refuses_the_text_the_reader_refuses_and_the_cache_answers_it_as_unreadable()
    {
        const string text = """{"nodes":[{"id":"a","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":null,"targetNodeId":"a","mappings":{}}]}""";

        Refused(text).Rule.Should().Be("L7");
        var engineRead = () => WorkflowDefinitionParse.FromText(text);
        engineRead.Should().Throw<JsonException>().WithMessage("*SourceNodeId*");
        var entry = WorkflowVersionDataCache.Entry.Create(text);
        entry.Definition.Should().BeNull();
        entry.ParseFailure.Should().NotBeNull();
    }

    [Fact]
    public void An_absent_null_or_blank_type_is_a_site_that_does_not_parse_never_a_fault()
    {
        var document = Accepted("""{"nodes":[{"id":"a","outputPorts":[{"name":"p"},{"name":"q","type":null},{"name":"r","type":""},{"name":"s","type":"Int"}],"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""");

        document.Sites.Select(s => (s.Location, s.PortName, s.RawType, s.Error?.Fault)).Should().Equal(
            ("/nodes/0/outputPorts/0/type", "p", (string?)null, (PortTypeSyntaxFault?)PortTypeSyntaxFault.Absent),
            ("/nodes/0/outputPorts/1/type", "q", null, PortTypeSyntaxFault.Absent),
            ("/nodes/0/outputPorts/2/type", "r", "", PortTypeSyntaxFault.Empty),
            ("/nodes/0/outputPorts/3/type", "s", "Int", null));
    }

    [Theory]
    [InlineData("{", "L1", "/nodes/0/configuration/InputPorts#")]
    [InlineData("""[{"name":"a","name":"b"}]""", "L3", "/nodes/0/configuration/InputPorts#/0/name")]
    [InlineData("""[{"name":"\ud800"}]""", "L4", "/nodes/0/configuration/InputPorts#/0/name")]
    [InlineData("""[{"Name":"a"}]""", "L5", "/nodes/0/configuration/InputPorts#/0/Name")]
    [InlineData("""[{"name":"a","isRequired":1}]""", "L7", "/nodes/0/configuration/InputPorts#/0/isRequired")]
    [InlineData("""{"name":"a"}""", "L7", "/nodes/0/configuration/InputPorts#")]
    public void A_port_list_is_a_document_of_its_own_whose_fault_is_the_definitions(string list, string rule, string location)
    {
        var text = JsonSerializer.Serialize(new { nodes = new[] { new { id = "a", configuration = new { InputPorts = list } } }, edges = Array.Empty<object>() });

        var fault = Refused(text);

        (fault.Rule, fault.Location, fault.NodeId).Should().Be((rule, location, "a"));
    }

    [Fact]
    public void A_port_list_nests_its_own_sixty_four_levels()
    {
        var deep = """[{"name":"a","type":""" + Nest(62) + "}]";
        var text = JsonSerializer.Serialize(new { nodes = new[] { new { id = "a", configuration = new { OutputPorts = deep } } }, edges = Array.Empty<object>() });

        var nearLimit = Refused(text);
        (nearLimit.Rule, nearLimit.Location).Should().Be(("L7", "/nodes/0/configuration/OutputPorts#/0/type"));
        var deeper = JsonSerializer.Serialize(new { nodes = new[] { new { id = "a", configuration = new { OutputPorts = """[{"name":"a","type":""" + Nest(63) + "}]" } } }, edges = Array.Empty<object>() });
        Refused(deeper).Rule.Should().Be("L2");
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"a","configuration":{"InputPorts":"[]","OutputPorts":"[]"},"position":{"x":0,"y":0}}],"edges":[]}""")]
    [InlineData("""{"nodes":[{"id":"a","configuration":{"inputPorts":"{","OUTPUTPORTS":5},"position":{"x":0,"y":0}}],"edges":[]}""")]
    public void No_list_or_a_key_that_is_not_the_librarys_is_no_site(string text)
    {
        Accepted(text).Sites.Should().BeEmpty();
    }

    [Fact]
    public void A_null_port_list_is_unreadable_for_there_is_one_spelling_of_empty()
    {
        var fault = Refused("""{"nodes":[{"id":"a","configuration":{"InputPorts":null},"position":{"x":0,"y":0}}],"edges":[]}""");

        fault.Rule.Should().Be("L7");
    }

    [Fact]
    public void A_port_list_value_that_is_not_a_string_is_unreadable()
    {
        var fault = Refused("""{"nodes":[{"id":"a","configuration":{"OutputPorts":[]},"position":{"x":0,"y":0}}],"edges":[]}""");

        (fault.Rule, fault.Location).Should().Be(("L7", "/nodes/0/configuration/OutputPorts"));
    }

    [Fact]
    public void Every_site_carries_its_kind_node_path_and_rfc6901_location()
    {
        var inner = Node("x/y~z", "dyn", new { InputPorts = """[{"name":"i","type":"Int"}]""" });
        inner.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "o", Type = "Int" }];
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["a/b~c"] = Graph([inner]) };

        var document = StoredWorkflowDocument.ReadModel(Graph([StartNode(), loop]), out _)!;

        document.Sites.Select(s => (s.Site, s.Location, s.NodeId)).Should().Equal(
            (PortTypeNameSite.DynamicInputPort, "/nodes/1/subCanvases/a~1b~0c/nodes/0/configuration/InputPorts#/0/type", "x/y~z"),
            (PortTypeNameSite.DeclaredOutput, "/nodes/1/subCanvases/a~1b~0c/nodes/0/outputPorts/0/type", "x/y~z"));
        document.Sites.Should().OnlyContain(s => s.CanvasPath.SequenceEqual(new[] { new CanvasPathSegment("loop", "a/b~c") }));
    }



    [Fact]
    public void The_writer_writes_camel_case_keys_one_spelling_per_field_and_keeps_config_keys_as_written()
    {
        var node = Node("a", "b", new { DeviceId = "d", Kind = "k" });
        node.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.Skip);
        var text = Stored(new WorkflowDefinition { Nodes = [node], Edges = [Edge("e", "a", "a")] });

        text.Should().Contain("\"nodes\":").And.Contain("\"blockTypeId\":\"b\"").And.Contain("\"configuration\":{\"DeviceId\":\"d\",\"Kind\":\"k\"}")
            .And.Contain("\"policy\":\"Skip\"").And.Contain("\"edges\":[{\"id\":\"e\"").And.NotContain("\"config\"")
            .And.NotContain("connections").And.NotContain("\"Nodes\"");
        Accepted(text);
    }

    [Fact]
    public void A_model_string_with_an_unpaired_surrogate_is_refused_under_L4_never_written_as_a_replacement()
    {
        var keyed = Node("a", "b");
        keyed.Config = new() { ["k\udc00"] = JsonDocument.Parse("1").RootElement.Clone() };
        foreach (var graph in new[] { Graph([Node("a\ud800", "b")]), Graph([keyed]) })
        {
            StoredWorkflowDocument.Write(graph, out var failure).Should().BeNull();
            failure.Should().Contain("unpaired surrogate");
            StoredWorkflowDocument.ReadModel(graph, out var fault).Should().BeNull();
            (fault!.Rule, fault.Detail).Should().Be(("L4", "a string with an unpaired surrogate in the model"));
        }
    }

    [Fact]
    public void A_model_that_cannot_be_written_is_refused_as_unreadable_not_thrown()
    {
        var node = Node("a", "b");
        node.Config = new() { ["k"] = default };

        var result = WorkflowCompiler.Compile(Graph([node]), Catalogue(Shape("b")), CanvasSignature.Empty);

        var refusal = result.Diagnostics.Should().ContainSingle().Which;
        refusal.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        refusal.Message.Should().Contain("cannot be written as JSON");
    }

    [Fact]
    public void A_model_with_a_null_node_or_a_null_source_is_refused_not_thrown()
    {
        var withNullNode = new WorkflowDefinition { Nodes = [StartNode(), null!] };
        var nullSource = (WorkflowConnectionDefinition)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(WorkflowConnectionDefinition));
        (nullSource.Id, nullSource.TargetNodeId) = ("e", "start");
        var withNullSource = Graph([StartNode()], [nullSource]);

        foreach (var (graph, location) in new[] { (withNullNode, "/nodes/1"), (withNullSource, "/edges/0/sourceNodeId") })
        {
            var refusal = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId)), CanvasSignature.Empty)
                .Diagnostics.Should().ContainSingle().Which;
            refusal.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
            refusal.Message.Should().Contain($"rule L7, at '{location}'");
        }
    }

    [Fact]
    public void The_reader_hands_the_compiler_the_model_the_engine_builds()
    {
        const string text = """{"nodes":[{"id":"a","blockTypeId":"b","configuration":{"X":1},"errorPolicy":{"policy":"EmitError"},"position":{"x":1e3,"y":-2}}],"edges":[{"id":"e","sourceNodeId":"a","targetNodeId":"a","mappings":{"In":{"type":"literal","literalValue":[1]}}}]}""";

        var read = Accepted(text).Definition;
        var engine = JsonSerializer.Deserialize<WorkflowDefinition>(text, WorkflowGraphHelpers.JsonOptions)!;

        Stored(read).Should().Be(Stored(engine));
    }
}
