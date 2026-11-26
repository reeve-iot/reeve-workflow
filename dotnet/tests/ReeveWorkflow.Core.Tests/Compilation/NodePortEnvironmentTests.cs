using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class NodePortEnvironmentTests
{
    [Fact]
    public void The_compiler_refuses_a_dotted_name_returned_by_a_port_resolver()
    {
        var graph = Graph([StartNode(), Node("sub", "workflow.run", new { WorkflowId = "child" })], [Edge("e1", "start", "sub")]);
        var environment = Environment((CanvasPath.Root, "sub", "child", [], [("result.value", PortValueType.String)]));
        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])), CanvasSignature.Empty, environment: environment);
        result.Diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.DottedName);
    }

    [Theory]
    [InlineData("other", false, CompilationRules.NodePortsUnresolved)]
    [InlineData("child", true, CompilationRules.ReferenceInputMapped)]
    public void An_untrusted_dotted_resolution_reports_the_reference_refusal(string reference, bool mapped, string rule)
    {
        var nodes = new[] { StartNode(), Node("sub", "workflow.run", new { WorkflowId = "child" }) };
        var graph = Graph(nodes, [mapped ? Edge("e", "start", "sub", ("WorkflowId", "WorkflowId")) : Edge("e", "start", "sub")]);
        var environment = Environment((CanvasPath.Root, "sub", reference, [], [("result.value", PortValueType.String)]));
        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])), CanvasSignature.Empty, environment: environment);
        result.Diagnostics.Should().Contain(d => d.RuleId == rule);
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.DottedName);
    }

    [Fact]
    public void A_node_whose_ports_were_resolved_elsewhere_is_checked_against_them()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "child" }), Node("n1", "notify")],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "n1", ("Result", "Message"))]);
        var environment = Environment((CanvasPath.Root, "sub", "child", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Json", null)]
    [InlineData("Collection<Json?>", null)]
    [InlineData(null, "Collection<Json>")]
    public void A_resolver_answer_holding_json_where_t1_refuses_it_is_refused_and_leaves_the_ports_unresolved(string? input, string? output)
    {
        var graph = Graph([StartNode(), Node("sub", "workflow.run", new { WorkflowId = "child" })], [Edge("e1", "start", "sub")]);
        var environment = Environment((CanvasPath.Root, "sub", "child",
            input is null ? [] : [("In", PortValueType.Of(input), false)],
            output is null ? [] : [("Out", PortValueType.Of(output))]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])), CanvasSignature.Empty, environment: environment);

        var port = input is null ? "Out" : "In";
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.PortTypeJsonPosition && d.NodeId == "sub" && d.PortName == port);
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodePortsUnresolved && d.NodeId == "sub");
    }

    [Fact]
    public void A_node_whose_ports_nobody_resolved_is_refused_rather_than_waved_through()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "child" })],
            [Edge("e1", "start", "sub")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true)),
            CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.NodePortsUnresolved);
    }

    [Fact]
    public void A_resolver_that_claimed_the_node_and_failed_names_its_own_reason_in_the_refusal()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "child" })],
            [Edge("e1", "start", "sub")]);
        var environment = FailedEnvironment(CanvasPath.Root, "sub", "the referenced workflow was deleted");

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true)),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.NodePortsUnresolved
                && d.Message.Contains("the referenced workflow was deleted"));
    }

    [Fact]
    public void A_required_configuration_field_is_still_checked_on_a_node_whose_ports_are_unresolved()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run")],
            [Edge("e1", "start", "sub")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true,
                configFields: [("Extra", ConfigFieldType.String, true)])),
            CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodePortsUnresolved);
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredConfigMissing && d.NodeId == "sub" && d.PortName == "Extra");
    }

    [Fact]
    public void A_second_required_input_on_an_unresolved_node_is_still_checked()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run")],
            [Edge("e1", "start", "sub")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true), ("Timeout", PortValueType.Number, true)])),
            CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodePortsUnresolved && d.NodeId == "sub");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "sub" && d.PortName == "Timeout");
        result.Diagnostics.Should().NotContain(d => d.PortName == "WorkflowId");
    }

    [Fact]
    public void A_source_wired_workflow_reference_is_refused_as_mapped_even_though_the_environment_answers()
    {
        var graph = Graph(
            [StartNode(), Node("picker", "notify"), Node("sub", "workflow.run")],
            [Edge("e1", "start", "picker"), Edge("e2", "start", "sub"),
                Edge("e3", "picker", "sub", ("Message", "TargetWorkflow"))]);
        var environment = Environment((CanvasPath.Root, "sub", null, [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", outputs: [("Message", PortValueType.String)]),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "TargetWorkflow",
                inputs: [("TargetWorkflow", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ReferenceInputMapped);
    }

    [Fact]
    public void EffectiveReference_reads_the_configured_value_regardless_of_any_wire_present()
    {
        var node = Node("sub", "workflow.run", new { WorkflowId = "configured-id" });

        var effective = EffectivePorts.EffectiveReference(node, "WorkflowId");

        effective.Should().Be("configured-id");
    }

    [Fact]
    public void A_workflow_reference_wired_only_from_a_node_that_does_not_exist_is_refused_as_mapped_too()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "configured-id" })],
            [Edge("e0", "start", "sub"), Edge("e1", "ghost", "sub", ("Value", "WorkflowId"))]);
        var environment = Environment((CanvasPath.Root, "sub", "configured-id", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost");
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ReferenceInputMapped && d.NodeId == "sub");
    }

    [Fact]
    public void A_literal_workflow_reference_is_refused_as_mapped_same_as_a_wire()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run")],
            [LiteralEdge("e1", "start", "sub", "WorkflowId", "11111111-1111-1111-1111-111111111111")]);
        var environment = Environment((CanvasPath.Root, "sub", "11111111-1111-1111-1111-111111111111", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ReferenceInputMapped);
    }

    [Fact]
    public void A_literal_reference_overriding_a_configured_one_is_refused_regardless_of_which_one_a_resolution_answers_for()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "configured-id" })],
            [LiteralEdge("e1", "start", "sub", "WorkflowId", "literal-id")]);
        var environment = Environment((CanvasPath.Root, "sub", "literal-id", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ReferenceInputMapped);
    }

    [Fact]
    public void A_non_string_literal_reference_is_refused_as_mapped()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "configured-id" })],
            [LiteralEdge("e1", "start", "sub", "WorkflowId", 42)]);
        var environment = Environment((CanvasPath.Root, "sub", "configured-id", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ReferenceInputMapped);
    }

    [Fact]
    public void A_valueless_literal_reference_is_refused_as_mapped()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "configured-id" })],
            [LiteralEdgeWithoutValue("e1", "start", "sub", "WorkflowId")]);
        var environment = Environment((CanvasPath.Root, "sub", "configured-id", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ReferenceInputMapped);
    }

    [Fact]
    public void A_null_literal_reference_is_an_unreadable_definition()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "configured-id" })],
            [LiteralEdge("e1", "start", "sub", "WorkflowId", null)]);
        var environment = Environment((CanvasPath.Root, "sub", "configured-id", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
    }

    [Fact]
    public void An_unfed_non_required_reference_has_no_effective_reference_and_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run")],
            [Edge("e1", "start", "sub")]);
        var environment = Environment((CanvasPath.Root, "sub", null, [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, false)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.NodePortsUnresolved
                && d.Message == "Node 'workflow.run' names no callee a resolver could ever answer for; its reference input "
                    + "'WorkflowId' is not configured with a non-empty string.");
    }

    [Fact]
    public void A_mapped_reference_nodes_other_wires_are_not_also_refused_as_unknown_ports()
    {
        var graph = Graph(
            [StartNode(), Node("src", "notify"), Node("sub", "workflow.run", new { WorkflowId = "configured-id" })],
            [Edge("e1", "start", "src"),
                Edge("e2", "src", "sub", ("Message", "WorkflowId"), ("Message", "ChildParam"))]);
        var environment = Environment((CanvasPath.Root, "sub", "configured-id", [("ChildParam", PortValueType.String, false)], []));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", outputs: [("Message", PortValueType.String)]),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.ReferenceInputMapped && d.NodeId == "sub");
    }

    [Fact]
    public void A_mapped_reference_node_does_not_take_the_ports_filed_for_its_configured_id()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run", new { WorkflowId = "configured-id" })],
            [LiteralEdge("e1", "start", "sub", "WorkflowId", "configured-id")]);
        var environment = Environment((CanvasPath.Root, "sub", "configured-id", [("ChildParam", PortValueType.String, true)], []));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ReferenceInputMapped);
    }

    [Fact]
    public void A_reference_fed_only_by_a_ghost_wire_is_not_also_refused_as_unfed()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run")],
            [Edge("e0", "start", "sub"), Edge("e1", "ghost", "sub", ("Value", "WorkflowId"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId)).Should().BeEquivalentTo(
            [(CompilationRules.ReferenceInputMapped, "sub"), (CompilationRules.EdgeEndpointUnknown, "ghost")]);
    }

    [Fact]
    public void A_literal_reference_is_refused_as_mapped_even_when_a_resolution_was_filed_for_it_differently_cased()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run")],
            [LiteralEdge("e1", "start", "sub", "WorkflowId", "Child-Id")]);
        var environment = Environment((CanvasPath.Root, "sub", "child-id", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.ReferenceInputMapped);
    }

    [Fact]
    public void A_flagged_blocks_own_port_type_wins_over_a_resolver_filing_the_same_name()
    {
        var node = Node("sub", "workflow.run");
        var shape = Shape("workflow.run", portsAreResolvedElsewhere: true,
            inputs: [("WorkflowId", PortValueType.String, true)]);
        var environment = Environment((CanvasPath.Root, "sub", null, [("WorkflowId", PortValueType.Number, false)], []));

        var inputs = TypesOf(Graph([node]), Catalogue(shape), CanvasSignature.Empty, node.Id, environment: environment).Inputs;

        inputs.Should().ContainSingle(p => p.Name == "WorkflowId");
        inputs.Single(p => p.Name == "WorkflowId").Type.Should().Be(PortValueType.String);
    }

    [Fact]
    public void A_flagged_blocks_own_declared_input_stays_required_even_once_the_environment_answers()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run")],
            [Edge("e1", "start", "sub")]);
        var environment = Environment((CanvasPath.Root, "sub", null, [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true,
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "sub" && d.PortName == "WorkflowId");
    }

    [Fact]
    public void A_flagged_blocks_own_declared_outputs_are_replaced_not_unioned_in()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run"), Node("n1", "notify")],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "n1", ("StaticThing", "Message"))]);
        var environment = Environment((CanvasPath.Root, "sub", null, [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true, outputs: [("StaticThing", PortValueType.String)]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.WirePortUnknown && d.NodeId == "sub" && d.PortName == "StaticThing");
    }

    [Fact]
    public void A_wire_into_a_resolved_elsewhere_nodes_filed_input_is_accepted()
    {
        var graph = Graph(
            [StartNode(), Node("src", "notify"), Node("sub", "workflow.run")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "sub", ("Message", "Target"))]);
        var environment = Environment((CanvasPath.Root, "sub", null, [("Target", PortValueType.String, false)], []));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", outputs: [("Message", PortValueType.String)]),
            Shape("workflow.run", portsAreResolvedElsewhere: true)),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_wire_into_a_name_the_resolver_never_filed_is_refused_as_an_unknown_port()
    {
        var graph = Graph(
            [StartNode(), Node("src", "notify"), Node("sub", "workflow.run")],
            [Edge("e1", "start", "src"), Edge("e2", "src", "sub", ("Message", "Unfiled"))]);
        var environment = Environment((CanvasPath.Root, "sub", null, [("Target", PortValueType.String, false)], []));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", outputs: [("Message", PortValueType.String)]),
            Shape("workflow.run", portsAreResolvedElsewhere: true)),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.WirePortUnknown && d.NodeId == "sub" && d.PortName == "Unfiled");
    }

    [Fact]
    public void A_wire_into_an_unresolved_target_is_not_also_flagged_as_an_unknown_port()
    {
        var graph = Graph(
            [StartNode(), Node("picker", "notify"), Node("sub", "workflow.run")],
            [Edge("e1", "start", "picker"), Edge("e2", "picker", "sub", ("Message", "AnyInput"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", outputs: [("Message", PortValueType.String)]),
            Shape("workflow.run", portsAreResolvedElsewhere: true)),
            CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.NodePortsUnresolved);
    }

    [Fact]
    public void A_wire_out_of_an_unresolved_source_still_checks_the_known_target_end()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run"), Node("n1", "notify")],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "n1", ("Result", "NoSuchInput"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true),
            Shape("notify", outputs: [("Sent", PortValueType.Boolean)])),
            CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(2);
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.NodePortsUnresolved && d.NodeId == "sub");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.WirePortUnknown && d.NodeId == "n1" && d.PortName == "NoSuchInput");
    }

    [Fact]
    public void A_passthrough_fed_from_an_unresolved_node_is_not_also_flagged_as_an_unknown_wire_port()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run"), Node("n1", "notify", passthrough: ["Result"])],
            [Edge("e1", "start", "sub"), Edge("e2", "sub", "n1", ("Result", "Result"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true),
            Shape("notify")),
            CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.RuleId.Should().Be(CompilationRules.NodePortsUnresolved);
    }

    [Fact]
    public void A_resolvers_required_filed_input_left_unfed_at_the_call_site_is_still_reported()
    {
        var graph = Graph(
            [StartNode(), Node("sub", "workflow.run")],
            [Edge("e1", "start", "sub")]);
        var environment = Environment((CanvasPath.Root, "sub", null, [("ChildParam", PortValueType.String, true)], []));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("workflow.run", portsAreResolvedElsewhere: true)),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "sub" && d.PortName == "ChildParam");
    }

    [Fact]
    public void A_resolved_elsewhere_node_inside_a_sub_canvas_is_keyed_by_its_own_canvas_path()
    {
        var body = Graph([Node("sub", "workflow.run")]);
        var container = Node("container", "container.block");
        container.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), container], [Edge("e1", "start", "container")]);
        var environment = Environment((CanvasPath.Root.Child("container", "Body"), "sub", null, [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("container.block", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [],
            }, subCanvasNames: ["Body"]),
            Shape("workflow.run", portsAreResolvedElsewhere: true)),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_wire_followed_by_a_later_literal_on_the_same_reference_input_is_refused_as_mapped_once()
    {
        var body = Graph(
            [Node("src", "notify"), Node("src2", "notify"), Node("sub", "workflow.run")],
            [Edge("e1", "src", "sub", ("Message", "WorkflowId")),
                LiteralEdge("e2", "src2", "sub", "WorkflowId", "11111111-1111-1111-1111-111111111111")]);
        var container = Node("container", "container.block");
        container.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), container], [Edge("e1", "start", "container")]);
        var environment = Environment((CanvasPath.Root.Child("container", "Body"), "sub", "11111111-1111-1111-1111-111111111111", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("container.block", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [],
            }, subCanvasNames: ["Body"]),
            Shape("notify", outputs: [("Message", PortValueType.String)]),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Select(d => d.RuleId).Should().BeEquivalentTo(
            [CompilationRules.ReferenceInputMapped, CompilationRules.DuplicateInputMapping]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public void The_compiler_and_the_librarys_own_save_gate_refuse_the_same_SubWorkflow_graphs(bool? literal, bool bothAccept)
    {
        var configuredChild = Guid.NewGuid();
        var otherChild = Guid.NewGuid();
        List<WorkflowConnectionDefinition> edges = [Edge("e1", "start", "src")];
        edges.Add(literal switch
        {
            true => LiteralEdge("e2", "src", "call", "WorkflowId", otherChild.ToString()),
            false => Edge("e2", "src", "call", ("Out", "WorkflowId")),
            null => Edge("e2", "src", "call"),
        });

        var graph = Graph(
            [
                StartNode(), Node("src", "emitter"),
                Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = configuredChild.ToString() }),
            ],
            edges);
        var environment = Environment((CanvasPath.Root, "call", configuredChild.ToString(), [], []));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("emitter", outputs: [("Out", PortValueType.String)]),
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId,
                inputs: [("WorkflowId", PortValueType.String, true)],
                portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId")),
            CanvasSignature.Empty, environment: environment);

        var compilerRefuses = result.Diagnostics.Any(d => d.RuleId == CompilationRules.ReferenceInputMapped);

        var gateRefuses = true;
        try
        {
            WorkflowGraphHelpers.ValidateSubWorkflowReferencesStatic(graph);
            gateRefuses = false;
        }
        catch (WorkflowValidationException)
        {
            gateRefuses = true;
        }

        compilerRefuses.Should().Be(gateRefuses, "the compiler and the save gate must agree on this graph");
        compilerRefuses.Should().Be(!bothAccept);
    }

    [Fact]
    public void A_real_wire_followed_by_a_ghost_one_on_the_same_reference_input_is_refused_as_mapped_once()
    {
        var body = Graph(
            [Node("src", "notify"), Node("sub", "workflow.run", new { WorkflowId = "configured-id" })],
            [Edge("e1", "src", "sub", ("Message", "WorkflowId")),
                Edge("e2", "ghost", "sub", ("Message", "WorkflowId"))]);
        var container = Node("container", "container.block");
        container.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), container], [Edge("e0", "start", "container")]);
        var environment = Environment((CanvasPath.Root.Child("container", "Body"), "sub", "configured-id", [], [("Result", PortValueType.String)]));

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("container.block", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [],
            }, subCanvasNames: ["Body"]),
            Shape("notify", outputs: [("Message", PortValueType.String)]),
            Shape("workflow.run", portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)])),
            CanvasSignature.Empty, environment: environment);

        result.Diagnostics.Should().HaveCount(3);
        result.Diagnostics.Should().ContainSingle(d =>
            d.RuleId == CompilationRules.ReferenceInputMapped && d.NodeId == "sub");
        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.DuplicateInputMapping);
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.EdgeEndpointUnknown && d.NodeId == "ghost");
    }
}

