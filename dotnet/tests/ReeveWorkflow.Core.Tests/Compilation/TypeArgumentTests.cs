using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public sealed class TypeArgumentTests
{
    private static readonly BlockShape Generic = Shape("generic", typeParameters: [new WorkflowTypeParameter("T", [])]);

    [Theory]
    [InlineData("Int<", CompilationRules.PortTypeMalformed)]
    [InlineData("int", CompilationRules.PortTypeAlias)]
    [InlineData("Unregistered", CompilationRules.PortTypeUnregistered)]
    [InlineData("Json", CompilationRules.PortTypeJsonPosition)]
    [InlineData("Collection<Json>", CompilationRules.PortTypeJsonPosition)]
    public void A_type_argument_is_graded_as_a_stored_type_site(string term, string expected)
    {
        var result = CompileWithArgument("T", term);

        result.Diagnostics.Should().Contain(d => d.RuleId == expected && d.NodeId == "n");
    }

    [Fact]
    public void An_argument_key_the_block_does_not_declare_is_refused()
    {
        var result = CompileWithArgument("Unknown", "Int");

        result.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.TypeArgumentUnknown && d.PortName == "Unknown");
    }

    [Fact]
    public void A_declared_argument_key_is_not_unknown()
    {
        var result = CompileWithArgument("T", "Int");

        result.Diagnostics.Should().NotContain(d => d.RuleId == CompilationRules.TypeArgumentUnknown);
    }

    private static CompilationResult CompileWithArgument(string name, string value)
    {
        var node = Node("n", "generic");
        node.TypeArguments = new Dictionary<string, string>(StringComparer.Ordinal) { [name] = value };
        return WorkflowCompiler.Compile(
            Graph([StartNode(), node], [Edge("e", "start", "n")]), Catalogue(Generic), CanvasSignature.Empty);
    }
}
