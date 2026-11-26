using System.Text.Json;
using FluentAssertions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using static ReeveWorkflow.Core.Tests.Compilation.CompilerTestGraphs;

namespace ReeveWorkflow.Core.Tests.Execution;

public class WorkflowVersionManifestBuilderTests
{
    private const string WorkflowIdField = "WorkflowId";

    [Fact]
    public async Task A_root_with_no_sub_workflow_needs_no_fetch()
    {
        var root = Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]);
        var fetches = 0;

        var manifest = await Build(root, fetchCurrent: (_, _) => { fetches++; return NotFound(); });

        fetches.Should().Be(0);
        manifest.Should().ContainSingle().Which.Key.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_configured_childs_current_version_is_pinned()
    {
        var child = Guid.NewGuid();
        var childVersion = Guid.NewGuid();
        var root = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = child.ToString() })],
            [Edge("e1", "start", "call")]);

        var manifest = await Build(root, fetchCurrent: (id, _) =>
            id == child ? Found(childVersion, NoopGraph()) : NotFound());

        manifest.Should().ContainKey(child).WhoseValue.Should().Be(childVersion);
    }

    [Fact]
    public async Task A_literal_mapping_into_WorkflowId_is_ignored_the_configured_child_is_still_pinned()
    {
        var configuredChild = Guid.NewGuid();
        var configuredChildVersion = Guid.NewGuid();
        var literalChild = Guid.NewGuid();

        var root = Graph(
            [
                StartNode(),
                Node("lit", "noop"),
                Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = configuredChild.ToString() }),
            ],
            [
                Edge("e1", "start", "lit"),
                LiteralEdge("e2", "lit", "call", WorkflowIdField, literalChild.ToString()),
            ]);

        var manifest = await Build(root, fetchCurrent: (id, _) =>
            id == configuredChild ? Found(configuredChildVersion, NoopGraph()) : NotFound());

        manifest.Should().ContainKey(configuredChild).WhoseValue.Should().Be(configuredChildVersion);
        manifest.Should().NotContainKey(literalChild);
    }

    [Fact]
    public async Task A_malformed_mapping_on_an_unrelated_input_does_not_stop_the_manifest_from_pinning_the_configured_child()
    {
        var child = Guid.NewGuid();
        var childVersion = Guid.NewGuid();
        var call = Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = child.ToString() });
        var root = Graph([StartNode(), call], []);
        root.Edges =
        [
            new WorkflowConnectionDefinition
            {
                Id = "e1", SourceNodeId = "start", TargetNodeId = "call",
                Mappings = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["Other"] = new Dictionary<string, object> { ["sourceOutput"] = 5 },
                }),
            },
        ];

        var manifest = await Build(root, fetchCurrent: (id, _) =>
            id == child ? Found(childVersion, NoopGraph()) : NotFound());

        manifest.Should().ContainKey(child).WhoseValue.Should().Be(childVersion);
    }

    [Fact]
    public async Task A_node_with_no_configured_value_at_all_is_not_collected_even_with_a_wire_present()
    {
        var root = Graph(
            [
                StartNode(),
                Node("emit", "noop"),
                Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId),
            ],
            [
                Edge("e1", "start", "emit"),
                Edge("e2", "emit", "call", ("Out", WorkflowIdField)),
            ]);

        var fetches = 0;
        var manifest = await Build(root, fetchCurrent: (_, _) => { fetches++; return NotFound(); });

        fetches.Should().Be(0);
        manifest.Should().HaveCount(1, "only the root — no configured value names a child to pin");
    }

    [Fact]
    public async Task Sub_canvases_are_walked_for_their_own_sub_workflow_nodes()
    {
        var child = Guid.NewGuid();
        var childVersion = Guid.NewGuid();
        var inner = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = child.ToString() })],
            [Edge("e1", "start", "call")]);

        var outerNode = Node("branch", "container");
        outerNode.SubCanvases = new Dictionary<string, WorkflowDefinition> { ["body"] = inner };
        var root = Graph([StartNode(), outerNode], [Edge("e1", "start", "branch")]);

        var manifest = await Build(root, fetchCurrent: (id, _) =>
            id == child ? Found(childVersion, NoopGraph()) : NotFound());

        manifest.Should().ContainKey(child).WhoseValue.Should().Be(childVersion);
    }

    [Fact]
    public async Task Two_nodes_naming_the_same_child_fetch_it_once()
    {
        var child = Guid.NewGuid();
        var childVersion = Guid.NewGuid();
        var root = Graph(
            [
                StartNode(),
                Node("call1", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = child.ToString() }),
                Node("call2", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = child.ToString() }),
            ],
            [Edge("e1", "start", "call1"), Edge("e2", "call1", "call2")]);

        var fetches = 0;
        var manifest = await Build(root, fetchCurrent: (id, _) =>
        {
            fetches++;
            return id == child ? Found(childVersion, NoopGraph()) : NotFound();
        });

        fetches.Should().Be(1, "a per-workflow dedup set guarantees termination and a single fetch per child");
        manifest.Should().ContainKey(child).WhoseValue.Should().Be(childVersion);
    }

    [Fact]
    public async Task A_child_the_fetch_cannot_find_is_left_out_of_the_manifest()
    {
        var child = Guid.NewGuid();
        var root = Graph(
            [StartNode(), Node("call", WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = child.ToString() })],
            [Edge("e1", "start", "call")]);

        var manifest = await Build(root, fetchCurrent: (_, _) => NotFound());

        manifest.Should().NotContainKey(child);
    }

    [Fact]
    public async Task A_child_the_engines_model_cannot_bind_pins_nothing_below_it_and_its_siblings_are_still_pinned()
    {
        var (unbindable, beneath, sibling, grandchild) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var versions = new[] { unbindable, sibling, grandchild }.ToDictionary(id => id, _ => Guid.NewGuid());
        static WorkflowDefinition Calls(params Guid[] children) => Graph(
            [StartNode(), .. children.Select((c, i) => Node("call" + i, WorkflowGraphHelpers.SubWorkflowBlockTypeId, new { WorkflowId = c.ToString() }))],
            [.. children.Select((_, i) => Edge("e" + i, "start", "call" + i))]);
        var unbindableJson = StoredWorkflowDocument.Write(Calls(beneath), out _)!
            .Replace("\"blockTypeId\":\"WorkflowInput\"", "\"blockTypeId\":\"WorkflowInput\",\"errorPolicy\":1");
        FluentActions.Invoking(() => JsonSerializer.Deserialize<WorkflowDefinition>(unbindableJson, WorkflowGraphHelpers.JsonOptions))
            .Should().Throw<JsonException>();
        var fetched = new List<Guid>();

        var manifest = await Build(Calls(unbindable, sibling), fetchCurrent: (id, _) =>
        {
            fetched.Add(id);
            return id == unbindable
                ? Task.FromResult<(Guid VersionId, string DefinitionJson)?>((versions[id], unbindableJson))
                : id == sibling ? Found(versions[id], Calls(grandchild))
                : id == grandchild ? Found(versions[id], NoopGraph()) : NotFound();
        });

        manifest.Should().Contain(versions.Select(v => KeyValuePair.Create(v.Key, v.Value))).And.HaveCount(4);
        fetched.Should().NotContain(beneath, "nothing below a definition the model cannot bind is read");
    }

    private static WorkflowDefinition NoopGraph() =>
        Graph([StartNode(), Node("n", "noop")], [Edge("e1", "start", "n")]);

    private static Task<(Guid VersionId, string DefinitionJson)?> Found(Guid versionId, WorkflowDefinition definition) =>
        Task.FromResult<(Guid VersionId, string DefinitionJson)?>(
            (versionId, StoredWorkflowDocument.Write(definition, out _)!));

    private static Task<(Guid VersionId, string DefinitionJson)?> NotFound() =>
        Task.FromResult<(Guid VersionId, string DefinitionJson)?>(null);

    private static Task<IReadOnlyDictionary<Guid, Guid>> Build(
        WorkflowDefinition root, Func<Guid, CancellationToken, Task<(Guid VersionId, string DefinitionJson)?>> fetchCurrent)
    {
        var rootWorkflowId = Guid.NewGuid();
        var rootVersionId = Guid.NewGuid();
        return WorkflowVersionManifestBuilder.BuildAsync(
            rootWorkflowId, rootVersionId,
            StoredWorkflowDocument.Write(root, out _)!,
            fetchCurrent, CancellationToken.None);
    }
}
