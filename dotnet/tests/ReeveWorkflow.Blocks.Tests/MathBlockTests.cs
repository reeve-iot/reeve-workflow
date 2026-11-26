using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting.Defaults;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Blocks.Tests;

public sealed class MathBlockTests
{
    private const string Graph = """
        {"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},
        {"id":"math","blockTypeId":"Math","position":{"x":120,"y":0},"configuration":CONFIG}],
        "edges":[{"id":"e0","sourceNodeId":"start","targetNodeId":"math","mappings":{}}]}
        """;

    private static readonly RegisteredWorkflowBlocks Blocks = CompiledGraph.Blocks();

    private static WorkflowDefinition Math(object a, object b, string? op = null, bool emitError = false)
    {
        var config = new Dictionary<string, object?> { ["A"] = a, ["B"] = b };
        if (op is not null)
            config["Op"] = op;
        var definition = CompiledGraph.Read(Graph.Replace("CONFIG", System.Text.Json.JsonSerializer.Serialize(config)));
        if (emitError)
            definition.Nodes.Single(n => n.Id == "math").ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.EmitError);
        return definition;
    }

    private static async Task<object?> Result(object a, object b, string? op = null) =>
        (await CompiledGraph.RunAsync(Math(a, b, op), Blocks))["math"]["Result"];

    private static async Task<Dictionary<string, object?>> Failure(object a, object b, string op)
    {
        var outputs = await CompiledGraph.RunAsync(Math(a, b, op, emitError: true), Blocks);
        outputs["math"].Should().NotContainKey("Result");
        return (Dictionary<string, object?>)outputs["math"]["_error"]!;
    }

    [Fact]
    public async Task Int_with_Int_answers_a_Number()
    {
        (await Result(7, 3)).Should().BeOfType<double>().Which.Should().Be(10.0);
    }

    [Fact]
    public async Task Int_with_Number_binds_T_to_Number_and_answers_a_Number()
    {
        (await Result(7, 2.5)).Should().BeOfType<double>().Which.Should().Be(9.5);
    }

    [Theory]
    [InlineData("Div", 7, 2, 3.5)]
    [InlineData("Pow", 2, 10, 1024.0)]
    public async Task Div_and_Pow_on_Int_answer_a_Number(string op, long a, long b, double expected)
    {
        (await Result(a, b, op)).Should().BeOfType<double>().Which.Should().Be(expected);
    }

    [Theory]
    [InlineData("Mod", 7, 0)]
    [InlineData("Div", 7, 0)]
    [InlineData("Mod", 7.5, 0.0)]
    [InlineData("Div", 7.5, 0.0)]
    public async Task Division_or_modulo_by_zero_fails_the_node(string op, object a, object b)
    {
        var error = await Failure(a, b, op);

        error["code"].Should().Be(WorkflowErrorCodes.BlockFailed);
        error["exceptionType"].Should().Be(nameof(DivideByZeroException));
    }

    [Theory]
    [InlineData("Pow", 10, 400)]
    [InlineData("Mul", 1e200, 1e200)]
    [InlineData("Pow", -1.0, 0.5)]
    public async Task A_Number_result_that_is_not_finite_fails_the_node(string op, object a, object b)
    {
        var error = await Failure(a, b, op);

        error["exceptionType"].Should().Be(nameof(OverflowException));
    }

    [Fact]
    public void An_expression_in_Op_is_not_refused_at_compile()
    {
        CompiledGraph.Refusals(Math(7, 3, "expr:\"Add\""), Blocks).Should().BeEmpty();
    }

    [Theory]
    [InlineData("x", "y")]
    [InlineData(true, false)]
    public void A_non_numeric_operand_is_type_parameter_constraint(object a, object b)
    {
        CompiledGraph.Compile(Math(a, b), Blocks).Diagnostics.Select(d => (d.RuleId, d.NodeId))
            .Should().Equal(("type-parameter-constraint", "math"));
    }

    [Fact]
    public void Math_declares_T_constrained_Numeric_and_a_Number_result()
    {
        var factory = new MathBlockFactory();

        factory.TypeParameters.Should().BeEquivalentTo([new WorkflowTypeParameter("T", [TypeParameterConstraints.Numeric])]);
        factory.Inputs.Select(p => (p.Name, p.Type.Name, p.IsRequired)).Should().Equal(("A", "$T", true), ("B", "$T", true));
        factory.Outputs.Select(p => (p.Name, p.Type.Name)).Should().Equal(("Result", "Number"));
    }

    [Theory]
    [InlineData("Add")]
    [InlineData("Sub")]
    [InlineData("Mul")]
    [InlineData("Div")]
    [InlineData("Mod")]
    [InlineData("Pow")]
    [InlineData("Min")]
    [InlineData("Max")]
    public void Result_is_a_Number_for_every_Op(string op)
    {
        var result = CompiledGraph.Compile(Math(7, 3, op), Blocks);

        result.Diagnostics.Should().BeEmpty();
        result.Program!.Canvases.SelectMany(c => c.Nodes).Single(n => n.NodeId == "math")
            .OutputTypes["Result"].Term.ToString().Should().Be("Number");
    }
}
