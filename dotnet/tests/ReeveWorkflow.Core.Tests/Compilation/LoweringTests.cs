using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class LoweringTests
{
    [Fact]
    public void The_program_carries_the_walk_order_of_every_canvas()
    {
        var graph = Graph(
            [Node("b", "write"), Node("a", "read"), StartNode()],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b")]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("read"), Shape("write")),
            Signature()).Program!;

        program.Canvases.Should().ContainSingle()
            .Which.NodeOrder.Should().ContainInOrder("start", "a", "b");
    }

    [Fact]
    public void An_expr_value_is_lowered_with_its_typed_tree()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"),
             Node("b", "notify", new { Message = "expr:${previous.a.Temperature} + 1" })],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b")]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("notify", inputs: [("Message", PortValueType.Number, true)])),
            Signature()).Program!;

        var expression = program.Canvases[0].Nodes.Single(n => n.NodeId == "b").Expressions["Message"];
        expression.Text.Should().Be("expr:${previous.a.Temperature} + 1");
        expression.Tree!.Term.ToString().Should().Be("Number");
    }

    [Fact]
    public void A_failed_compilation_carries_no_program()
    {
        var graph = Graph([StartNode(), Node("a", "unknown-block")], [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId)), Signature());

        result.Succeeded.Should().BeFalse();
        result.Program.Should().BeNull();
    }

    [Fact]
    public void The_root_canvas_path_is_empty_and_its_signature_is_the_one_compile_was_given()
    {
        var graph = Graph([StartNode()]);
        var signature = Signature(inputs: [("Count", PortValueType.Number, true)]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId)), signature).Program!;

        var root = program.Canvases.Single();
        root.CanvasPath.Should().Be(CanvasPath.Root);
        root.Signature.Inputs.Should().ContainSingle(p => p.Name == "Count");
    }

    [Fact]
    public void A_sub_canvas_is_its_own_compiled_canvas_findable_from_its_container_node()
    {
        var body = Graph([StartNode()]);
        var container = Node("branch", "if-else");
        container.SubCanvases = new Dictionary<string, ReeveWorkflow.Core.Model.WorkflowDefinition> { ["Then"] = body };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "branch")]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("if-else", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasNames: ["Then"])),
            Signature()).Program!;

        var containerNode = program.Canvases[0].Nodes.Single(n => n.NodeId == "branch");
        containerNode.SubCanvases.Should().ContainKey("Then");
        var childPath = containerNode.SubCanvases["Then"];

        childPath.Should().Be(CanvasPath.Root.Child("branch", "Then"));
        program.Canvases.Should().ContainSingle(c => c.CanvasPath == childPath);
    }

    [Fact]
    public void A_sub_canvas_carries_its_own_signature_not_the_roots()
    {
        var body = Graph([StartNode()]);
        var container = Node("branch", "if-else");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Then"] = body };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "branch")]);
        var rootSignature = Signature(inputs: [("Count", PortValueType.Number, true)]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("if-else", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasNames: ["Then"])),
            rootSignature).Program!;

        var root = program.Canvases.Single(c => c.CanvasPath.IsRoot);
        var child = program.Canvases.Single(c => c.CanvasPath == CanvasPath.Root.Child("branch", "Then"));

        child.Signature.Inputs.Should().BeEmpty("the Then branch declares no inputs of its own");
        root.Signature.Inputs.Should().ContainSingle(p => p.Name == "Count");
    }

    [Fact]
    public void A_cyclic_root_refuses_without_throwing_and_carries_no_program()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop"), Node("b", "noop")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b"), Edge("e3", "b", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), Signature());

        result.Succeeded.Should().BeFalse();
        result.Program.Should().BeNull();
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.Cycle);
    }

    [Fact]
    public void A_cyclic_sub_canvas_refuses_without_throwing_and_carries_no_program()
    {
        var body = Graph(
            [Node("b1", "noop"), Node("b2", "noop")],
            [Edge("be1", "b1", "b2"), Edge("be2", "b2", "b1")]);
        var loop = Node("loop", "loop.for");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
                Shape("loop.for", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] },
                    subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] },
                    subCanvasNames: ["Body"]),
                Shape("noop")),
            Signature());

        result.Succeeded.Should().BeFalse();
        result.Program.Should().BeNull();
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.Cycle && d.CanvasPath.Contains(new CanvasPathSegment("loop", "Body")));
    }

    [Fact]
    public void Inputs_carries_the_config_baseline_and_incoming_carries_the_wire_unevaluated()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"),
             Node("b", "notify", new { Message = "configured" })],
            [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Temperature", "Reading"))]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true), ("Reading", PortValueType.Number, false)])),
            Signature()).Program!;

        var node = program.Canvases[0].Nodes.Single(n => n.NodeId == "b");
        node.Inputs.Should().ContainKey("Message").WhoseValue.Should().Be("configured");
        node.Inputs.Should().NotContainKey("Reading");

        node.Incoming.Should().ContainSingle(e => e.SourceNodeId == "a")
            .Which.Mappings.Should().ContainSingle(m => m.SourceOutput == "Temperature" && m.TargetInput == "Reading");
    }

    [Fact]
    public void A_node_with_no_resilience_config_carries_four_absent_values()
    {
        var graph = Graph([StartNode(), Node("a", "noop")], [Edge("e1", "start", "a")]);

        var program = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), Signature()).Program!;

        var node = program.Canvases[0].Nodes.Single(n => n.NodeId == "a");
        node.Resilience.Should().Be(new ConfiguredResilience(null, null, null, null));
    }

    [Fact]
    public void A_configured_resilience_value_is_carried_exactly_even_when_it_equals_no_special_default()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryEnabled = false, retryCount = 7, retryBackoffSeconds = 0.5, timeoutSeconds = 45 })],
            [Edge("e1", "start", "a")]);

        var program = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), Signature()).Program!;

        var node = program.Canvases[0].Nodes.Single(n => n.NodeId == "a");
        node.Resilience.Should().Be(new ConfiguredResilience(45, false, 7, 0.5));
    }

    [Fact]
    public void A_configured_resilience_value_equal_to_the_executors_own_default_is_still_carried()
    {
        var graph = Graph(
            [StartNode(), Node("a", "noop", new { retryEnabled = true, retryCount = 3, retryBackoffSeconds = 1.0, timeoutSeconds = 30 })],
            [Edge("e1", "start", "a")]);

        var program = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("noop")), Signature()).Program!;

        var node = program.Canvases[0].Nodes.Single(n => n.NodeId == "a");
        node.Resilience.Should().Be(new ConfiguredResilience(30, true, 3, 1.0));
    }

    [Fact]
    public void Inputs_excludes_polly_keys_and_design_time_keys_the_merge_would_skip()
    {
        var graph = Graph(
            [StartNode(), Node("a", "notify", new
            {
                timeoutSeconds = 5,
                __designOnly = "x",
                Message = "configured",
            })],
            [Edge("e1", "start", "a")]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), Signature()).Program!;

        var node = program.Canvases[0].Nodes.Single(n => n.NodeId == "a");
        node.Inputs.Should().NotContainKey("timeoutSeconds");
        node.Inputs.Should().NotContainKey("__designOnly");
        node.Inputs.Should().ContainKey("Message").WhoseValue.Should().Be("configured");
    }

    [Fact]
    public void Expressions_is_keyed_case_sensitively()
    {
        var graph = Graph(
            [StartNode(), Node("a", "notify", new Dictionary<string, JsonElement>
            {
                ["Message"] = JsonSerializer.SerializeToElement("expr:1 + 1"),
                ["message"] = JsonSerializer.SerializeToElement("expr:2 + 2"),
            })],
            [Edge("e1", "start", "a")]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("notify")), Signature()).Program!;

        var node = program.Canvases[0].Nodes.Single(n => n.NodeId == "a");
        node.Expressions.Should().HaveCount(2);
        node.Expressions.Should().ContainKey("Message");
        node.Expressions.Should().ContainKey("message");
    }

    [Fact]
    public void SubCanvases_is_keyed_case_sensitively()
    {
        var bodyThen = Graph([StartNode()]);
        var bodythen = Graph([StartNode()]);
        var container = Node("branch", "if-else");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Then"] = bodyThen, ["then"] = bodythen };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "branch")]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("if-else",
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [], ["then"] = [] },
                subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [], ["then"] = [] },
                subCanvasNames: ["Then", "then"])),
            Signature()).Program!;

        var containerNode = program.Canvases[0].Nodes.Single(n => n.NodeId == "branch");
        containerNode.SubCanvases.Should().HaveCount(2);
        containerNode.SubCanvases.Should().ContainKey("Then");
        containerNode.SubCanvases.Should().ContainKey("then");
    }

    [Fact]
    public void A_sub_canvas_with_no_nodes_still_appears_in_the_program()
    {
        var body = Graph([]);
        var container = Node("branch", "if-else");
        container.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Then"] = body };
        var graph = Graph([StartNode(), container], [Edge("e1", "start", "branch")]);

        var program = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("if-else", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasNames: ["Then"])),
            Signature()).Program!;

        program.Canvases.Should().ContainSingle(c => c.CanvasPath == CanvasPath.Root.Child("branch", "Then"))
            .Which.Nodes.Should().BeEmpty();
    }
}
