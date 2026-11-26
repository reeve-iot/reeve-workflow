using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class ScopeVariableCompileTests
{
    private static readonly WorkflowTypeParameter T = new("T", []);

    private static PortValueType Decl(string term) => PortValueType.OfDeclaration(term);

    private static IBlockShapeSource Source() => Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("read", outputs: [("Ints", Decl("Collection<Int>")), ("Words", Decl("Collection<String>"))]),
        Shape("keep",
            inputs: [("Items", Decl("Collection<$T>"), false), ("Predicate", Decl("Expression<Boolean>"), true), ("Label", PortValueType.String, false)],
            typeParameters: [T],
            scopeVariables: new Dictionary<string, ExpressionScopeVariable[]> { ["Predicate"] = [new("item", "$T")] }),
        Shape("plain", inputs: [("Template", Decl("Expression<Boolean>"), true), ("Text", PortValueType.String, false)]),
        Shape("watch",
            inputs: [("Template", Decl("Expression<Boolean>"), true)],
            scopeVariables: new Dictionary<string, ExpressionScopeVariable[]> { ["Template"] = [new("value", "Int")] }));

    private static CompilationResult Compile(WorkflowNodeDefinition node, params (string Output, string Input)[] wires) =>
        WorkflowCompiler.Compile(
            Graph([StartNode(), Node("a", "read"), node], [Edge("e1", "start", "a"), Edge("e2", "a", node.Id, wires)]),
            Source(), CanvasSignature.Empty);

    private static string Describe(IReadOnlyList<CompilationDiagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => d.RuleId + ": " + d.Message));

    private static WorkflowNodeDefinition Keep(string predicate, string? label = null) =>
        Node("n", "keep", label is null
            ? new Dictionary<string, object?> { ["Predicate"] = predicate }
            : new Dictionary<string, object?> { ["Predicate"] = predicate, ["Label"] = label });

    private static IEnumerable<TypedRead> Reads(TypedExpression node) => node switch
    {
        TypedRead read => [read],
        TypedConversion conversion => Reads(conversion.Operand),
        TypedOperation operation => operation.Operands.SelectMany(Reads),
        _ => [],
    };

    [Fact]
    public void A_variable_is_typed_at_the_term_the_node_binds_its_parameter_to()
    {
        var result = Compile(Keep("expr:${item} > 2"), ("Ints", "Items"));

        result.Diagnostics.Should().BeEmpty(Describe(result.Diagnostics));
        var tree = result.Program!.Canvases[0].Nodes.Single(n => n.NodeId == "n").Expressions["Predicate"].Tree!;
        Reads(tree).Should().ContainSingle().Which.Should().Match<TypedRead>(r => r.Namespace == "item" && r.Term.ToString() == "Int");
    }

    [Fact]
    public void The_same_text_is_refused_where_the_parameter_binds_another_term()
    {
        Compile(Keep("expr:${item} > 2"), ("Words", "Items")).Diagnostics
            .Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionTypeMismatch && d.NodeId == "n");
        Compile(Keep("expr:${item} == \"x\""), ("Words", "Items")).Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void A_type_argument_binds_the_parameter_a_variable_names()
    {
        var node = Node("n", "keep", new Dictionary<string, object?> { ["Predicate"] = "expr:${item} > 2" });
        node.TypeArguments = new Dictionary<string, string> { ["T"] = "Number" };

        var diagnostics = Compile(node).Diagnostics;

        diagnostics.Should().BeEmpty(Describe(diagnostics));
    }

    [Fact]
    public void A_variable_whose_term_names_a_parameter_nothing_binds_is_type_parameter_unbound()
    {
        var diagnostics = Compile(Keep("expr:${item} > 2")).Diagnostics;

        diagnostics.Should().Contain(d => d.RuleId == CompilationRules.TypeParameterUnbound && d.NodeId == "n" && d.Message.Contains("item"),
            Describe(diagnostics));
    }

    [Fact]
    public void A_closed_variable_is_typed_at_its_declared_term()
    {
        Compile(Node("n", "watch", new Dictionary<string, object?> { ["Template"] = "expr:${value} > 2" })).Diagnostics.Should().BeEmpty();
        Compile(Node("n", "watch", new Dictionary<string, object?> { ["Template"] = "expr:${value} > \"a\"" })).Diagnostics
            .Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionTypeMismatch);
    }

    [Fact]
    public void A_member_after_a_variable_is_a_field_access_the_variable_is_read_whole()
    {
        Compile(Node("n", "watch", new Dictionary<string, object?> { ["Template"] = "expr:${value.x} > 2" })).Diagnostics
            .Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionFieldAccessInvalid);
    }

    [Fact]
    public void The_result_must_still_fit_the_expression_input_s_T()
    {
        Compile(Keep("expr:${item}"), ("Ints", "Items")).Diagnostics
            .Should().ContainSingle(d => d.RuleId == CompilationRules.ExpressionResultTypeMismatch && d.PortName == "Predicate");
    }

    [Fact]
    public void The_program_carries_each_variable_s_closed_type_by_input()
    {
        var node = Compile(Keep("expr:${item} > 2"), ("Ints", "Items")).Program!.Canvases[0].Nodes.Single(n => n.NodeId == "n");

        node.ScopeVariables["Predicate"]["item"].Term.ToString().Should().Be("Int");
        node.ScopeVariables.Should().NotContainKey("Label");
    }

    [Fact]
    public void A_variable_read_in_another_input_of_the_same_node_is_out_of_scope()
    {
        var diagnostics = Compile(Keep("expr:${item} > 2", label: "expr:${item}"), ("Ints", "Items")).Diagnostics;

        diagnostics.Should().ContainSingle(Describe(diagnostics)).Which.Should()
            .Match<CompilationDiagnostic>(d => d.RuleId == CompilationRules.ExpressionOutOfScope && d.NodeId == "n");
    }

    [Fact]
    public void A_variable_the_input_does_not_declare_is_out_of_scope_even_when_another_name_is_declared()
    {
        var diagnostics = Compile(Keep("expr:${value} > 2"), ("Ints", "Items")).Diagnostics;

        diagnostics.Should().ContainSingle(Describe(diagnostics)).Which.RuleId.Should().Be(CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_variable_read_in_an_Expression_input_that_declares_none_is_out_of_scope()
    {
        var diagnostics = Compile(Node("n", "plain", new Dictionary<string, object?> { ["Template"] = "expr:${item} > 2" })).Diagnostics;

        diagnostics.Should().ContainSingle(Describe(diagnostics)).Which.RuleId.Should().Be(CompilationRules.ExpressionOutOfScope);
    }

    [Fact]
    public void A_variable_read_in_the_input_of_another_node_is_out_of_scope()
    {
        var diagnostics = Compile(Node("n", "plain", new Dictionary<string, object?> { ["Text"] = "expr:${value}", ["Template"] = "expr:true" })).Diagnostics;

        diagnostics.Should().ContainSingle(Describe(diagnostics)).Which.RuleId.Should().Be(CompilationRules.ExpressionOutOfScope);
    }

    [Theory]
    [InlineData("${item}")]
    [InlineData("${value}")]
    [InlineData("expr:${item} > 1")]
    public async Task A_preview_has_no_scope_variables(string expression)
    {
        var result = await new ExpressionPreviewService().PreviewAsync(expression, "global", new ServiceCollection().BuildServiceProvider(), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Error!.RuleId.Should().Be(CompilationRules.ExpressionOutOfScope);
    }
}
