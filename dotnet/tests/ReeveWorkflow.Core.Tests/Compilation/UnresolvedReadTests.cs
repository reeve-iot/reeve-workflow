using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class UnresolvedReadTests
{
    private static ExpressionTypingResult Type(string expression, Func<string, string, ReadLookup> previous) =>
        ExpressionTyper.Type(expression, new ExpressionTypingScope.Builder().Previous(previous).Build());

    [Fact]
    public void A_read_of_a_declaration_a_rule_reports_leaves_no_tree_and_no_refusal()
    {
        var result = Type("${previous.n.o} * true", (_, _) => ReadLookup.Unresolved);

        result.IsTyped.Should().BeFalse();
        result.Refusals.Should().BeEmpty();
    }

    [Fact]
    public void An_expression_with_an_unresolved_read_still_reports_what_it_can_check_elsewhere()
    {
        var result = Type("${previous.n.o} * true + len(5 > \"a\")", (_, _) => ReadLookup.Unresolved);

        result.IsTyped.Should().BeFalse();
        result.Refusals.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.ExpressionTypeMismatch);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("Collection<Any>")]
    [InlineData("Collection<Collection<Any>>")]
    public void A_type_holding_Any_is_a_reserved_name_and_its_read_is_unresolved(string declared)
    {
        ExpressionResolution.Lookup(PortValueType.Of(declared), optional: false, WorkflowTypeVocabulary.Builtin)
            .Kind.Should().Be(ReadLookupKind.Unresolved);
    }

    [Theory]
    [InlineData("Collection<Int>", "Typed")]
    [InlineData("Gadget", "Unresolved")]
    [InlineData("Float", "Unresolved")]
    [InlineData("int", "Unresolved")]
    public void Any_other_type_is_typed_or_one_a_rule_reports(string declared, string kind)
    {
        ExpressionResolution.Lookup(PortValueType.Of(declared), optional: false, WorkflowTypeVocabulary.Builtin).Kind.ToString().Should().Be(kind);
    }

    [Fact]
    public void A_blank_type_is_unresolved_and_not_the_interim()
    {
        ExpressionResolution.Lookup(default, optional: false, WorkflowTypeVocabulary.Builtin).Kind.ToString().Should().Be("Unresolved");
    }

    private static IReadOnlyList<CompilationDiagnostic> Compile(string expression, WorkflowNodeDefinition source, params BlockShape[] shapes)
    {
        var graph = Graph(
            [StartNode(), source, Node("n1", "notify", new { Message = "expr:" + expression })],
            [Edge("e1", "start", source.Id), Edge("e2", source.Id, "n1")]);
        return WorkflowCompiler.Compile(
            graph,
            Catalogue([Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), Shape("notify", inputs: [("Message", PortValueType.String, true)]), .. shapes]),
            CanvasSignature.Empty).Diagnostics;
    }

    private static WorkflowNodeDefinition Dynamic(string portJson) => Node("a", "dyn.source", new { OutputPorts = $"[{portJson}]" });

    private static readonly BlockShape DynamicSource = Shape("dyn.source", hasDynamicPorts: true, source: "plugin:dyn");

    [Theory]
    [InlineData("""{"name":"o","type":"CollectionInt"}""", CompilationRules.PortTypeMalformed)]
    [InlineData("""{"name":"o","type":"Collection<"}""", CompilationRules.PortTypeMalformed)]
    [InlineData("""{"name":"o","type":"Gadget"}""", CompilationRules.PortTypeUnregistered)]
    [InlineData("""{"name":"o","type":"Float"}""", CompilationRules.PortTypeAlias)]
    [InlineData("""{"name":"o","type":"Any"}""", CompilationRules.PortTypeAlias)]
    [InlineData("""{"name":"o","type":"Collection<Any>"}""", CompilationRules.PortTypeAlias)]
    public void A_dynamic_port_the_vocabulary_does_not_spell_is_reported_at_its_declaration_and_the_read_adds_nothing(string port, string rule)
    {
        var diagnostics = Compile("${previous.a.o} * true", Dynamic(port), DynamicSource);

        diagnostics.Should().Contain(d => d.RuleId == rule && d.NodeId == "a");
        diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_node_whose_ports_never_resolved_is_reported_once_and_the_read_adds_nothing()
    {
        var sub = Node("a", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = "missing" });
        var diagnostics = Compile(
            "${previous.a.Result} * true", sub,
            Shape(WorkflowGraphHelpers.SubWorkflowBlockTypeId, portsAreResolvedElsewhere: true, runsWorkflowFrom: "WorkflowId",
                inputs: [("WorkflowId", PortValueType.String, true)]));

        diagnostics.Should().ContainSingle(d => d.RuleId == CompilationRules.NodePortsUnresolved);
        diagnostics.Should().NotContain(d => d.RuleId.StartsWith("expression-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_read_of_an_unbound_port_is_refused_with_type_parameter_unbound()
    {
        var shape = new BlockShape(
            BlockTypeId: "pick",
            Inputs: [],
            Outputs: [new PortShape("Out", PortValueType.OfDeclaration("$T"), false)],
            ConfigurationFields: [],
            Flags: new BlockFlags(false, false, false, false, false),
            SubCanvasInputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
            SubCanvasOutputs: new Dictionary<string, IReadOnlyList<PortShape>>(),
            Source: null,
            TypeParameters: [new WorkflowTypeParameter("T")]);

        var diagnostics = Compile("${previous.a.Out} * true", Node("a", "pick"), shape);

        diagnostics.Should().ContainSingle().Which.RuleId.Should().Be(CompilationRules.TypeParameterUnbound);
    }

    [Fact]
    public void A_read_of_a_port_declared_Any_is_refused_where_it_is_declared()
    {
        var shape = Shape("calc", outputs: [("Result", PortValueType.Of("Any")), ("Rows", PortValueType.Of("Collection<Any>"))]);
        (string, string?, string?)[] declared = [(CompilationRules.PortTypeAlias, "a", "Result"), (CompilationRules.PortTypeAlias, "a", "Rows")];

        Compile("${previous.a.Result} * true", Node("a", "calc"), shape).Select(d => (d.RuleId, d.NodeId, d.PortName))
            .Should().BeEquivalentTo(declared);
        Compile("${previous.a.Rows} == null", Node("a", "calc"), shape).Select(d => (d.RuleId, d.NodeId, d.PortName))
            .Should().BeEquivalentTo(declared);
        Compile("${previous.a.Result} * true + len(5 > \"a\")", Node("a", "calc"), shape).Select(d => (d.RuleId, d.NodeId))
            .Should().BeEquivalentTo([(CompilationRules.PortTypeAlias, "a"), (CompilationRules.PortTypeAlias, "a"), (CompilationRules.ExpressionTypeMismatch, "n1")]);
    }
}
