using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed class WorkflowDefinitionSecretsEdgeSaveTests
{
    private static readonly Guid Target = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Elsewhere = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly FakeSecretCodec codec = new();

    private static string Stored(string plaintext) => FakeSecretCodec.Ciphertext(Target, plaintext);

    private static string Edge(Dictionary<string, string> literal, Dictionary<string, string>? configuration = null, string edgeId = "e1") =>
        SecretDocs.EdgeFlow(literal, configuration, edgeId);

    private async Task<WorkflowSecretAdmission> AdmitAsync(string? replaced, string definition, bool withCodec = true) =>
        await new WorkflowDefinitionSecrets(new SecretTestBlocks(), withCodec ? codec : null)
            .AdmitAsync(new WorkflowSecretSave(User, Target, WorkflowDefinitionOrigin.Edited), replaced, definition, default);

    private async Task<WorkflowSecretSaveRefusedException> RefusalAsync(string? replaced, string definition, bool withCodec = true) =>
        (await FluentActions.Awaiting(() => AdmitAsync(replaced, definition, withCodec))
            .Should().ThrowAsync<WorkflowSecretSaveRefusedException>()).Which;

    [Fact]
    public async Task A_plaintext_the_name_rule_does_not_catch_is_stored_as_written()
    {
        var admitted = await AdmitAsync(null, Edge(new() { ["Accept"] = "text/plain" }));

        SecretDocs.EdgeEntries(admitted.Definition)["Accept"].Should().Be("text/plain");
        codec.Protections.Should().BeEmpty();
    }

    [Fact]
    public async Task A_plaintext_the_name_rule_catches_is_protected_at_the_edges_site()
    {
        var admitted = await AdmitAsync(null, Edge(new() { ["Authorization"] = "Bearer t" }));

        var entries = SecretDocs.EdgeEntries(admitted.Definition);
        entries["Authorization"].Should().Be(Stored("Bearer t"));
        admitted.Definition.Should().NotContain("Bearer t");
        var (site, _) = codec.Protections.Should().ContainSingle().Which;
        (site.WorkflowId, site.NodeId, site.Input, site.Key).Should().Be((Target, "h", "Headers", "Authorization"));
    }

    [Fact]
    public async Task A_protect_form_is_protected_and_never_stored_as_given()
    {
        var admitted = await AdmitAsync(null, Edge(new() { ["X-Custom"] = "protect:hunter2" }));

        SecretDocs.EdgeEntries(admitted.Definition)["X-Custom"].Should().Be(Stored("hunter2"));
        admitted.Definition.Should().NotContain("protect:").And.NotContain("hunter2");
    }

    [Fact]
    public async Task A_protect_form_with_no_codec_is_refused_as_codec_missing_and_names_the_edge()
    {
        var refusal = await RefusalAsync(null, Edge(new() { ["X-Custom"] = "protect:hunter2" }), withCodec: false);

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretCodecMissing);
        refusal.Message.Should().Contain("'e1'").And.NotContain("hunter2");
    }

    [Fact]
    public async Task A_star_keeps_the_entry_stored_on_the_same_edge()
    {
        var replaced = Edge(new() { ["X-Token"] = Stored("kept"), ["Accept"] = "a" });

        var admitted = await AdmitAsync(replaced, Edge(new() { ["X-Token"] = "***", ["Accept"] = "b" }));

        SecretDocs.EdgeEntries(admitted.Definition).Should().Equal(new Dictionary<string, string> { ["X-Token"] = Stored("kept"), ["Accept"] = "b" });
    }

    [Fact]
    public async Task A_star_on_a_create_is_unmatched()
    {
        var refusal = await RefusalAsync(null, Edge(new() { ["X-Token"] = "***" }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        refusal.Message.Should().Contain("'e1'");
    }

    [Fact]
    public async Task A_star_is_matched_at_its_own_place_and_never_at_another_one_that_holds_the_same_key()
    {
        var replaced = Edge(new() { ["X-Token"] = Stored("edge") }, new() { ["X-Token"] = Stored("config") });

        var admitted = await AdmitAsync(replaced, Edge(new() { ["X-Token"] = "***" }, new() { ["X-Token"] = "***" }));

        SecretDocs.EdgeEntries(admitted.Definition)["X-Token"].Should().Be(Stored("edge"));
        SecretDocs.Entries(admitted.Definition, "h")["X-Token"].Should().Be(Stored("config"));
    }

    [Theory]
    [InlineData("the configuration holds it")]
    [InlineData("another edge holds it")]
    public async Task A_star_on_an_edge_is_unmatched_when_only_another_place_holds_the_entry(string scenario)
    {
        var replaced = scenario == "the configuration holds it"
            ? Edge(new() { ["Accept"] = "a" }, new() { ["X-Token"] = Stored("config") })
            : Edge(new() { ["X-Token"] = Stored("edge") }, edgeId: "e2");

        (await RefusalAsync(replaced, Edge(new() { ["X-Token"] = "***" }))).Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
    }

    [Fact]
    public async Task A_protected_value_equal_to_the_stored_entry_passes()
    {
        var replaced = Edge(new() { ["X-Token"] = Stored("kept") });

        var admitted = await AdmitAsync(replaced, Edge(new() { ["X-Token"] = Stored("kept") }));

        SecretDocs.EdgeEntries(admitted.Definition)["X-Token"].Should().Be(Stored("kept"));
        codec.Protections.Should().BeEmpty();
    }

    [Theory]
    [InlineData("another ciphertext")]
    [InlineData("another workflow")]
    [InlineData("create")]
    [InlineData("only the configuration holds it")]
    public async Task Any_other_protected_value_is_unmatched_a_pasted_one_on_create_included(string scenario)
    {
        var replaced = scenario switch
        {
            "create" => null,
            "only the configuration holds it" => Edge(new() { ["Accept"] = "a" }, new() { ["X-Token"] = Stored("seen") }),
            _ => Edge(new() { ["X-Token"] = Stored("kept") }),
        };
        var pasted = scenario == "another workflow" ? FakeSecretCodec.Ciphertext(Elsewhere, "kept") : Stored("seen");

        var refusal = await RefusalAsync(replaced, Edge(new() { ["X-Token"] = pasted }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        refusal.Message.Should().NotContain(pasted);
    }

    [Fact]
    public async Task A_reference_on_an_edge_is_bound_when_new_and_dropped_when_gone()
    {
        var replaced = Edge(new() { ["X-Gone"] = "ref:vault:gone#k", ["X-Same"] = "ref:vault:same#k" });

        var admitted = await AdmitAsync(replaced, Edge(new() { ["X-New"] = "ref:vault:new#k", ["X-Same"] = "ref:vault:same#k" }));

        SecretDocs.EdgeEntries(admitted.Definition)["X-New"].Should().Be("ref:vault:new#k");
        admitted.BoundReferences.Should().Equal("ref:vault:new#k");
        admitted.DroppedReferences.Should().Equal("ref:vault:gone#k");
        codec.Admissions.Should().ContainSingle();
    }

    [Fact]
    public async Task A_reference_that_moves_from_the_configuration_to_an_edge_is_dropped_there_and_bound_here()
    {
        var replaced = Edge(new() { ["Accept"] = "a" }, new() { ["X-Ref"] = "ref:vault:r#k" });

        var admitted = await AdmitAsync(replaced, Edge(new() { ["X-Ref"] = "ref:vault:r#k" }));

        admitted.BoundReferences.Should().Equal("ref:vault:r#k");
        admitted.DroppedReferences.Should().Equal("ref:vault:r#k");
    }

    [Fact]
    public async Task A_star_over_a_stored_name_rule_plaintext_protects_it_when_there_is_a_codec()
    {
        var replaced = Edge(new() { ["Authorization"] = "Bearer legacy" });

        var admitted = await AdmitAsync(replaced, Edge(new() { ["Authorization"] = "***" }));

        SecretDocs.EdgeEntries(admitted.Definition)["Authorization"].Should().Be(Stored("Bearer legacy"));
    }
}
