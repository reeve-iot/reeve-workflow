using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class PortsPhaseTests
{
    [Fact]
    public void A_required_input_fed_by_neither_a_wire_nor_a_config_value_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify")],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "n1" && d.PortName == "Message");
    }

    [Fact]
    public void A_required_input_the_runtime_would_fill_from_config_is_not_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "hello" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_required_input_wired_only_from_a_node_that_does_not_exist_is_refused_as_unfed_too()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify")],
            [Edge("e0", "start", "n1"), Edge("e1", "ghost", "n1", ("Text", "Message"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "n1" && d.PortName == "Message");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost");
    }

    [Fact]
    public void A_required_input_fed_by_a_literal_on_a_wire_to_a_node_that_does_not_exist_is_still_fed()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify")],
            [Edge("e0", "start", "n1"), LiteralEdge("e1", "ghost", "n1", "Message", "hello")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost");
    }

    [Fact]
    public void A_required_input_whose_only_config_value_is_a_polly_key_is_still_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { retryCount = 3 })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("retryCount", PortValueType.Number, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredInputUnfed && d.PortName == "retryCount");
    }

    [Fact]
    public void A_required_input_configured_to_the_empty_string_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "n1" && d.PortName == "Message");
    }

    [Fact]
    public void A_required_input_configured_to_null_is_an_unreadable_document()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = (object?)null })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
    }

    [Fact]
    public void A_sub_workflow_node_wired_on_both_ends_compiles_against_a_resolving_environment()
    {
        const string childId = "11111111-1111-1111-1111-111111111111";
        var graph = Graph(
            [StartNode(), Node("src", "read"),
             Node("sub", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId }),
             Node("sink", "notify")],
            [Edge("e1", "start", "src"),
             Edge("e2", "src", "sub", ("Temperature", "Setpoint")),
             Edge("e3", "sub", "sink", ("Result", "Message"))]);

        var environment = Environment((CanvasPath.Root, "sub", childId,
            [("Setpoint", PortValueType.Number, true)], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("read", outputs: [("Temperature", PortValueType.Number)]),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true,
                runsWorkflowFrom: "WorkflowId", inputs: [("WorkflowId", PortValueType.String, true)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_edge_naming_an_input_the_block_has_not_got_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "hi" })],
            [Edge("e1", "start", "n1", ("Value", "Recipient"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            signature: Signature(inputs: [("Value", PortValueType.String, false)]));

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.WirePortUnknown && d.PortName == "Recipient");
    }

    [Fact]
    public void An_edge_naming_an_output_the_source_has_not_got_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify", new { Message = "hi" })],
            [Edge("e1", "start", "n1", ("Nope", "Message"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            signature: Signature(inputs: [("Value", PortValueType.String, false)]));

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.WirePortUnknown && d.PortName == "Nope");
    }

    [Fact]
    public void A_passthrough_fed_a_valueless_literal_is_port_type_unresolved_not_wire_port_unknown()
    {
        var n2 = Node("n2", "notify", new { Message = "hi" });
        var graph = Graph(
            [StartNode(), Node("n1", "relay", passthrough: ["Retries"]), n2],
            [LiteralEdgeWithoutValue("e1", "start", "n1", "Retries"),
             Edge("e2", "n1", "n2", ("Retries", "Backoff"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("relay"),
            Shape("notify", inputs: [("Message", PortValueType.String, true), ("Backoff", PortValueType.Number, false)])),
            CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.PortTypeUnresolved && d.NodeId == "n1" && d.PortName == "Retries");
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.WirePortUnknown);
    }

    [Fact]
    public void A_passthrough_fed_a_null_literal_is_an_unreadable_definition()
    {
        var n2 = Node("n2", "notify", new { Message = "hi" });
        var graph = Graph(
            [StartNode(), Node("n1", "relay", passthrough: ["Retries"]), n2],
            [LiteralEdge("e1", "start", "n1", "Retries", null),
             Edge("e2", "n1", "n2", ("Retries", "Backoff"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("relay"),
            Shape("notify", inputs: [("Message", PortValueType.String, true), ("Backoff", PortValueType.Number, false)])),
            CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
    }

    [Fact]
    public void A_refusal_names_the_source_of_the_block_it_could_check_so_an_operator_knows_whom_to_blame()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "bt.sensor", new { Target = "x" })],
            [Edge("e1", "start", "n1", ("Value", "Missing"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bt.sensor", inputs: [("Target", PortValueType.String, true)], source: "reeve-plugin-bt@2.1.0"));

        var result = WorkflowCompiler.Compile(
            graph, catalogue, signature: Signature(inputs: [("Value", PortValueType.String, false)]));

        result.Diagnostics.Should().Contain(d => d.BlockSource == "reeve-plugin-bt@2.1.0");
    }

    [Fact]
    public void A_required_configuration_field_left_empty_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "bt.sensor")],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bt.sensor", configFields: [("ApiKey", ConfigFieldType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.RequiredConfigMissing && d.NodeId == "n1" && d.PortName == "ApiKey");
    }

    [Fact]
    public void A_required_configuration_field_set_to_an_empty_string_counts_as_unfilled()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "bt.sensor", new { ApiKey = "" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bt.sensor", configFields: [("ApiKey", ConfigFieldType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.RequiredConfigMissing);
    }

    [Fact]
    public void A_required_configuration_field_set_to_the_empty_string_counts_as_unfilled()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "bt.sensor", new { ApiKey = "" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bt.sensor", configFields: [("ApiKey", ConfigFieldType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.RequiredConfigMissing);
    }

    [Fact]
    public void A_required_configuration_field_that_is_filled_is_not_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "bt.sensor", new { ApiKey = "secret" })],
            [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bt.sensor", configFields: [("ApiKey", ConfigFieldType.String, true)])), CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_declared_output_shadowing_a_static_output_is_refused()
    {
        var node = Node("n1", "bt.sensor");
        node.OutputPorts = [new() { Name = "Value", Type = "Number" }];
        var graph = Graph([StartNode(), node], [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bt.sensor", outputs: [("Value", PortValueType.String)], supportsDeclaredOutputs: true)), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.DeclaredOutputShadowsStatic
                && d.NodeId == "n1" && d.PortName == "Value");
    }

    [Fact]
    public void A_declared_output_that_names_nothing_static_is_not_refused()
    {
        var node = Node("n1", "bt.sensor");
        node.OutputPorts = [new() { Name = "Extra", Type = "Number" }];
        var graph = Graph([StartNode(), node], [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("bt.sensor", outputs: [("Value", PortValueType.String)], supportsDeclaredOutputs: true)), CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_dynamic_input_port_declared_the_way_the_editor_writes_it_can_satisfy_a_required_wire()
    {
        var node = Node("n1", "dyn.block", new { InputPorts = """[{"name":"Target","type":"String","isRequired":true}]""" });
        var graph = Graph(
            [StartNode(), node],
            [Edge("e1", "start", "n1", ("Value", "Target"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.block", hasDynamicPorts: true)),
            signature: Signature(inputs: [("Value", PortValueType.String, false)]));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void A_dynamic_required_input_left_unfed_is_refused_like_a_static_one()
    {
        var node = Node("n1", "dyn.block", new { InputPorts = """[{"name":"Target","type":"String","isRequired":true}]""" });
        var graph = Graph([StartNode(), node], [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.block", hasDynamicPorts: true)), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredInputUnfed && d.PortName == "Target");
    }

    [Fact]
    public void A_port_list_that_is_not_json_makes_the_definition_unreadable_rather_than_portless()
    {
        var node = Node("n1", "dyn.block", new { InputPorts = "{ not json" });
        var graph = Graph([StartNode(), node], [Edge("e1", "start", "n1")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.block", hasDynamicPorts: true)), CanvasSignature.Empty);

        var refusal = result.Diagnostics.Should().ContainSingle().Which;
        refusal.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        refusal.NodeId.Should().Be("n1");
        refusal.Message.Should().Contain("rule L1, at '/nodes/1/configuration/InputPorts#'");
    }

    [Fact]
    public void A_wire_naming_a_dynamically_declared_output_is_not_refused()
    {
        var source = Node("n1", "dyn.block", new { OutputPorts = """[{"name":"Reading","type":"String"}]""" });
        var graph = Graph(
            [StartNode(), source, Node("n2", "notify", new { Message = "hi" })],
            [Edge("e1", "start", "n1"), Edge("e2", "n1", "n2", ("Reading", "Message"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("dyn.block", hasDynamicPorts: true),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue();
    }
}
