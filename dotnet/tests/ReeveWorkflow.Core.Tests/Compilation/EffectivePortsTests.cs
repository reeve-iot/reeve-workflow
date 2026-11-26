using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class EffectivePortsTests
{
    [Fact]
    public void The_passthrough_the_editor_writes_compiles_on_both_ends()
    {
        var n2 = Node("n2", "notify", passthrough: ["Room"]);
        var graph = Graph(
            [StartNode(), Node("src", "rooms"), n2, Node("n3", "log")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "n2", ("Room", "Room")),
             Edge("e3", "n2", "n3", ("Room", "Room"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("rooms", outputs: [("Room", PortValueType.Of("Room"))]),
            Shape("notify", outputs: [("Sent", PortValueType.Boolean)]),
            Shape("log", inputs: [("Room", PortValueType.Of("Room"), true)])), CanvasSignature.Empty, HostTypes("Room"));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_passthrough_name_nothing_could_ever_feed_compiles_to_no_port_on_either_end()
    {
        var n2 = Node("n2", "notify", passthrough: ["Untracked"]);
        var graph = Graph(
            [StartNode(), n2, Node("n3", "log")],
            [Edge("e1", "start", "n2"), Edge("e2", "n2", "n3", ("Untracked", "Untracked"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", outputs: [("Sent", PortValueType.Boolean)]),
            Shape("log", inputs: [("Untracked", PortValueType.String, false)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.WirePortUnknown && d.NodeId == "n2" && d.PortName == "Untracked");
    }

    [Fact]
    public void A_passthrough_of_an_already_declared_input_keeps_its_declared_type()
    {
        var node = Node("n1", "notify", passthrough: ["Message"]);
        var shape = Shape("notify", inputs: [("Message", PortValueType.String, true)]);

        var outputs = Terms(TypesOf(Graph([node]), Catalogue(shape), CanvasSignature.Empty, node.Id).Outputs);

        outputs.Should().Contain("Message", "String");
    }

    [Fact]
    public void A_passthrough_of_an_already_declared_dynamic_input_keeps_its_declared_type()
    {
        var node = Node("n1", "dyn.block",
            new { InputPorts = """[{"name":"Target","type":"Room"}]""" }, passthrough: ["Target"]);
        var shape = Shape("dyn.block", hasDynamicPorts: true);

        var outputs = Terms(TypesOf(Graph([node]), Catalogue(shape), CanvasSignature.Empty, node.Id, HostTypes("Room")).Outputs);

        outputs.Should().Contain("Target", "Room");
    }

    [Fact]
    public void A_passthrough_fed_by_a_wire_takes_the_feeding_wires_source_term()
    {
        var n2 = Node("n2", "notify", passthrough: ["Room"]);
        var graph = Graph([Node("src", "rooms"), n2], [Edge("e1", "src", "n2", ("Room", "Room"))]);
        var shapes = Catalogue(
            Shape("rooms", outputs: [("Room", PortValueType.Of("Room"))]),
            Shape("notify", outputs: [("Sent", PortValueType.Boolean)]));

        var outputs = Terms(TypesOf(graph, shapes, CanvasSignature.Empty, n2.Id, HostTypes("Room")).Outputs);

        outputs.Should().Contain("Room", "Room");
    }

    [Fact]
    public void Two_edges_below_the_root_feeding_the_same_name_take_the_last()
    {
        var n2 = Node("n2", "notify", passthrough: ["Room"]);
        var body = Graph(
            [Node("first", "textRooms"), Node("second", "typedRooms"), n2],
            [Edge("e1", "first", "n2", ("Room", "Room")), Edge("e2", "second", "n2", ("Room", "Room"))]);
        var loop = Node("loop", "loop");
        loop.SubCanvases = new() { ["body"] = body };
        var shapes = Catalogue(
            Shape("loop", subCanvasNames: ["body"], onlyDrivesItsSubCanvases: true),
            Shape("textRooms", outputs: [("Room", PortValueType.String)]),
            Shape("typedRooms", outputs: [("Room", PortValueType.Of("Room"))]),
            Shape("notify", outputs: [("Sent", PortValueType.Boolean)]));

        var outputs = Terms(TypesOf(Graph([loop]), shapes, CanvasSignature.Empty, n2.Id, HostTypes("Room"),
            path: CanvasPath.Root.Child("loop", "body")).Outputs);

        outputs.Should().Contain("Room", "Room");
    }

    [Theory]
    [MemberData(nameof(TypedLiterals))]
    public void A_passthrough_fed_by_a_literal_takes_the_literals_term(object literal, string term)
    {
        var n1 = Node("n1", "notify", passthrough: ["Items"]);
        var graph = Graph([StartNode(), n1], [LiteralEdge("e1", "start", "n1", "Items", literal)]);
        var shapes = Catalogue(Shape("notify", outputs: [("Sent", PortValueType.Boolean)]), Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId));

        var outputs = Terms(TypesOf(graph, shapes, CanvasSignature.Empty, n1.Id).Outputs);

        outputs.Should().Contain("Items", term);
    }

    public static TheoryData<object, string> TypedLiterals() => new()
    {
        { 3, "Int" },
        { 2.5, "Number" },
        { "x", "String" },
        { true, "Boolean" },
        { new List<object?> { 1, 2 }, "Collection<Int>" },
        { new List<object?> { 1, 2.5 }, "Collection<Number>" },
        { new List<object?> { 1, null }, "Collection<Int?>" },
    };

    [Theory]
    [MemberData(nameof(UntypedLiterals))]
    public void A_passthrough_fed_by_a_literal_without_a_term_is_refused_by_name(object? literal)
    {
        var n1 = Node("n1", "notify", passthrough: ["Items"]);
        var graph = Graph([StartNode(), n1], [LiteralEdge("e1", "start", "n1", "Items", literal)]);

        AssertRefusedByName(graph);
    }

    [Fact]
    public void A_passthrough_fed_by_the_null_literal_is_refused_by_name()
    {
        var n1 = Node("n1", "notify", passthrough: ["Items"]);
        var graph = Graph([StartNode(), n1], [LiteralEdgeWithoutValue("e1", "start", "n1", "Items")]);

        AssertRefusedByName(graph);
    }

    private static void AssertRefusedByName(WorkflowDefinition graph)
    {
        var shapes = Catalogue(Shape("notify", outputs: [("Sent", PortValueType.Boolean)]), Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId));

        var context = WorkflowCompiler.Run(graph, shapes, CanvasSignature.Empty);

        Terms(context.Types[new NodeKey(CanvasPath.Root, "n1")].Outputs).Should().Contain("Items", "refused");
        context.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeUnresolved)
            .Which.Should().Match<CompilationDiagnostic>(d => d.NodeId == "n1" && d.PortName == "Items");
    }

    public static TheoryData<object?> UntypedLiterals() => new()
    {
        new Dictionary<string, object?> { ["retries"] = 3 },
        new List<object?>(),
        new List<object?> { 1, "two" },
    };

    [Fact]
    public void A_passthrough_fed_only_by_a_configuration_value_takes_the_values_term()
    {
        var node = Node("n1", "notify", new { ApiKey = 3 }, passthrough: ["ApiKey"]);
        var shape = Shape("notify", outputs: [("Sent", PortValueType.Boolean)], configFields: [("ApiKey", ConfigFieldType.Number, false)]);

        var outputs = Terms(TypesOf(Graph([node]), Catalogue(shape), CanvasSignature.Empty, node.Id).Outputs);

        outputs.Should().Contain("ApiKey", "Int");
    }

    [Theory]
    [MemberData(nameof(UntypedLiterals))]
    public void A_passthrough_fed_only_by_a_configuration_value_without_a_term_is_refused_by_name(object? value)
    {
        var n1 = Node("n1", "notify", new Dictionary<string, object?> { ["Items"] = value }, passthrough: ["Items"]);
        var graph = Graph([StartNode(), n1], [Edge("e1", "start", "n1")]);

        AssertRefusedByName(graph);
    }

    [Fact]
    public void A_passthrough_name_the_block_already_statically_outputs_is_not_duplicated_or_retyped()
    {
        var node = Node("n1", "notify", passthrough: ["Sent"]);
        var shape = Shape("notify",
            inputs: [("Sent", PortValueType.String, false)], outputs: [("Sent", PortValueType.Boolean)]);

        var outputs = TypesOf(Graph([node]), Catalogue(shape), CanvasSignature.Empty, node.Id).Outputs!;

        outputs.Should().ContainSingle(p => p.Name == "Sent").Which.Term!.ToString().Should().Be("Boolean");
    }

    [Fact]
    public void A_passthrough_name_colliding_with_a_nodes_own_declared_output_is_not_duplicated_or_retyped()
    {
        var node = Node("n1", "notify", passthrough: ["Room"]);
        node.OutputPorts = [new() { Name = "Room", Type = "Room" }];
        var shape = Shape("notify", outputs: [("Sent", PortValueType.Boolean)], supportsDeclaredOutputs: true);

        var outputs = TypesOf(Graph([node]), Catalogue(shape), CanvasSignature.Empty, node.Id, HostTypes("Room")).Outputs!;

        outputs.Should().ContainSingle(p => p.Name == "Room").Which.Term!.ToString().Should().Be("Room");
    }

    [Fact]
    public void A_name_repeated_inside_passthrough_is_not_added_twice()
    {
        var node = Node("n1", "notify", passthrough: ["Message", "Message"]);
        var shape = Shape("notify", inputs: [("Message", PortValueType.String, true)]);

        var outputs = TypesOf(Graph([node]), Catalogue(shape), CanvasSignature.Empty, node.Id).Outputs!;

        outputs.Should().ContainSingle(p => p.Name == "Message");
    }

    [Fact]
    public void A_mutual_passthrough_cycle_terminates_with_no_type_on_either_side()
    {
        var a = Node("a", "notify", passthrough: ["X"]);
        var b = Node("b", "notify", passthrough: ["X"]);
        var graph = Graph([a, b], [Edge("e1", "a", "b", ("X", "X")), Edge("e2", "b", "a", ("X", "X"))]);

        var context = WorkflowCompiler.Run(graph, Catalogue(Shape("notify", outputs: [("Sent", PortValueType.Boolean)])), CanvasSignature.Empty);

        context.Types.Should().NotContainKey(new NodeKey(CanvasPath.Root, "a"));
        context.Types.Should().NotContainKey(new NodeKey(CanvasPath.Root, "b"));
    }
}
