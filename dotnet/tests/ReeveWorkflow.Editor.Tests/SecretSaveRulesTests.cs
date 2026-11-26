using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Core.Tests.TestSupport;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class SecretSaveRulesTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static string Stored(Guid workflowId, string plaintext) => FakeSecretCodec.Ciphertext(workflowId, plaintext);

    private static Dictionary<string, string> Headers(string key, string value) => new() { [key] = value };

    private static async Task<InvalidWorkflowEditRequestException> RefusalAsync(Func<Task> save) =>
        (await save.Should().ThrowAsync<InvalidWorkflowEditRequestException>()).Which;

    [Fact]
    public async Task Create_stores_the_protected_form_under_the_new_workflow_and_never_the_one_it_was_given()
    {
        using var host = new SecretHost();

        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "protect:hunter2")))));

        var stored = await host.LatestAsync(created.Workflow.Id);
        SecretDocs.Entries(stored, "h")["X-Token"].Should().Be(Stored(created.Workflow.Id, "hunter2"));
        stored.Should().NotContain("protect:");
        host.Codec!.Protections.Should().ContainSingle().Which.Site.WorkflowId.Should().Be(created.Workflow.Id);
    }

    [Fact]
    public async Task A_star_keeps_the_entry_of_the_current_version_when_no_draft_exists()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "protect:v1")))));
        created.VersionPublished.Should().BeTrue("the graph compiles, so the first version is current");

        var updated = await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "***"), url: "https://other.test"))));

        SecretDocs.Entries(await host.LatestAsync(created.Workflow.Id), "h")["X-Token"].Should().Be(Stored(created.Workflow.Id, "v1"));
        updated.VersionNumber.Should().Be(2);
        host.Codec!.Protections.Should().HaveCount(1, "keeping an entry protects nothing again");
    }

    [Fact]
    public async Task A_star_keeps_the_entry_of_the_draft_a_save_replaces_not_the_current_versions()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "protect:v1")))));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "protect:v2")), AutoPublish: false)));

        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "***"), url: "https://other.test"), AutoPublish: false)));

        SecretDocs.Entries(await host.LatestAsync(created.Workflow.Id), "h")["X-Token"].Should().Be(Stored(created.Workflow.Id, "v2"));
    }

    [Fact]
    public async Task A_star_on_create_is_refused_as_unmatched_and_nothing_is_written()
    {
        using var host = new SecretHost();

        var refusal = await RefusalAsync(() => host.EditAsync(e =>
            e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "***"))))));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        (await host.WorkflowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_star_with_no_stored_entry_on_update_is_refused_and_the_workflow_is_unchanged()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "protect:v1")))));

        var refusal = await RefusalAsync(() => host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-New", "***"))))));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        (await host.VersionCountAsync(created.Workflow.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Two_canvases_that_share_a_node_id_keep_their_own_entries_on_update()
    {
        using var host = new SecretHost();
        var both = SecretDocs.Doc(
            SecretDocs.Http("h", Headers("X-Token", "protect:root")),
            SecretDocs.Box("box", SecretDocs.Http("h", Headers("X-Token", "protect:inner"))));
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, both)));
        var stars = SecretDocs.Doc(
            SecretDocs.Http("h", Headers("X-Token", "***"), url: "https://other.test"),
            SecretDocs.Box("box", SecretDocs.Http("h", Headers("X-Token", "***"), url: "https://other.test")));

        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, stars, AutoPublish: false)));

        var stored = await host.LatestAsync(created.Workflow.Id);
        SecretDocs.Entries(stored, "h")["X-Token"].Should().Be(Stored(created.Workflow.Id, "root"));
        SecretDocs.Entries(stored, "h", box: "box")["X-Token"].Should().Be(Stored(created.Workflow.Id, "inner"));
    }

    [Fact]
    public async Task A_protected_value_pasted_from_elsewhere_is_unmatched_on_create_and_on_update()
    {
        using var host = new SecretHost();
        var foreign = Stored(Guid.NewGuid(), "seen in a trace");

        var onCreate = await RefusalAsync(() => host.EditAsync(e =>
            e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", foreign))))));
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "protect:v1")))));
        var onUpdate = await RefusalAsync(() => host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", foreign))))));

        onCreate.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        onUpdate.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        (await host.WorkflowCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_protect_form_with_no_codec_is_refused_as_codec_missing_and_a_name_rule_plaintext_is_stored_as_written()
    {
        using var host = new SecretHost(withCodec: false);

        var refusal = await RefusalAsync(() => host.EditAsync(e =>
            e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Custom", "protect:x"))))));
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("Authorization", "Bearer t")))));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretCodecMissing);
        SecretDocs.Entries(await host.LatestAsync(created.Workflow.Id), "h")["Authorization"].Should().Be("Bearer t");
    }

    [Theory]
    [InlineData("X-Custom", "protect:x")]
    [InlineData("Authorization", "Bearer t")]
    public async Task A_protect_the_host_refuses_is_secret_refused_and_nothing_is_written(string key, string value)
    {
        using var host = new SecretHost();
        host.Codec!.RefuseProtect = "policy says no";

        var refusal = await RefusalAsync(() => host.EditAsync(e =>
            e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers(key, value))))));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
        refusal.Message.Should().Contain("policy says no");
        (await host.WorkflowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Bound_and_dropped_references_reach_one_admission_with_the_editors_user_and_are_reported_on_the_result()
    {
        using var host = new SecretHost();

        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null,
            SecretDocs.Flow(new() { ["X-A"] = "ref:vault:a#k", ["X-B"] = "ref:vault:b#k" }))));
        var dropped = await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null,
            SecretDocs.Flow(new() { ["X-A"] = "ref:vault:a#k", ["X-C"] = "ref:vault:c#k" }))));

        created.BoundReferences.Should().Equal("ref:vault:a#k", "ref:vault:b#k");
        created.DroppedReferences.Should().BeEmpty();
        dropped.BoundReferences.Should().Equal("ref:vault:c#k");
        dropped.DroppedReferences.Should().Equal("ref:vault:b#k");
        host.Codec!.Admissions.Should().HaveCount(2);
        var (save, bound, unbound) = host.Codec.Admissions[1];
        (save.UserId, save.WorkflowId).Should().Be((Owner, created.Workflow.Id));
        bound.Should().Equal("ref:vault:c#k");
        unbound.Should().Equal("ref:vault:b#k");
        host.Codec.Admissions[0].Save.WorkflowId.Should().Be(created.Workflow.Id, "a create's admission names the workflow it is about to make");
    }

    [Fact]
    public async Task A_save_that_binds_and_drops_nothing_asks_for_no_admission_and_a_save_that_writes_nothing_reports_nothing()
    {
        using var host = new SecretHost();
        var definition = SecretDocs.Flow(new() { ["X-A"] = "ref:vault:a#k", ["Accept"] = "x" });
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, definition)));
        host.Codec!.Admissions.Clear();

        var changed = await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null,
            SecretDocs.Flow(new() { ["X-A"] = "ref:vault:a#k", ["Accept"] = "y" }))));
        var repeated = await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Renamed", null,
            SecretDocs.Flow(new() { ["X-A"] = "ref:vault:a#k", ["Accept"] = "y" }))));

        host.Codec.Admissions.Should().BeEmpty();
        changed.BoundReferences.Should().BeEmpty();
        changed.DroppedReferences.Should().BeEmpty();
        repeated.VersionId.Should().BeNull("a repeated definition writes no version");
        repeated.BoundReferences.Should().BeEmpty();
        repeated.DroppedReferences.Should().BeEmpty();
    }

    [Fact]
    public async Task An_admission_the_host_refuses_refuses_the_save_and_the_result_lists_nothing_because_there_is_no_result()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("Accept", "x")))));
        host.Codec!.RefuseAdmission = "not your secret";

        var onUpdate = await RefusalAsync(() => host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:a#k"))))));
        var onCreate = await RefusalAsync(() => host.EditAsync(e => e.CreateAsync(Owner,
            new CreateWorkflowRequest("Second", null, SecretDocs.Flow(Headers("X-A", "ref:vault:a#k"))))));

        onUpdate.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
        onCreate.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
        onUpdate.Message.Should().Contain("not your secret");
        (await host.VersionCountAsync(created.Workflow.Id)).Should().Be(1);
        (await host.WorkflowCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_rollback_that_re_binds_a_reference_admits_it_and_reports_it()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:a#k")))));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("Accept", "x")))));
        host.Codec!.Admissions.Clear();

        var rolledBack = await host.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        rolledBack.BoundReferences.Should().Equal("ref:vault:a#k");
        rolledBack.DroppedReferences.Should().BeEmpty();
        var (save, bound, unbound) = host.Codec.Admissions.Should().ContainSingle().Which;
        (save.UserId, save.WorkflowId).Should().Be((Owner, created.Workflow.Id));
        bound.Should().Equal("ref:vault:a#k");
        unbound.Should().BeEmpty();
        SecretDocs.Entries(await host.LatestAsync(created.Workflow.Id), "h")["X-A"].Should().Be("ref:vault:a#k");
    }

    [Fact]
    public async Task A_rollback_copies_a_protected_entry_of_its_own_workflow_without_asking_it_to_match_the_version_it_replaces()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "protect:v1")))));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-Token", "protect:v2")))));

        var rolledBack = await host.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        rolledBack.VersionNumber.Should().Be(3);
        SecretDocs.Entries(await host.LatestAsync(created.Workflow.Id), "h")["X-Token"].Should().Be(Stored(created.Workflow.Id, "v1"));
        host.Codec!.Admissions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rollback_whose_references_the_host_refuses_is_refused_with_the_code_and_adds_no_version()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:a#k")))));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("Accept", "x")))));
        host.Codec!.RefuseAdmission = "not your secret";

        var refusal = await RefusalAsync(() => host.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1)));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
        (await host.VersionCountAsync(created.Workflow.Id)).Should().Be(2);
    }

    [Fact]
    public async Task Discarding_a_draft_by_sending_the_current_text_admits_and_reports_the_references_it_swaps()
    {
        using var host = new SecretHost();
        var current = SecretDocs.Flow(Headers("X-A", "ref:vault:a#k"));
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, current)));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:b#k")), AutoPublish: false)));
        (await host.DraftOfAsync(created.Workflow.Id)).Should().NotBeNull();
        host.Codec!.Admissions.Clear();

        var discarded = await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, current, AutoPublish: false)));

        (await host.DraftOfAsync(created.Workflow.Id)).Should().BeNull("the current text discards the draft");
        discarded.BoundReferences.Should().Equal("ref:vault:a#k");
        discarded.DroppedReferences.Should().Equal("ref:vault:b#k");
        var (save, bound, unbound) = host.Codec.Admissions.Should().ContainSingle().Which;
        (save.UserId, save.WorkflowId).Should().Be((Owner, created.Workflow.Id));
        bound.Should().Equal("ref:vault:a#k");
        unbound.Should().Equal("ref:vault:b#k");
    }

    [Fact]
    public async Task A_discard_whose_references_the_host_refuses_keeps_the_draft()
    {
        using var host = new SecretHost();
        var current = SecretDocs.Flow(Headers("X-A", "ref:vault:a#k"));
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, current)));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:b#k")), AutoPublish: false)));
        host.Codec!.RefuseAdmission = "not your secret";

        var refusal = await RefusalAsync(() => host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, current, AutoPublish: false))));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretRefused);
        (await host.DraftOfAsync(created.Workflow.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_rollback_drops_what_the_version_it_replaces_held_and_does_not_re_bind_what_both_hold()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null,
            SecretDocs.Flow(new() { ["X-A"] = "ref:vault:a#k", ["X-S"] = "ref:vault:s#k" }))));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null,
            SecretDocs.Flow(new() { ["X-B"] = "ref:vault:b#k", ["X-S"] = "ref:vault:s#k" }))));
        host.Codec!.Admissions.Clear();

        var rolledBack = await host.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        rolledBack.BoundReferences.Should().Equal("ref:vault:a#k");
        rolledBack.DroppedReferences.Should().Equal("ref:vault:b#k");
    }

    [Fact]
    public async Task A_rollback_with_a_draft_open_drops_the_references_of_the_draft_and_not_those_of_the_current_version()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:a#k")))));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:b#k")))));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:d#k")), AutoPublish: false)));
        host.Codec!.Admissions.Clear();

        var rolledBack = await host.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        rolledBack.BoundReferences.Should().Equal("ref:vault:a#k");
        rolledBack.DroppedReferences.Should().Equal("ref:vault:d#k");
    }

    [Fact]
    public async Task An_edge_literal_is_protected_on_create_and_a_star_on_one_with_nothing_stored_is_refused()
    {
        using var host = new SecretHost();

        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null,
            SecretDocs.EdgeFlow(Headers("Authorization", "Bearer hunter2")))));
        var onStar = await RefusalAsync(() => host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Second", null,
            SecretDocs.EdgeFlow(Headers("X-Custom", "***"))))));

        var stored = await host.LatestAsync(created.Workflow.Id);
        SecretDocs.EdgeEntries(stored)["Authorization"].Should().Be(Stored(created.Workflow.Id, "Bearer hunter2"));
        stored.Should().NotContain("hunter2");
        onStar.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        (await host.WorkflowCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_save_of_a_new_version_conflicts_when_a_draft_appeared_after_it_read_the_workflow_and_it_swaps_references()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:a#k")))));
        host.Codec!.WhileAsking = () => host.InsertDraftAsync(created.Workflow.Id, SecretDocs.Flow(Headers("X-A", "ref:vault:c#k")));

        await FluentActions.Awaiting(() => host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
                new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:d#k")), AutoPublish: false))))
            .Should().ThrowAsync<WorkflowDraftChangedException>();
    }

    [Theory]
    [InlineData("Authorization: Bearer hunter2")]
    [InlineData("Bearer hunter2")]
    public async Task A_key_that_is_no_field_name_is_refused_on_create_and_on_update_and_nothing_is_written(string key)
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("Accept", "x")))));

        var onUpdate = await RefusalAsync(() => host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers(key, "v"))))));
        var onCreate = await RefusalAsync(() => host.EditAsync(e =>
            e.CreateAsync(Owner, new CreateWorkflowRequest("Second", null, SecretDocs.Flow(Headers(key, "v"))))));

        onUpdate.Code.Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
        onCreate.Code.Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
        onUpdate.Message.Should().NotContain("hunter2");
        (await host.VersionCountAsync(created.Workflow.Id)).Should().Be(1);
        (await host.WorkflowCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_node_written_under_the_unlisted_config_key_is_refused_before_any_entry_of_it_is_read_and_nothing_is_written()
    {
        using var host = new SecretHost();
        const string unlisted = """{"nodes":[{"id":"h","blockTypeId":"http","position":{"x":0,"y":0},"config":{"Headers":{"Authorization":"Bearer hunter2"}}}],"edges":[]}""";

        var onCreate = await FluentActions.Awaiting(() => host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, unlisted))))
            .Should().ThrowAsync<InvalidWorkflowDefinitionException>();

        onCreate.Which.Message.Should().Contain("config").And.NotContain("hunter2");
        (await host.WorkflowCountAsync()).Should().Be(0);
        host.Codec!.Protections.Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_into_a_draft_another_save_rewrote_after_this_one_read_it_conflicts()
    {
        using var host = new SecretHost();
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:a#k")))));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:b#k")), AutoPublish: false)));
        var concurrent = SecretDocs.Flow(Headers("X-A", "ref:vault:c#k"));
        host.Codec!.WhileAsking = () => host.RewriteDraftAsync(created.Workflow.Id, concurrent);

        await FluentActions.Awaiting(() => host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
                new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:d#k")), AutoPublish: false))))
            .Should().ThrowAsync<WorkflowDraftChangedException>();
    }

    [Fact]
    public async Task A_discard_of_a_draft_another_save_rewrote_after_this_one_read_it_conflicts_and_keeps_that_draft()
    {
        using var host = new SecretHost();
        var current = SecretDocs.Flow(Headers("X-A", "ref:vault:a#k"));
        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, current)));
        await host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("X-A", "ref:vault:b#k")), AutoPublish: false)));
        host.Codec!.WhileAsking = () => host.RewriteDraftAsync(created.Workflow.Id, SecretDocs.Flow(Headers("X-A", "ref:vault:c#k")));

        await FluentActions.Awaiting(() => host.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
                new UpdateWorkflowRequest("Ship", null, current, AutoPublish: false))))
            .Should().ThrowAsync<WorkflowDraftChangedException>();

        (await host.DraftOfAsync(created.Workflow.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_imported_create_stores_the_version_without_what_an_edit_would_refuse_and_the_references_the_host_refused_and_reports_each_site()
    {
        using var host = new SecretHost();
        host.Codec!.RefusedReferences.Add("ref:vault:b#k");
        var definition = SecretDocs.Flow(new()
        {
            ["X-Star"] = "***", ["X-Foreign"] = Stored(Guid.NewGuid(), "seen in an export"), ["X-A"] = "ref:vault:a#k",
            ["X-B"] = "ref:vault:b#k", ["X-New"] = "protect:fresh", ["Accept"] = "text/plain",
        });

        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, definition, Origin: WorkflowDefinitionOrigin.Imported)));

        created.VersionPublished.Should().BeTrue("what is left compiles");
        SecretDocs.Entries(await host.LatestAsync(created.Workflow.Id), "h").Should().Equal(new Dictionary<string, string>
        {
            ["X-New"] = Stored(created.Workflow.Id, "fresh"), ["Accept"] = "text/plain",
        });
        created.DroppedSecrets.Select(d => (d.Key, d.Reason)).Should().BeEquivalentTo(
        [
            ("X-Star", "masked"), ("X-Foreign", "foreign-protected"),
            ("X-A", "reference-refused: not yours"), ("X-B", "reference-refused: not yours"),
        ]);
        created.BoundReferences.Should().BeEmpty("the host refused the call, so every reference the save bound is left out");
    }

    [Fact]
    public async Task The_same_create_with_origin_Edited_is_refused_and_writes_nothing()
    {
        using var host = new SecretHost();
        var definition = SecretDocs.Flow(new() { ["X-Star"] = "***", ["Accept"] = "text/plain" });

        var refusal = await RefusalAsync(() => host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, definition, Origin: WorkflowDefinitionOrigin.Edited))));
        var imported = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, definition, Origin: WorkflowDefinitionOrigin.Imported)));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretValueUnmatched);
        (await host.WorkflowCountAsync()).Should().Be(1);
        imported.DroppedSecrets.Should().ContainSingle();
    }

    [Fact]
    public async Task An_imported_create_still_refuses_a_key_that_is_no_field_name()
    {
        using var host = new SecretHost();

        var refusal = await RefusalAsync(() => host.EditAsync(e => e.CreateAsync(Owner,
            new CreateWorkflowRequest("Ship", null, SecretDocs.Flow(Headers("Authorization: Bearer hunter2", "v")), Origin: WorkflowDefinitionOrigin.Imported))));

        refusal.Code.Should().Be(WorkflowSaveRefusalCodes.SecretKeyInvalid);
        (await host.WorkflowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_imported_create_whose_every_entry_is_dropped_still_publishes_an_empty_map()
    {
        using var host = new SecretHost();

        var created = await host.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null,
            SecretDocs.Flow(new() { ["X-Star"] = "***" }), Origin: WorkflowDefinitionOrigin.Imported)));

        created.VersionPublished.Should().BeTrue();
        created.DroppedSecrets.Should().ContainSingle();
        SecretDocs.Entries(await host.LatestAsync(created.Workflow.Id), "h").Should().BeEmpty();
    }

    private sealed class SecretHost : IDisposable
    {
        private readonly SqliteConnection connection = new("Filename=:memory:");
        private readonly ServiceProvider provider;

        public SecretHost(bool withCodec = true)
        {
            connection.Open();
            Codec = withCodec ? new FakeSecretCodec() : null;
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IWorkflowBlockFactory>(SecretTestBlocks.Http);
            services.AddSingleton<IWorkflowBlockFactory>(SecretTestBlocks.Box);
            if (Codec is not null)
                services.AddSingleton<IWorkflowSecretCodec>(Codec);
            services.AddWorkflows(options =>
            {
                options.ConfigureDbContext(builder => builder.UseSqlite(connection));
            }).AddWorkflowEditor();
            provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        }

        public FakeSecretCodec? Codec { get; }

        public async Task<T> EditAsync<T>(Func<IWorkflowEditor, Task<T>> work)
        {
            using var scope = provider.CreateScope();
            return await work(scope.ServiceProvider.GetRequiredService<IWorkflowEditor>());
        }

        private async Task<T> ReadAsync<T>(Func<WorkflowDbContext, Task<T>> read)
        {
            using var scope = provider.CreateScope();
            return await read(scope.ServiceProvider.GetRequiredService<WorkflowDbContext>());
        }

        public Task<string> LatestAsync(Guid workflowId) => ReadAsync(context => context.WorkflowVersions
            .Where(v => v.WorkflowId == workflowId).OrderByDescending(v => v.VersionNumber).Select(v => v.DefinitionJson).FirstAsync());

        public Task<int> VersionCountAsync(Guid workflowId) =>
            ReadAsync(context => context.WorkflowVersions.CountAsync(v => v.WorkflowId == workflowId));

        public Task<Guid?> DraftOfAsync(Guid workflowId) =>
            ReadAsync(context => context.Workflows.Where(w => w.Id == workflowId).Select(w => w.DraftVersionId).SingleAsync());

        public async Task RewriteDraftAsync(Guid workflowId, string definition)
        {
            var draft = await DraftOfAsync(workflowId);
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowVersions
                .Where(v => v.Id == draft)
                .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, definition));
        }

        public async Task InsertDraftAsync(Guid workflowId, string definition)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var version = new WorkflowVersion(Guid.NewGuid(), workflowId, 2, definition, null, null, Owner, DateTimeOffset.UtcNow);
            db.WorkflowVersions.Add(version);
            await db.SaveChangesAsync();
            await db.Workflows.Where(w => w.Id == workflowId)
                .ExecuteUpdateAsync(set => set.SetProperty(w => w.DraftVersionId, (Guid?)version.Id));
        }

        public Task<int> WorkflowCountAsync() => ReadAsync(context => context.Workflows.CountAsync());

        public void Dispose()
        {
            provider.Dispose();
            connection.Dispose();
        }
    }
}
