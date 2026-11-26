using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class PolymorphicPortTests
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
        SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["Body"] = [new PortShape("Result", Declared("$R"), false)],
        },
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

    private static readonly PortValueType Room = PortValueType.Of("Room");

    private static WorkflowNodeDefinition Loop(WorkflowDefinition body)
    {
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        return loop;
    }

    private static WorkflowDefinition OverRooms(WorkflowNodeDefinition loop) => Graph(
        [StartNode(), Node("src", "rooms"), loop],
        [Edge("e1", "start", "src"), Edge("e2", "src", loop.Id, ("Rooms", "Collection"))]);

    private static BlockShape Rooms => Shape("rooms", outputs: [("Rooms", PortValueType.CollectionOf(Room))]);

    [Fact]
    public void The_item_of_a_loop_over_a_collection_of_rooms_is_a_room()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "notify")],
            [Edge("be1", "in", "n1", ("Item", "Room"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Rooms, ForEach,
            Shape("notify", inputs: [("Room", Room, false)]));

        var context = WorkflowCompiler.Run(OverRooms(Loop(body)), catalogue, CanvasSignature.Empty, HostTypes("Room"));

        context.Diagnostics.Should().BeEmpty();
        context.Signatures[CanvasPath.Root.Child("loop", "Body")].Inputs.Should()
            .ContainSingle(p => p.Name == "Item").Which.Type.Should().Be(Room);
    }

    [Fact]
    public void Results_is_a_collection_of_whatever_the_body_produces_as_result()
    {
        var body = Graph(
            [Node("src", "room-source"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("be1", "src", "out", ("Room", "Result"))]);
        var graph = OverRooms(Loop(body));
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            Rooms, Shape("room-source", outputs: [("Room", Room)]), ForEach);

        var context = WorkflowCompiler.Run(graph, catalogue, CanvasSignature.Empty, HostTypes("Room"));

        context.Diagnostics.Should().BeEmpty();
        Terms(context.Types[new NodeKey(CanvasPath.Root, "loop")].Outputs).Should().Contain("Results", "Collection<Room>");
    }

    [Fact]
    public void A_loop_with_nothing_wired_into_its_collection_is_refused_once_not_twice()
    {
        var graph = Graph([StartNode(), Loop(Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId)]))], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "loop" && d.PortName == "Collection");
    }

    [Fact]
    public void Filtered_is_the_collection_that_feeds_it()
    {
        var graph = Graph(
            [StartNode(), Node("src", "rooms"), Node("f", "filter")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "f", ("Rooms", "Collection"))]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Rooms, Filter);

        var context = WorkflowCompiler.Run(graph, catalogue, CanvasSignature.Empty, HostTypes("Room"));

        context.Diagnostics.Should().BeEmpty();
        Terms(context.Types[new NodeKey(CanvasPath.Root, "f")].Outputs).Should().Contain("Filtered", "Collection<Room>");
    }

    [Fact]
    public void A_loop_whose_body_never_sets_result_compiles_with_Results_unbound()
    {
        var graph = OverRooms(Loop(Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId)])));
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Rooms, ForEach);

        var context = WorkflowCompiler.Run(graph, catalogue, CanvasSignature.Empty, HostTypes("Room"));

        context.Diagnostics.Should().BeEmpty();
        Terms(context.Types[new NodeKey(CanvasPath.Root, "loop")].Outputs).Should().Contain("Results", "unbound");
        WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Room")).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_wire_from_an_unbound_Results_is_refused_by_its_reader()
    {
        var graph = Graph(
            [StartNode(), Node("src", "rooms"), Loop(Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId)])), Node("sink", "sink")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "loop", ("Rooms", "Collection")), Edge("e3", "loop", "sink", ("Results", "In"))]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Rooms, ForEach,
            Shape("sink", inputs: [("In", PortValueType.CollectionOf(Room), false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Room"));

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.TypeParameterUnbound && d.NodeId == "sink" && d.PortName == "In");
    }

    [Fact]
    public void A_blank_declared_output_type_is_malformed_even_when_its_source_publishes_a_real_type()
    {
        var then = Graph(
            [Node("calc", "compute"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("be1", "calc", "out", ("Value", "raw"))]);
        var branch = Node("branch", "container");
        branch.SubCanvases = new() { ["Then"] = then, ["Else"] = Graph([]) };
        branch.OutputPorts = [new SubCanvasOutputPortDefinition
        {
            Name = "setpoint", Type = "", Sources = new() { ["Then"] = "raw" },
        }];
        var graph = Graph([StartNode(), branch], [Edge("e1", "start", "branch")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            Container(),
            Shape("compute", outputs: [("Value", Room)]));

        var context = WorkflowCompiler.Run(graph, catalogue, CanvasSignature.Empty, HostTypes("Room"));

        var malformed = context.Diagnostics.Should().ContainSingle().Which;
        malformed.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
        malformed.NodeId.Should().Be("branch");
        malformed.PortName.Should().Be("setpoint");
        context.Types[new NodeKey(CanvasPath.Root, "branch")].Outputs.Should().NotContain(p => p.Name == "setpoint" && p.State == PortState.Typed);
    }

    [Fact]
    public void A_blank_declared_output_whose_source_never_answers_is_declined_not_minted_Any()
    {
        var branch = Node("branch", "container");
        branch.SubCanvases = new()
        {
            ["Then"] = Graph([Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)]),
            ["Else"] = Graph([]),
        };
        branch.OutputPorts = [new SubCanvasOutputPortDefinition
        {
            Name = "setpoint", Type = "", Sources = new() { ["Then"] = "raw" },
        }];
        var graph = Graph([StartNode(), branch], [Edge("e1", "start", "branch")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId), Container());

        var context = WorkflowCompiler.Run(graph, catalogue, CanvasSignature.Empty);

        context.Types[new NodeKey(CanvasPath.Root, "branch")].Outputs.Should().NotContain(p => p.Name == "setpoint" && p.State == PortState.Typed);
    }

    private static BlockShape Container() => Shape("container", supportsDeclaredOutputs: true,
        subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [], ["Else"] = [] },
        subCanvasNames: ["Then", "Else"]);

    [Fact]
    public void The_stored_branch_signature_and_the_owners_outputs_agree_on_the_same_wire()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("be1", "in", "out", ("Item", "Result"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId), Rooms, ForEach);

        var context = WorkflowCompiler.Run(OverRooms(Loop(body)), catalogue, CanvasSignature.Empty, HostTypes("Room"));

        context.Diagnostics.Should().BeEmpty();
        var signature = context.Signatures[CanvasPath.Root.Child("loop", "Body")];
        signature.Inputs.Should().ContainSingle(p => p.Name == "Item").Which.Type.Should().Be(Room);
        signature.Outputs.Should().ContainSingle(p => p.Name == "Result").Which.Type.Should().Be(Room);
        Terms(context.Types[new NodeKey(CanvasPath.Root, "loop")].Outputs).Should().Contain("Results", "Collection<Room>");
    }

    [Fact]
    public void A_loop_over_a_list_literal_of_one_shared_type_binds_Item_precisely()
    {
        var graph = Graph([StartNode(), Loop(Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId)]))],
            [LiteralEdge("e1", "start", "loop", "Collection", new List<object?> { 1L, 2L, 3L })]);

        var context = WorkflowCompiler.Run(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach), CanvasSignature.Empty);

        context.Diagnostics.Should().BeEmpty();
        context.Signatures[CanvasPath.Root.Child("loop", "Body")].Inputs.Should()
            .ContainSingle(p => p.Name == "Item").Which.Type.Should().Be(PortValueType.Int);
    }

    [Fact]
    public void A_loop_over_a_mixed_list_literal_is_a_type_parameter_conflict()
    {
        var graph = Graph([StartNode(), Loop(Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId)]))],
            [LiteralEdge("e1", "start", "loop", "Collection", new List<object?> { 1L, "two" })]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.TypeParameterConflict && d.NodeId == "loop");
    }

    [Fact]
    public void A_loop_over_a_source_declaring_a_collection_of_Any_is_refused_at_the_source()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "notify")],
            [Edge("be1", "in", "n1", ("Item", "Room"))]);
        var graph = Graph(
            [StartNode(), Node("src", "bare-source"), Loop(body)],
            [Edge("e1", "start", "src"), Edge("e2", "src", "loop", ("Items", "Collection"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bare-source", outputs: [("Items", PortValueType.Of("Collection<Any>"))]),
            ForEach,
            Shape("notify", inputs: [("Room", Room, false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Room"));

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.PortTypeAlias && d.NodeId == "src" && d.PortName == "Items");
        result.Program.Should().BeNull();
    }

    [Fact]
    public void A_node_wired_output_to_own_input_refuses_as_a_cycle_not_a_crash()
    {
        var graph = Graph(
            [StartNode(), Node("f", "filter")],
            [Edge("e1", "start", "f"), Edge("e2", "f", "f", ("Filtered", "Collection"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Filter), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.Cycle);
    }
}