public class NodePortResolutionTests
{
    [Fact]
    public void Resolved_requires_a_signature()
    {
        var act = () => NodePortResolution.Resolved("child", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Failed_requires_a_non_blank_reason()
    {
        var act = () => NodePortResolution.Failed("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolved_reports_Found_carries_the_reference_it_answered_for_and_no_failure_reason()
    {
        var resolution = NodePortResolution.Resolved("child", CanvasSignature.Empty);

        resolution.Found.Should().BeTrue();
        resolution.Reference.Should().Be("child");
        resolution.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Failed_reports_not_Found_and_carries_no_signature()
    {
        var resolution = NodePortResolution.Failed("no such workflow");

        resolution.Found.Should().BeFalse();
        resolution.Signature.Should().BeNull();
    }

    [Fact]
    public void The_builder_refuses_the_structs_own_default_as_a_resolution()
    {
        var act = () => new NodePortEnvironmentBuilder().Add(CanvasPath.Root, "sub", default);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Empty_answers_nothing_and_gains_no_answers_once_a_builder_elsewhere_files_one()
    {
        _ = new NodePortEnvironmentBuilder().Add(CanvasPath.Root, "sub", NodePortResolution.Resolved(null, CanvasSignature.Empty)).Build();

        NodePortEnvironment.Empty.For(CanvasPath.Root, "sub").Should().BeNull();
    }
}
