using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class DeclaredBranchCompilationTests
{
    private static readonly BlockShape Collect = Shape("collect",
        subCanvasOutputs: new Dictionary<string, (string, PortValueType, bool)[]>
        {
            ["Body"] = [("Done", PortValueType.String, true), ("Note", PortValueType.String, false)],
        },
        supportsDeclaredOutputs: true, subCanvasNames: ["Body"]);

    private static WorkflowDefinition Root(WorkflowNodeDefinition container) =>
        Graph([StartNode(), container], [Edge("e", "start", container.Id)]);

    private static WorkflowDefinition OpenedEmpty() => DeclaredBranchesFixture.Opened(withExit: true);

    [Fact]
    public void A_required_output_of_a_left_out_branch_is_unfed_on_its_exit_node_and_an_optional_one_is_not()
    {
        var result = WorkflowCompiler.Compile(Root(Node("c", "collect")), Catalogue(Collect), CanvasSignature.Empty);

        result.Diagnostics.Select(d => (d.RuleId, d.NodeId, d.PortName, string.Join("/", d.CanvasPath.Select(s => s.NodeId + ":" + s.BranchName))))
            .Should().Equal((CompilationRules.RequiredInputUnfed, DeclaredBranches.ExitNodeId, "Done", "c:Body"));
    }

    [Fact]
    public void A_left_out_branch_keeps_the_sites_of_its_container_so_a_malformed_port_type_is_still_reported()
    {
        var absent = Node("c", "collect");
        absent.OutputPorts = [new() { Name = "p", DisplayName = "p", Type = "Collection<", Sources = new() { ["Body"] = "Note" } }];
        var opened = Node("c", "collect");
        opened.OutputPorts = absent.OutputPorts;
        opened.SubCanvases = new() { ["Body"] = OpenedEmpty() };

        var verdict = Rules(WorkflowCompiler.Compile(Root(absent), Catalogue(Collect), CanvasSignature.Empty));

        verdict.Should().Contain($"{CompilationRules.PortTypeMalformed} c p");
        verdict.Should().Equal(Rules(WorkflowCompiler.Compile(Root(opened), Catalogue(Collect), CanvasSignature.Empty)));
    }

    [Fact]
    public void The_catalogue_is_asked_about_each_stored_node_once()
    {
        var inner = Node("c", "collect");
        var outer = Node("o", "collect");
        outer.SubCanvases = new() { ["Body"] = Root(inner) };
        var shapes = new CountingShapes(Catalogue(Collect));

        WorkflowCompiler.Compile(Root(outer), shapes, CanvasSignature.Empty);

        shapes.Asked.Should().Equal("collect", "collect");
    }

    [Fact]
    public void The_document_the_compiler_is_given_keeps_its_branches_as_they_were()
    {
        var container = Node("c", "collect");
        var document = StoredWorkflowDocument.ReadModel(Root(container), out _)!;

        var context = WorkflowCompiler.Run(document, Catalogue(Collect), CanvasSignature.Empty);

        document.Definition.Nodes.Last().SubCanvases.Should().BeNull();
        context.Document.Definition.Nodes.Last().SubCanvases.Should().ContainKey("Body");
    }

    [Fact]
    public void The_shapes_a_verdict_depends_on_are_those_of_the_branch_opened_empty()
    {
        var opened = Node("c", "collect");
        opened.SubCanvases = new() { ["Body"] = OpenedEmpty() };

        WorkflowCompiler.ShapesUsedBy(Root(Node("c", "collect")), Catalogue(Collect)).Keys
            .Should().BeEquivalentTo(WorkflowCompiler.ShapesUsedBy(Root(opened), Catalogue(Collect)).Keys)
            .And.Contain(WorkflowGraphHelpers.WorkflowOutputBlockTypeId);
    }

    [Fact]
    public void A_filled_node_holds_its_own_branches_first_then_each_absent_one_in_declared_order()
    {
        var node = Node("c", "collect");
        node.SubCanvases = new() { ["B"] = OpenedEmpty() };

        var filled = DeclaredBranches.Filled(Root(node), n => n.BlockTypeId == "collect" ? new(["A", "B", "C"], _ => false) : null);

        filled.Nodes.Last().SubCanvases!.Keys.Should().Equal("B", "A", "C");
    }

    [Fact]
    public void A_filled_copy_keeps_every_other_member_of_each_node_and_canvas_it_copies()
    {
        var inner = Every(Node("i", "collect"), "i");
        var outer = Every(Node("o", "collect"), "o");
        outer.SubCanvases = new() { ["Other"] = Every(Root(inner)) };
        var root = Every(Root(outer));

        var filled = DeclaredBranches.Filled(root, n => n.BlockTypeId == "collect" ? new(["Other", "Body"], _ => false) : null);

        var written = JsonSerializer.SerializeToNode(filled, WorkflowGraphHelpers.JsonOptions)!;
        var o = written["nodes"]![1]!;
        o["subCanvases"]!.AsObject().Remove("Body").Should().BeTrue();
        o["subCanvases"]!["Other"]!["nodes"]![1]!["subCanvases"]!.AsObject().Remove("Body").Should().BeTrue();
        written.ToJsonString().Should().Be(JsonSerializer.Serialize(root, WorkflowGraphHelpers.JsonOptions));
    }

    [Fact]
    public void A_declared_output_whose_key_for_the_branch_is_blank_writes_no_exit_node()
    {
        var node = Node("c", "collect");
        node.OutputPorts = [new() { Name = "v", DisplayName = "v", Type = "String", Sources = new() { ["Else"] = "" } }];

        StoredWorkflowDocument.Write(DeclaredBranches.Empty(DeclaredBranches.WritesExit(node, "Else", false)), out _)
            .Should().Be(DeclaredBranchesFixture.OpenedEntryOnly);
    }

    private static WorkflowNodeDefinition Every(WorkflowNodeDefinition node, string id)
    {
        node.Position = new() { X = 3, Y = 4 };
        node.Config = new() { ["k"] = JsonSerializer.SerializeToElement(id) };
        node.Passthrough = [id];
        node.TypeArguments = new() { ["T"] = "Int" };
        node.OutputPorts = [new() { Name = "p", DisplayName = "p", Type = "String", Sources = new() { ["Other"] = "k" } }];
        node.ErrorPolicy = new NodeErrorConfig(NodeErrorPolicy.ContinueWithDefault, "{}");
        node.SubCanvases ??= new() { ["Other"] = OpenedEmpty() };
        typeof(WorkflowNodeDefinition).GetProperties().Should().OnlyContain(p => p.GetValue(node) != null);
        return node;
    }

    private static WorkflowDefinition Every(WorkflowDefinition canvas)
    {
        canvas.OutputMappings = [new() { SourceNodeId = "start", SourceOutput = "s", WorkflowOutput = "w" }];
        typeof(WorkflowDefinition).GetProperties().Should().OnlyContain(p => p.GetValue(canvas) != null);
        return canvas;
    }

    private static List<string> Rules(CompilationResult result) =>
        [.. result.Diagnostics.Select(d => $"{d.RuleId} {d.NodeId} {d.PortName}").Order(StringComparer.Ordinal)];

    private sealed class CountingShapes(IBlockShapeSource inner) : IBlockShapeSource
    {
        public List<string> Asked { get; } = [];

        public BlockShape? Find(string blockTypeId)
        {
            Asked.Add(blockTypeId);
            return inner.Find(blockTypeId);
        }
    }
}

internal static class DeclaredBranchesFixture
{
    public const string OpenedEntryOnly =
        """{"nodes":[{"id":"__workflow_input__","blockTypeId":"WorkflowInput","position":{"x":50,"y":100},"configuration":{}}],"edges":[]}""";

    public static WorkflowDefinition Opened(bool withExit) => System.Text.Json.JsonSerializer.Deserialize<WorkflowDefinition>(withExit
        ? """{"nodes":[{"id":"__workflow_input__","blockTypeId":"WorkflowInput","position":{"x":50,"y":100},"configuration":{}},{"id":"__workflow_output__","blockTypeId":"WorkflowOutput","position":{"x":800,"y":100},"configuration":{}}],"edges":[]}"""
        : OpenedEntryOnly,
        WorkflowGraphHelpers.JsonOptions)!;
}
