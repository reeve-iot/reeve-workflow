using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class MapValueTests
{
    private static readonly WorkflowTypeVocabulary Vocabulary = WorkflowTypeVocabulary.Builtin;

    private static ClosedPortType Closed(string term) => Vocabulary.ClosedTypeOf(PortTerm.Parse(term, PortTermGrammar.Document))!;

    private static KeyValuePair<string, object?> Entry(string key, object? value) => new(key, value);

    [Fact]
    public void A_map_type_knows_its_key_and_its_value_and_has_the_dictionary_clr_shape()
    {
        var type = Closed("Map<String,Collection<Int?>>?");

        type.ClrType.Should().Be(typeof(IReadOnlyDictionary<string, IReadOnlyList<long?>>));
        type.KeyType!.Term.ToString().Should().Be("String");
        type.ValueType!.Term.ToString().Should().Be("Collection<Int?>");
        type.ElementType.Should().BeNull();
        Closed("Collection<Int>").ValueType.Should().BeNull();
        Vocabulary.ClrTypeOf(PortTerm.Parse("Map<String,Int>", PortTermGrammar.Document)).Should().Be(typeof(IReadOnlyDictionary<string, long>));
    }

    [Fact]
    public void CreateMap_keeps_the_order_it_was_given_and_compares_keys_ordinally()
    {
        var map = Closed("Map<String,Int>").CreateMap([Entry("b", 2), Entry("X-A", 1), Entry("x-a", (short)3)]);

        var typed = map.Should().BeAssignableTo<IReadOnlyDictionary<string, long>>().Subject;
        typed.Keys.Should().Equal("b", "X-A", "x-a");
        typed.Values.Should().Equal(2L, 1L, 3L);
        typed["X-A"].Should().Be(1);
        typed["x-a"].Should().Be(3);
        typed.ContainsKey("x-A").Should().BeFalse();
        typed.TryGetValue("b", out var b).Should().BeTrue();
        b.Should().Be(2);
        ((Func<long>)(() => typed["missing"])).Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void CreateMap_refuses_a_repeated_key_whatever_its_value()
    {
        var build = () => Closed("Map<String,Int>").CreateMap([Entry("a", 1), Entry("b", 2), Entry("a", 1)]);

        build.Should().Throw<ArgumentException>().WithMessage("*'a'*more than once*");
        var differ = () => Closed("Map<String,String>").CreateMap([Entry("k", "x"), Entry("k", "y")]);
        differ.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateMap_refuses_a_value_off_the_value_term()
    {
        Closed("Map<String,Int>").Invoking(t => t.CreateMap([Entry("a", null)])).Should().Throw<ArgumentException>().WithMessage("*'a'*null*");
        Closed("Map<String,Int>").Invoking(t => t.CreateMap([Entry("a", "1")])).Should().Throw<ArgumentException>().WithMessage("*String*");
        Closed("Map<String,Number>").Invoking(t => t.CreateMap([Entry("a", double.NaN)])).Should().Throw<ArgumentException>().WithMessage("*non-finite*");
        Closed("Map<String,Int>").Invoking(t => t.CreateMap([Entry(null!, 1)])).Should().Throw<ArgumentException>();
        Closed("Map<String,Int?>").CreateMap([Entry("a", null)]).Should().BeAssignableTo<IReadOnlyDictionary<string, long?>>()
            .Which.Should().ContainKey("a").WhoseValue.Should().BeNull();
    }

    [Fact]
    public void A_map_is_built_only_by_a_map_type_and_a_list_only_by_a_collection_type()
    {
        Closed("Collection<Int>").Invoking(t => t.CreateMap([])).Should().Throw<InvalidOperationException>();
        Closed("Map<String,Int>").Invoking(t => t.CreateList([])).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_output_gate_admits_exactly_the_map_clr_shape()
    {
        var type = Closed("Map<String,Int>");

        ValueGate.Admit(type, new Dictionary<string, long> { ["a"] = 1 }, out _).Should().BeNull();
        ValueGate.Admit(type, type.CreateMap([Entry("a", 1)]), out _).Should().BeNull();
        ValueGate.Admit(type, new Dictionary<string, int> { ["a"] = 1 }, out _).Should().NotBeNull("the values are int, not long");
        ValueGate.Admit(type, new Dictionary<string, object?> { ["a"] = 1L }, out _).Should().NotBeNull();
        ValueGate.Admit(type, new List<long> { 1 }, out _).Should().NotBeNull("a list is not a map");
        ValueGate.Admit(type, new Dictionary<int, long> { [1] = 1 }, out _).Should().NotBeNull("the key is a string");
        ValueGate.Admit(type, null, out _).Should().NotBeNull();
        ValueGate.Admit(Closed("Map<String,Int>?"), null, out _).Should().BeNull();
        ValueGate.Admit(Closed("Collection<Int>"), new Dictionary<string, long>(), out _).Should().NotBeNull("a map is not a list");
    }

    [Fact]
    public void A_map_is_written_as_a_JSON_object_in_the_order_it_holds()
    {
        Closed("Map<String,Int>").ToJson(Closed("Map<String,Int>").CreateMap([Entry("b", 2), Entry("a", 1)]))!.Value.GetRawText()
            .Should().Be("""{"b":2,"a":1}""");
        Closed("Map<String,Int>").ToJson(new Dictionary<string, long>())!.Value.GetRawText().Should().Be("{}");
        Closed("Map<String,Int?>").ToJson(new Dictionary<string, long?> { ["n"] = null })!.Value.GetRawText().Should().Be("""{"n":null}""");
        Closed("Map<String,Collection<Int>>").ToJson(new Dictionary<string, IReadOnlyList<long>> { ["l"] = new List<long> { 1, 2 } })!.Value
            .GetRawText().Should().Be("""{"l":[1,2]}""");
        Closed("Collection<Map<String,Int>>").ToJson(new List<IReadOnlyDictionary<string, long>> { new Dictionary<string, long> { ["a"] = 1 } })!.Value
            .GetRawText().Should().Be("""[{"a":1}]""");
        Closed("Map<String,Map<String,Int>>").ToJson(new Dictionary<string, IReadOnlyDictionary<string, long>> { ["o"] = new Dictionary<string, long> { ["i"] = 1 } })!.Value
            .GetRawText().Should().Be("""{"o":{"i":1}}""");
    }

    [Fact]
    public void A_value_that_is_no_map_or_holds_a_null_it_may_not_is_not_written_as_a_map()
    {
        Closed("Map<String,Int>").Invoking(t => t.ToJson(new List<long> { 1 })).Should().Throw<ArgumentException>();
        Closed("Map<String,Int>").Invoking(t => t.ToJson("text")).Should().Throw<ArgumentException>();
        Closed("Map<String,String>").Invoking(t => t.ToJson(new Dictionary<string, string?> { ["a"] = null })).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Map<String,Int>", """{"b":2,"a":1}""")]
    [InlineData("Map<String,Int>", "{}")]
    [InlineData("Map<String,Number>", """{"a":1,"b":2.5}""")]
    [InlineData("Map<String,Int?>", """{"a":null,"b":3}""")]
    [InlineData("Map<String,Collection<String>>", """{"a":["x"],"b":[]}""")]
    [InlineData("Map<String,Map<String,Boolean>>", """{"a":{"t":true}}""")]
    [InlineData("Collection<Map<String,Int>>", """[{"a":1},{}]""")]
    [InlineData("Map<String,DateTime>", """{"at":"2026-10-14T09:30:00+02:00"}""")]
    public void A_map_survives_the_persisted_value_codec(string term, string json)
    {
        var type = Closed(term);
        using var document = JsonDocument.Parse(json);

        var read = type.FromJson(document.RootElement);

        type.ClrType.IsInstanceOfType(read).Should().BeTrue();
        using var writtenDocument = JsonDocument.Parse(WrittenBy(type, read));
        JsonSerializer.Serialize(writtenDocument.RootElement).Should().Be(JsonSerializer.Serialize(document.RootElement));
    }

    private static string WrittenBy(ClosedPortType type, object? value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            type.WriteTo(writer, value);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [Fact]
    public void A_map_read_back_keeps_the_order_of_the_JSON_and_two_keys_that_differ_in_case()
    {
        using var document = JsonDocument.Parse("""{"z":1,"X-A":2,"x-a":3}""");

        var read = (IReadOnlyDictionary<string, long>)Closed("Map<String,Int>").FromJson(document.RootElement)!;

        read.Keys.Should().Equal("z", "X-A", "x-a");
    }

    [Theory]
    [InlineData("Map<String,Int>", "[1]", "a JSON array")]
    [InlineData("Map<String,Int>", "5", "a JSON number")]
    [InlineData("Map<String,Int>", "null", "null")]
    [InlineData("Map<String,Int>", """{"a":"x"}""", "a member holding a JSON string")]
    [InlineData("Map<String,Int>", """{"a":null}""", "a member holding null")]
    [InlineData("Map<String,Int>", """{"a":1,"a":2}""", "a repeated member name")]
    [InlineData("Map<String,Collection<Int>>", """{"a":{"b":1}}""", "a member holding a JSON object")]
    [InlineData("Collection<Int>", """{"a":1}""", "a JSON object")]
    public void A_JSON_value_that_is_no_map_of_the_term_is_refused_naming_what_was_found(string term, string json, string reason)
    {
        using var document = JsonDocument.Parse(json);

        var read = () => Closed(term).FromJson(document.RootElement);

        read.Should().Throw<PersistedValueReadException>().Which.Reason.Should().Be(reason);
    }

    [Fact]
    public void A_nullable_map_reads_a_JSON_null_as_null()
    {
        using var document = JsonDocument.Parse("null");

        Closed("Map<String,Int>?").FromJson(document.RootElement).Should().BeNull();
    }

    [Fact]
    public void A_copy_of_a_map_is_a_new_instance_with_the_same_entries_at_every_level()
    {
        var type = Closed("Map<String,Collection<Map<String,Int>>>");
        var inner = new Dictionary<string, long> { ["k"] = 1 };
        var original = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, long>>> { ["b"] = new List<IReadOnlyDictionary<string, long>> { inner }, ["a"] = [] };

        var copy = (IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, long>>>)type.Copy(original)!;

        copy.Should().NotBeSameAs(original);
        copy.Keys.Should().Equal("b", "a");
        copy["b"].Should().NotBeSameAs(original["b"]);
        copy["b"][0].Should().NotBeSameAs(inner).And.Equal(inner);
        type.Copy(null).Should().BeNull();
        Closed("Map<String,Int>").Copy("not a map").Should().Be("not a map");
    }

    [Fact]
    public void A_map_compares_equal_to_another_with_the_same_entries_whatever_the_order()
    {
        var type = Closed("Map<String,Int>");
        var ab = type.CreateMap([Entry("a", 1), Entry("b", 2)]);
        var ba = type.CreateMap([Entry("b", 2), Entry("a", 1)]);

        TermValues.AreEqual(ab, ba).Should().BeTrue();
        TermValues.AreEqual(ab, new Dictionary<string, long> { ["b"] = 2, ["a"] = 1 }).Should().BeTrue();
        TermValues.AreEqual(ab, type.CreateMap([Entry("a", 1)])).Should().BeFalse("a count differs");
        TermValues.AreEqual(ab, type.CreateMap([Entry("a", 1), Entry("b", 3)])).Should().BeFalse("a value differs");
        TermValues.AreEqual(ab, type.CreateMap([Entry("a", 1), Entry("B", 2)])).Should().BeFalse("keys compare ordinally");
        TermValues.AreEqual(type.CreateMap([]), type.CreateMap([])).Should().BeTrue();
        TermValues.AreEqual(ab, null).Should().BeFalse();
        TermValues.AreEqual(
            new List<IReadOnlyDictionary<string, long>> { new Dictionary<string, long> { ["a"] = 1, ["b"] = 2 } },
            new List<IReadOnlyDictionary<string, long>> { new Dictionary<string, long> { ["b"] = 2, ["a"] = 1 } }).Should().BeTrue();
    }

    private static object? Converted(string from, string to, object? value)
    {
        var conversion = Vocabulary.ConversionOf(PortTerm.Parse(from, PortTermGrammar.Document), PortTerm.Parse(to, PortTermGrammar.Document))!;
        return conversion.Apply(value);
    }

    [Fact]
    public void The_binder_converts_a_map_value_by_value_and_delivers_a_dictionary_of_the_target_value_type()
    {
        var source = new Dictionary<string, long> { ["b"] = 2, ["a"] = 1 };

        var number = Converted("Map<String,Int>", "Map<String,Number>", source);
        var lifted = Converted("Map<String,Int>", "Map<String,Int?>", source);

        number.Should().BeAssignableTo<IReadOnlyDictionary<string, double>>().Which.Should().Equal(new Dictionary<string, double> { ["b"] = 2, ["a"] = 1 });
        ((IReadOnlyDictionary<string, double>)number!).Keys.Should().Equal("b", "a");
        lifted.Should().BeAssignableTo<IReadOnlyDictionary<string, long?>>().Which.Should().Equal(new Dictionary<string, long?> { ["b"] = 2, ["a"] = 1 });
    }

    [Fact]
    public void The_binder_converts_through_a_map_nested_in_a_collection_or_a_map()
    {
        var inCollection = Converted("Collection<Map<String,Int>>", "Collection<Map<String,Number>>",
            new List<IReadOnlyDictionary<string, long>> { new Dictionary<string, long> { ["a"] = 1 }, new Dictionary<string, long>() });
        var inMap = Converted("Map<String,Collection<Int>>", "Map<String,Collection<Number>>",
            new Dictionary<string, IReadOnlyList<long>> { ["l"] = new List<long> { 1, 2 } });
        var deep = Converted("Map<String,Map<String,Int>>", "Map<String,Map<String,Number>>",
            new Dictionary<string, IReadOnlyDictionary<string, long>> { ["o"] = new Dictionary<string, long> { ["i"] = 1 } });

        inCollection.Should().BeAssignableTo<IReadOnlyList<IReadOnlyDictionary<string, double>>>().Which.Select(m => m.Count).Should().Equal(1, 0);
        inMap.Should().BeAssignableTo<IReadOnlyDictionary<string, IReadOnlyList<double>>>().Which["l"].Should().Equal(1.0, 2.0);
        deep.Should().BeAssignableTo<IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>().Which["o"]["i"].Should().Be(1.0);
    }

    [Fact]
    public void The_binder_passes_a_map_whose_clr_type_does_not_change_and_a_null_map_as_they_are()
    {
        var source = new Dictionary<string, string> { ["a"] = "x" };

        Converted("Map<String,String>", "Map<String,String?>", source).Should().BeSameAs(source);
        Converted("Map<String,Int>", "Map<String,Number>", null).Should().BeNull();
        Converted("Map<String,Int>", "Map<String,Int>?", source).Should().BeSameAs(source);
    }

    [Fact]
    public void The_binder_refuses_a_value_that_is_no_map_where_a_map_is_converted()
    {
        var convert = () => Converted("Map<String,Int>", "Map<String,Number>", new List<long> { 1 });

        convert.Should().Throw<Exception>().Which.Message.Should().Contain("not a value of");
    }

    [Fact]
    public void A_map_feeds_a_Json_port_as_an_object()
    {
        var json = Converted("Map<String,Int>", "Json", new Dictionary<string, long> { ["b"] = 2, ["a"] = 1 });

        json.Should().BeOfType<JsonElement>().Which.GetRawText().Should().Be("""{"b":2,"a":1}""");
    }

    [Fact]
    public void A_map_answers_the_capabilities_of_its_value_through_the_public_api()
    {
        var term = PortTerm.Parse("Map<String,Int>", PortTermGrammar.Document);

        Vocabulary.Implements(term, WorkflowCapability.Equatable).Should().BeTrue();
        Vocabulary.Implements(term, WorkflowCapability.JsonSerializable).Should().BeTrue();
        Vocabulary.Implements(term, WorkflowCapability.Iterable).Should().BeFalse();
        Vocabulary.Implements(term, WorkflowCapability.Comparable).Should().BeFalse();
        Vocabulary.CapabilitiesOf(term).Should().Equal(WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable);
        Vocabulary.CapabilitiesOf(PortTerm.Parse("Map<String,Int>?", PortTermGrammar.Document)).Should().Equal(WorkflowCapability.JsonSerializable);
        Vocabulary.IterableItemOf(term).Should().BeNull("a map is not iterated; IterateOver refuses it");
        Vocabulary.IterableItemOf(PortTerm.Parse("Collection<Map<String,Int>>", PortTermGrammar.Document))!.ToString().Should().Be("Map<String,Int>");
    }

    [Fact]
    public void A_map_of_an_unregistered_value_is_not_answered_for()
    {
        var term = PortTerm.Parse("Map<String,Widget>", PortTermGrammar.Document);

        Vocabulary.RefusalOf(term)!.Code.Should().Be(CompilationRules.PortTypeUnregistered);
        Vocabulary.Invoking(v => v.Implements(term, WorkflowCapability.Equatable)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PortValueType_MapOf_declares_a_map_port()
    {
        var declared = PortValueType.MapOf(PortValueType.Int);

        declared.Name.Should().Be("Map<String,Int>");
        declared.IsMap.Should().BeTrue();
        declared.IsCollection.Should().BeFalse();
        PortValueType.MapOf(PortValueType.CollectionOf(PortValueType.String)).Name.Should().Be("Map<String,Collection<String>>");
        PortValueType.MapOf(PortValueType.Of("Int?")).Name.Should().Be("Map<String,Int?>");
        PortValueType.MapOf(declared).Name.Should().Be("Map<String,Map<String,Int>>");
        PortValueType.Of("Map<String,Int>?").IsMap.Should().BeFalse();
        PortValueType.Int.IsMap.Should().BeFalse();
        default(PortValueType).IsMap.Should().BeFalse();
        ((Action)(() => PortValueType.MapOf(default))).Should().Throw<ArgumentException>();
    }

    private static CompiledNode LoweredNode(CompilationResult result, string id) =>
        result.Program!.Canvases.SelectMany(c => c.Nodes).Single(n => n.NodeId == id);

    private static string[] Rules(IEnumerable<CompilationDiagnostic> diagnostics) => [.. diagnostics.Select(d => d.RuleId)];

    private static CompilationResult CompileLiteralOnto(string type, object? literal)
    {
        var graph = Graph([StartNode(), Node("a", "gadget")], [LiteralEdge("e1", "start", "a", "In", literal)]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("gadget", inputs: [("In", PortValueType.OfDeclaration(type), false)]));
        return WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);
    }

    [Theory]
    [InlineData("Map<String,Int>", """{"a":1,"b":2}""", true)]
    [InlineData("Map<String,Int>", "{}", true)]
    [InlineData("Map<String,Int>", """{"a":"x"}""", false)]
    [InlineData("Map<String,Int>", """{"a":null}""", false)]
    [InlineData("Map<String,Int?>", """{"a":null}""", true)]
    [InlineData("Map<String,Int>", "[1]", false)]
    [InlineData("Map<String,Int>", "5", false)]
    [InlineData("Map<String,Number>", """{"X-A":1,"x-a":2.5}""", true)]
    [InlineData("Map<String,Collection<Int>>", """{"a":[1,2]}""", true)]
    [InlineData("Map<String,Collection<Int>>", """{"a":[1,"x"]}""", false)]
    [InlineData("Collection<Int>", """{"a":1}""", false)]
    [InlineData("Int", """{"a":1}""", false)]
    public void An_object_literal_on_a_map_input_is_graded_per_member(string type, string literal, bool accepted)
    {
        var result = CompileLiteralOnto(type, JsonSerializer.Deserialize<JsonElement>(literal));

        Rules(result.Diagnostics).Should().Equal(accepted ? [] : [CompilationRules.LiteralPortValueInvalid]);
    }

    [Fact]
    public void A_map_literal_is_lowered_into_a_map_in_the_order_it_was_written()
    {
        var result = CompileLiteralOnto("Map<String,Number>", JsonSerializer.Deserialize<JsonElement>("""{"b":1,"X-A":2.5,"x-a":3}"""));

        var literal = LoweredNode(result, "a").Incoming.Single().Literals["In"];

        literal.Should().BeAssignableTo<IReadOnlyDictionary<string, double>>().Which.Should().Equal(
            new Dictionary<string, double> { ["b"] = 1, ["X-A"] = 2.5, ["x-a"] = 3 });
        ((IReadOnlyDictionary<string, double>)literal!).Keys.Should().Equal("b", "X-A", "x-a");
    }

    [Fact]
    public void A_map_port_compiles_as_a_root_output_and_a_wire_between_maps_is_assignable()
    {
        var graph = Graph(
            [StartNode(), Node("s", "src"), Node("t", "gadget")],
            [Edge("e1", "start", "s"), Edge("e2", "s", "t", ("Out", "In"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("src", outputs: [("Out", PortValueType.MapOf(PortValueType.Int))]),
            Shape("gadget", inputs: [("In", PortValueType.MapOf(PortValueType.Of("Number?")), false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        Rules(result.Diagnostics).Should().BeEmpty();
        LoweredNode(result, "t").Incoming.Single(e => e.SourceNodeId == "s").Conversions["In"].ToString().Should().Be("Elementwise(NullLift(IntToNumber))");
    }

    private static BlockShape Pick => Shape(
        "pick",
        inputs: [("In", PortValueType.MapOf(PortValueType.OfDeclaration("$T")), true)],
        outputs: [("Out", PortValueType.MapOf(PortValueType.OfDeclaration("$T?")))],
        typeParameters: [new WorkflowTypeParameter("T")]);

    [Fact]
    public void A_block_declaring_a_map_of_a_type_parameter_binds_it_from_a_map_wire()
    {
        var graph = Graph(
            [StartNode(), Node("s", "src"), Node("p", "pick")],
            [Edge("e1", "start", "s"), Edge("e2", "s", "p", ("Out", "In"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Pick, Shape("src", outputs: [("Out", PortValueType.MapOf(PortValueType.Int))]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        Rules(result.Diagnostics).Should().BeEmpty();
        var node = LoweredNode(result, "p");
        node.TypeArguments["T"].Term.ToString().Should().Be("Int");
        node.InputTypes["In"].Term.ToString().Should().Be("Map<String,Int>");
        node.OutputTypes["Out"].Term.ToString().Should().Be("Map<String,Int?>");
    }

    [Fact]
    public void A_block_declaring_a_map_of_a_type_parameter_binds_it_from_an_object_literal()
    {
        var graph = Graph([StartNode(), Node("p", "pick")], [LiteralEdge("e1", "start", "p", "In", JsonSerializer.Deserialize<JsonElement>("""{"a":1,"b":2}"""))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Pick), CanvasSignature.Empty);

        Rules(result.Diagnostics).Should().BeEmpty();
        LoweredNode(result, "p").TypeArguments["T"].Term.ToString().Should().Be("Int");
    }

    [Fact]
    public void An_object_literal_on_a_bare_type_parameter_binds_nothing_and_is_refused()
    {
        var bare = Shape("bare", inputs: [("In", PortValueType.OfDeclaration("$T"), true)], typeParameters: [new WorkflowTypeParameter("T")]);
        var graph = Graph([StartNode(), Node("b", "bare")], [LiteralEdge("e1", "start", "b", "In", JsonSerializer.Deserialize<JsonElement>("""{"a":1}"""))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), bare), CanvasSignature.Empty);

        Rules(result.Diagnostics).Should().Contain(CompilationRules.LiteralPortValueInvalid);
    }

    [Fact]
    public void Nullable_and_collection_wrappers_over_a_type_parameter_build_the_name_the_declaration_grammar_reads()
    {
        var parameter = PortValueType.OfDeclaration("$T");

        PortValueType.CollectionOf(parameter).Name.Should().Be("Collection<$T>");
        PortValueType.MapOf(parameter).Name.Should().Be("Map<String,$T>");
        PortValueType.MapOf(PortValueType.CollectionOf(parameter)).Name.Should().Be("Map<String,Collection<$T>>");
        PortValueType.CollectionOf(PortValueType.MapOf(PortValueType.OfDeclaration("$T?"))).Name.Should().Be("Collection<Map<String,$T?>>");
        PortValueType.MapOf(parameter).IsMap.Should().BeTrue();
    }

    [Fact]
    public void The_variables_snapshot_writes_a_map_as_an_object_with_sorted_keys_wherever_it_nests()
    {
        var map = Closed("Map<String,Int>").CreateMap([Entry("b", 2L), Entry("a", 1L)]);
        var nested = Closed("Collection<Map<String,Int>>").CreateList([map]);

        var json = WorkflowVariablesEncoder.Encode(new Dictionary<string, object?> { ["m"] = map, ["l"] = nested });

        json.Should().Be("""{"l":[{"a":1,"b":2}],"m":{"a":1,"b":2}}""");
    }

    [Fact]
    public void A_map_does_not_feed_a_collection_input_so_IterateOver_over_a_map_is_refused()
    {
        var graph = Graph([StartNode(), Node("s", "src"), Node("loop", "box")], [Edge("e1", "start", "s"), Edge("e2", "s", "loop", ("Out", "Collection"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("src", outputs: [("Out", PortValueType.MapOf(PortValueType.Int))]),
            GenericShapes.Box);

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        Rules(result.Diagnostics).Should().Contain(CompilationRules.PortTypeMismatch);
    }
}
