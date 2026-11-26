using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CanvasSignatureTests
{
    [Fact]
    public void A_wire_out_of_the_start_node_names_a_declared_parameter()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify")],
            [Edge("e1", "start", "n1", ("temperature", "Message"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            signature: Signature(inputs: [("temperature", PortValueType.String, true)]));

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_wire_out_of_the_start_node_naming_no_parameter_is_refused()
    {
        var graph = Graph(
            [StartNode(), Node("n1", "notify")],
            [Edge("e1", "start", "n1", ("humidity", "Message"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            signature: Signature(inputs: [("temperature", PortValueType.Number, true)]));

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.WirePortUnknown && d.PortName == "humidity");
    }

    [Fact]
    public void A_wire_into_the_end_node_from_a_declared_output_is_accepted()
    {
        var end = Node("end", WorkflowGraphHelpers.WorkflowOutputBlockTypeId);
        var graph = Graph(
            [StartNode(), Node("n1", "sensor"), end],
            [Edge("e1", "start", "n1"), Edge("e2", "n1", "end", ("Reading", "temperature"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            Shape("sensor", outputs: [("Reading", PortValueType.Number)])),
            signature: Signature(outputs: [("temperature", PortValueType.Number, true)]));

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void The_end_node_left_unfed_for_a_required_declared_output_is_refused()
    {
        var end = Node("end", WorkflowGraphHelpers.WorkflowOutputBlockTypeId);
        var graph = Graph([StartNode(), end], [Edge("e1", "start", "end")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId)),
            signature: Signature(outputs: [("temperature", PortValueType.Number, true)]));

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "end" && d.PortName == "temperature");
    }

    [Fact]
    public void A_passthrough_fed_directly_by_the_start_node_takes_the_declared_parameters_type()
    {
        var relay = Node("relay", "notify", passthrough: ["temperature"]);
        var graph = Graph(
            [StartNode(), relay, Node("n2", "log")],
            [Edge("e1", "start", "relay", ("temperature", "temperature")),
             Edge("e2", "relay", "n2", ("temperature", "Reading"))]);
        var shapes = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify"),
            Shape("log", inputs: [("Reading", PortValueType.Number, true)]));
        var signature = Signature(inputs: [("temperature", PortValueType.Number, true)]);

        var result = WorkflowCompiler.Compile(graph, shapes, signature: signature);

        result.Diagnostics.Should().BeEmpty();

        Terms(TypesOf(graph, shapes, signature, relay.Id).Outputs).Should().Contain("temperature", "Number");
    }

    [Fact]
    public void Text_that_will_not_parse_as_json_is_refused()
    {
        var ok = CanvasSignature.TryRead(
            "{ not an array", null, out var signature, out var diagnostics);

        ok.Should().BeFalse();
        signature.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.SignatureUnreadable);
    }

    [Fact]
    public void An_object_where_an_array_belongs_is_refused()
    {
        var ok = CanvasSignature.TryRead("{}", null, out var signature, out var diagnostics);

        ok.Should().BeFalse();
        signature.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.SignatureUnreadable);
    }

    [Fact]
    public void An_array_of_non_object_entries_is_refused()
    {
        var ok = CanvasSignature.TryRead("[1,2]", null, out var signature, out _);

        ok.Should().BeFalse();
        signature.Should().BeNull();
    }

    [Fact]
    public void A_bare_value_not_wrapped_in_an_array_is_refused()
    {
        var ok = CanvasSignature.TryRead("""{"name":"x"}""", null, out var signature, out _);

        ok.Should().BeFalse();
        signature.Should().BeNull();
    }

    [Fact]
    public void A_parameter_entry_with_no_name_is_refused_not_dropped()
    {
        var ok = CanvasSignature.TryRead(
            """[{"type":"Number","isRequired":true}]""", null, out var signature, out var diagnostics);

        ok.Should().BeFalse();
        signature.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.SignatureUnreadable);
    }

    [Fact]
    public void A_parameter_entry_with_an_empty_name_is_refused()
    {
        var ok = CanvasSignature.TryRead("""[{"name":""}]""", null, out var signature, out _);

        ok.Should().BeFalse();
        signature.Should().BeNull();
    }

    [Fact]
    public void A_missing_document_is_an_empty_signature_not_a_refusal()
    {
        var ok = CanvasSignature.TryRead(null, null, out var signature, out var diagnostics);

        ok.Should().BeTrue();
        diagnostics.Should().BeEmpty();
        signature.Should().NotBeNull();
        signature!.Inputs.Should().BeEmpty();
        signature.Outputs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData(" ")]
    public void A_json_null_literal_or_blank_text_is_a_refusal_not_an_empty_signature(string text)
    {
        var ok = CanvasSignature.TryRead(text, null, out var signature, out var diagnostics);

        ok.Should().BeFalse();
        signature.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.SignatureUnreadable);
    }

    [Fact]
    public void A_parameter_entry_with_no_type_reads_the_raw_type_as_null_not_Any()
    {
        var parameters = StoredWorkflowDocument.ReadParameters("""[{"name":"note"}]""", PortTypeNameSite.InputParameter, out var fault);

        fault.Should().BeNull();
        parameters.Should().ContainSingle().Which.Should().Match<StoredPortTypeSite>(p =>
            p.PortName == "note" && p.RawType == null && !p.IsRequired && p.Location == "/0/type");
    }

    [Fact]
    public void A_parameter_entry_missing_type_is_port_type_malformed_not_Any()
    {
        var ok = CanvasSignature.TryRead("""[{"name":"note"}]""", null, out var signature, out var diagnostics);

        ok.Should().BeFalse();
        signature.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.PortTypeMalformed && d.NodeId == null && d.PortName == "note");
    }

    [Fact]
    public void A_well_formed_parameter_carries_its_declared_type_and_isRequired_through_to_a_port()
    {
        var ok = CanvasSignature.TryRead(
            """[{"name":"temperature","type":"Number","isRequired":true}]""",
            null, out var signature, out var diagnostics);

        ok.Should().BeTrue();
        diagnostics.Should().BeEmpty();
        var port = signature!.Inputs.Should().ContainSingle().Which;
        port.Name.Should().Be("temperature");
        port.Type.Should().Be(PortValueType.Number);
        port.IsRequired.Should().BeTrue();
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void A_parameter_isRequired_that_is_not_a_boolean_makes_its_list_unreadable(string flag)
    {
        CanvasSignature.TryRead(null, $$"""[{"name":"a","type":"Int","isRequired":{{flag}}}]""", out var signature, out var diagnostics)
            .Should().BeFalse();

        signature.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Message.Should().Contain("output parameters").And.Contain("rule L7, at '/0/isRequired'");
    }

    [Fact]
    public void A_parameter_is_required_only_when_isRequired_is_true()
    {
        CanvasSignature.TryRead(null, """[{"name":"a","type":"Int","isRequired":false},{"name":"b","type":"Int"}]""", out var signature, out _)
            .Should().BeTrue();

        signature!.Outputs.Select(p => p.IsRequired).Should().Equal(false, false);
    }

    [Fact]
    public void A_wire_out_of_the_start_node_in_a_sub_canvas_is_checked_against_the_branchs_declared_inputs()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "notify")],
            [Edge("be1", "in", "n1", ("Current", "Message"))]);
        var loop = Node("loop", "ForLoop");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ForLoop", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [("Index", PortValueType.Int, false), ("Current", PortValueType.String, false)],
            }, subCanvasNames: ["Body"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_wire_out_of_the_start_node_in_a_sub_canvas_naming_no_declared_branch_input_is_refused()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "notify")],
            [Edge("be1", "in", "n1", ("Collection", "Message"))]);
        var loop = Node("loop", "ForLoop");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ForLoop", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [("Index", PortValueType.Int, false), ("Current", PortValueType.Number, false)],
            }, subCanvasNames: ["Body"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.WirePortUnknown
            && d.PortName == "Collection" && d.CanvasPath.Contains(new CanvasPathSegment("loop", "Body")));
    }

    [Fact]
    public void A_while_loop_body_can_feed_its_declared_Continue_output()
    {
        var body = Graph(
            [Node("cond", "condition"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("be1", "cond", "out", ("Value", "Continue"))]);
        var loop = Node("loop", "WhileLoop");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            Shape("WhileLoop", subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [("Continue", PortValueType.Boolean, false)],
            }, subCanvasNames: ["Body"]),
            Shape("condition", outputs: [("Value", PortValueType.Boolean)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void An_if_else_branch_can_feed_the_raw_key_its_parents_declared_output_projects()
    {
        var then = Graph(
            [Node("calc", "compute"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("be1", "calc", "out", ("Value", "raw"))]);
        var branch = Node("branch", "IfElse");
        branch.SubCanvases = new() { ["Then"] = then, ["Else"] = Graph([]) };
        branch.OutputPorts = [new SubCanvasOutputPortDefinition
        {
            Name = "setpoint", Type = "Number", Sources = new() { ["Then"] = "raw" },
        }];

        var graph = Graph([StartNode(), branch], [Edge("e1", "start", "branch")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            Shape("IfElse", supportsDeclaredOutputs: true,
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [], ["Else"] = [] },
                subCanvasNames: ["Then", "Else"]),
            Shape("compute", outputs: [("Value", PortValueType.Number)])), CanvasSignature.Empty);

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_projected_branch_output_carries_the_parent_ports_declared_type_not_Any()
    {
        var branch = Node("branch", "IfElse");
        branch.OutputPorts = [new SubCanvasOutputPortDefinition
        {
            Name = "setpoint", Type = "Number", Sources = new() { ["Then"] = "raw" },
        }];

        var signature = CanvasSignature.ForSubCanvas(branch, Shape("IfElse"), "Then", forwarded: []);

        signature.Outputs.Should().ContainSingle(p => p.Name == "raw").Which.Type.Should().Be(PortValueType.Number);
    }

    [Fact]
    public void A_projected_branch_output_with_no_declared_type_is_declined_not_minted_Any()
    {
        var branch = Node("branch", "IfElse");
        branch.OutputPorts = [new SubCanvasOutputPortDefinition
        {
            Name = "setpoint", Type = "", Sources = new() { ["Then"] = "raw" },
        }];

        var signature = CanvasSignature.ForSubCanvas(branch, Shape("IfElse"), "Then", forwarded: []);

        signature.Outputs.Should().NotContain(p => p.Name == "raw");
    }

    [Fact]
    public void Two_declared_outputs_projecting_the_same_raw_key_do_not_duplicate_the_branchs_port()
    {
        var branch = Node("branch", "IfElse");
        branch.OutputPorts =
        [
            new SubCanvasOutputPortDefinition { Name = "a", Type = "Number", Sources = new() { ["Then"] = "raw" } },
            new SubCanvasOutputPortDefinition { Name = "b", Type = "String", Sources = new() { ["Then"] = "raw" } },
        ];

        var signature = CanvasSignature.ForSubCanvas(branch, Shape("IfElse"), "Then", forwarded: []);

        signature.Outputs.Should().ContainSingle(p => p.Name == "raw");
    }

    [Fact]
    public void A_projected_raw_key_matching_a_declared_branch_output_does_not_duplicate_it()
    {
        var branch = Node("branch", "IfElse");
        branch.OutputPorts = [new SubCanvasOutputPortDefinition
        {
            Name = "setpoint", Type = "String", Sources = new() { ["Then"] = "raw" },
        }];
        var shape = Shape("IfElse", subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]>
        {
            ["Then"] = [("raw", PortValueType.Number, false)],
        }, subCanvasNames: ["Then"]);

        var signature = CanvasSignature.ForSubCanvas(branch, shape, "Then", forwarded: []);

        signature.Outputs.Should().ContainSingle(p => p.Name == "raw")
            .Which.Type.Should().Be(PortValueType.Number);
    }

    [Fact]
    public void ForSubCanvas_projects_the_raw_key_Sources_declares_for_that_branch_not_another_ones()
    {
        var branch = Node("branch", "IfElse");
        branch.OutputPorts = [new SubCanvasOutputPortDefinition
        {
            Name = "setpoint", Type = "Number", Sources = new() { ["Then"] = "thenRaw", ["Else"] = "elseRaw" },
        }];

        var signature = CanvasSignature.ForSubCanvas(branch, Shape("IfElse"), "Else", forwarded: []);

        signature.Outputs.Should().ContainSingle().Which.Name.Should().Be("elseRaw");
    }

    [Fact]
    public void A_forwarded_name_colliding_with_a_declared_input_loses_to_the_declared_one()
    {
        var branch = Node("branch", "ForLoop");
        var shape = Shape("ForLoop", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
        {
            ["Body"] = [("Current", PortValueType.Number, true)],
        }, subCanvasNames: ["Body"]);

        var signature = CanvasSignature.ForSubCanvas(
            branch, shape, "Body", forwarded: [new PortShape("Current", PortValueType.String, false)]);

        signature.Inputs.Should().ContainSingle(p => p.Name == "Current")
            .Which.Should().Match<PortShape>(p => p.Type == PortValueType.Number && p.IsRequired);
    }

    [Fact]
    public void A_branchs_start_node_offers_the_containers_own_forwarded_passthrough_name()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "notify")],
            [Edge("be1", "in", "n1", ("note", "Message"))]);
        var container = Node("container", "relay", passthrough: ["note"]);
        container.SubCanvases = new() { ["Body"] = body };

        var graph = Graph(
            [StartNode(), container],
            [Edge("e1", "start", "container", ("note", "note"))]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("relay", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])),
            signature: Signature(inputs: [("note", PortValueType.String, true)]));

        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_branch_does_not_offer_a_name_the_container_never_forwards_even_though_the_run_delivers_it()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "notify")],
            [Edge("be1", "in", "n1", ("Condition", "Message"))]);
        var container = Node("branch", "IfElse");
        container.SubCanvases = new() { ["Then"] = body, ["Else"] = Graph([]) };

        var graph = Graph(
            [StartNode(), container], [LiteralEdge("e1", "start", "container", "Condition", true)]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("IfElse", inputs: [("Condition", PortValueType.Boolean, true)],
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [], ["Else"] = [] },
                subCanvasNames: ["Then", "Else"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.WirePortUnknown
            && d.PortName == "Condition" && d.CanvasPath.Contains(new CanvasPathSegment("branch", "Then")));
    }

    [Fact]
    public void A_node_naming_a_sub_canvas_its_block_never_declared_gets_its_own_rule_and_nothing_else()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "notify")],
            [Edge("be1", "in", "n1", ("anything", "Message"))]);
        var loop = Node("loop", "ForLoop");
        loop.SubCanvases = new() { ["Wrong"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ForLoop", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [("Index", PortValueType.Int, false)],
            }, subCanvasNames: ["Body"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.SubCanvasNameUnknown && d.NodeId == "loop");
    }

    [Fact]
    public void A_sub_canvas_name_differing_only_by_case_from_a_declared_one_is_still_unknown()
    {
        var body = Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId)]);
        var loop = Node("loop", "ForLoop");
        loop.SubCanvases = new() { ["body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ForLoop", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]>
            {
                ["Body"] = [],
            }, subCanvasNames: ["Body"])), CanvasSignature.Empty);

        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.SubCanvasNameUnknown && d.NodeId == "loop");
    }

    [Fact]
    public void An_unresolved_frame_gates_its_own_children_two_levels_down_too()
    {
        var body2 = Graph(
            [Node("in2", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n2", "notify")],
            [Edge("be2", "in2", "n2", ("forwarded", "Message"))]);
        var inner = Node("inner", "ForLoop", passthrough: ["forwarded"]);
        inner.SubCanvases = new() { ["Body"] = body2 };

        var body1 = Graph(
            [Node("in1", WorkflowGraphHelpers.WorkflowInputBlockTypeId), inner],
            [Edge("be1", "in1", "inner", ("forwarded", "forwarded"))]);
        var outer = Node("outer", "gone.block");
        outer.SubCanvases = new() { ["Body"] = body1 };

        var graph = Graph([StartNode(), outer], [Edge("e1", "start", "outer")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ForLoop", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.BlockTypeUnknown && d.NodeId == "outer");
    }

    [Fact]
    public void A_branch_of_an_unbound_container_reports_only_the_containers_own_refusal()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "notify")],
            [Edge("be1", "in", "n1", ("anything", "Message"))]);
        var loop = Node("loop", "gone.block");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("notify", inputs: [("Message", PortValueType.String, true)])), CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Match<CompilationDiagnostic>(d =>
                d.RuleId == CompilationRules.BlockTypeUnknown && d.NodeId == "loop");
    }

    [Fact]
    public void A_bound_containers_body_reports_all_four_port_refusals_plus_its_two_wire_ones()
    {
        var node = Node("n1", "widget");
        node.OutputPorts = [new() { Name = "Value", Type = "String" }];
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), node],
            [Edge("be1", "in", "n1", ("Nope", "NotAPort"))]);
        var loop = Node("loop", "ForLoop");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ForLoop", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Body"] = [] }, subCanvasNames: ["Body"]),
            Shape("widget",
                inputs: [("Message", PortValueType.String, true)],
                outputs: [("Value", PortValueType.String)],
                configFields: [("ApiKey", ConfigFieldType.String, true)],
                supportsDeclaredOutputs: true)), CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(5);
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredInputUnfed && d.NodeId == "n1" && d.PortName == "Message");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredConfigMissing && d.NodeId == "n1" && d.PortName == "ApiKey");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.DeclaredOutputShadowsStatic && d.NodeId == "n1" && d.PortName == "Value");
        result.Diagnostics.Where(d => d.RuleId == CompilationRules.WirePortUnknown).Should().HaveCount(2);
    }

    [Fact]
    public void An_unresolved_frame_still_reports_its_signature_independent_refusals_but_not_its_wire_ones()
    {
        var node = Node("n1", "widget");
        node.OutputPorts = [new() { Name = "Value", Type = "String" }];
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), node],
            [Edge("be1", "in", "n1", ("Nope", "NotAPort"))]);
        var loop = Node("loop", "gone.block");
        loop.SubCanvases = new() { ["Body"] = body };

        var graph = Graph([StartNode(), loop], [Edge("e1", "start", "loop")]);

        var result = WorkflowCompiler.Compile(graph, Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("widget",
                inputs: [("Message", PortValueType.String, true)],
                outputs: [("Value", PortValueType.String)],
                configFields: [("ApiKey", ConfigFieldType.String, true)],
                supportsDeclaredOutputs: true)), CanvasSignature.Empty);

        result.Diagnostics.Should().HaveCount(3);
        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown && d.NodeId == "loop");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.RequiredConfigMissing && d.NodeId == "n1" && d.PortName == "ApiKey");
        result.Diagnostics.Should().Contain(d =>
            d.RuleId == CompilationRules.DeclaredOutputShadowsStatic && d.NodeId == "n1" && d.PortName == "Value");
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.RequiredInputUnfed);
        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.WirePortUnknown);
    }

    [Fact]
    public void A_stored_lone_surrogate_escape_is_refused_not_thrown()
    {
        CanvasSignature.TryRead("""[{"name":"x","type":"\ud800"}]""", null, out var signature, out var diagnostics)
            .Should().BeFalse();

        signature.Should().BeNull();
        diagnostics.Should().ContainSingle().Which.Message.Should().Contain("rule L4, at '/0/type'");
    }

    [Fact]
    public void Mutating_a_callers_list_after_construction_does_not_change_the_signature()
    {
        var callerOwned = new List<PortShape> { new("a", PortValueType.String, true) };
        var signature = new CanvasSignature(callerOwned, []);

        callerOwned.Add(new PortShape("b", PortValueType.Number, false));

        signature.Inputs.Should().HaveCount(1).And.OnlyContain(p => p.Name == "a");
    }

    [Fact]
    public void A_with_expressions_new_list_is_snapshotted_the_same_way_the_constructor_is()
    {
        var callerOwned = new List<PortShape> { new("a", PortValueType.String, true) };
        var updated = CanvasSignature.Empty with { Inputs = callerOwned };

        callerOwned.Add(new PortShape("b", PortValueType.Number, false));

        updated.Inputs.Should().HaveCount(1).And.OnlyContain(p => p.Name == "a");
    }

    [Fact]
    public void Mutating_a_callers_output_list_after_construction_does_not_change_the_signature()
    {
        var callerOwned = new List<PortShape> { new("a", PortValueType.String, true) };
        var signature = new CanvasSignature([], callerOwned);

        callerOwned.Add(new PortShape("b", PortValueType.Number, false));

        signature.Outputs.Should().HaveCount(1).And.OnlyContain(p => p.Name == "a");
    }
}
