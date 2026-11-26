using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class PortTypeMalformedTests
{
    private static readonly string[] StackedRules =
        [CompilationRules.PortTypeMismatch, CompilationRules.PortTypeUnresolved, CompilationRules.WirePortUnknown];

    private static readonly BlockShape ForEach = new(
        BlockTypeId: "foreach",
        Inputs: [new PortShape("Collection", PortValueType.OfDeclaration("Collection<$T>"), true)],
        Outputs: [new PortShape("Results", PortValueType.OfDeclaration("Collection<$R>"), false)],
        ConfigurationFields: [],
        Flags: new BlockFlags(false, false, false, true, false),
        SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["Body"] =
            [
                new PortShape("Index", PortValueType.Int, false),
                new PortShape("Item", PortValueType.OfDeclaration("$T"), false),
            ],
        },
        SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>
        {
            ["Body"] = [new PortShape("Result", PortValueType.OfDeclaration("$R"), false)],
        },
        Source: null,
        SubCanvasNames: ["Body"],
        TypeParameters: [new WorkflowTypeParameter("T"), new WorkflowTypeParameter("R")]);

    private static readonly PortValueType CollectionOfInt = PortValueType.Of("Collection<Int>");

    private static IBlockShapeSource DynamicCatalogue(PortValueType sinkInput) => Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("dyn.source", hasDynamicPorts: true, source: "plugin:dyn"),
        Shape("act", inputs: [("In", sinkInput, true)]));

    private static WorkflowDefinition DynamicSourceGraph(string rawEntryJson) => Graph(
        [StartNode(), Node("a", "dyn.source", new { OutputPorts = $"[{rawEntryJson}]" }), Node("b", "act")],
        [Edge("e1", "start", "a"), Edge("e2", "a", "b", ("Items", "In"))]);

    [Theory]
    [InlineData("""{"name":"Items"}""", PortTypeSyntaxFault.Absent)]
    [InlineData("""{"name":"Items","type":null}""", PortTypeSyntaxFault.Absent)]
    [InlineData("""{"name":"Items","type":""}""", PortTypeSyntaxFault.Empty)]
    [InlineData("""{"name":"Items","type":"CollectionInt"}""", PortTypeSyntaxFault.ReservedCollectionPrefix)]
    public void A_dynamic_output_with_a_malformed_type_is_refused_on_its_own_node_and_port(
        string rawEntryJson, PortTypeSyntaxFault fault)
    {
        var result = WorkflowCompiler.Compile(
            DynamicSourceGraph(rawEntryJson), DynamicCatalogue(PortValueType.Int), CanvasSignature.Empty);

        var malformed = result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeMalformed).Which;
        malformed.NodeId.Should().Be("a");
        malformed.PortName.Should().Be("Items");
        malformed.CanvasPath.Should().BeEmpty();
        malformed.BlockSource.Should().Be("plugin:dyn");
        malformed.Message.Should().Contain(FaultWording(fault));
        result.Diagnostics.Should().NotContain(d => StackedRules.Contains(d.RuleId));
    }

    [Theory]
    [InlineData("null", "/nodes/1/configuration/OutputPorts#/0")]
    [InlineData("5", "/nodes/1/configuration/OutputPorts#/0")]
    [InlineData("\"Int\"", "/nodes/1/configuration/OutputPorts#/0")]
    [InlineData("[]", "/nodes/1/configuration/OutputPorts#/0")]
    [InlineData("""{"name":7,"type":"Int"}""", "/nodes/1/configuration/OutputPorts#/0/name")]
    [InlineData("""{"name":null,"type":"Int"}""", "/nodes/1/configuration/OutputPorts#/0/name")]
    [InlineData("""{"type":"Int"}""", "/nodes/1/configuration/OutputPorts#/0/name")]
    [InlineData("""{"name":"Items","type":5}""", "/nodes/1/configuration/OutputPorts#/0/type")]
    public void A_port_list_entry_the_reader_refuses_makes_the_definition_unreadable_on_its_own(string rawEntryJson, string location)
    {
        var result = WorkflowCompiler.Compile(
            DynamicSourceGraph(rawEntryJson), DynamicCatalogue(PortValueType.Int), CanvasSignature.Empty);

        var refusal = result.Diagnostics.Should().ContainSingle().Which;
        refusal.RuleId.Should().Be(CompilationRules.DefinitionUnreadable);
        refusal.NodeId.Should().Be("a");
        refusal.Message.Should().Contain("rule L7").And.Contain($"at '{location}'");
    }

    [Fact]
    public void A_port_list_is_read_on_a_block_without_dynamic_ports_too()
    {
        var graph = Graph(
            [StartNode(), Node("a", "fixed", new { InputPorts = """[{"name":"In","type":"Int"}]""", OutputPorts = """[{"name":"Out"}]""" })],
            [Edge("e1", "start", "a")]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("fixed")), CanvasSignature.Empty);

        var malformed = result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeMalformed).Which;
        malformed.Message.Should().StartWith("Dynamic output port 'Out' of node 'a'");
        result.Diagnostics.Where(d => d.RuleId == CompilationRules.PortListWithoutDynamicPorts).Select(d => d.PortName)
            .Should().BeEquivalentTo(["InputPorts", "OutputPorts"]);
        result.Diagnostics.Should().HaveCount(3);
    }

    [Fact]
    public void A_port_list_on_a_block_without_dynamic_ports_adds_no_port_and_unresolves_none()
    {
        var graph = Graph(
            [StartNode(), Node("src", "ints"), Node("a", "fixed", new { InputPorts = """[{"name":"Extra","type":"Foo Bar"}]""" })],
            [Edge("e1", "start", "src"), Edge("e2", "src", "a", ("Out", "In"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ints", outputs: [("Out", PortValueType.Int)]),
            Shape("fixed", inputs: [("In", PortValueType.Boolean, false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        result.Diagnostics.Select(d => d.RuleId).Should().BeEquivalentTo(
            [CompilationRules.PortTypeMalformed, CompilationRules.PortListWithoutDynamicPorts, CompilationRules.PortTypeMismatch]);
    }

    [Fact]
    public void A_loop_fed_through_a_passthrough_of_a_malformed_port_stacks_nothing()
    {
        var p = Node("p", "pass");
        p.Passthrough = ["Items"];
        var body = Graph([StartNode("in"), Node("use", "use")], [Edge("b1", "in", "use", ("Item", "X"))]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph(
            [StartNode(), Node("a", "dyn", new { OutputPorts = """[{"name":"Items","type":"CollectionInt"}]""" }), p, loop],
            [Edge("e1", "start", "a"), Edge("e2", "a", "p", ("Items", "Items")), Edge("e3", "p", "loop", ("Items", "Collection"))]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("dyn", hasDynamicPorts: true),
            Shape("pass"), ForEach, Shape("use", inputs: [("X", PortValueType.Int, false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal((CompilationRules.PortTypeMalformed, "a", "Items"));
    }

    [Fact]
    public void A_malformed_port_forwarded_into_a_branch_by_passthrough_stacks_nothing()
    {
        var body = Graph([StartNode("bin"), Node("use", "use")], [Edge("b1", "bin", "use", ("Item", "X"))]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var then = Graph([StartNode("tin"), loop, Node("act", "act")],
            [Edge("t1", "tin", "loop", ("Items", "Collection")), Edge("t2", "tin", "act", ("Items", "In"))]);
        var container = Node("c", "container", passthrough: ["Items"]);
        container.SubCanvases = new() { ["Then"] = then };
        var graph = Graph(
            [StartNode(), Node("a", "dyn", new { OutputPorts = """[{"name":"Items","type":"CollectionInt"}]""" }), container],
            [Edge("e1", "start", "a"), Edge("e2", "a", "c", ("Items", "Items"))]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("dyn", hasDynamicPorts: true),
            Shape("container", subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] }, subCanvasNames: ["Then"]),
            ForEach, Shape("use", inputs: [("X", PortValueType.Int, false)]), Shape("act", inputs: [("In", PortValueType.Int, true)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal((CompilationRules.PortTypeMalformed, "a", "Items"));
    }

    [Fact]
    public void A_port_list_on_a_static_block_never_waives_a_mismatch()
    {
        var q = Node("q", "pass");
        q.Passthrough = ["Out"];
        var graph = Graph(
            [StartNode(), Node("p", "strings", new { OutputPorts = """[{"name":"x","type":"CollectionInt"}]""" }), q, Node("act", "act")],
            [Edge("e1", "start", "p"), Edge("e2", "p", "q", ("Out", "Out")), Edge("e3", "q", "act", ("Out", "In"))]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("strings", outputs: [("Out", PortValueType.String)]),
            Shape("pass"), Shape("act", inputs: [("In", PortValueType.Int, true)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().BeEquivalentTo(
        [
            (CompilationRules.PortTypeMalformed, "p", "x"), (CompilationRules.PortListWithoutDynamicPorts, "p", "OutputPorts"),
            (CompilationRules.PortTypeMismatch, "act", "In"),
        ]);
    }

    [Fact]
    public void A_concatenated_dynamic_port_spelling_names_the_bracketed_form_in_its_message()
    {
        var result = WorkflowCompiler.Compile(
            DynamicSourceGraph("""{"name":"Items","type":"CollectionInt"}"""), DynamicCatalogue(PortValueType.Int),
            CanvasSignature.Empty);

        result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeMalformed)
            .Which.Message.Should().Contain("write Collection<T> with the type of its elements");
    }

    [Fact]
    public void An_unclosed_dynamic_output_wired_into_a_collection_input_is_malformed_not_a_mismatch()
    {
        var result = WorkflowCompiler.Compile(
            DynamicSourceGraph("""{"name":"Items","type":"Collection<Int"}"""), DynamicCatalogue(CollectionOfInt),
            CanvasSignature.Empty);

        var malformed = result.Diagnostics.Should().ContainSingle().Which;
        malformed.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
        malformed.NodeId.Should().Be("a");
        malformed.PortName.Should().Be("Items");
        malformed.Message.Should().Contain(FaultWording(PortTypeSyntaxFault.MissingClose)).And.Contain("at 14");
    }

    [Fact]
    public void A_malformed_dynamic_input_is_refused_and_its_wire_is_not_type_checked()
    {
        var graph = Graph(
            [StartNode(), Node("src", "ints"), Node("b", "dyn.sink", new { InputPorts = """[{"name":"In","type":"Foo Bar"}]""" })],
            [Edge("e1", "start", "src"), Edge("e2", "src", "b", ("Out", "In"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape("ints", outputs: [("Out", PortValueType.Int)]),
            Shape("dyn.sink", hasDynamicPorts: true));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        var malformed = result.Diagnostics.Should().ContainSingle().Which;
        malformed.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
        malformed.NodeId.Should().Be("b");
        malformed.PortName.Should().Be("In");
        malformed.Message.Should().StartWith("Dynamic input port 'In'");
    }

    [Fact]
    public void A_malformed_dynamic_port_inside_a_sub_canvas_names_that_canvas()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
                Node("inner", "dyn.source", new { OutputPorts = """[{"name":"Items","type":"Pt "}]""" })],
            [Edge("be1", "in", "inner")]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph([StartNode(), loop],
            [LiteralEdge("e1", "start", "loop", "Collection", new List<object?> { 1L })]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach, Shape("dyn.source", hasDynamicPorts: true));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        var malformed = result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeMalformed).Which;
        malformed.NodeId.Should().Be("inner");
        malformed.CanvasPath.Should().Equal(CanvasPath.Root.Child("loop", "Body").Segments);
    }

    [Theory]
    [InlineData("Pt ", PortTypeSyntaxFault.IllegalCharacter)]
    [InlineData("", PortTypeSyntaxFault.Empty)]
    [InlineData(null, PortTypeSyntaxFault.Absent)]
    public void A_declared_output_with_a_malformed_type_is_refused_on_its_node_and_port(
        string? type, PortTypeSyntaxFault fault)
    {
        var branch = Node("branch", "container");
        branch.SubCanvases = new()
        {
            ["Then"] = Graph([Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)]),
        };
        branch.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "setpoint", Type = type, Sources = new() { ["Then"] = "raw" } }];
        var graph = Graph([StartNode(), branch], [Edge("e1", "start", "branch")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            Shape("container", supportsDeclaredOutputs: true, source: "plugin:c",
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasNames: ["Then"]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        var malformed = result.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.PortTypeMalformed).Which;
        malformed.NodeId.Should().Be("branch");
        malformed.PortName.Should().Be("setpoint");
        malformed.CanvasPath.Should().BeEmpty();
        malformed.Message.Should().StartWith("Declared output 'setpoint'").And.Contain(FaultWording(fault));
        result.Diagnostics.Should().NotContain(d => StackedRules.Contains(d.RuleId));
    }

    [Fact]
    public void A_declared_output_with_a_malformed_type_stacks_nothing_in_its_branch()
    {
        var then = Graph(
            [Node("calc", "compute"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("be1", "calc", "out", ("Value", "raw"))]);
        var branch = Node("branch", "container");
        branch.SubCanvases = new() { ["Then"] = then };
        branch.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "setpoint", Type = "Pt ", Sources = new() { ["Then"] = "raw" } }];
        var graph = Graph([StartNode(), branch], [Edge("e1", "start", "branch")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            Shape("compute", outputs: [("Value", PortValueType.Int)]),
            Shape("container", supportsDeclaredOutputs: true,
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] },
                subCanvasNames: ["Then"]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "branch", "setpoint"));
    }

    [Fact]
    public void A_declared_output_absent_from_the_stored_json_reads_as_Absent_not_Any()
    {
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(
            """{"nodes":[{"id":"branch","blockTypeId":"container","outputPorts":[{"name":"setpoint"}]}],"edges":[]}""",
            WorkflowGraphHelpers.JsonOptions)!;

        definition.Nodes.Single().OutputPorts!.Single().Type.Should().BeNull();
    }

    [Fact]
    public void A_root_input_parameter_with_no_type_is_refused_at_the_root_with_no_node()
    {
        CanvasSignature.TryRead("""[{"name":"level","isRequired":true}]""", null, out var signature, out var diagnostics)
            .Should().BeFalse();

        signature.Should().BeNull();
        var malformed = diagnostics.Should().ContainSingle().Which;
        malformed.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
        malformed.NodeId.Should().BeNull();
        malformed.PortName.Should().Be("level");
        malformed.CanvasPath.Should().BeEmpty();
        malformed.Message.Should().StartWith("Workflow input parameter 'level'")
            .And.Contain(FaultWording(PortTypeSyntaxFault.Absent));
    }

    [Fact]
    public void A_root_output_parameter_typed_bare_Collection_is_refused_with_the_element_type_hint()
    {
        CanvasSignature.TryRead(null, """[{"name":"all","type":"Collection"}]""", out var signature, out var diagnostics)
            .Should().BeFalse();

        signature.Should().BeNull();
        var malformed = diagnostics.Should().ContainSingle().Which;
        malformed.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
        malformed.NodeId.Should().BeNull();
        malformed.PortName.Should().Be("all");
        malformed.Message.Should().StartWith("Workflow output parameter 'all'").And.Contain("write Collection<T> with the type of its elements").And.NotContain("Collection<Any>");
    }

    [Fact]
    public void Every_malformed_parameter_is_reported_not_only_the_first()
    {
        CanvasSignature.TryRead(
            """[{"name":"a"},{"name":"ok","type":"Int"},{"name":"b","type":""}]""",
            """[{"name":"c","type":"Dictionary<K,V>"}]""", out _, out var diagnostics).Should().BeFalse();

        diagnostics.Select(d => (d.RuleId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a"), (CompilationRules.PortTypeMalformed, "b"),
            (CompilationRules.PortTypeMalformed, "c"));
        diagnostics[0].Message.Should().Contain(FaultWording(PortTypeSyntaxFault.Absent));
        diagnostics[2].Message.Should().Contain(FaultWording(PortTypeSyntaxFault.IllegalCharacter));
    }

    [Fact]
    public void A_parameter_type_that_is_not_a_string_makes_its_list_unreadable()
    {
        CanvasSignature.TryRead("""[{"name":"ok","type":"Int"},{"name":"a","type":5}]""", null, out var signature, out var diagnostics)
            .Should().BeFalse();

        signature.Should().BeNull();
        var refusal = diagnostics.Should().ContainSingle().Which;
        refusal.RuleId.Should().Be(CompilationRules.SignatureUnreadable);
        refusal.Message.Should().Contain("input parameters").And.Contain("rule L7").And.Contain("at '/1/type'");
    }

    [Fact]
    public async Task A_version_whose_own_input_parameter_has_no_type_gets_a_verdict_naming_it()
    {
        var world = new CompilationServiceTests.ServiceWorld();
        var version = CompilationServiceTests.Version(
            Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]), inputs: """[{"name":"level"}]""");

        var verdict = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        verdict.IsValid.Should().BeFalse();
        var malformed = verdict.Diagnostics.Should().ContainSingle().Which;
        malformed.RuleId.Should().Be(CompilationRules.PortTypeMalformed);
        malformed.NodeId.Should().BeNull();
        malformed.PortName.Should().Be("level");
    }

    [Fact]
    public async Task A_verdict_carries_every_malformed_parameter_not_only_the_first()
    {
        var world = new CompilationServiceTests.ServiceWorld();
        var version = CompilationServiceTests.Version(
            Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]),
            inputs: """[{"name":"level"},{"name":"zone","type":"CollectionRoom"}]""");

        var verdict = await world.Service.EnsureAsync(version, null, CancellationToken.None);

        verdict.Diagnostics.Select(d => (d.RuleId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "level"), (CompilationRules.PortTypeMalformed, "zone"));
    }

    [Fact]
    public void A_loop_fed_from_a_well_formed_output_of_a_node_with_a_malformed_declared_output_reports_only_that_node()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "act")],
            [Edge("be1", "in", "n1", ("Item", "In"))]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var branch = Node("branch", "container");
        branch.SubCanvases = new() { ["Then"] = Graph([Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)]) };
        branch.OutputPorts =
        [
            new SubCanvasOutputPortDefinition { Name = "bad", Type = "Pt " },
            new SubCanvasOutputPortDefinition { Name = "good", Type = "Collection<Int>" },
        ];
        var graph = Graph(
            [StartNode(), branch, loop],
            [Edge("e1", "start", "branch"), Edge("e2", "branch", "loop", ("good", "Collection"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId), ForEach,
            Shape("container", supportsDeclaredOutputs: true,
                subCanvasInputs: new Dictionary<string, (string, PortValueType, bool)[]> { ["Then"] = [] }, subCanvasNames: ["Then"]),
            Shape("act", inputs: [("In", PortValueType.Of("Device"), false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Device"));

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "branch", "bad"));
    }

    [Fact]
    public async Task A_child_workflow_whose_parameter_is_malformed_reaches_its_caller_as_node_ports_unresolved_naming_it()
    {
        var world = new CompilationServiceTests.ServiceWorld(extraShapes: [CompilationServiceTests.SubWorkflowShape()]);
        var childId = Guid.NewGuid();
        var childVersion = new WorkflowVersion(
            Guid.NewGuid(), childId, 1, "{}", """[{"name":"zone","type":"Foo Bar"}]""", "[]", Guid.NewGuid(),
            CompilationServiceTests.T0);
        var child = new Workflow(childId, "child", null, Guid.NewGuid(), CompilationServiceTests.T0);
        child.SetCurrentVersion(childVersion, CompilationServiceTests.T0);
        world.WorkflowStore.AddPublished(child);

        var graph = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = childId.ToString() })],
            [Edge("e1", "start", "call")]);

        var verdict = await world.Service.EnsureAsync(CompilationServiceTests.Version(graph), null, CancellationToken.None);

        verdict.IsValid.Should().BeFalse();
        var unresolved = verdict.Diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.NodePortsUnresolved).Which;
        unresolved.NodeId.Should().Be("call");
        unresolved.Message.Should().Contain("Workflow input parameter 'zone'").And.Contain("'Foo Bar'");
        verdict.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.PortTypeMalformed,
            "the child's own parameter is the child's finding; the caller reports only that it cannot read it");
    }

    [Fact]
    public void A_loop_fed_from_a_malformed_dynamic_output_reports_only_the_source()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "act")],
            [Edge("be1", "in", "n1", ("Item", "In"))]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = """[{"name":"Items","type":"CollectionDevice"}]""" }), loop],
            [Edge("e1", "start", "a"), Edge("e2", "a", "loop", ("Items", "Collection"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach,
            Shape("dyn.source", hasDynamicPorts: true),
            Shape("act", inputs: [("In", PortValueType.Of("Device"), false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Device"));

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a", "Items"));
    }

    [Fact]
    public void A_filter_fed_from_a_malformed_dynamic_output_reports_only_the_source()
    {
        var filter = new BlockShape(
            BlockTypeId: "filter",
            Inputs: [new PortShape("Collection", PortValueType.OfDeclaration("Collection<$T>"), true)],
            Outputs: [new PortShape("Filtered", PortValueType.OfDeclaration("Collection<$T>"), false)],
            ConfigurationFields: [],
            Flags: new BlockFlags(false, false, false, false, false),
            SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
            SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
            Source: null,
            TypeParameters: [new WorkflowTypeParameter("T")]);
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = """[{"name":"Items","type":"CollectionDevice"}]""" }),
                Node("f", "filter"), Node("s", "act")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "f", ("Items", "Collection")), Edge("e3", "f", "s", ("Filtered", "In"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), filter,
            Shape("dyn.source", hasDynamicPorts: true),
            Shape("act", inputs: [("In", PortValueType.Of("Collection<Device>"), false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Device"));

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a", "Items"));
    }

    [Fact]
    public void A_loop_fed_from_an_unresolved_host_node_reports_only_that_node()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "act")],
            [Edge("be1", "in", "n1", ("Item", "In"))]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph(
            [StartNode(), Node("a", "host.call"), loop],
            [Edge("e1", "start", "a"), Edge("e2", "a", "loop", ("Items", "Collection"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach,
            Shape("host.call", portsAreResolvedElsewhere: true),
            Shape("act", inputs: [("In", PortValueType.Of("Device"), false)]));

        var result = WorkflowCompiler.Compile(
            graph, catalogue, CanvasSignature.Empty, HostTypes("Device"), FailedEnvironment(CanvasPath.Root, "a", "no answer"));

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId)).Should().Equal((CompilationRules.NodePortsUnresolved, "a"));
    }

    private static readonly BlockShape Filter = new(
        BlockTypeId: "filter",
        Inputs: [new PortShape("Collection", PortValueType.OfDeclaration("Collection<$T>"), true)],
        Outputs: [new PortShape("Filtered", PortValueType.OfDeclaration("Collection<$T>"), false)],
        ConfigurationFields: [],
        Flags: new BlockFlags(false, false, false, false, false),
        SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
        SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
        Source: null,
        TypeParameters: [new WorkflowTypeParameter("T")]);

    private const string MalformedItems = """[{"name":"Items","type":"CollectionDevice"}]""";

    [Fact]
    public void A_port_traced_through_a_malformed_one_exists_by_name_with_no_type()
    {
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = MalformedItems }), Node("f", "filter")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "f", ("Items", "Collection"))]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Filter, Shape("dyn.source", hasDynamicPorts: true));

        var outputs = TypesOf(graph, catalogue, CanvasSignature.Empty, "f").Outputs!;

        outputs.Should().ContainSingle(p => p.Name == "Filtered").Which.State.Should().Be(PortState.Refused);
    }

    [Fact]
    public void A_passthrough_cannot_give_an_untyped_port_of_the_same_name_a_type()
    {
        var f = Node("f", "filter");
        f.Passthrough = ["Filtered"];
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = MalformedItems }), Node("src", "ints"), f],
            [Edge("e1", "start", "a"), Edge("e0", "start", "src"), Edge("e2", "a", "f", ("Items", "Collection")),
                Edge("e3", "src", "f", ("Out", "Filtered"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Filter, Shape("dyn.source", hasDynamicPorts: true),
            Shape("ints", outputs: [("Out", PortValueType.Int)]));

        var outputs = TypesOf(graph, catalogue, CanvasSignature.Empty, "f").Outputs!;

        outputs.Should().ContainSingle(p => p.Name == "Filtered").Which.State.Should().Be(PortState.Refused);
    }

    [Fact]
    public void An_expression_naming_a_port_traced_through_a_malformed_one_stacks_nothing()
    {
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = MalformedItems }), Node("f", "filter"),
                Node("s", "notify", new { Message = "expr:${previous.f.Filtered}" })],
            [Edge("e1", "start", "a"), Edge("e2", "a", "f", ("Items", "Collection")), Edge("e3", "f", "s")]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Filter, Shape("dyn.source", hasDynamicPorts: true),
            Shape("notify", configFields: [("Message", ConfigFieldType.String, false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a", "Items"));
    }

    [Fact]
    public void A_parameter_expression_in_a_body_whose_item_is_untyped_stacks_nothing()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId),
                Node("n1", "notify", new { Message = "expr:${parameter.Item}" })],
            [Edge("be1", "in", "n1")]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = MalformedItems }), loop],
            [Edge("e1", "start", "a"), Edge("e2", "a", "loop", ("Items", "Collection"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach, Shape("dyn.source", hasDynamicPorts: true),
            Shape("notify", configFields: [("Message", ConfigFieldType.String, false)]));

        var context = WorkflowCompiler.Run(graph, catalogue, CanvasSignature.Empty);

        context.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a", "Items"));
        var bodySignature = context.Signatures[CanvasPath.Root.Child("loop", "Body")];
        bodySignature.Inputs.Should().NotContain(p => p.Name == "Item");
        bodySignature.UntypedInputs.Should().Equal(new UntypedPort("Item", false));
    }

    [Fact]
    public void A_passthrough_traced_through_a_malformed_port_reaches_the_branch_untyped_and_stacks_nothing()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "act")],
            [Edge("be1", "in", "n1", ("Items", "In"))]);
        var loop = Node("loop", "foreach");
        loop.Passthrough = ["Items"];
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = MalformedItems }), loop],
            [Edge("e1", "start", "a"), Edge("e2", "a", "loop", ("Items", "Items")),
                LiteralEdge("e3", "start", "loop", "Collection", new List<object?> { 1L })]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach, Shape("dyn.source", hasDynamicPorts: true),
            Shape("act", inputs: [("In", PortValueType.Of("Device"), false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Device"));

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a", "Items"));
    }

    [Fact]
    public void A_malformed_stored_port_exists_untyped_on_its_own_node()
    {
        var node = Node("a", "dyn.source", new
        {
            InputPorts = """[{"name":"In","type":"Pt ","isRequired":true}]""",
            OutputPorts = MalformedItems,
        });
        node.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "setpoint", Type = "" }];
        var graph = Graph([StartNode(), node], [Edge("e1", "start", "a")]);
        var shape = Shape("dyn.source", hasDynamicPorts: true);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), shape);
        var types = TypesOf(graph, catalogue, CanvasSignature.Empty, "a");

        types.Inputs.Select(p => (p.Name, p.State, p.IsRequired)).Should().Equal(("In", PortState.Refused, true));
        types.Outputs!.Select(p => (p.Name, p.State)).Should().Equal(("Items", PortState.Refused), ("setpoint", PortState.Refused));
    }

    [Fact]
    public void A_loop_fed_through_a_filter_from_a_malformed_dynamic_output_reports_only_the_source()
    {
        var body = Graph(
            [Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId), Node("n1", "act")],
            [Edge("be1", "in", "n1", ("Item", "In"))]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = MalformedItems }), Node("f", "filter"), loop],
            [Edge("e1", "start", "a"), Edge("e2", "a", "f", ("Items", "Collection")), Edge("e3", "f", "loop", ("Filtered", "Collection"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach, Filter, Shape("dyn.source", hasDynamicPorts: true),
            Shape("act", inputs: [("In", PortValueType.Of("Device"), false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Device"));

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a", "Items"));
    }

    [Fact]
    public void A_passthrough_of_an_untyped_input_is_itself_untyped_and_stacks_nothing()
    {
        var pair = new BlockShape(
            BlockTypeId: "pair",
            Inputs:
            [
                new PortShape("A", PortValueType.OfDeclaration("Collection<$T>"), true),
                new PortShape("B", PortValueType.OfDeclaration("Collection<$T>"), false),
            ],
            Outputs: [],
            ConfigurationFields: [],
            Flags: new BlockFlags(false, false, false, false, false),
            SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
            SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
            Source: null,
            TypeParameters: [new WorkflowTypeParameter("T")]);
        var p = Node("p", "pair");
        p.Passthrough = ["B"];
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = MalformedItems }), p, Node("s", "act")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "p", ("Items", "A")), Edge("e3", "p", "s", ("B", "In"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), pair, Shape("dyn.source", hasDynamicPorts: true),
            Shape("act", inputs: [("In", PortValueType.Of("Device"), false)]));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty, HostTypes("Device"));

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a", "Items"));
    }

    [Fact]
    public void A_required_untyped_input_that_nothing_feeds_is_still_required_input_unfed()
    {
        var pair = new BlockShape(
            BlockTypeId: "pair",
            Inputs:
            [
                new PortShape("A", PortValueType.OfDeclaration("Collection<$T>"), true),
                new PortShape("B", PortValueType.OfDeclaration("Collection<$T>"), true),
            ],
            Outputs: [],
            ConfigurationFields: [],
            Flags: new BlockFlags(false, false, false, false, false),
            SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
            SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
            Source: null,
            TypeParameters: [new WorkflowTypeParameter("T")]);
        var graph = Graph(
            [StartNode(), Node("a", "dyn.source", new { OutputPorts = MalformedItems }), Node("p", "pair")],
            [Edge("e1", "start", "a"), Edge("e2", "a", "p", ("Items", "A"))]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), pair, Shape("dyn.source", hasDynamicPorts: true));

        var result = WorkflowCompiler.Compile(graph, catalogue, CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeMalformed, "a", "Items"),
            (CompilationRules.RequiredInputUnfed, "p", "B"));
    }

    [Fact]
    public void A_loop_whose_body_returns_a_collection_of_Any_is_refused_at_the_source_and_feeds_no_typed_collection()
    {
        var body = Graph(
            [Node("src", "bag"), Node("out", WorkflowGraphHelpers.WorkflowOutputBlockTypeId)],
            [Edge("be1", "src", "out", ("Items", "Result"))]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph(
            [StartNode(), loop, Node("sink", "ints")],
            [LiteralEdge("e1", "start", "loop", "Collection", new List<object?> { 1L }),
                Edge("e2", "loop", "sink", ("Results", "In"))]);
        var catalogue = Catalogue(
            Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
            Shape(WorkflowGraphHelpers.WorkflowOutputBlockTypeId),
            ForEach,
            Shape("bag", outputs: [("Items", PortValueType.Of("Collection<Any>"))]),
            Shape("ints", inputs: [("In", CollectionOfInt, true)]));

        var context = WorkflowCompiler.Run(graph, catalogue, CanvasSignature.Empty);

        context.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.PortTypeAlias, "src", "Items"));
    }

    [Fact]
    public void A_list_literal_of_empty_lists_binds_nothing_and_is_refused_at_the_literal()
    {
        var body = Graph([Node("in", WorkflowGraphHelpers.WorkflowInputBlockTypeId)]);
        var loop = Node("loop", "foreach");
        loop.SubCanvases = new() { ["Body"] = body };
        var graph = Graph(
            [StartNode(), loop],
            [LiteralEdge("e1", "start", "loop", "Collection", new List<object?> { new List<object?>(), new List<object?>() })]);
        var catalogue = Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), ForEach);

        var context = WorkflowCompiler.Run(graph, catalogue, CanvasSignature.Empty);

        context.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName)).Should().Equal(
            (CompilationRules.TypeParameterUnbound, "loop", "Collection"));
    }

    private static string FaultWording(PortTypeSyntaxFault fault) =>
        new PortTypeSyntaxError(fault, 0, "x", null).Message["'x' is not a port type name: ".Length..^" at 0.".Length];
}
