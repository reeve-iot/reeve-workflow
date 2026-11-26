using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class WorkflowVersionDiffTests : IDisposable
{
    private readonly SqliteWorkflowDatabase database = new();
    private readonly Guid owner = Guid.NewGuid();

    public void Dispose() => database.Dispose();

    [Fact]
    public async Task An_error_policy_in_the_configuration_is_a_configuration_change_and_no_policy_change()
    {
        var diff = await DiffAsync(
            Node("""{"errorPolicy":{"policy":"FailWorkflow"}}"""),
            Node("""{"errorPolicy":{"policy":"ContinueWithDefault"}}"""));

        var node = diff.ModifiedNodes.Should().ContainSingle().Subject;
        node.ErrorPolicyChanged.Should().BeFalse();
        node.ConfigChanges.Keys.Should().Equal("errorPolicy");
    }

    [Fact]
    public async Task A_changed_retry_setting_is_reported_as_a_retry_policy_change()
    {
        var diff = await DiffAsync(
            Node("""{"retryCount":3}"""),
            Node("""{"retryCount":7}"""));

        diff.ModifiedNodes.Should().ContainSingle()
            .Which.RetryPolicyChanged.Should().BeTrue();
    }

    [Fact]
    public async Task An_error_policy_the_engine_reads_from_the_node_is_reported_too()
    {
        var diff = await DiffAsync(
            BesideConfig("""{"policy":"FailWorkflow"}"""),
            BesideConfig("""{"policy":"ContinueWithDefault"}"""));

        diff.ModifiedNodes.Should().ContainSingle()
            .Which.ErrorPolicyChanged.Should().BeTrue();
    }

    [Fact]
    public async Task An_error_policy_beside_the_configuration_spelled_two_ways_is_no_change()
    {
        var diff = () => DiffAsync(
            BesideConfig("""{"policy":"Skip"}"""),
            BesideConfig("""{"policy":"Skip","routeToErrorPort":false}"""));

        (await diff.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message
            .Should().Contain("error-policy-route-unread");
    }

    [Fact]
    public async Task A_policy_change_beside_a_configuration_change_is_reported_as_both()
    {
        var diff = await DiffAsync(
            """{"nodes":[{"id":"n1","blockTypeId":"whatever","position":{"x":0,"y":0},"configuration":{"Url":"a"},"errorPolicy":{"policy":"FailWorkflow"}}],"edges":[]}""",
            """{"nodes":[{"id":"n1","blockTypeId":"whatever","position":{"x":0,"y":0},"configuration":{"Url":"b"},"errorPolicy":{"policy":"EmitError"}}],"edges":[]}""");

        var node = diff.ModifiedNodes.Should().ContainSingle().Subject;
        node.ErrorPolicyChanged.Should().BeTrue();
        node.ConfigChanges.Keys.Should().Equal("Url");
    }

    [Fact]
    public async Task An_edge_the_editor_wrote_is_reported_as_added()
    {
        var diff = await DiffAsync(Wired(""), Wired("""{"id":"e1","sourceNodeId":"n1","targetNodeId":"n1","mappings":{}}"""));

        diff.AddedEdges.Should().ContainSingle().Which.Id.Should().Be("e1");
    }

    [Fact]
    public async Task A_version_under_the_unlisted_connections_key_is_refused_not_diffed_as_no_edges()
    {
        var wired = """
            {"nodes":[],"connections":[{"id":"e1","sourceNodeId":"n1","targetNodeId":"n1","mappings":{}}],"edges":[]}
            """;

        var diff = () => DiffAsync("""{"nodes":[],"edges":[]}""", wired);

        (await diff.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message
            .Should().Contain("version 2 cannot be read: unlisted key 'connections' on canvas (rule stored-key-unknown");
    }

    [Fact]
    public async Task A_node_whose_error_policy_and_retry_are_untouched_reports_neither()
    {
        var diff = await DiffAsync(
            Node("""{"errorPolicy":{"policy":"FailWorkflow"},"retryCount":3,"Url":"a"}"""),
            Node("""{"errorPolicy":{"policy":"FailWorkflow"},"retryCount":3,"Url":"b"}"""));

        var node = diff.ModifiedNodes.Should().ContainSingle().Subject;
        node.ErrorPolicyChanged.Should().BeFalse();
        node.RetryPolicyChanged.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"nodes":[{"id":"n1","blockTypeId":"whatever","position":{"x":0,"y":0},"configuration":{}}],"Nodes":[{"id":"n1","blockTypeId":"Script","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "rule L5")]
    [InlineData("""{"nodes":[{"id":"n1","blockTypeId":"whatever","configuration":{"Url":"a"},"config":{"Url":"b"},"position":{"x":0,"y":0}}],"edges":[]}""", "rule stored-key-unknown")]
    [InlineData("""{"nodes":[{"id":"n1","blockTypeId":"a","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"b","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "duplicate-node-id", "n1", "/nodes/1/id")]
    [InlineData("""{"nodes":[{"id":"loop","subCanvases":{"Body":{"nodes":[{"id":"n1","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", "duplicate-node-id", "n1", "/nodes/0/subCanvases/Body/nodes/1/id")]
    public async Task A_version_the_reader_refuses_makes_the_diff_refuse_naming_it(string definitionB, string because, string? duplicateId = null, string? pointer = null)
    {
        var diff = () => DiffAsync("""{"nodes":[{"id":"n1","blockTypeId":"whatever","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""", definitionB);

        var refusal = (await diff.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which;
        refusal.Message.Should().Contain("version 2").And.Contain(because);
        if (duplicateId is not null)
            refusal.Message.Should().Contain($"id '{duplicateId}'").And.Contain($"at '{pointer}'");
    }

    [Fact]
    public async Task A_node_whose_block_type_changed_is_reported_removed_and_added()
    {
        var diff = await DiffAsync(
            """{"nodes":[{"id":"n1","blockTypeId":"Harmless","configuration":{},"position":{"x":0,"y":0}}],"edges":[]}""",
            """{"nodes":[{"id":"n1","blockTypeId":"RunScript","configuration":{},"position":{"x":0,"y":0}}],"edges":[]}""");

        diff.RemovedNodes.Should().ContainSingle().Which.BlockTypeId.Should().Be("Harmless");
        diff.AddedNodes.Should().ContainSingle().Which.BlockTypeId.Should().Be("RunScript");
        diff.ModifiedNodes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_block_swap_inside_a_sub_canvas_is_reported_removed_and_added_with_its_canvas()
    {
        static string Loop(string inner) =>
            """{"nodes":[{"id":"loop","blockTypeId":"ForEach","position":{"x":0,"y":0},"configuration":{},"subCanvases":{"Body":{"nodes":[{"id":"n1","blockTypeId":""" + "\"" + inner + "\"" + ""","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}}}],"edges":[]}""";

        var diff = await DiffAsync(Loop("Harmless"), Loop("RunScript"));

        var path = new[] { new WorkflowDiffCanvasStep("loop", "Body") };
        diff.RemovedNodes.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = "n1", BlockTypeId = "Harmless", CanvasPath = path });
        diff.AddedNodes.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = "n1", BlockTypeId = "RunScript", CanvasPath = path });
        diff.ModifiedNodes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_configuration_change_and_an_edge_two_canvases_down_are_reported_with_their_canvas()
    {
        static string Nested(string url, string edges) =>
            """{"nodes":[{"id":"if","blockTypeId":"If","position":{"x":0,"y":0},"configuration":{},"subCanvases":{"Then":{"nodes":[{"id":"loop","blockTypeId":"ForEach","position":{"x":0,"y":0},"configuration":{},"subCanvases":{"Body":{"nodes":[{"id":"n1","blockTypeId":"Http","position":{"x":0,"y":0},"configuration":{"Url":""" + "\"" + url + "\"" + """}}],"edges":[""" + edges + """]}}}],"edges":[]}}}],"edges":[]}""";

        var diff = await DiffAsync(Nested("a", ""), Nested("b", """{"id":"e1","sourceNodeId":"n1","targetNodeId":"n1","mappings":{}}"""));

        WorkflowDiffCanvasStep[] path = [new("if", "Then"), new("loop", "Body")];
        var node = diff.ModifiedNodes.Should().ContainSingle().Subject;
        node.Id.Should().Be("n1");
        node.CanvasPath.Should().Equal(path);
        node.ConfigChanges.Should().ContainKey("Url").WhoseValue.Should().Be(new WorkflowDiffValueChange("\"a\"", "\"b\""));
        diff.AddedEdges.Should().ContainSingle().Which.CanvasPath.Should().Equal(path);
        diff.AddedNodes.Should().BeEmpty();
    }

    [Fact]
    public async Task One_id_on_two_canvases_is_two_nodes_and_a_root_node_has_an_empty_path()
    {
        var diff = await DiffAsync(
            """{"nodes":[],"edges":[]}""",
            """{"nodes":[{"id":"x","blockTypeId":"A","subCanvases":{"Body":{"nodes":[{"id":"x","blockTypeId":"B","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""");

        diff.AddedNodes.Select(n => (n.Id, n.BlockTypeId, n.CanvasPath.Count)).Should().Equal(("x", "A", 0), ("x", "B", 1));
    }

    [Fact]
    public async Task One_edge_id_on_two_canvases_is_two_edges()
    {
        const string both = """{"nodes":[{"id":"a","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}},{"id":"c","blockTypeId":"If","subCanvases":{"Then":{"nodes":[{"id":"a","blockTypeId":"x","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"a","targetNodeId":"a","mappings":{}}]}},"position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"a","targetNodeId":"c","mappings":{}}]}""";

        var same = await DiffAsync(both, both);
        var added = await DiffAsync("""{"nodes":[],"edges":[]}""", both);

        same.AddedEdges.Should().BeEmpty();
        same.RemovedEdges.Should().BeEmpty();
        added.AddedEdges.Select(e => (e.Id, e.CanvasPath.Count)).Should().Equal(("e", 0), ("e", 1));
    }

    [Fact]
    public async Task A_key_the_reader_does_not_list_makes_the_diff_refuse()
    {
        var diff = () => DiffAsync("""{"nodes":[],"edges":[]}""", """{"nodes":[{"id":"n1","blockTypeId":"b","name":5}],"edges":[]}""");

        (await diff.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message
            .Should().Contain("unlisted key 'name' on node (rule stored-key-unknown");
    }

    [Fact]
    public async Task A_first_version_under_the_unlisted_connections_key_is_refused_naming_it()
    {
        var diff = () => DiffAsync("""{"nodes":[],"connections":[{"id":"e1","sourceNodeId":"n1","targetNodeId":"n1","mappings":{}}],"edges":[]}""", """{"nodes":[],"edges":[]}""");

        (await diff.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message
            .Should().Contain("version 1 cannot be read: unlisted key 'connections' on canvas (rule stored-key-unknown");
    }

    [Theory]
    [InlineData("""[{"Name":"b","type":"Int"}]""", "version 2's input parameters cannot be read: a key 'Name' where 'name' is meant")]
    [InlineData("""[{"name":"b","type":"Int","name":"c"}]""", "version 2's input parameters cannot be read: ")]
    public async Task A_parameter_list_that_has_no_one_meaning_makes_the_diff_refuse(string inputs, string says)
    {
        var diff = () => DiffAsync("""{"nodes":[],"edges":[]}""", """{"nodes":[],"edges":[]}""", """[{"name":"a","type":"Int"}]""", inputs);

        (await diff.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message.Should().Contain(says);
    }

    [Theory]
    [InlineData(",\"id\":\"e\"", "duplicate-edge-id")]
    [InlineData("", "a connection with no 'id' (rule L7, at '/edges/0/id')")]
    public async Task Two_edges_with_one_id_or_none_are_refused_not_compared(string id, string says)
    {
        var before = "{\"nodes\":[],\"edges\":[{\"sourceNodeId\":\"a\",\"targetNodeId\":\"b\",\"mappings\":{}" + id
            + "},{\"sourceNodeId\":\"b\",\"targetNodeId\":\"a\",\"mappings\":{}" + id + "}]}";
        var after = before.Replace("\"targetNodeId\":\"a\"", "\"targetNodeId\":\"c\"");

        var diff = () => DiffAsync(before, after);

        (await diff.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message.Should().Contain(says);
    }

    [Fact]
    public async Task Output_parameters_are_compared_and_a_list_the_reader_refuses_is_refused()
    {
        var diff = await DiffAsync("""{"nodes":[],"edges":[]}""", """{"nodes":[],"edges":[]}""",
            outputsA: """[{"name":"total","type":"Int"}]""", outputsB: """[{"name":"total","type":"String"}]""");
        var refused = () => DiffAsync("""{"nodes":[],"edges":[]}""", """{"nodes":[],"edges":[]}""", outputsB: """[{"Name":"total"}]""");

        diff.OutputParameterChanges.Modified.Should().Equal("total");
        diff.ParameterChanges.Modified.Should().BeEmpty();
        (await refused.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message
            .Should().Contain("version 2's output parameters cannot be read");
    }

    [Fact]
    public async Task Parameters_are_compared_entry_by_entry_under_their_names()
    {
        var diff = await DiffAsync("""{"nodes":[],"edges":[]}""", """{"nodes":[],"edges":[]}""",
            """[{"name":"kept","type":"Int"},{"name":"retyped","type":"Int"},{"name":"gone","type":"Int"}]""",
            """[{"name":"retyped","type":"String"},{"name":"kept","type":"Int"},{"name":"new","type":"Int"}]""");

        diff.ParameterChanges.Added.Should().Equal("new");
        diff.ParameterChanges.Removed.Should().Equal("gone");
        diff.ParameterChanges.Modified.Should().Equal("retyped");
    }

    [Fact]
    public async Task Two_parameters_of_one_name_are_compared_in_order_since_the_compiler_takes_the_first()
    {
        const string intFirst = """[{"name":"b","type":"Int"},{"name":"b","type":"String"}]""";
        const string stringFirst = """[{"name":"b","type":"String"},{"name":"b","type":"Int"}]""";

        var diff = await DiffAsync("""{"nodes":[],"edges":[]}""", """{"nodes":[],"edges":[]}""", intFirst, stringFirst, intFirst, stringFirst);
        var same = await DiffAsync("""{"nodes":[],"edges":[]}""", """{"nodes":[],"edges":[]}""", intFirst, intFirst, intFirst, intFirst);

        diff.ParameterChanges.Modified.Should().Equal("b");
        diff.OutputParameterChanges.Modified.Should().Equal("b");
        same.ParameterChanges.Modified.Should().BeEmpty();
        same.OutputParameterChanges.Modified.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wire_drawn_again_last_is_reported_as_the_order_of_the_canvas_edges()
    {
        const string nodes = """[{"id":"in","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"a","blockTypeId":"Leaf","position":{"x":0,"y":0},"configuration":{}},{"id":"s","blockTypeId":"Leaf","position":{"x":0,"y":0},"configuration":{}}]""";
        var drawn = $$"""{"nodes":{{nodes}},"edges":[{"id":"e1","sourceNodeId":"in","targetNodeId":"a","mappings":{} },{"id":"e2","sourceNodeId":"in","targetNodeId":"s","mappings":{} }]}""";
        var redrawn = $$"""{"nodes":{{nodes}},"edges":[{"id":"e2","sourceNodeId":"in","targetNodeId":"s","mappings":{} },{"id":"e9","sourceNodeId":"in","targetNodeId":"a","mappings":{} }]}""";

        var diff = await DiffAsync(drawn, redrawn);

        diff.ModifiedCanvases.Should().ContainSingle().Which.ChangedFields.Should().Equal("edges");
        (diff.AddedEdges.Count + diff.RemovedEdges.Count + diff.ModifiedNodes.Count).Should().Be(0);
    }

    [Fact]
    public async Task The_catalog_hands_the_diff_its_blocks_so_a_leaf_block_s_default_timeout_written_out_is_no_change()
    {
        var leaf = await DiffAsync(Node("{}").Replace("whatever", "Leaf"), Node("""{"timeoutSeconds":30}""").Replace("whatever", "Leaf"));
        var unknown = await DiffAsync(Node("{}"), Node("""{"timeoutSeconds":30}"""));

        leaf.ModifiedNodes.Should().BeEmpty();
        unknown.ModifiedNodes.Should().ContainSingle().Which.ConfigChanges.Keys.Should().Equal("timeoutSeconds");
    }

    [Fact]
    public async Task Retries_switched_on_for_a_block_not_idempotent_are_a_retry_policy_change()
    {
        var diff = await DiffAsync(Node("{}").Replace("whatever", "Leaf"), Node("""{"retryEnabled":true}""").Replace("whatever", "Leaf"));

        diff.ModifiedNodes.Should().ContainSingle().Which.RetryPolicyChanged.Should().BeTrue();
    }

    private async Task<WorkflowDiffResponse> DiffAsync(
        string definitionA, string definitionB, string? inputsA = null, string? inputsB = null,
        string? outputsA = null, string? outputsB = null)
    {
        var workflowId = Seed(definitionA, definitionB, inputsA, inputsB, outputsA, outputsB);
        using var scope = database.Services.CreateScope();
        var blocks = new LeafOnly();
        var catalog = new WorkflowCatalog(
            scope.ServiceProvider.GetRequiredService<WorkflowDbContext>(), blocks,
            new ReeveWorkflow.Core.Compilation.WorkflowCompilationService(
                blocks, scope.ServiceProvider.GetRequiredService<ReeveWorkflow.Core.Hosting.IWorkflowRunScopeFactory>()),
            new ReeveWorkflow.Core.Hosting.WorkflowDefinitionSecrets(blocks, scope.ServiceProvider.GetService<ReeveWorkflow.Core.Hosting.IWorkflowSecretCodec>()));
        return await catalog.DiffVersionsAsync(owner, workflowId, 1, 2);
    }

    private Guid Seed(string definitionA, string definitionB, string? inputsA, string? inputsB, string? outputsA, string? outputsB)
    {
        var workflowId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        db.Workflows.Add(new Workflow(
            workflowId, "diffed", description: null,
            createdByUserId: owner, createdAtUtc: now));
        db.WorkflowVersions.Add(new WorkflowVersion(
            Guid.NewGuid(), workflowId, versionNumber: 1, definitionJson: definitionA,
            inputParametersJson: inputsA, outputParametersJson: outputsA,
            createdByUserId: owner, createdAtUtc: now, isPublished: true));
        db.WorkflowVersions.Add(new WorkflowVersion(
            Guid.NewGuid(), workflowId, versionNumber: 2, definitionJson: definitionB,
            inputParametersJson: inputsB, outputParametersJson: outputsB,
            createdByUserId: owner, createdAtUtc: now, isPublished: true));
        db.SaveChanges();
        return workflowId;
    }

    private static string Node(string configuration) => $$"""
        {"nodes":[{"id":"n1","blockTypeId":"whatever","position":{"x":0,"y":0},"configuration":{{configuration}}}],"edges":[]}
        """;

    private static string BesideConfig(string errorPolicy) => $$"""
        {"nodes":[{"id":"n1","blockTypeId":"whatever","position":{"x":0,"y":0},"configuration":{},"errorPolicy":{{errorPolicy}}}],"edges":[]}
        """;

    private static string Wired(string edge) => $$"""
        {"nodes":[],"edges":[{{edge}}]}
        """;

    private sealed class LeafOnly : IWorkflowBlocks
    {
        public IWorkflowBlockFactory? Find(string blockTypeId) =>
            string.Equals(blockTypeId, "Leaf", StringComparison.OrdinalIgnoreCase) ? new Leaf() : null;

        public bool ContainsPrivilegedBlock(string? definitionJson) => false;

        public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => [];
    }

    private sealed class Leaf : IWorkflowBlockFactory
    {
        public string BlockTypeId => "Leaf";
        public string DisplayName => "Leaf";
        public string Description => "";
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
