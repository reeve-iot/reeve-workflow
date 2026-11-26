using FluentAssertions;
using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;
using ReeveWorkflow.Core.Tests.TestSupport;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class TypePhaseTests
{
    [Fact]
    public void Compiler_literal_verdict_matches_each_shared_contract_vector()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.LiteralCoercionPath()));
        var options = LiteralVectors.OptionsOf(document.RootElement);
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var type = PortValueType.Of(row.GetProperty("type").GetString()!);
            if (PortTermGrading.JsonMisplaced(PortTerm.Parse(type.Name, PortTermGrammar.Document), TermPosition.Input))
                continue;
            var expected = row.GetProperty("accepted").GetBoolean();
            var spelledAsText = row.TryGetProperty("text", out var text);
            var json = default(JsonElement);
            var hasValue = spelledAsText ? (json = JsonDocument.Parse(text.GetString()!).RootElement).ValueKind != JsonValueKind.Undefined : row.TryGetProperty("value", out json);
            var literal = hasValue
                ? (object)new { type = "literal", literalValue = json }
                : new { type = "literal" };
            var graph = Graph([StartNode(), Node("n", "receiver")], [new WorkflowConnectionDefinition
            {
                Id = "e", SourceNodeId = "start", TargetNodeId = "n",
                Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["Value"] = literal }),
            }]);
            var shape = Shape("receiver", inputs: [("Value", type, true)]);

            var result = WorkflowCompiler.Compile(
                graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), shape), CanvasSignature.Empty, options);
            result.Fault.Should().BeNull($"{row}: a vector ends in a verdict, never a fault");

            if (!spelledAsText && hasValue && json.ValueKind is JsonValueKind.Null)
            {
                result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.DefinitionUnreadable, $"{row}");
                continue;
            }

            if (!spelledAsText && hasValue && !LiteralVectors.StoredAsWritten(json))
            {
                result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.DefinitionUnreadable, $"{row}");
                continue;
            }

            if (spelledAsText && !expected)
            {
                result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.DefinitionUnreadable, $"{row}");
                result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.LiteralPortValueInvalid, $"{row}");
                continue;
            }

            if (RefusedByVocabulary(type, options, result, $"{row}"))
                continue;

            result.Diagnostics.Any(d => d.RuleId == CompilationRules.LiteralPortValueInvalid)
                .Should().Be(!expected, $"{row}");
        }
    }

    private static bool RefusedByVocabulary(PortValueType type, CompilationOptions options, CompilationResult result, string because)
    {
        if (PortTermGrading.Grade(type.Name, TermSeam.Declaration, TermPosition.Input, options.Vocabulary.Table, out var refusal) is not null
            || refusal!.Value.Rule is not (CompilationRules.PortTypeAlias or CompilationRules.PortTypeUnregistered))
            return false;
        result.Diagnostics.Should().Contain(d => d.RuleId == refusal.Value.Rule, because);
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.LiteralPortValueInvalid, because);
        return true;
    }

    [Fact]
    public void A_configuration_value_feeding_a_port_with_no_field_gets_the_verdict_a_wire_literal_gets()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.LiteralCoercionPath()));
        var options = LiteralVectors.OptionsOf(document.RootElement);
        var graded = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            if (!row.TryGetProperty("value", out var json) || json.ValueKind is JsonValueKind.Null)
                continue;
            var type = PortValueType.Of(row.GetProperty("type").GetString()!);
            var graph = Graph([StartNode(), Node("n", "receiver", new Dictionary<string, object?> { ["Value"] = json })],
                [Edge("e", "start", "n")]);
            var shape = Shape("receiver", inputs: [("Value", type, false)]);

            var result = WorkflowCompiler.Compile(
                graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), shape), CanvasSignature.Empty, options);
            result.Fault.Should().BeNull($"{row}: a vector ends in a verdict, never a fault");

            if (!LiteralVectors.StoredAsWritten(json))
            {
                result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.DefinitionUnreadable, $"{row}");
                continue;
            }

            if (RefusedByVocabulary(type, options, result, $"{row}"))
                continue;

            result.Diagnostics.Any(d => d.RuleId == CompilationRules.LiteralPortValueInvalid && d.NodeId == "n" && d.PortName == "Value")
                .Should().Be(!row.GetProperty("accepted").GetBoolean(), $"{row}");
            graded++;
        }

        graded.Should().BeGreaterThan(100);
    }

    [Fact]
    public void A_configuration_field_naming_an_input_gets_the_verdict_the_inputs_term_gets()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.LiteralCoercionPath()));
        var options = LiteralVectors.OptionsOf(document.RootElement);
        var graded = 0;
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            if (!row.TryGetProperty("value", out var json) || json.ValueKind is JsonValueKind.Null || !LiteralVectors.StoredAsWritten(json))
                continue;
            var type = PortValueType.Of(row.GetProperty("type").GetString()!);
            var graph = Graph([StartNode(), Node("n", "receiver", new Dictionary<string, object?> { ["Value"] = json })],
                [Edge("e", "start", "n")]);
            var shape = Shape("receiver", inputs: [("Value", type, false)], configFields: [("Value", ConfigFieldType.String, false)]);

            var result = WorkflowCompiler.Compile(
                graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), shape), CanvasSignature.Empty, options);
            result.Fault.Should().BeNull($"{row}: a vector ends in a verdict, never a fault");

            if (RefusedByVocabulary(type, options, result, $"{row}"))
                continue;

            result.Diagnostics.Any(d => d.RuleId == CompilationRules.LiteralPortValueInvalid && d.NodeId == "n" && d.PortName == "Value")
                .Should().Be(!row.GetProperty("accepted").GetBoolean(), $"{row}");
            graded++;
        }

        graded.Should().BeGreaterThan(100);
    }

    [Theory]
    [InlineData("expr:1 + 1", false)]
    [InlineData("5", false)]
    public void A_configuration_expression_or_text_for_a_text_port_with_no_field_is_not_refused(string text, bool refused)
    {
        var graph = Graph([StartNode(), Node("n", "receiver", new Dictionary<string, object?> { ["Value"] = text })],
            [Edge("e", "start", "n")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("receiver", inputs: [("Value", PortValueType.String, false)])), CanvasSignature.Empty);

        result.Diagnostics.Any(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).Should().Be(refused);
    }

    [Theory]
    [InlineData("Collection<String>", "\"x\"", true)]
    [InlineData("Collection<String>", "{\"a\":1}", true)]
    [InlineData("Collection<String>", "[\"a\",\"b\"]", false)]
    [InlineData("Collection<Collection<String>>", "\"x\"", true)]
    [InlineData("Collection<String>", "[1]", true)]
    public void A_configuration_value_for_a_collection_port_is_graded_as_a_wire_literal_is(string type, string json, bool refused)
    {
        var value = JsonDocument.Parse(json).RootElement.Clone();
        var graph = Graph([StartNode(), Node("n", "receiver", new Dictionary<string, object?> { ["Items"] = value })],
            [Edge("e", "start", "n")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("receiver", inputs: [("Items", PortValueType.Of(type), false)])), CanvasSignature.Empty);

        result.Diagnostics.Any(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).Should().Be(refused);
    }

    [Fact]
    public void A_port_with_a_configuration_field_of_its_name_is_graded_once_by_the_field()
    {
        var graph = Graph([StartNode(), Node("n", "receiver", new Dictionary<string, object?> { ["Value"] = 5 })],
            [Edge("e", "start", "n")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("receiver", inputs: [("Value", PortValueType.String, false)], configFields: [("Value", ConfigFieldType.String, false)])),
            CanvasSignature.Empty);

        result.Diagnostics.Count(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).Should().Be(1);
    }

    [Theory]
    [InlineData("5", false)]
    [InlineData("not-a-number", false)]
    [InlineData(5, true)]
    public void A_literal_for_an_Int_port_is_the_number_and_never_text_that_reads_as_one(object literal, bool accepted)
    {
        var graph = Graph([StartNode(), Node("n", "receiver")],
            [LiteralEdge("e", "start", "n", "Value", literal)]);
        var shape = Shape("receiver", inputs: [("Value", PortValueType.Int, true)]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), shape), CanvasSignature.Empty);

        result.Diagnostics.Any(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).Should().Be(!accepted);
    }

    [Theory]
    [InlineData(ConfigFieldType.Boolean, "expr:${previous.read.Value} > 3", false)]
    [InlineData(ConfigFieldType.Int, "expr:1 + 1", false)]
    [InlineData(ConfigFieldType.Number, "expr:${parameter.x}", false)]
    [InlineData(ConfigFieldType.Boolean, "maybe", true)]
    public void A_configuration_expression_is_not_graded_as_a_scalar(ConfigFieldType type, string text, bool refused)
    {
        var graph = Graph([StartNode(), Node("n", "gate", new Dictionary<string, object?> { ["Field"] = text })],
            [Edge("e", "start", "n")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("gate", configFields: [("Field", type, false)])), CanvasSignature.Empty);

        result.Diagnostics.Any(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).Should().Be(refused);
    }

    [Fact]
    public void A_literal_into_a_typed_workflow_output_is_refused()
    {
        var graph = Graph([StartNode(), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [LiteralEdge("e", "start", "out", "Count", "abc")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId)),
            new CanvasSignature([], [new PortShape("Count", PortValueType.Int, false)]));

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.LiteralPortValueInvalid
            && d.NodeId == "out" && d.PortName == "Count");
    }

    [Fact]
    public void Compiler_refuses_unparseable_text_for_an_int_literal_port()
    {
        var graph = Graph([StartNode(), Node("n", "receiver")],
            [LiteralEdge("e", "start", "n", "Value", "not-a-number")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("receiver", inputs: [("Value", PortValueType.Int, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.LiteralPortValueInvalid
            && d.NodeId == "n" && d.PortName == "Value");
    }

    [Fact]
    public void A_wire_carrying_a_type_the_input_cannot_take_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"), Node("b", "deliver")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Temperature", "Depot"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)])), CanvasSignature.Empty, HostTypes("Depot"));

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.PortTypeMismatch && d.PortName == "Depot" && d.NodeId == "b");
    }

    [Fact]
    public void A_wire_that_only_widens_is_not_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "count"), Node("b", "scale")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Count", "Factor"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("count", outputs: [("Count", PortValueType.Int)]),
            Shape("scale", inputs: [("Factor", PortValueType.Number, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Each_of_two_wires_into_one_input_is_graded()
    {
        var graph = Graph(
            [StartNode(), Node("s", "text"), Node("i", "count"), Node("b", "scale")],
            [Edge("e1", "start", "s"), Edge("e2", "start", "i"), Edge("e3", "s", "b", ("Text", "Factor")),
             Edge("e4", "i", "b", ("Count", "Factor"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("text", outputs: [("Text", PortValueType.String)]),
            Shape("count", outputs: [("Count", PortValueType.Int)]),
            Shape("scale", inputs: [("Factor", PortValueType.Int, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.DuplicateInputMapping);
        result.Diagnostics.Should().ContainSingle(d =>
            d.RuleId == CompilationRules.PortTypeMismatch && d.NodeId == "b" && d.PortName == "Factor");
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData(3, false)]
    public void A_configuration_value_on_a_resolver_answered_input_is_graded_by_its_term(object count, bool refused)
    {
        const string childId = "11111111-1111-1111-1111-111111111111";
        var graph = Graph(
            [StartNode(), Node("sub", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new Dictionary<string, object?> { ["WorkflowId"] = childId, ["Count"] = count })],
            [Edge("e1", "start", "sub")]);
        var environment = Environment((CanvasPath.Root, "sub", childId, [("Count", PortValueType.Int, true)], []));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true,
                runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        if (refused)
        {
            result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.LiteralPortValueInvalid && d.NodeId == "sub" && d.PortName == "Count");
        }
        else
        {
            result.Diagnostics.Should().BeEmpty();
        }
    }

    [Fact]
    public void A_literal_mapping_is_never_reported_as_a_type_mismatch()
    {
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = """[{"name":"","type":"Int"}]""" }),
             Node("b", "deliver")],
            [Edge("e1", "start", "a"), LiteralEdge("e2", "a", "b", "Depot", "not-a-depot-object")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.source", hasDynamicPorts: true),
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)])), CanvasSignature.Empty, HostTypes("Depot"));

        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public void A_wire_from_a_node_whose_block_type_is_unknown_is_not_also_reported_as_a_mismatch()
    {
        var graph = Graph(
            [StartNode(), Node("a", "missing-block"), Node("b", "deliver")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Temperature", "Depot"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)])), CanvasSignature.Empty, HostTypes("Depot"));

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public void A_dynamic_output_declared_bool_feeds_an_input_declared_Boolean()
    {
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = """[{"name":"Flag","type":"bool"}]""" }),
             Node("b", "act")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Flag", "On"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.source", hasDynamicPorts: true),
            Shape("act", inputs: [("On", PortValueType.Boolean, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public void An_asymmetric_host_widening_from_options_applies_in_its_own_direction_only()
    {
        var options = new CompilationOptions
        {
            TypeRegistry = new WorkflowTypeRegistry(
                [new WorkflowTypeRecord("Depot", typeof(string)), new WorkflowTypeRecord("BondedDepot", typeof(string)) { WidensTo = ["Depot"] }])
        };
        var shapes = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bonded", outputs: [("Out", PortValueType.Of("BondedDepot"))]),
            Shape("depot", outputs: [("Out", PortValueType.Of("Depot"))]),
            Shape("bondedSink", inputs: [("In", PortValueType.Of("BondedDepot"), true)]),
            Shape("depotSink", inputs: [("In", PortValueType.Of("Depot"), true)]));

        var forward = WorkflowCompiler.Compile(
            Graph([StartNode(), Node("a", "bonded"), Node("b", "depotSink")],
                [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Out", "In"))]),
            shapes, CanvasSignature.Empty, options);
        forward.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.PortTypeMismatch);

        var backward = WorkflowCompiler.Compile(
            Graph([StartNode(), Node("a", "depot"), Node("b", "bondedSink")],
                [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Out", "In"))]),
            shapes, CanvasSignature.Empty, options);
        backward.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public void A_mismatch_through_the_root_signatures_declared_parameter_type_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify")],
            [Edge("e1", "start", "n1", ("temperature", "Message"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            signature: Signature(inputs: [("temperature", PortValueType.Number, true)]));

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public void A_mismatch_through_a_sub_canvas_branch_declared_input_type_is_refused()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "deliver")],
            [Edge("be1", "in", "n1", ("Current", "Depot"))]);
        var loop = Node("loop", "ForLoop");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ForLoop", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [("Current", PortValueType.Number, false)],
            }, subCanvasNames: ["Body"]),
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)])), CanvasSignature.Empty, HostTypes("Depot"));

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public void A_mismatch_through_a_dynamically_declared_output_port_type_is_refused()
    {
        var source = Node("n1", "dyn.block", new { OutputPorts = """[{"name":"Reading","type":"String"}]""" });
        var graph = Graph(
            [StartNode(), source, Node("n2", "deliver")],
            [Edge("e1", "start", "n1"), Edge("e2", "n1", "n2", ("Reading", "Depot"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.block", hasDynamicPorts: true),
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)])), CanvasSignature.Empty, HostTypes("Depot"));

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public void A_mismatch_through_a_resolver_supplied_output_type_is_refused()
    {
        const string childId = "11111111-1111-1111-1111-111111111111";
        var graph = Graph(
            [StartNode(), Node("sub", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId }),
             Node("sink", "deliver")],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "sink", ("Result", "Depot"))]);

        var environment = Environment((CanvasPath.Root, "sub", childId, [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true,
                runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)]),
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)])),
            CanvasSignature.Empty, HostTypes("Depot"), environment: environment);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Fact]
    public void A_mismatch_through_the_signatures_declared_result_type_is_refused()
    {
        var end = Node("end", WorkflowGraphHelpers.WorkflowOutputBlockTypeId);
        var graph = Graph(
            [StartNode(), Node("n1", "sensor"), end],
            [Edge("e1", "start", "n1"), Edge("e2", "n1", "end", ("Reading", "temperature"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            Shape("sensor", outputs: [("Reading", PortValueType.String)])),
            signature: Signature(outputs: [("temperature", PortValueType.Number, true)]));

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    private static readonly BlockShape ForEach = new(
        BlockTypeId: "foreach",
        Inputs: [new PortShape("Collection", PortValueType.OfDeclaration("Collection<$T>"), true)],
        Outputs: [new PortShape("Results", PortValueType.OfDeclaration("Collection<$R>"), false)],
        ConfigurationFields: [],
        Flags: new BlockFlags(false, false, false, true, false),
        SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [] },
        SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["Body"] = [new PortShape("Result", PortValueType.OfDeclaration("$R"), false)],
        },
        Source: null,
        SubCanvasNames: ["Body"],
        TypeParameters: [new WorkflowTypeParameter("T"), new WorkflowTypeParameter("R")]);

    [Fact]
    public void A_mismatch_through_a_body_bound_parameter_is_refused()
    {
        var body = Graph(
            [Node("src", "room-source"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("be1", "src", "out", ("Room", "Result"))]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph(
            [StartNode(), Node("rooms", "rooms-source"), loop, Node("sink", "deliver")],
            [Edge("e1", "start", "rooms"), Edge("e2", "rooms", "loop", ("Rooms", "Collection")),
             Edge("e3", "loop", "sink", ("Results", "Depot"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("rooms-source", outputs: [("Rooms", PortValueType.CollectionOf(PortValueType.Of("Room")))]),
            Shape("room-source", outputs: [("Room", PortValueType.Of("Room"))]),
            ForEach,
            Shape("deliver", inputs: [("Depot", PortValueType.Of("Depot"), true)])), CanvasSignature.Empty, HostTypes("Depot", "Room"));

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Theory]
    [InlineData("Collection< >")]
    [InlineData("Collection ")]
    public void A_malformed_dynamic_port_type_is_reported_not_a_crash(string outType)
    {
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = "[{\"name\":\"Items\",\"type\":\"" + outType + "\"}]" }),
             Node("b", "act")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Items", "In"))]);

        var compile = () => WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.source", hasDynamicPorts: true),
            Shape("act", inputs: [("In", PortValueType.String, true)])), CanvasSignature.Empty);

        compile.Should().NotThrow();
        var result = compile();
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeMalformed);
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.PortTypeMismatch);
    }

    [Theory]
    [InlineData("5", true)]
    [InlineData(5, false)]
    public void A_configuration_literal_for_an_Int_field_is_the_number_and_never_text_that_reads_as_one(object value, bool refused)
    {
        var graph = Graph([StartNode(), Node("n", "gate", new Dictionary<string, object?> { ["Field"] = value })],
            [Edge("e", "start", "n")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("gate", configFields: [("Field", ConfigFieldType.Int, false)])), CanvasSignature.Empty);

        result.Diagnostics.Any(d => d.RuleId == CompilationRules.LiteralPortValueInvalid).Should().Be(refused);
    }

}
