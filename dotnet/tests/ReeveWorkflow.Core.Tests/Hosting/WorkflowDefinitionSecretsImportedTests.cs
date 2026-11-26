using FluentAssertions;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed class WorkflowDefinitionSecretsImportedTests
{
    private static readonly Guid Target = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Elsewhere = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly FakeSecretCodec codec = new();

    private IWorkflowDefinitionSecrets Secrets(bool withCodec = true) =>
        new WorkflowDefinitionSecrets(new SecretTestBlocks(), withCodec ? codec : null);

    private static WorkflowSecretSave Save(WorkflowDefinitionOrigin origin) => new(User, Target, origin);

    private static string Stored(string plaintext) => FakeSecretCodec.Ciphertext(Target, plaintext);

    private static string Headers(Dictionary<string, string> headers, string id = "h") => SecretDocs.Doc(SecretDocs.Http(id, headers));

    private Task<WorkflowSecretAdmission> ImportAsync(string? replaced, string definition, bool withCodec = true) =>
        Secrets(withCodec).AdmitAsync(Save(WorkflowDefinitionOrigin.Imported), replaced, definition, default).AsTask();

    private static WorkflowDroppedSecret Dropped(string key, string reason, string node = "h", string canvas = "", string? edge = null, string input = "Headers") =>
        new(canvas, node, input, key, edge, reason);

    [Fact]
    public async Task A_star_with_no_stored_entry_is_dropped_and_reported_and_the_rest_is_stored()
    {
        var admitted = await ImportAsync(null, Headers(new()
        {
            ["X-Star"] = "***", ["Accept"] = "text/plain", ["X-Ref"] = "ref:vault:a#k", ["X-New"] = "protect:fresh",
        }));

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string>
        {
            ["Accept"] = "text/plain", ["X-Ref"] = "ref:vault:a#k", ["X-New"] = Stored("fresh"),
        });
        admitted.DroppedSecrets.Should().Equal(Dropped("X-Star", "masked"));
        admitted.BoundReferences.Should().Equal("ref:vault:a#k");
    }

    [Fact]
    public async Task A_foreign_protected_value_is_dropped_and_its_ciphertext_is_stored_nowhere()
    {
        var foreign = FakeSecretCodec.Ciphertext(Elsewhere, "seen in an export");

        var admitted = await ImportAsync(null, Headers(new() { ["X-Foreign"] = foreign, ["Accept"] = "a" }));

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string> { ["Accept"] = "a" });
        admitted.Definition.Should().NotContain(foreign);
        admitted.DroppedSecrets.Should().Equal(Dropped("X-Foreign", "foreign-protected"));
    }

    [Fact]
    public async Task A_protected_value_equal_to_the_stored_entry_and_a_star_over_a_stored_entry_are_kept_not_dropped()
    {
        var replaced = Headers(new() { ["X-Token"] = Stored("kept"), ["X-Other"] = Stored("other") });

        var admitted = await ImportAsync(replaced, Headers(new() { ["X-Token"] = Stored("kept"), ["X-Other"] = "***" }));

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string>
        {
            ["X-Token"] = Stored("kept"), ["X-Other"] = Stored("other"),
        });
        admitted.DroppedSecrets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_protect_form_and_a_star_with_no_codec_are_dropped_and_a_plaintext_is_kept_as_written()
    {
        var admitted = await ImportAsync(null, Headers(new()
        {
            ["X-Custom"] = "protect:hunter2", ["X-Star"] = "***", ["Authorization"] = "Bearer t",
        }), withCodec: false);

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string> { ["Authorization"] = "Bearer t" });
        admitted.DroppedSecrets.Should().Equal(Dropped("X-Custom", "codec-missing"), Dropped("X-Star", "masked"));
        admitted.Definition.Should().NotContain("hunter2");
    }

    [Fact]
    public async Task A_protect_form_and_a_name_rule_plaintext_whose_protection_the_host_refuses_are_dropped_with_the_host_reason()
    {
        codec.RefuseProtect = "policy says no";

        var admitted = await ImportAsync(null, Headers(new()
        {
            ["X-Custom"] = "protect:hunter2", ["Authorization"] = "Bearer t", ["Accept"] = "text/plain",
        }));

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string> { ["Accept"] = "text/plain" });
        admitted.DroppedSecrets.Should().Equal(
            Dropped("X-Custom", "protect-refused: policy says no"), Dropped("Authorization", "protect-refused: policy says no"));
        admitted.Definition.Should().NotContain("hunter2").And.NotContain("Bearer t");
    }

    [Fact]
    public async Task A_star_over_a_stored_name_rule_plaintext_the_host_will_not_protect_is_dropped()
    {
        codec.RefuseProtect = "policy says no";

        var admitted = await ImportAsync(
            Headers(new() { ["Authorization"] = "Bearer legacy" }), Headers(new() { ["Authorization"] = "***" }));

        SecretDocs.Entries(admitted.Definition, "h").Should().BeEmpty();
        admitted.DroppedSecrets.Should().Equal(Dropped("Authorization", "protect-refused: policy says no"));
    }

    [Fact]
    public async Task The_references_the_host_refuses_are_dropped_and_admission_is_asked_again_without_them()
    {
        var replaced = Headers(new() { ["X-Old"] = "ref:vault:old#k", ["X-Kept"] = "ref:vault:kept#k" });
        codec.RefusedReferences.Add("ref:vault:new#k");

        var admitted = await ImportAsync(replaced, Headers(new()
        {
            ["X-New"] = "ref:vault:new#k", ["X-Kept"] = "ref:vault:kept#k", ["Accept"] = "a",
        }));

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string>
        {
            ["X-Kept"] = "ref:vault:kept#k", ["Accept"] = "a",
        });
        admitted.DroppedSecrets.Should().Equal(Dropped("X-New", "reference-refused: not yours"));
        codec.Admissions.Should().HaveCount(2);
        codec.Admissions[0].Bound.Should().Equal("ref:vault:new#k");
        codec.Admissions[1].Bound.Should().BeEmpty();
        codec.Admissions[1].Unbound.Should().Equal("ref:vault:old#k");
        admitted.BoundReferences.Should().BeEmpty();
        admitted.DroppedReferences.Should().Equal("ref:vault:old#k");
    }

    [Fact]
    public async Task Every_reference_the_save_binds_is_dropped_when_the_host_refuses_the_call_and_nothing_is_asked_again_when_nothing_is_left()
    {
        codec.RefuseAdmission = "not your secret";

        var admitted = await ImportAsync(null, Headers(new() { ["X-A"] = "ref:vault:a#k", ["X-B"] = "ref:vault:b#k", ["Accept"] = "a" }));

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string> { ["Accept"] = "a" });
        admitted.DroppedSecrets.Should().Equal(
            Dropped("X-A", "reference-refused: not your secret"), Dropped("X-B", "reference-refused: not your secret"));
        codec.Admissions.Should().ContainSingle();
        admitted.BoundReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refusal_of_the_references_the_save_only_drops_is_still_a_refusal_because_there_is_nothing_to_drop()
    {
        codec.RefuseAdmission = "not your secret";

        var refusal = await FluentActions.Awaiting(() => ImportAsync(
                Headers(new() { ["X-Old"] = "ref:vault:old#k" }), Headers(new() { ["Accept"] = "a" })))
            .Should().ThrowAsync<WorkflowSecretSaveRefusedException>();

        refusal.Which.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
        codec.Admissions.Should().ContainSingle("a call that binds nothing leaves nothing to drop, so the host is not asked again");
    }

    [Fact]
    public async Task A_key_with_a_tilde_and_a_key_written_with_a_JSON_escape_are_dropped_and_reported_by_their_decoded_names()
    {
        var text = Headers(new() { ["X~Star"] = "***", ["X-Star"] = "***", ["Accept"] = "a" }).Replace("\"X-Star\"", "\"X-St\\u0061r\"");

        var admitted = await ImportAsync(null, text);

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string> { ["Accept"] = "a" });
        admitted.DroppedSecrets.Should().Equal(Dropped("X~Star", "masked"), Dropped("X-Star", "masked"));
    }

    [Fact]
    public async Task A_restored_version_is_not_an_import_and_its_admission_refuses_the_origin_before_asking_the_host()
    {
        await FluentActions.Awaiting(() => Secrets().AdmitRestoredAsync(
                Save(WorkflowDefinitionOrigin.Imported), null, Headers(new() { ["X-Ref"] = "ref:vault:a#k" }), default).AsTask())
            .Should().ThrowAsync<NotSupportedException>();
        codec.Admissions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_dropped_sites_name_the_canvas_the_node_the_input_the_key_and_the_edge()
    {
        var definition = SecretDocs.EdgeFlow(new() { ["X-Edge"] = "***" }, new() { ["X-Conf"] = "***" });
        var inner = SecretDocs.Doc(SecretDocs.Box("box", SecretDocs.Http("in", new() { ["X-Inner"] = "***" })));

        var edge = await ImportAsync(null, definition);
        var boxed = await ImportAsync(null, inner);

        edge.DroppedSecrets.Should().BeEquivalentTo(
            [Dropped("X-Conf", "masked"), Dropped("X-Edge", "masked", edge: "e1")]);
        boxed.DroppedSecrets.Should().Equal(Dropped("X-Inner", "masked", node: "in", canvas: "box/Body"));
        SecretDocs.EdgeEntries(edge.Definition).Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_that_drops_nothing_reports_nothing_and_is_answered_as_it_came()
    {
        var definition = Headers(new() { ["Accept"] = "text/plain" });

        var admitted = await ImportAsync(null, definition);

        admitted.DroppedSecrets.Should().BeEmpty();
        admitted.Definition.Should().BeSameAs(definition);
    }

    [Theory]
    [InlineData("***")]
    [InlineData("protect:x")]
    [InlineData("ref:vault:a#k")]
    [InlineData("plain")]
    public async Task A_key_that_is_no_field_name_is_refused_for_an_import_too_and_never_named(string value)
    {
        var refusal = await FluentActions.Awaiting(() => ImportAsync(
                null, Headers(new() { ["Authorization: Bearer hunter2"] = value, ["X-Star"] = "***" })))
            .Should().ThrowAsync<WorkflowSecretSaveRefusedException>();

        refusal.Which.Code.Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
        refusal.Which.Message.Should().NotContain("hunter2");
        codec.Protections.Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_definition_is_refused_with_origin_Edited_as_the_save_rules_say()
    {
        var definition = Headers(new() { ["X-Star"] = "***", ["Accept"] = "a" });

        var refusal = await FluentActions.Awaiting(() =>
                Secrets().AdmitAsync(Save(WorkflowDefinitionOrigin.Edited), null, definition, default).AsTask())
            .Should().ThrowAsync<WorkflowSecretSaveRefusedException>();

        refusal.Which.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        (await ImportAsync(null, definition)).DroppedSecrets.Should().ContainSingle();
    }

    [Fact]
    public async Task The_dry_run_reports_the_stars_and_the_foreign_protected_values_and_asks_the_codec_for_neither_protection_nor_admission()
    {
        var definition = Headers(new()
        {
            ["X-Star"] = "***", ["X-Foreign"] = FakeSecretCodec.Ciphertext(Elsewhere, "x"), ["X-Custom"] = "protect:hunter2",
            ["Authorization"] = "Bearer t", ["X-Ref"] = "ref:vault:a#k", ["Accept"] = "a",
        });

        var predicted = Secrets().PredictImportDrops(definition);

        predicted.Should().Equal(Dropped("X-Star", "masked"), Dropped("X-Foreign", "foreign-protected"));
        codec.Protections.Should().BeEmpty();
        codec.Admissions.Should().BeEmpty();
    }

    [Fact]
    public void The_dry_run_with_no_codec_reports_the_stars_only()
    {
        var predicted = Secrets(withCodec: false).PredictImportDrops(Headers(new() { ["X-Star"] = "***", ["X-Foreign"] = "enc:x", ["X-Custom"] = "protect:y" }));

        predicted.Should().Equal(Dropped("X-Star", "masked"));
    }

    [Fact]
    public void The_dry_run_names_edge_literals_and_sub_canvases_and_reports_nothing_for_a_definition_without_drops()
    {
        var predicted = Secrets().PredictImportDrops(SecretDocs.EdgeFlow(new() { ["X-Edge"] = "***" }));

        predicted.Should().Equal(Dropped("X-Edge", "masked", edge: "e1"));
        Secrets().PredictImportDrops(Headers(new() { ["Accept"] = "a", ["X-Ref"] = "ref:vault:a#k" })).Should().BeEmpty();
        Secrets().PredictImportDrops("not json").Should().BeEmpty();
    }

    [Fact]
    public async Task The_dry_run_predicts_what_the_save_drops_for_these_two_forms()
    {
        var definition = SecretDocs.Doc(
            SecretDocs.Http("h", new() { ["X-Star"] = "***", ["X-Foreign"] = FakeSecretCodec.Ciphertext(Elsewhere, "x"), ["Accept"] = "a" }),
            SecretDocs.Box("box", SecretDocs.Http("in", new() { ["X-Inner"] = "***" })));

        var predicted = Secrets().PredictImportDrops(definition);
        var dropped = (await ImportAsync(null, definition)).DroppedSecrets;

        predicted.Should().BeEquivalentTo(dropped, o => o.WithStrictOrdering());
    }
}
