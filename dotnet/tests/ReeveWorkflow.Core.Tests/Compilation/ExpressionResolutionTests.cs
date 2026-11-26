using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class ExpressionResolutionTests
{
    [Fact]
    public void A_previous_path_naming_a_node_that_is_not_in_scope_is_refused()
    {
        var graph = Graph(
            [StartNode(),
             Node("n1", "notify", new { Message = "expr:${previous.typo.value}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.ExpressionOutOfScope && d.NodeId == "n1");
    }

    [Fact]
    public void A_previous_path_naming_an_output_the_node_does_not_publish_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"),
             Node("n1", "notify", new { Message = "expr:${previous.a.humidity}" })],
            [Edge("e1", "start", "a"), Edge("e2", "a", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void An_unregistered_namespace_is_refused()
    {
        var graph = Graph(
            [StartNode(),
             Node("n1", "notify", new { Message = "expr:${device.kitchen.temperature}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionUnknownNamespace);
    }

    [Fact]
    public void A_host_resolver_that_answers_no_makes_its_own_namespace_refusable()
    {
        var graph = Graph(
            [StartNode(),
             Node("n1", "notify", new { Message = "expr:${device.gone.temperature}" })],
            [Edge("e1", "start", "n1")]);

        var options = new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new RefusingDeviceNamespace(), 2)] };

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty, options);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionUnknownNamespace);
    }

    [Fact]
    public void An_expression_that_does_not_parse_is_refused_at_its_position()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${a.b} +" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionSyntax)
            .Which.Message.Should().Contain("position 8");
    }

    [Fact]
    public void A_previous_reference_to_a_node_already_refused_as_unresolved_does_not_cascade()
    {
        var graph = Graph(
            [StartNode(),
             Node("sub", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = "missing" }),
             Node("n1", "notify", new { Message = "expr:${previous.sub.Result}" })],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true,
                runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.NodePortsUnresolved);
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_parameter_the_workflow_declares_is_in_scope()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${parameter.Who}" })],
            [Edge("e1", "start", "n1")]);

        var signature = Signature(inputs: [("Who", PortValueType.String, true)]);
        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), signature);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_parameter_the_workflow_does_not_declare_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${parameter.Missing}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_bare_now_is_in_scope()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${now}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.Of("DateTime"), true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_known_now_field_is_in_scope()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${now.year}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.Of("Int"), true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_now_field_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${now.century}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void An_Expression_input_is_typed_at_compile_time()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "wait", new { Template = "expr:${previous.typo.value}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("wait", inputs: [("Template", PortValueType.Of("Expression<Boolean>"), false)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.ExpressionOutOfScope && d.NodeId == "n1");
    }

    [Fact]
    public void A_previous_output_reference_that_differs_only_in_case_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"),
             Node("n1", "notify", new { Message = "expr:${previous.a.temperature}" })],
            [Edge("e1", "start", "a"), Edge("e2", "a", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_literal_mapping_carrying_an_expression_is_checked_the_same_as_a_configuration_value()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify")],
            [Edge("e1", "start", "n1"), LiteralEdge("e2", "start", "n1", "Message", "expr:${previous.typo.value}")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_sub_canvas_declared_input_is_in_scope_as_a_parameter_inside_its_own_body()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "notify", new { Message = "expr:${parameter.Item}" })],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var loopShape = Shape("loop.for",
            subCanvasInputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]>
            {
                ["Body"] = [("Item", PortValueType.String, true)],
            }, subCanvasNames: ["Body"]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), loopShape,
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_parameter_the_run_seeds_but_the_branch_does_not_declare_is_still_refused()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "notify", new { Message = "expr:${parameter.Condition}" })],
            [Edge("be1", "bstart", "inner")]);
        var gateNode = Node("gate", "ifelse");
        gateNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Then"] = body };
        var graph = Graph([StartNode(), gateNode], [Edge("e1", "start", "gate")]);

        var gateShape = Shape("ifelse",
            subCanvasInputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]>
            {
                ["Then"] = [],
            }, subCanvasNames: ["Then"]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), gateShape,
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_root_parameter_is_not_in_scope_inside_a_sub_canvas_that_does_not_declare_it()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "notify", new { Message = "expr:${parameter.RootOnly}" })],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var signature = Signature(inputs: [("RootOnly", PortValueType.String, true)]);
        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("loop.for", subCanvasInputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), signature);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void Two_out_of_scope_references_in_one_expression_are_reported_in_a_stable_order()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${previous.zzz.v} == ${now.century}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics[0].Message.Should().Contain("now.century");
        result.Diagnostics[1].Message.Should().Contain("previous.zzz.v");
    }

    [Fact]
    public void A_parameter_reference_inside_a_sub_canvas_with_an_undeclared_name_does_not_cascade()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "notify", new { Message = "expr:${parameter.Item}" })],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Nope"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var loopShape = Shape("loop.for",
            subCanvasInputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]> { ["Body"] = [] },
            subCanvasNames: ["Body"]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), loopShape,
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.SubCanvasNameUnknown);
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_parameter_reference_inside_a_sub_canvas_of_an_unbound_container_does_not_cascade()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "notify", new { Message = "expr:${parameter.Item}" })],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "gone.block");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_syntax_error_inside_a_sub_canvas_of_an_unbound_container_still_refuses()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "notify", new { Message = "expr:${a.b} +" })],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "gone.block");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionSyntax);
    }

    [Fact]
    public void A_now_reference_inside_a_sub_canvas_of_an_unbound_container_still_refuses()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "notify", new { Message = "expr:${now.century}" })],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "gone.block");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_host_namespace_check_inside_a_sub_canvas_of_an_unbound_container_still_refuses()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
             Node("inner", "notify", new { Message = "expr:${device.gone.temperature}" })],
            [Edge("be1", "bstart", "inner")]);
        var loopNode = Node("loop", "gone.block");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var options = new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new RefusingDeviceNamespace(), 2)] };
        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty, options);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ExpressionUnknownNamespace);
    }

    [Fact]
    public void A_stale_configuration_expression_on_an_input_a_wire_feeds_is_still_checked()
    {
        var graph = Graph(
            [StartNode(), Node("a", "read"),
             Node("n1", "notify", new { Message = "expr:${previous.typo.value}" })],
            [Edge("e1", "start", "a"), Edge("e2", "a", "n1", ("Temperature", "Message"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void An_expression_in_a_design_time_only_configuration_key_is_not_checked()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify",
                new { Message = "hello", __designReferenceDeviceId = "expr:${previous.typo.value}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_Expression_input_fed_by_a_literal_mapping_is_typed_at_compile_time()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "wait")],
            [LiteralEdge("e1", "start", "n1", "Template", "expr:${previous.typo.value}")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("wait", inputs: [("Template", PortValueType.Of("Expression<Boolean>"), false)])),
            CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.ExpressionOutOfScope && d.NodeId == "n1");
    }

    [Fact]
    public void An_empty_now_field_is_a_syntax_error()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${now.}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionSyntax);
    }

    [Fact]
    public void Only_the_literal_mapping_that_actually_applies_to_an_input_is_checked()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("other", "read"), Node("inner", "notify")],
            [Edge("be0", "bstart", "other"),
             LiteralEdge("be1", "bstart", "inner", "Message", "expr:${previous.typo.value}"),
             LiteralEdge("be2", "other", "inner", "Message", "expr:toString(${now})")]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("loop.for", subCanvasInputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DuplicateInputMapping);
    }

    [Fact]
    public void A_literal_mapping_beside_a_wire_into_the_same_input_is_still_checked()
    {
        var body = Graph(
            [Node("bstart", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("other", "read"), Node("inner", "notify")],
            [Edge("be0", "bstart", "other"),
             LiteralEdge("be1", "bstart", "inner", "Message", "expr:${previous.typo.value}"),
             Edge("be2", "other", "inner", ("Temperature", "Message"))]);
        var loopNode = Node("loop", "loop.for");
        loopNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = body };
        var graph = Graph([StartNode(), loopNode], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape("loop.for", subCanvasInputs: new Dictionary<string, (string Name, PortValueType Type, bool Required)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_host_check_that_answers_yes_without_a_term_is_refused_as_a_malformed_type()
    {
        var graph = Graph(
            [StartNode(),
             Node("n1", "notify", new { Message = "expr:${device.kitchen.temperature}" })],
            [Edge("e1", "start", "n1")]);

        var options = new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new StubDeviceNamespace(NamespacePathCheck.Yes, null), 2)] };

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty, options);

        result.Diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.PortTypeMalformed && d.NodeId == "n1" && d.Message.Contains("device.kitchen.temperature"));
    }

    [Fact]
    public void A_host_check_that_answers_yes_leaves_the_reference_alone()
    {
        var graph = Graph(
            [StartNode(),
             Node("n1", "notify", new { Message = "expr:${device.kitchen.temperature}" })],
            [Edge("e1", "start", "n1")]);

        var options = new CompilationOptions { NamespaceRegistrations = [HostNamespaceRegistration.For(new StubDeviceNamespace(NamespacePathCheck.Yes), 2)] };

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty, options);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_registration_check_receives_the_key_alone()
    {
        string[]? received = null;
        var registered = new RecordingCheck("device", NamespacePathCheck.Yes, segments => received = segments.ToArray());
        var registration = new WorkflowExpressionNamespace("device", 2, registered,
            (_, _) => throw new InvalidOperationException("compile must not make a resolver"));
        var options = new CompilationOptions { NamespaceRegistrations = [registration] };
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "expr:${device.kitchen.temperature}" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty, options);

        result.Diagnostics.Should().BeEmpty();
        received.Should().Equal("kitchen", "temperature");
    }

    private sealed record RecordingCheck(
        string Namespace, NamespacePathCheck Answer, Action<IReadOnlyList<string>> Record) : IExpressionNamespaceStaticCheck
    {
        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments)
        {
            Record(segments);
            return new(Answer, "String");
        }
    }

    private sealed class RefusingDeviceNamespace : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "device";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => NamespacePathCheckAnswer.No;
    }

    private sealed class StubDeviceNamespace(NamespacePathCheck answer, string? term = "String") : IExpressionNamespaceStaticCheck
    {
        public string Namespace => "device";

        public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) => new(answer, term);
    }
}
