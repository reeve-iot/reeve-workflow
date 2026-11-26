using FluentAssertions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Tests.TestSupport;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Hosting;

public sealed class WorkflowDefinitionSecretsSaveTests
{
    private static readonly Guid Target = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Elsewhere = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly FakeSecretCodec codec = new();

    private IWorkflowDefinitionSecrets Secrets(bool withCodec = true) =>
        new WorkflowDefinitionSecrets(new SecretTestBlocks(), withCodec ? codec : null);

    private static WorkflowSecretSave Save() => new(User, Target, WorkflowDefinitionOrigin.Edited);

    private static string Stored(string plaintext) => FakeSecretCodec.Ciphertext(Target, plaintext);

    private static string Headers(Dictionary<string, string> headers, string id = "h") => SecretDocs.Doc(SecretDocs.Http(id, headers));

    private async Task<WorkflowSecretAdmission> AdmitAsync(string? replaced, string definition, bool withCodec = true) =>
        await Secrets(withCodec).AdmitAsync(Save(), replaced, definition, default);

    private async Task<WorkflowSecretSaveRefusedException> RefusalAsync(string? replaced, string definition, bool withCodec = true) =>
        (await FluentActions.Awaiting(() => AdmitAsync(replaced, definition, withCodec))
            .Should().ThrowAsync<WorkflowSecretSaveRefusedException>()).Which;

    [Fact]
    public async Task A_star_keeps_the_entry_stored_at_its_site()
    {
        var replaced = Headers(new() { ["X-Token"] = Stored("kept"), ["Accept"] = "text/plain" });

        var admitted = await AdmitAsync(replaced, Headers(new() { ["X-Token"] = "***", ["Accept"] = "text/html" }));

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string> { ["X-Token"] = Stored("kept"), ["Accept"] = "text/html" });
        codec.Protections.Should().BeEmpty();
        codec.Admissions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_star_keeps_a_plaintext_entry_stored_by_a_host_with_no_codec()
    {
        var replaced = Headers(new() { ["X-Token"] = "stored-in-clear" });

        var admitted = await AdmitAsync(replaced, Headers(new() { ["X-Token"] = "***" }), withCodec: false);

        SecretDocs.Entries(admitted.Definition, "h")["X-Token"].Should().Be("stored-in-clear");
    }

