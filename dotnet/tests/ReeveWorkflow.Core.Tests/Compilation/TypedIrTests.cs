using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class TypedIrTests
{
    private static PortValueType Declared(string text) => PortValueType.OfDeclaration(text);

    private static readonly BlockShape ForEach = new(
        BlockTypeId: "foreach",
        Inputs: [new PortShape("Collection", Declared("Collection<$T>"), true)],
        Outputs: [new PortShape("Results", Declared("Collection<$R>"), false)],
        ConfigurationFields: [],
        Flags: new BlockFlags(false, false, false, true, false),
        SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["Body"] = [new PortShape("Index", PortValueType.Int, false), new PortShape("Item", Declared("$T"), false)],
        },
        SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>> { ["Body"] = [new PortShape("Result", Declared("$R"), false)] },
        Source: null,
        SubCanvasNames: ["Body"],
        TypeParameters: [new WorkflowTypeParameter("T"), new WorkflowTypeParameter("R")]);

    private static readonly BlockShape Filter = new(
        BlockTypeId: "filter",
        Inputs: [new PortShape("Collection", Declared("Collection<$T>"), true)],
        Outputs: [new PortShape("Filtered", Declared("Collection<$T>"), false)],
        ConfigurationFields: [],
        Flags: new BlockFlags(false, false, false, false, false),
        SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
        SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
        Source: null,
        TypeParameters: [new WorkflowTypeParameter("T")]);

    private static BlockShape Numbers => Shape("numbers", outputs: [("Values", PortValueType.CollectionOf(PortValueType.Int))]);

    private static CompiledNode Lowered(WorkflowDefinition graph, IBlockShapeSource catalogue, string nodeId, NodePortEnvironment? environment = null)
    {
        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, environment: environment);
        result.Diagnostics.Should().BeEmpty(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ": " + d.Message)));
        return result.Program!.Canvases.SelectMany(c => c.Nodes).Single(n => n.NodeId == nodeId);
    }

    [Fact]
    public void Filter_over_a_collection_of_Int_carries_the_closed_terms_and_their_CLR_types()
    {
        var graph = Graph(
            [StartNode(), Node("src", "numbers"), Node("f", "filter")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "f", ("Values", "Collection"))]);

        var node = Lowered(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Numbers, Filter), "f");

        node.InputTypes["Collection"].Term.ToString().Should().Be("Collection<Int>");
        node.OutputTypes["Filtered"].ClrType.Should().Be(typeof(IReadOnlyList<long>));
        node.TypeArguments["T"].ClrType.Should().Be(typeof(long));
        node.Types["$T"].Term.ToString().Should().Be("Int");
        node.Types["Filtered"].Should().BeSameAs(node.OutputTypes["Filtered"]);
        node.Types["Collection"].Should().BeSameAs(node.InputTypes["Collection"]);
    }

    [Fact]
    public void A_loop_hands_its_block_the_body_ports_and_drops_an_output_nothing_binds_or_reads()
    {
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId)]) };
        var graph = Graph(
            [StartNode(), Node("src", "numbers"), loop],
            [Edge("e1", "start", "src"), Edge("e2", "src", "loop", ("Values", "Collection"))]);

        var node = Lowered(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Numbers, ForEach), "loop");

        node.Types["Body.Item"].Term.ToString().Should().Be("Int");
        node.Types["Body.Index"].ClrType.Should().Be(typeof(long));
        node.DroppedOutputs.Should().Equal("Results");
        node.OutputTypes.Should().NotContainKey("Results");
        node.TypeArguments.Keys.Should().Equal("T");
        node.Types.Values.Should().OnlyContain(t => !t.Term.ContainsParameter, "no unbound parameter reaches the IR");
    }

    [Theory]
    [InlineData("Int", "Number", "IntToNumber")]
    [InlineData("Int", "Number?", "NullLift(IntToNumber)")]
    [InlineData("Int", "Int?", "NullLift")]
    [InlineData("Collection<Int>", "Collection<Number>", "Elementwise(IntToNumber)")]
    [InlineData("Collection<Int>", "Collection<Int?>", "Elementwise(NullLift)")]
    [InlineData("String", "String?", "Identity")]
    public void A_wire_records_the_conversion_of_its_assignment(string from, string to, string conversion)
    {
        var graph = Graph(
            [StartNode(), Node("src", "src"), Node("sink", "sink")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "sink", ("V", "X"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("src", outputs: [("V", Declared(from))]),
            Shape("sink", inputs: [("X", Declared(to), true)]));

        var node = Lowered(graph, catalogue, "sink");

        node.Incoming.Single(e => e.SourceNodeId == "src").Conversions["X"].ToString().Should().Be(conversion);
    }

    [Fact]
    public void An_expr_result_of_Int_into_a_Number_input_records_IntToNumber()
    {
        var graph = Graph(
            [StartNode(), Node("src", "src"), Node("sink", "sink", new { X = "expr:${previous.src.V}" })],
            [Edge("e1", "start", "src"), Edge("e2", "src", "sink")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("src", outputs: [("V", PortValueType.Int)]),
            Shape("sink", inputs: [("X", PortValueType.Number, true)]));

        var node = Lowered(graph, catalogue, "sink");

        node.Expressions["X"].Conversion!.ToString().Should().Be("IntToNumber");
    }

    [Fact]
    public void Configuration_values_are_coerced_to_the_CLR_type_of_the_term_they_meet()
    {
        var graph = Graph(
            [StartNode(), Node("sink", "sink", new Dictionary<string, object?>
            {
                ["N"] = 5, ["D"] = "2026-10-13T10:00:00+02:00", ["L"] = new[] { 1, 2 }, ["Scale"] = 2, ["Mode"] = "",
            })],
            [Edge("e1", "start", "sink")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("sink",
                inputs: [("N", PortValueType.Number, false), ("D", PortValueType.Of("DateTime"), false),
                    ("L", PortValueType.CollectionOf(PortValueType.Int), false)],
                configFields: [("Scale", ConfigFieldType.Number, false), ("Mode", ConfigFieldType.Enum, false)],
                enumValues: ["Fast", "Slow"]));

        var node = Lowered(graph, catalogue, "sink");

        node.Inputs["N"].Should().BeOfType<double>().Which.Should().Be(5.0);
        node.Inputs["D"].Should().Be(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.FromHours(2)));
        node.Inputs["L"].Should().BeOfType<List<long>>().Which.Should().Equal(1L, 2L);
        node.Inputs["Scale"].Should().BeOfType<double>().Which.Should().Be(2.0);
        node.Inputs.Should().NotContainKey("Mode");
    }

    [Fact]
    public void A_literal_mapping_is_coerced_to_its_inputs_CLR_type()
    {
        var graph = Graph(
            [StartNode(), Node("sink", "sink")],
            [LiteralEdge("e1", "start", "sink", "N", 5)]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("sink", inputs: [("N", PortValueType.Number, true)]));

        var node = Lowered(graph, catalogue, "sink");

        node.Incoming.Single().Literals["N"].Should().BeOfType<double>().Which.Should().Be(5.0);
    }

    [Fact]
    public void The_required_inputs_include_those_a_resolver_answers()
    {
        var graph = Graph(
            [StartNode(), Node("sub", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = "child", Count = 3 })],
            [Edge("e1", "start", "sub")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true,
                runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)]));
        var environment = Environment((CanvasPath.Root, "sub", "child", [("Count", PortValueType.Int, true)], []));

        var node = Lowered(graph, catalogue, "sub", environment);

        node.RequiredInputs.Should().BeEquivalentTo(["WorkflowId", "Count"]);
        node.Inputs["Count"].Should().Be(3L);
    }

    [Fact]
    public void CreateList_builds_the_terms_list_normalising_each_element_as_the_gate_does()
    {
        var type = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse("Collection<Int>", PortTermGrammar.Document))!;

        var list = type.CreateList([1, 2L, (short)3]);

        list.Should().BeOfType<List<long>>().Which.Should().Equal(1L, 2L, 3L);
        type.Invoking(t => t.CreateList([null])).Should().Throw<ArgumentException>().WithMessage("*not nullable*");
        type.Invoking(t => t.CreateList(["a"])).Should().Throw<ArgumentException>().WithMessage("*String*");
        WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Atom("Int"))!.Invoking(t => t.CreateList([]))
            .Should().Throw<InvalidOperationException>().WithMessage("*not a collection*");
    }

    [Theory]
    [InlineData("Collection<Number>")]
    [InlineData("Collection<Number?>")]
    public void CreateList_and_Copy_refuse_a_non_finite_Number_element_and_keep_a_finite_one(string term)
    {
        var type = WorkflowTypeVocabulary.Builtin.ClosedTypeOf(PortTerm.Parse(term, PortTermGrammar.Document))!;

        type.CreateList([1.5, 2.5f]).Count.Should().Be(2);
        type.Invoking(t => t.CreateList([1.5, double.NaN])).Should().Throw<ArgumentException>().WithMessage("*Element 1*non-finite*");
        type.Invoking(t => t.CreateList([double.PositiveInfinity])).Should().Throw<ArgumentException>().WithMessage("*non-finite*");
        type.Invoking(t => t.CreateList([float.NegativeInfinity])).Should().Throw<ArgumentException>().WithMessage("*non-finite*");
        type.Invoking(t => t.Copy(new List<double> { 1.5, double.NaN })).Should().Throw<ArgumentException>().WithMessage("*non-finite*");
    }

    [Fact]
    public void ToJson_writes_the_value_with_its_terms_serializer_and_maps_a_JSON_null_to_null()
    {
        var vocabulary = WorkflowTypeVocabulary.Builtin;
        var numbers = vocabulary.ClosedTypeOf(PortTerm.Parse("Collection<Number?>", PortTermGrammar.Document))!;
        var when = vocabulary.ClosedTypeOf(PortTerm.Atom("DateTime"))!;

        numbers.ToJson(new List<double?> { 1.5, null })!.Value.GetRawText().Should().Be("[1.5,null]");
        when.ToJson(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero))!.Value.GetString().Should().Be("2026-10-13T10:00:00+00:00");
        vocabulary.ClosedTypeOf(PortTerm.Atom("Int"))!.ToJson(null).Should().BeNull();
        vocabulary.ClosedTypeOf(PortTerm.Atom("Json"))!.ToJson(JsonDocument.Parse("null").RootElement).Should().BeNull();
    }

    [Fact]
    public void A_10000_deep_wire_lowers_and_converts_on_a_256KB_stack()
    {
        const int depth = 10_000;
        string Deep(string atom) => string.Concat(Enumerable.Repeat("Collection<", depth)) + atom + new string('>', depth);
        var graph = Graph(
            [StartNode(), Node("wo", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("e1", "start", "wo", ("P", "O"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId));
        var signature = Signature(inputs: [("P", PortValueType.Of(Deep("Int")), true)], outputs: [("O", PortValueType.Of(Deep("Number")), true)]);

        OnASmallStack(() =>
        {
            var result = WorkflowCompiler.Compile(graph, catalogue, signature);
            result.Diagnostics.Should().BeEmpty();
            var node = result.Program!.Canvases.Single().Nodes.Single(n => n.NodeId == "wo");
            var conversion = node.Incoming.Single().Conversions["O"];
            var text = conversion.ToString();
            text.Should().HaveLength((depth * "Elementwise()".Length) + "IntToNumber".Length);
            text.Should().StartWith("Elementwise(Elementwise(").And.Contain("(IntToNumber)");

            object? value = new List<object?> { 7 };
            for (var i = 1; i < depth; i++)
                value = new List<object?> { value };
            var converted = conversion.Apply(value);
            var copied = node.InputTypes["O"].Copy(converted);
            for (var i = 1; i < depth; i++)
                copied = ((System.Collections.IList)copied!)[0];
            copied.Should().BeOfType<List<double>>().Which.Should().Equal(7.0);
        });
    }

    [Fact]
    public void A_conversion_refuses_a_value_off_its_source_term()
    {
        var vocabulary = WorkflowTypeVocabulary.Builtin;
        PortConversion Of(string from, string to) =>
            vocabulary.ConversionOf(PortTerm.Parse(from, PortTermGrammar.Document), PortTerm.Parse(to, PortTermGrammar.Document))!;

        Of("Int", "Number").Invoking(c => c.Apply("five")).Should().Throw<PortConversionException>().WithMessage("*String*Int*");
        Of("Collection<Int>", "Collection<Number>").Invoking(c => c.Apply(5L)).Should().Throw<PortConversionException>();
        Of("Collection<Int>", "Collection<Int?>").Invoking(c => c.Apply(new List<object?> { "a" }))
            .Should().Throw<PortConversionException>().WithMessage("*String*");
        Of("Int", "Json").Invoking(c => c.Apply(1.5)).Should().Throw<PortConversionException>().WithMessage("*Double*Int*");
        Of("Int", "Json").Apply(2L).Should().BeOfType<JsonElement>().Which.GetInt64().Should().Be(2);
    }

    [Fact]
    public void Default_outputs_are_coerced_to_their_outputs_CLR_types()
    {
        var node = Lowered(DefaultsGraph("""{"V":2,"L":[1,2],"D":"2026-10-13T10:00:00+00:00","J":{"a":1},"N":null}"""), DefaultsCatalogue, "src");

        node.DefaultOutputs!["V"].Should().BeOfType<long>().Which.Should().Be(2L);
        node.DefaultOutputs["L"].Should().BeOfType<List<long>>().Which.Should().Equal(1L, 2L);
        node.DefaultOutputs["D"].Should().Be(new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero));
        node.DefaultOutputs["J"].Should().BeOfType<JsonElement>().Which.GetProperty("a").GetInt32().Should().Be(1);
        node.DefaultOutputs["N"].Should().BeNull();
    }

    [Theory]
    [InlineData("""{"V":1.5}""", "V")]
    [InlineData("""{"V":null}""", "V")]
    [InlineData("""{"L":[1,"x"]}""", "L")]
    [InlineData("""{"D":"not a date"}""", "D")]
    [InlineData("""{"J":null}""", "J")]
    public void A_default_output_that_is_not_a_literal_of_its_term_is_refused(string json, string output)
    {
        var result = WorkflowCompiler.Compile(DefaultsGraph(json), DefaultsCatalogue, CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(
            d => d.RuleId == CompilationRules.LiteralPortValueInvalid && d.NodeId == "src" && d.PortName == output);
    }

    [Fact]
    public void A_default_output_text_that_repeats_an_output_name_is_refused_at_the_save_naming_the_member()
    {
        var result = WorkflowCompiler.Compile(DefaultsGraph("""{"V":"x","V":1}"""), DefaultsCatalogue, CanvasSignature.Empty);

        var diagnostic = result.Diagnostics.Should().ContainSingle().Subject;
        (diagnostic.RuleId, diagnostic.NodeId).Should().Be((CompilationRules.LiteralPortValueInvalid, "src"));
        diagnostic.Message.Should().Contain("'V'").And.Contain("more than once");
    }

    [Theory]
    [InlineData("""{"V":""")]
    [InlineData("[1]")]
    [InlineData("3")]
    public void A_default_output_text_that_is_not_a_JSON_object_is_refused_at_the_save(string json)
    {
        var result = WorkflowCompiler.Compile(DefaultsGraph(json), DefaultsCatalogue, CanvasSignature.Empty);

        var diagnostic = result.Diagnostics.Should().ContainSingle().Subject;
        (diagnostic.RuleId, diagnostic.NodeId).Should().Be((CompilationRules.LiteralPortValueInvalid, "src"));
    }

    [Fact]
    public void A_blank_default_output_text_gives_no_outputs_and_no_diagnostic()
    {
        WorkflowCompiler.Compile(DefaultsGraph("  "), DefaultsCatalogue, CanvasSignature.Empty).Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_string_literal_of_a_host_term_lowers_to_a_string()
    {
        var result = CompileDepot(typeof(string), null, "north");

        result.Diagnostics.Should().BeEmpty();
        result.Program!.Canvases.Single().Nodes.Single(n => n.NodeId == "sink").Inputs["D"].Should().Be("north");
    }

    [Fact]
    public void A_Uuid_literal_of_a_Guid_host_term_lowers_to_the_Guid()
    {
        var id = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

        var result = CompileDepot(typeof(Guid), WorkflowLiteralFormat.Uuid, id.ToString());

        result.Diagnostics.Should().BeEmpty();
        result.Program!.Canvases.Single().Nodes.Single(n => n.NodeId == "sink").Inputs["D"].Should().Be(id);
    }

    [Fact]
    public void A_collection_and_a_nullable_of_a_host_term_lower_through_the_records_JsonTypeInfo()
    {
        var first = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
        var second = Guid.Parse("6ba7b810-9dad-41d1-80b4-00c04fd430c8");

        var many = CompileDepot(typeof(Guid), WorkflowLiteralFormat.Uuid, new[] { first.ToString(), second.ToString() }, "Collection<Depot>");
        var maybe = CompileDepot(typeof(Guid), WorkflowLiteralFormat.Uuid, first.ToString(), "Depot?");

        many.Diagnostics.Should().BeEmpty();
        many.Program!.Canvases.Single().Nodes.Single(n => n.NodeId == "sink").Inputs["D"].Should().BeAssignableTo<IEnumerable<Guid>>().Which.Should().Equal(first, second);
        maybe.Diagnostics.Should().BeEmpty();
        maybe.Program!.Canvases.Single().Nodes.Single(n => n.NodeId == "sink").Inputs["D"].Should().Be(first);
    }

    private static CompilationResult CompileDepot(Type clr, WorkflowLiteralFormat? format, object literal, string port = "Depot")
    {
        var options = new CompilationOptions
        {
            TypeRegistry = new WorkflowTypeRegistry([new WorkflowTypeRecord("Depot", clr)
            {
                Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String, format),
                JsonTypeInfo = StrictJson.Info(clr),
            }]),
        };
        var graph = Graph([StartNode(), Node("sink", "sink", new { D = literal })], [Edge("e1", "start", "sink")]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("sink", inputs: [("D", PortValueType.Of(port), true)]));

        return WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, options);
    }

    private static IBlockShapeSource DefaultsCatalogue => Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("src", outputs: [("V", PortValueType.Int), ("L", PortValueType.CollectionOf(PortValueType.Int)), ("D", PortValueType.Of("DateTime")),
            ("J", PortValueType.Of("Json")), ("N", PortValueType.Of("Int?"))]));

    private static WorkflowDefinition DefaultsGraph(string json)
    {
        var src = Node("src", "src");
        src.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault, json);
        return Graph([StartNode(), src], [Edge("e1", "start", "src")]);
    }

    private static void OnASmallStack(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        }, maxStackSize: 256 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }
}
