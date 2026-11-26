using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class ExpressionResultTypeTests
{
    private static readonly WorkflowTypeParameter T = new("T", []);

    private static IReadOnlyList<CompilationDiagnostic> Compile(WorkflowNodeDefinition target, params WorkflowConnectionDefinition[] more) =>
        WorkflowCompiler.Compile(
            Graph([StartNode(), Node("a", "read"), target], [Edge("e1", "start", "a"), Edge("e2", "a", target.Id), .. more]),
            Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
                Shape("read", outputs: [("Temperature", PortValueType.Number), ("Count", PortValueType.Int)]),
                Shape("notify", inputs: [("Message", PortValueType.String, true)]),
                Shape("level", inputs: [("Value", PortValueType.Number, true)]),
                Shape("limit", configFields: [("Limit", ConfigFieldType.Int, false)]),
                Shape("wait", inputs: [("Template", PortValueType.OfDeclaration("Expression<Boolean>"), true)]),
                Shape("gather", inputs: [("Items", PortValueType.OfDeclaration("Collection<$T>"), true)], typeParameters: [T]),
                Shape("hold", inputs: [("Value", PortValueType.OfDeclaration("$T"), true)], typeParameters: [T])),
            CanvasSignature.Empty).Diagnostics;

    private static void RefusedAs(IReadOnlyList<CompilationDiagnostic> diagnostics, string rule, string port) =>
        diagnostics.Should().ContainSingle(string.Join("; ", diagnostics.Select(d => d.RuleId + ": " + d.Message)))
            .Which.Should().Match<CompilationDiagnostic>(d => d.RuleId == rule && d.NodeId == "n" && d.PortName == port);

    [Theory]
    [InlineData("notify", "Message", "expr:${previous.a.Count} + 1")]
    [InlineData("level", "Value", "expr:\"warm\"")]
    [InlineData("level", "Value", "expr:${previous.a.Temperature} > 20")]
    public void A_result_its_input_cannot_take_is_refused(string block, string input, string expression) =>
        RefusedAs(Compile(Node("n", block, new Dictionary<string, object?> { [input] = expression })),
            CompilationRules.ExpressionResultTypeMismatch, input);

    [Theory]
    [InlineData("notify", "Message", "expr:toString(${previous.a.Count})")]
    [InlineData("level", "Value", "expr:${previous.a.Temperature}")]
    [InlineData("level", "Value", "expr:${previous.a.Count} + 1")]
    public void A_result_assignable_to_its_input_compiles(string block, string input, string expression) =>
        Compile(Node("n", block, new Dictionary<string, object?> { [input] = expression })).Should().BeEmpty();

    [Fact]
    public void A_result_a_configuration_field_cannot_take_is_refused()
    {
        RefusedAs(Compile(Node("n", "limit", new { Limit = "expr:\"ten\"" })), CompilationRules.ExpressionResultTypeMismatch, "Limit");
        Compile(Node("n", "limit", new { Limit = "expr:${previous.a.Count} * 2" })).Should().BeEmpty();
    }

    [Theory]
    [InlineData("expr:\"warm\"", CompilationRules.ExpressionResultTypeMismatch)]
    [InlineData("expr:${previous.a.Count} > 2", CompilationRules.ExpressionResultTypeMismatch)]
    [InlineData("expr:${previous.a.Count} + 1", null)]
    [InlineData("expr:${previous.a.Temperature} * 2", null)]
    public void An_expr_configuration_value_on_a_wire_fed_passthrough_name_is_graded_against_the_wires_term(string value, string? rule)
    {
        var target = Node("n", "log", new { Temperature = value }, passthrough: ["Temperature"]);
        var diagnostics = WorkflowCompiler.Compile(
            Graph([StartNode(), Node("a", "read"), target], [Edge("e1", "start", "a"), Edge("e2", "a", "n", ("Temperature", "Temperature"))]),
            Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
                Shape("read", outputs: [("Temperature", PortValueType.Number), ("Count", PortValueType.Int)]),
                Shape("log")),
            CanvasSignature.Empty).Diagnostics;

        if (rule is null)
            diagnostics.Should().BeEmpty();
        else
            RefusedAs(diagnostics, rule, "Temperature");
    }

    [Theory]
    [InlineData("expr:${previous.a.Temperature} > 20", null)]
    [InlineData("expr:${previous.a.Count}", CompilationRules.ExpressionResultTypeMismatch)]
    [InlineData("expr:${previous.a.Count} > 2", null)]
    [InlineData("hello", CompilationRules.LiteralPortValueInvalid)]
    [InlineData(true, CompilationRules.LiteralPortValueInvalid)]
    public void An_Expression_input_takes_only_expr_text_whose_result_fits_T(object value, string? rule)
    {
        var diagnostics = Compile(Node("n", "wait", new Dictionary<string, object?> { ["Template"] = value }));

        if (rule is null)
            diagnostics.Should().BeEmpty();
        else
            RefusedAs(diagnostics, rule, "Template");
    }

    [Fact]
    public void A_wire_into_an_Expression_input_is_a_port_type_mismatch_whatever_its_term()
    {
        var wait = Node("n", "wait");
        var diagnostics = WorkflowCompiler.Compile(
            Graph([StartNode(), Node("a", "read"), wait],
                [Edge("e1", "start", "a"), Edge("e2", "a", "n", ("Temperature", "Template"))]),
            Catalogue(
                Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
                Shape("read", outputs: [("Temperature", PortValueType.Boolean)]),
                Shape("wait", inputs: [("Template", PortValueType.OfDeclaration("Expression<Boolean>"), true)])),
            CanvasSignature.Empty).Diagnostics;

        RefusedAs(diagnostics, CompilationRules.PortTypeMismatch, "Template");
    }

    [Fact]
    public void A_result_that_fits_no_binding_of_a_parameterised_input_is_refused()
    {
        RefusedAs(Compile(Node("n", "gather", new { Items = "expr:\"x\"" })), CompilationRules.ExpressionResultTypeMismatch, "Items");
    }

    [Fact]
    public void A_null_result_on_a_parameterised_input_leaves_its_parameter_unbound()
    {
        RefusedAs(Compile(Node("n", "hold", new { Value = "expr:null" })), CompilationRules.TypeParameterUnbound, "Value");
    }
}