    [Fact]
    public async Task A_star_over_a_stored_name_rule_plaintext_protects_it_and_every_other_stored_form_is_kept_as_it_is()
    {
        var replaced = Headers(new()
        {
            ["Authorization"] = "Bearer legacy", ["X-Plain"] = "plain-note", ["X-Token"] = Stored("kept"), ["X-Ref"] = "ref:vault:r#k",
        });

        var admitted = await AdmitAsync(replaced, Headers(new()
        {
            ["Authorization"] = "***", ["X-Plain"] = "***", ["X-Token"] = "***", ["X-Ref"] = "***",
        }));

        SecretDocs.Entries(admitted.Definition, "h").Should().Equal(new Dictionary<string, string>
        {
            ["Authorization"] = Stored("Bearer legacy"), ["X-Plain"] = "plain-note", ["X-Token"] = Stored("kept"), ["X-Ref"] = "ref:vault:r#k",
        });
        codec.Protections.Should().ContainSingle().Which.Site.Key.Should().Be("Authorization");
        admitted.BoundReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task A_star_over_a_stored_name_rule_plaintext_is_refused_when_the_host_will_not_protect_it()
    {
        codec.RefuseProtect = "policy says no";

        var refusal = await RefusalAsync(
            Headers(new() { ["Authorization"] = "Bearer legacy" }), Headers(new() { ["Authorization"] = "***" }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
    }

    [Theory]
    [InlineData("Authorization: Bearer hunter2")]
    [InlineData("Bearer hunter2")]
    [InlineData("")]
    [InlineData("X-Token=hunter2")]
    public async Task A_key_that_is_no_field_name_is_refused_with_the_input_and_position_and_never_the_key(string key)
    {
        var refusal = await RefusalAsync(null, Headers(new() { ["Accept"] = "a", [key] = "v" }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
        refusal.Message.Should().Contain("'Headers'").And.Contain("'h'").And.NotContain("hunter2");
        if (key.Length == 0)
            refusal.Message.Should().Contain("row 2");
        codec.Protections.Should().BeEmpty();
    }

    [Theory]
    [InlineData("***")]
    [InlineData("protect:x")]
    [InlineData("ref:vault:a#k")]
    public async Task A_key_that_is_no_field_name_is_refused_whatever_its_value_is(string value)
    {
        var replaced = Headers(new() { ["Authorization: Bearer hunter2"] = Stored("kept") });

        (await RefusalAsync(replaced, Headers(new() { ["Authorization: Bearer hunter2"] = value }))).Code
            .Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
    }

    [Fact]
    public async Task A_key_that_is_no_field_name_on_an_edge_literal_is_refused_and_names_the_edge()
    {
        var refusal = await RefusalAsync(null, SecretDocs.EdgeFlow(new() { ["Authorization: Bearer hunter2"] = "v" }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
        refusal.Message.Should().Contain("'e1'").And.NotContain("hunter2");
    }

    [Fact]
    public async Task A_key_that_is_no_field_name_is_refused_before_any_entry_is_protected()
    {
        var refusal = await RefusalAsync(null, Headers(new() { ["X-A"] = "protect:one", ["Authorization"] = "Bearer two", ["Bad Key"] = "v" }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
        codec.Protections.Should().BeEmpty();
        codec.Admissions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"nested\":\"x\"}")]
    public async Task A_key_that_is_no_field_name_is_refused_when_its_value_is_no_string(string json)
    {
        var definition = Headers(new() { ["Accept"] = "a" }).Replace("\"Accept\":\"a\"", $"\"Accept\":\"a\",\"Authorization: Bearer hunter2\":{json}");

        var refusal = await RefusalAsync(null, definition);

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
        refusal.Message.Should().Contain("key 'Authorization...'").And.NotContain("hunter2");
    }

    [Fact]
    public async Task A_member_that_is_no_string_under_a_field_name_is_stored_as_it_was_sent()
    {
        var definition = Headers(new() { ["Accept"] = "a" }).Replace("\"Accept\":\"a\"", "\"Accept\":\"a\",\"X-Count\":1");

        var admitted = await AdmitAsync(null, definition);

        admitted.Definition.Should().Be(definition);
    }

    [Fact]
    public async Task Every_token_character_is_allowed_in_a_key()
    {
        var admitted = await AdmitAsync(null, Headers(new() { ["X-Api_Key.1~|`^'*+&%$#!"] = "v" }));

        SecretDocs.Entries(admitted.Definition, "h").Should().ContainKey("X-Api_Key.1~|`^'*+&%$#!");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("another key")]
    [InlineData("another node")]
    public async Task A_star_with_no_stored_entry_is_refused_as_unmatched_and_names_the_site(string scenario)
    {
        var replaced = scenario switch
        {
            "create" => null,
            "another key" => Headers(new() { ["Other"] = Stored("x") }),
            _ => Headers(new() { ["X-Token"] = Stored("x") }, id: "elsewhere"),
        };

        var refusal = await RefusalAsync(replaced, Headers(new() { ["X-Token"] = "***" }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        refusal.Message.Should().Contain("'h'").And.Contain("'Headers'").And.Contain("'X-Token'");
    }

    [Fact]
    public async Task Two_canvases_that_share_a_node_id_keep_their_own_entries()
    {
        var replaced = SecretDocs.Doc(
            SecretDocs.Http("h", new() { ["X-Token"] = Stored("root") }),
            SecretDocs.Box("box", SecretDocs.Http("h", new() { ["X-Token"] = Stored("inner") })));
        var request = SecretDocs.Doc(
            SecretDocs.Http("h", new() { ["X-Token"] = "***" }),
            SecretDocs.Box("box", SecretDocs.Http("h", new() { ["X-Token"] = "***" })));

        var admitted = await AdmitAsync(replaced, request);

        SecretDocs.Entries(admitted.Definition, "h")["X-Token"].Should().Be(Stored("root"));
        SecretDocs.Entries(admitted.Definition, "h", box: "box")["X-Token"].Should().Be(Stored("inner"));
    }

    [Fact]
    public async Task A_star_in_a_canvas_whose_node_id_exists_only_in_another_canvas_is_unmatched()
    {
        var replaced = SecretDocs.Doc(SecretDocs.Http("h", new() { ["X-Token"] = Stored("root") }), SecretDocs.Box("box"));
        var request = SecretDocs.Doc(
            SecretDocs.Http("h", new() { ["X-Token"] = "***" }),
            SecretDocs.Box("box", SecretDocs.Http("h", new() { ["X-Token"] = "***" })));

        (await RefusalAsync(replaced, request)).Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
    }

    [Fact]
    public async Task A_protect_form_is_protected_under_the_target_workflow_and_never_stored_as_given()
    {
        var admitted = await AdmitAsync(null, Headers(new() { ["X-Custom"] = "protect:hunter2", ["Accept"] = "text/plain" }));

        var entries = SecretDocs.Entries(admitted.Definition, "h");
        entries["X-Custom"].Should().Be(Stored("hunter2"));
        entries["Accept"].Should().Be("text/plain");
        admitted.Definition.Should().NotContain("protect:").And.NotContain("hunter2");
        var (site, plaintext) = codec.Protections.Should().ContainSingle().Which;
        plaintext.Should().Be("hunter2");
        (site.WorkflowId, site.NodeId, site.Input, site.Key, site.CanvasPath).Should().Be((Target, "h", "Headers", "X-Custom", ""));
    }

    [Fact]
    public async Task A_protect_form_names_the_canvas_of_a_node_in_a_sub_canvas()
    {
        await AdmitAsync(null, SecretDocs.Doc(SecretDocs.Box("box", SecretDocs.Http("h", new() { ["X-Custom"] = "protect:a" }))));

        codec.Protections.Should().ContainSingle().Which.Site.CanvasPath.Should().Be("box/Body");
    }

    [Fact]
    public async Task A_protect_form_protects_its_text_whatever_it_looks_like()
    {
        var admitted = await AdmitAsync(null, Headers(new() { ["X-Custom"] = "protect:enc:foreign" }));

        codec.Protections.Single().Plaintext.Should().Be("enc:foreign");
        SecretDocs.Entries(admitted.Definition, "h")["X-Custom"].Should().Be(Stored("enc:foreign"));
    }

    [Fact]
    public async Task A_protect_form_with_no_codec_is_refused_as_codec_missing()
    {
        var refusal = await RefusalAsync(null, Headers(new() { ["X-Custom"] = "protect:hunter2" }), withCodec: false);

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretCodecMissing);
        refusal.Message.Should().Contain("'X-Custom'").And.NotContain("hunter2");
    }

    [Fact]
    public async Task A_plaintext_the_name_rule_catches_is_protected_and_any_other_is_stored_as_written()
    {
        var admitted = await AdmitAsync(null, Headers(new() { ["Authorization"] = "Bearer t", ["X-Api-Key"] = "k", ["Accept"] = "text/plain" }));

        var entries = SecretDocs.Entries(admitted.Definition, "h");
        entries["Authorization"].Should().Be(Stored("Bearer t"));
        entries["X-Api-Key"].Should().Be(Stored("k"));
        entries["Accept"].Should().Be("text/plain");
        codec.Protections.Select(p => p.Site.Key).Should().BeEquivalentTo("Authorization", "X-Api-Key");
    }

    [Fact]
    public async Task A_plaintext_the_name_rule_catches_is_stored_as_written_when_there_is_no_codec()
    {
        var admitted = await AdmitAsync(null, Headers(new() { ["Authorization"] = "Bearer t" }), withCodec: false);

        SecretDocs.Entries(admitted.Definition, "h")["Authorization"].Should().Be("Bearer t");
    }

    [Theory]
    [InlineData("protect:hunter2", "X-Custom")]
    [InlineData("Bearer t", "Authorization")]
    public async Task A_protect_the_host_refuses_refuses_the_save_with_the_host_reason(string value, string key)
    {
        codec.RefuseProtect = "policy says no";

        var refusal = await RefusalAsync(null, Headers(new() { [key] = value }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
        refusal.Message.Should().Contain("policy says no").And.Contain($"'{key}'");
    }

    [Fact]
    public async Task A_new_reference_is_stored_as_written_and_reaches_one_admission_with_the_editors_user()
    {
        var admitted = await AdmitAsync(null, Headers(new() { ["X-A"] = "ref:vault:a#k", ["X-B"] = "ref:vault:b#k", ["Accept"] = "x" }));

        SecretDocs.Entries(admitted.Definition, "h")["X-A"].Should().Be("ref:vault:a#k");
        admitted.BoundReferences.Should().Equal("ref:vault:a#k", "ref:vault:b#k");
        admitted.DroppedReferences.Should().BeEmpty();
        var (save, bound, unbound) = codec.Admissions.Should().ContainSingle().Which;
        (save.UserId, save.WorkflowId, save.Origin).Should().Be((User, Target, WorkflowDefinitionOrigin.Edited));
        bound.Should().Equal("ref:vault:a#k", "ref:vault:b#k");
        unbound.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stored_reference_the_save_drops_or_replaces_is_admitted_as_dropped_in_the_same_call()
    {
        var replaced = Headers(new() { ["X-Gone"] = "ref:vault:gone#k", ["X-Swap"] = "ref:vault:old#k", ["X-Same"] = "ref:vault:same#k" });

        var admitted = await AdmitAsync(replaced, Headers(new() { ["X-Swap"] = "ref:vault:new#k", ["X-Same"] = "ref:vault:same#k" }));

        admitted.BoundReferences.Should().Equal("ref:vault:new#k");
        admitted.DroppedReferences.Should().Equal("ref:vault:gone#k", "ref:vault:old#k");
        codec.Admissions.Should().ContainSingle().Which.Unbound.Should().Equal("ref:vault:gone#k", "ref:vault:old#k");
    }

    [Fact]
    public async Task A_reference_in_a_node_the_save_removes_is_dropped()
    {
        var replaced = Headers(new() { ["X-Gone"] = "ref:vault:gone#k" });

        var admitted = await AdmitAsync(replaced, SecretDocs.Doc());

        admitted.DroppedReferences.Should().Equal("ref:vault:gone#k");
    }

    [Fact]
    public async Task No_admission_is_asked_when_nothing_is_bound_or_dropped()
    {
        var replaced = Headers(new() { ["X-Same"] = "ref:vault:same#k", ["Accept"] = "a" });

        var admitted = await AdmitAsync(replaced, Headers(new() { ["X-Same"] = "ref:vault:same#k", ["Accept"] = "b" }));
        await AdmitAsync(null, Headers(new() { ["Accept"] = "a" }));

        codec.Admissions.Should().BeEmpty();
        admitted.BoundReferences.Should().BeEmpty();
        admitted.DroppedReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_the_host_will_not_admit_references_for_is_refused_with_the_host_reason()
    {
        codec.RefuseAdmission = "not your secret";

        var refusal = await RefusalAsync(null, Headers(new() { ["X-A"] = "ref:vault:a#k" }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
        refusal.Message.Should().Contain("not your secret");
    }

    [Fact]
    public async Task A_protected_value_equal_to_the_stored_entry_passes_unchanged()
    {
        var replaced = Headers(new() { ["X-Token"] = Stored("kept") });

        var admitted = await AdmitAsync(replaced, Headers(new() { ["X-Token"] = Stored("kept") }));

        SecretDocs.Entries(admitted.Definition, "h")["X-Token"].Should().Be(Stored("kept"));
        codec.Protections.Should().BeEmpty();
    }

    [Theory]
    [InlineData("another ciphertext")]
    [InlineData("another key")]
    [InlineData("another workflow")]
    [InlineData("create")]
    public async Task Any_other_protected_value_is_refused_as_unmatched_a_pasted_one_on_create_included(string scenario)
    {
        var replaced = scenario == "create" ? null : Headers(new() { ["X-Token"] = Stored("kept") });
        var pasted = scenario switch
        {
            "another workflow" => FakeSecretCodec.Ciphertext(Elsewhere, "kept"),
            _ => Stored("seen in a trace"),
        };
        var key = scenario == "another key" ? "X-Other" : "X-Token";

        var refusal = await RefusalAsync(replaced, Headers(new() { [key] = pasted }));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        refusal.Message.Should().NotContain(pasted);
    }

    [Fact]
    public async Task An_input_that_is_not_secret_capable_and_a_block_nobody_registered_are_left_as_written()
    {
        var plain = new { id = "p", blockTypeId = "http", position = new { x = 0, y = 0 }, configuration = new { Tags = new { Authorization = "***", Other = "protect:x" } } };
        var unknown = new { id = "u", blockTypeId = "nobody", position = new { x = 0, y = 0 }, configuration = new { Headers = new { Authorization = "***", Other = "protect:x" } } };

        var admitted = await AdmitAsync(null, SecretDocs.Doc(plain, unknown));

        SecretDocs.Entries(admitted.Definition, "p", input: "Tags").Should().Equal(new Dictionary<string, string> { ["Authorization"] = "***", ["Other"] = "protect:x" });
        SecretDocs.Entries(admitted.Definition, "u").Should().Equal(new Dictionary<string, string> { ["Authorization"] = "***", ["Other"] = "protect:x" });
        codec.Protections.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expression_in_place_of_the_map_is_run_time_data_and_is_left_as_written()
    {
        var node = new { id = "h", blockTypeId = "http", position = new { x = 0, y = 0 }, configuration = new { Headers = "expr:${start.Headers}" } };

        var admitted = await AdmitAsync(null, SecretDocs.Doc(node));

        admitted.Definition.Should().Contain("expr:${start.Headers}");
        codec.Protections.Should().BeEmpty();
    }

    [Fact]
    public async Task A_definition_that_enters_nothing_is_answered_as_it_came()
    {
        var definition = Headers(new() { ["Accept"] = "text/plain", ["X-Same"] = "ref:vault:same#k" });

        var admitted = await AdmitAsync(definition, definition);

        admitted.Definition.Should().BeSameAs(definition);
    }

    [Fact]
    public async Task A_replacement_is_judged_against_its_own_stored_entries_under_the_target_workflow_it_is_given()
    {
        var replaced = Headers(new() { ["X-Token"] = Stored("kept") });
        var other = new WorkflowSecretSave(User, Elsewhere, WorkflowDefinitionOrigin.Edited);

        var admitted = await Secrets().AdmitAsync(other, replaced, Headers(new() { ["X-Token"] = "***", ["X-New"] = "protect:n" }), default);

        SecretDocs.Entries(admitted.Definition, "h")["X-New"].Should().Be(FakeSecretCodec.Ciphertext(Elsewhere, "n"));
        codec.Protections.Should().ContainSingle().Which.Site.WorkflowId.Should().Be(Elsewhere);
    }
}
