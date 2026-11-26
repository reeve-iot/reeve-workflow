using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.Compilation;

public class CanvasFrameTests
{
    [Fact]
    public void A_childs_path_appends_one_segment_for_the_node_id_and_sub_canvas_name()
    {
        var sub = new WorkflowDefinition { Nodes = [] };
        var root = new CanvasFrame(CanvasPath.Root, new WorkflowDefinition { Nodes = [] });

        var child = root.Child("loop", "Body", sub);

        child.Path.Should().Be(CanvasPath.Root.Child("loop", "Body"));
        child.Canvas.Should().BeSameAs(sub);
    }

    [Fact]
    public void A_grandchilds_path_nests_under_its_parents_own_path()
    {
        var leaf = new WorkflowDefinition { Nodes = [] };
        var mid = new WorkflowDefinition { Nodes = [] };
        var root = new CanvasFrame(CanvasPath.Root, new WorkflowDefinition { Nodes = [] });

        var grandchild = root.Child("outer", "Then", mid).Child("inner", "Body", leaf);

        grandchild.Path.Should().Be(CanvasPath.Root.Child("outer", "Then").Child("inner", "Body"));
    }
}
