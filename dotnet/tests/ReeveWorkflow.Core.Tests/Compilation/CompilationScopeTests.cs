using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CompilationScopeTests
{
    private static BlockShape LoopShapeDeclaringBody() =>
        Shape("loop.for",
            subCanvasInputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]> { ["Body"] = [] },
            subCanvasOutputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]> { ["Body"] = [] },
            subCanvasNames: ["Body"]);

    [Fact]
    public void A_node_sees_what_ran_before_it_on_its_own_canvas()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"), Node("b", "write")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b")]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("write")), CanvasSignature.Empty);

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("b", "start", out _).Should().Be(ScopeVisibility.Known);
        scope.TryResolve("b", "a", out var aOutputs).Should().Be(ScopeVisibility.Known);
        aOutputs.Should().ContainSingle(p => p.Name == "Temperature");
    }

    [Fact]
    public void A_node_does_not_see_the_node_that_comes_after_it()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"), Node("b", "write")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b")]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("read"), Shape("write")), CanvasSignature.Empty);

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("a", "b", out _).Should().Be(ScopeVisibility.NotInScope);
    }

    [Fact]
    public void A_node_does_not_see_itself()
    {
        var graph = Graph([StartNode(), Node("a", "read")], [Edge("e1", "start", "a")]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)])), CanvasSignature.Empty);

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("a", "a", out _).Should().Be(ScopeVisibility.NotInScope);
    }

    [Fact]
    public void A_node_can_see_its_frames_input_node_even_when_the_walk_places_it_later()
    {
        var body = Graph(
            [Node("isolated", "noop"), Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("inner", "write")],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var context = WorkflowCompiler.Run(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), LoopShapeDeclaringBody(),
            Shape("noop"), Shape("write")), CanvasSignature.Empty);
        var childKey = RootFrame(graph).Child("loop", "Body", body).Path;

        context.Order[childKey].Should().ContainInOrder("isolated", "bstart");

        var scope = context.Scopes[childKey];
        scope.TryResolve("isolated", "bstart", out _).Should().Be(ScopeVisibility.Known);
        scope.TryResolve("isolated", "inner", out _).Should().Be(ScopeVisibility.NotInScope);
    }

    [Fact]
    public void Every_frame_input_node_is_visible_not_only_the_first()
    {
        var body = Graph(
            [Node("isolated", "noop"),
             Node("in1", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("in2", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "write")],
            [Edge("be1", "in1", "inner")]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var context = WorkflowCompiler.Run(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), LoopShapeDeclaringBody(),
            Shape("noop"), Shape("write")), CanvasSignature.Empty);
        var childKey = RootFrame(graph).Child("loop", "Body", body).Path;

        context.Order[childKey][0].Should().Be("isolated");

        var scope = context.Scopes[childKey];
        scope.TryResolve("isolated", "in1", out _).Should().Be(ScopeVisibility.Known);
        scope.TryResolve("isolated", "in2", out _).Should().Be(ScopeVisibility.Known);
    }

    [Fact]
    public void A_frame_input_node_does_not_see_itself()
    {
        var graph = Graph([StartNode(), Node("a", "read")], [Edge("e1", "start", "a")]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("read")), CanvasSignature.Empty);

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("start", "start", out _).Should().Be(ScopeVisibility.NotInScope);
    }

    [Fact]
    public void TryResolve_refuses_a_referencing_node_id_this_frames_own_walk_never_reached()
    {
        var graph = Graph([StartNode(), Node("a", "read")], [Edge("e1", "start", "a")]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("read")), CanvasSignature.Empty);
        var scope = Build(frame, context.Order[frame.Path], context);

        var act = () => scope.TryResolve("no-such-node", "start", out _);

        act.Should().Throw<ArgumentException>().WithMessage("*no-such-node*frame's own canvas walk*");
    }

    [Fact]
    public void A_node_in_a_sub_canvas_cannot_see_a_node_of_its_parent()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("inner", "write")],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };

        var graph = Graph(
            [StartNode(), Node("parentNode", "read"), loopNode],
            [Edge("e1", "start", "parentNode"), Edge("e2", "parentNode", "loop")]);

        var rootFrame = RootFrame(graph);
        var childFrame = rootFrame.Child("loop", "Body", body);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("loop.for"),
            Shape("write"));

        var context = ContextFor(rootFrame, catalogue, CanvasSignature.Empty);
        context.Order[childFrame.Path] = WorkflowGraphHelpers.TopologicalSort(body);
        context.Signatures[childFrame.Path] = CanvasSignature.Empty;
        foreach (var node in body.Nodes)
        {
            if (catalogue.Find(node.BlockTypeId) is { } shape)
                context.Bound[new NodeKey(childFrame.Path, node.Id)] = shape;
        }

        context.Scopes[rootFrame.Path] = Build(rootFrame, context.Order[rootFrame.Path], context);

        var scope = Build(childFrame, context.Order[childFrame.Path], context);

        scope.TryResolve("inner", "bstart", out _).Should().Be(ScopeVisibility.Known);
        scope.TryResolve("inner", "parentNode", out _).Should().Be(ScopeVisibility.NotInScope);
        scope.TryResolve("inner", "loop", out _).Should().Be(ScopeVisibility.NotInScope);
    }

    [Fact]
    public void A_node_whose_block_never_bound_keeps_its_place_in_the_walk_but_is_unresolved()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"), Node("b", "gone.block"), Node("c", "write")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b"), Edge("e3", "b", "c")]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("write")), CanvasSignature.Empty);

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("b", "start", out _).Should().Be(ScopeVisibility.Known);
        scope.TryResolve("b", "a", out _).Should().Be(ScopeVisibility.Known);
        scope.TryResolve("b", "c", out _).Should().Be(ScopeVisibility.NotInScope);
        scope.TryResolve("c", "b", out _).Should().Be(ScopeVisibility.Unresolved);
    }

    [Fact]
    public void A_bound_node_that_declares_no_outputs_is_a_known_empty_publisher_not_an_unresolved_one()
    {
        var graph = Graph(
            [StartNode(), Node("b", "sink"), Node("c", "write")],
            [Edge("e1", "start", "b"), Edge("e2", "b", "c")]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("sink"), Shape("write")), CanvasSignature.Empty);

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("c", "b", out var outputs).Should().Be(ScopeVisibility.Known);
        outputs.Should().BeEmpty();
    }

    [Fact]
    public void A_resolved_elsewhere_node_with_no_trustworthy_answer_is_unresolved_not_known_empty()
    {
        var graph = Graph(
            [StartNode(), Node("sub", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = "missing" }), Node("sink", "notify")],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "sink", ("Result", "Message"))]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true,
                runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            CanvasSignature.Empty);
        context.PortsUnresolved.Add(new NodeKey(frame.Path, "sub"));

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("sink", "sub", out _).Should().Be(ScopeVisibility.Unresolved);
    }

    [Fact]
    public void A_frame_input_nodes_visible_outputs_come_from_the_signature_not_the_block_shape()
    {
        var graph = Graph([StartNode(), Node("a", "read")], [Edge("e1", "start", "a")]);
        var frame = RootFrame(graph);
        var signature = Signature(inputs: [("Value", PortValueType.String, false)]);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId, outputs: [("WrongName", PortValueType.Number)]),
            Shape("read")), signature);

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("a", "start", out var outputs).Should().Be(ScopeVisibility.Known);
        outputs.Should().ContainSingle(p => p.Name == "Value" && p.Type == PortValueType.String);
    }

    [Fact]
    public void A_passthrough_nodes_visible_output_carries_its_resolved_type_not_a_placeholder()
    {
        var n2 = Node("n2", "notify", passthrough: ["Room"]);
        var graph = Graph(
            [StartNode(), Node("src", "rooms"), n2, Node("n3", "log")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "n2", ("Room", "Room")), Edge("e3", "n2", "n3")]);
        var frame = RootFrame(graph);
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("rooms", outputs: [("Room", PortValueType.Of("Room"))]),
            Shape("notify", outputs: [("Sent", PortValueType.Boolean)]),
            Shape("log")), CanvasSignature.Empty, options: HostTypes("Room"));

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("n3", "n2", out var outputs).Should().Be(ScopeVisibility.Known);
        outputs.Should().ContainSingle(p => p.Name == "Room" && p.Type == PortValueType.Of("Room"));
    }

    [Fact]
    public void A_resolved_elsewhere_nodes_visible_outputs_come_from_the_environment()
    {
        const string childId = "11111111-1111-1111-1111-111111111111";
        var graph = Graph(
            [StartNode(), Node("sub", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId }), Node("sink", "notify")],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "sink", ("Result", "Message"))]);
        var frame = RootFrame(graph);
        var environment = Environment((CanvasPath.Root, "sub", childId, [], [("Result", PortValueType.String)]));
        var context = ContextFor(frame, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true,
                runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        var scope = Build(frame, context.Order[frame.Path], context);

        scope.TryResolve("sink", "sub", out var outputs).Should().Be(ScopeVisibility.Known);
        outputs.Should().ContainSingle(p => p.Name == "Result" && p.Type == PortValueType.String);
    }

    [Fact]
    public void Compiling_a_definition_with_a_sub_canvas_files_a_scope_for_every_frame()
    {
        var body = Graph([Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId)]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var context = WorkflowCompiler.Run(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), LoopShapeDeclaringBody()), CanvasSignature.Empty);

        context.Diagnostics.Should().BeEmpty();
        context.Scopes.Should().ContainKeys(CanvasPath.Root, CanvasPath.Root.Child("loop", "Body"));
    }

    [Fact]
    public void A_cyclic_canvas_gets_no_scope_entry_at_all()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop"), Node("b", "noop")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b"), Edge("e3", "b", "a")]);

        var context = WorkflowCompiler.Run(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        context.Scopes.Should().NotContainKey(CanvasPath.Root);
    }

    [Fact]
    public void A_root_refused_for_reachability_alone_still_files_its_order_and_scope()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop"), Node("stray", "noop")],
            [Edge("e1", "start", "a")]);

        var context = WorkflowCompiler.Run(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), CanvasSignature.Empty);

        context.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExtraRoot);
        context.Order.Should().ContainKey(CanvasPath.Root);
        context.Scopes.Should().ContainKey(CanvasPath.Root);
    }

    private static CompilationScope Build(CanvasFrame frame, IReadOnlyList<string> order, CompilationContext context)
    {
        context.Order[frame.Path] = order;
        new BindingPass(context, []).Visit(frame);
        return context.Scopes[frame.Path];
    }
}
