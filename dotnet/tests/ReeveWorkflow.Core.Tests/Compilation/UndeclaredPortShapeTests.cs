using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class UndeclaredPortShapeTests
{
    private const string PortList = """[{"name":"P","type":"String"}]""";

    private static IBlockShapeSource Blocks() => Catalogue(
        Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId),
        Shape("plain"),
        Shape("dynamic", hasDynamicPorts: true),
        Shape("branch", supportsDeclaredOutputs: true, subCanvasNames: ["Then", "Else"]));

    private static WorkflowNodeDefinition Declaring(WorkflowNodeDefinition node, Dictionary<string, string>? sources)
    {
        node.OutputPorts = [new SubCanvasOutputPortDefinition { Name = "Out", DisplayName = "Out", Type = "String", Sources = sources }];
        return node;
    }

    private static IReadOnlyList<CompilationDiagnostic> Compile(WorkflowNodeDefinition node) =>
        WorkflowCompiler.Compile(Graph([StartNode(), node], [Edge("e1", "start", node.Id)]), Blocks(), CanvasSignature.Empty).Diagnostics;

    [Theory]
    [InlineData("InputPorts")]
    [InlineData("OutputPorts")]
    public void A_port_list_on_a_block_without_dynamic_ports_is_refused_naming_the_key(string key)
    {
        var diagnostics = Compile(Node("n", "plain", new Dictionary<string, string> { [key] = PortList }));

        diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.PortListWithoutDynamicPorts && d.NodeId == "n" && d.PortName == key);
    }

    [Fact]
    public void The_same_port_list_on_a_block_with_dynamic_ports_is_not_refused()
    {
        Compile(Node("n", "dynamic", new { InputPorts = PortList, OutputPorts = PortList })).Should().BeEmpty();
    }

    [Fact]
    public void An_empty_port_list_on_a_block_without_dynamic_ports_holds_no_port_and_is_not_refused()
    {
        Compile(Node("n", "plain", new { InputPorts = "[]" })).Should().BeEmpty();
    }

    [Fact]
    public void A_declared_output_on_a_block_that_supports_none_is_refused_once()
    {
        var diagnostics = Compile(Declaring(Node("n", "plain"), new() { ["Then"] = "k" }));

        diagnostics.Should().ContainSingle().Which.Should().Match<CompilationDiagnostic>(d =>
            d.RuleId == CompilationRules.DeclaredOutputsUnsupported && d.NodeId == "n");
    }

    [Fact]
    public void A_declared_output_sourced_from_a_branch_the_block_declares_is_not_refused()
    {
        Compile(Declaring(Node("n", "branch"), new() { ["Then"] = "k", ["Else"] = "k" })).Should().BeEmpty();
    }

    [Fact]
    public void A_declared_output_sourced_from_a_key_that_is_no_branch_is_refused_naming_the_output()
    {
        var diagnostics = Compile(Declaring(Node("n", "branch"), new() { ["Then"] = "k", ["then"] = "k", ["Nowhere"] = "k" }));

        diagnostics.Where(d => d.RuleId == CompilationRules.DeclaredOutputSourceUnknown).Should().HaveCount(2)
            .And.OnlyContain(d => d.NodeId == "n" && d.PortName == "Out");
        diagnostics.Select(d => d.Message).Should().Contain(m => m.Contains("'Nowhere'")).And.Contain(m => m.Contains("'then'"));
    }

    [Fact]
    public void A_declared_output_with_no_sources_on_a_block_that_supports_them_is_not_refused()
    {
        Compile(Declaring(Node("n", "branch"), null)).Should().BeEmpty();
    }
}
