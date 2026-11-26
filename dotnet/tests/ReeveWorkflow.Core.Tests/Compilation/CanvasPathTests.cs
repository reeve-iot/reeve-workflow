using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CanvasPathTests
{
    [Fact]
    public void Two_canvases_whose_ids_collide_under_a_slash_are_two_canvases()
    {
        var innerA = Graph([Node("i1", "read")]);
        var innerB = Graph([Node("i2", "read")]);

        var x = Node("x", "branching");
        x.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["a/Body"] = innerA };
        var xa = Node("x/a", "branching");
        xa.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["Body"] = innerB };

        var graph = Graph(
            [StartNode(), x, xa],
            [Edge("e1", "start", "x"), Edge("e2", "x", "x/a")]);

        var branching = Shape("branching", subCanvasInputs:
            new Dictionary<string, (string Name, PortValueType Type, bool Required)[]>
            {
                ["a/Body"] = [],
                ["Body"] = [],
            },
            subCanvasNames: ["a/Body", "Body"]);

        var result = WorkflowCompiler.Compile(
            graph, Catalogue(Shape(WorkflowGraphHelpers.WorkflowInputBlockTypeId), branching, Shape("read")),
            CanvasSignature.Empty);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Diagnostics.Select(d => d.RuleId + ":" + d.Message)));
        var program = result.Program!;
        program.Canvases.Select(c => c.CanvasPath).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Two_paths_over_the_same_segments_are_equal()
    {
        CanvasPath.Root.Child("x", "Body").Should().Be(CanvasPath.Root.Child("x", "Body"));
    }

    [Fact]
    public void A_slash_in_a_node_id_does_not_make_one_path_look_like_another()
    {
        CanvasPath.Root.Child("x", "a/Body").Should().NotBe(CanvasPath.Root.Child("x/a", "Body"));
    }

    [Fact]
    public void Two_branches_of_the_same_node_are_different_paths()
    {
        CanvasPath.Root.Child("x", "Then").Should().NotBe(CanvasPath.Root.Child("x", "Else"));
    }

    [Fact]
    public void Two_paths_with_a_matching_last_segment_but_a_different_first_one_are_not_equal()
    {
        CanvasPath.Root.Child("a", "X").Child("shared", "Y")
            .Should().NotBe(CanvasPath.Root.Child("b", "X").Child("shared", "Y"));
    }
}
