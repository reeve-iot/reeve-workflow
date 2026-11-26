using System.Data.Common;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Editor;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed partial class PublishGateOnPostgresTests(ClusterBackends backends)
{
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string ValidB = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"noop","position":{"x":240,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{}}]}""";
    private const string ValidC = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"c","blockTypeId":"noop","position":{"x":240,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"c","mappings":{}}]}""";
    private const string Unknown = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"gone.block","position":{"x":240,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n1","mappings":{}}]}""";
    private const string Retry3 = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"noop","position":{"x":240,"y":0},"configuration":{"retryCount":3}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""";
    private const string Retry30 = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"noop","position":{"x":240,"y":0},"configuration":{"retryCount":3.0}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""";
    private const string Retry1e0 = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"noop","position":{"x":240,"y":0},"configuration":{"retryCount":1e0}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""";
    private const string OneInput = """[{"name":"n","type":"Int"}]""";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Changed_a_save_that_repeats_the_current_documents_respelled_and_reordered_writes_no_version()
    {
        await using var desk = await DeskAsync("gate_changed");
        var created = await desk.CreateAsync(Valid, OneInput);
        const string respelled = """{ "nodes" : [ { "configuration" : { }, "blockTypeId" : "WorkflowInput", "position" : { "y" : 0, "x" : 0 }, "id" : "start" } ],"edges":[] }""";

        var asSent = await desk.SaveAsync(created.Workflow.Id, Valid, autoPublish: true, inputs: OneInput);
        var asRespelled = await desk.SaveAsync(created.Workflow.Id, respelled, autoPublish: true, inputs: """[ {"type":"Int", "name":"n"} ]""");

        asSent.VersionId.Should().BeNull("the request's documents are the current version's, as jsonb stores them");
        asRespelled.VersionId.Should().BeNull();
        asRespelled.VersionPublished.Should().BeFalse();
        (await desk.NumbersAsync(created.Workflow.Id)).Should().Equal(1);
        (await desk.RevisionAsync(created.Workflow.Id)).Should().Be(3, "each save moves the create's revision 1 by one, though neither writes a version");
    }

    [Fact]
    public async Task Changed_a_default_save_equal_to_the_draft_as_stored_publishes_it_if_it_compiles_and_reports_it_if_not()
    {
        await using var desk = await DeskAsync("gate_changed_republish");
        var valid = await desk.CreateAsync(Valid);
        var validDraft = await desk.SaveAsync(valid.Workflow.Id, ValidB);
        var invalid = await desk.CreateAsync(Valid);
        var invalidDraft = await desk.SaveAsync(invalid.Workflow.Id, Unknown);
        const string validBRespelled = """{ "edges" : [ {"targetNodeId":"b", "sourceNodeId":"start", "id":"e","mappings":{}} ], "nodes" : [ {"configuration":{},"blockTypeId":"WorkflowInput","position":{"y":0,"x":0},"id":"start"}, {"position":{"y":0,"x":240},"blockTypeId":"noop","configuration":{ },"id":"b"} ] }""";

        var published = await desk.SaveAsync(valid.Workflow.Id, validBRespelled, autoPublish: true);
        var refused = await desk.SaveAsync(invalid.Workflow.Id, Unknown, autoPublish: true);

        published.VersionPublished.Should().BeTrue();
        published.VersionId.Should().Be(validDraft.VersionId);
        (await desk.RowAsync(valid.Workflow.Id)).CurrentVersionId.Should().Be(validDraft.VersionId);
        (await desk.NumbersAsync(valid.Workflow.Id)).Should().Equal(1, 2);
        refused.VersionPublished.Should().BeFalse();
        refused.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        (await desk.RowAsync(invalid.Workflow.Id)).DraftVersionId.Should().Be(invalidDraft.VersionId);
    }

    [Fact]
    public async Task Changed_is_judged_as_stored_against_the_draft_then_the_current_version_each_document_alone()
    {
        await using var desk = await DeskAsync("gate_changed_draft");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        var revision = await desk.RevisionAsync(created.Workflow.Id);
        const string validBRespelled = """{ "edges" : [ {"targetNodeId":"b", "sourceNodeId":"start", "id":"e","mappings":{}} ], "nodes" : [ {"configuration":{},"blockTypeId":"WorkflowInput","position":{"y":0,"x":0},"id":"start"}, {"position":{"y":0,"x":240},"blockTypeId":"noop","configuration":{ },"id":"b"} ] }""";
        const string validRespelled = """{ "nodes" : [ { "configuration" : { }, "blockTypeId" : "WorkflowInput", "position" : { "y" : 0, "x" : 0 }, "id" : "start" } ],"edges":[] }""";

        var same = await desk.SaveAsync(created.Workflow.Id, validBRespelled);
        (await desk.RevisionAsync(created.Workflow.Id)).Should().Be(revision + 1, "a save that writes no version still moves the revision by one");
        var outputs = await desk.SaveAsync(created.Workflow.Id, validBRespelled, outputs: OneInput);
        var discarded = await desk.SaveAsync(created.Workflow.Id, validRespelled, autoPublish: true);

        same.VersionId.Should().BeNull("the request is the draft as jsonb stores it");
        outputs.VersionId.Should().Be(draft.VersionId, "only the output list differs, and it is written");
        (await desk.VersionAsync(draft.VersionId!.Value)).OutputParametersJson.Should().Be(await desk.AsStoredAsync(OneInput));
        discarded.VersionId.Should().BeNull();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.DraftVersionId.Should().BeNull("the request is the current version as jsonb stores it");
        row.CurrentVersionId.Should().Be(created.VersionId);
        (await desk.NumbersAsync(created.Workflow.Id)).Should().Equal(1, 2);
    }

    [Fact]
    public async Task An_auto_published_save_of_1e0_is_published_valid_under_the_stored_texts_digest()
    {
        await using var desk = await DeskAsync("gate_1e0");
        var created = await desk.CreateAsync(Valid);

        var saved = await desk.SaveAsync(created.Workflow.Id, Retry1e0, autoPublish: true);

        saved.VersionPublished.Should().BeTrue("jsonb stores 1e0 as 1, and the gate compiles what it stored");
        var stored = await desk.VersionAsync(saved.VersionId!.Value);
        stored.DefinitionJson.Should().Contain("\"retryCount\": 1}");
        var compiler = desk.Services.GetRequiredService<WorkflowCompilationService>();
        var asStored = await compiler.EnsureAsync(Copy(stored), null, default);
        var asSent = await compiler.EnsureAsync(Copy(stored, Retry1e0), null, default);
        asSent.IsValid.Should().BeFalse("a door that compiled the request's text would have refused it");
        (await desk.VerdictsAsync(saved.VersionId!.Value)).Should().Equal((asStored.DefinitionDigest, true));
    }

    [Fact]
    public async Task Each_door_leaves_one_verdict_row_for_the_version_it_wrote()
    {
        await using var desk = await DeskAsync("gate_one_verdict");
        var created = await desk.CreateAsync(Valid);
        (await desk.VerdictsAsync(created.VersionId!.Value)).Should().HaveCount(1, "create");

        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        (await desk.VerdictsAsync(draft.VersionId!.Value)).Should().HaveCount(1, "a draft save");

        await desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));
        (await desk.VerdictsAsync(draft.VersionId!.Value)).Should().HaveCount(1, "publish compiles the text the save recorded");

        var auto = await desk.SaveAsync(created.Workflow.Id, ValidC, autoPublish: true);
        (await desk.VerdictsAsync(auto.VersionId!.Value)).Should().HaveCount(1, "an auto-published save");

        var copy = await desk.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));
        (await desk.VerdictsAsync(copy.VersionId!.Value)).Should().HaveCount(1, "rollback, under the copy's id");
    }

    [Fact]
    public async Task A_save_between_an_auto_publishs_commit_and_its_read_makes_the_auto_publish_conflict()
    {
        await using var desk = await DeskAsync("gate_save_after_auto_commit");
        var created = await desk.CreateAsync(Valid);
        desk.Hooks.AfterCommit(1, () => desk.SaveAsync(created.Workflow.Id, Unknown));

        var auto = () => desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);

        await auto.Should().ThrowAsync<WorkflowDraftChangedException>("the draft it read back is another save's");
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersion!.IsPublished.Should().BeFalse();
        row.DraftVersion.DefinitionJson.Should().Be(await desk.AsStoredAsync(Unknown), "the second save rewrote the first's draft");
    }

    [Fact]
    public async Task A_rollback_copys_stored_documents_equal_its_targets_ordinal()
    {
        await using var desk = await DeskAsync("gate_rollback_copy");
        const string spaced = """{"nodes":[ {"id":"start", "blockTypeId":"WorkflowInput", "position":{"x":1E2,"y":-0.0},"configuration":{}} ],"edges":[]}""";
        var created = await desk.CreateAsync(spaced, OneInput, "[ ]");
        await desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);

        var copy = await desk.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        var target = await desk.VersionAsync(created.VersionId!.Value);
        var stored = await desk.VersionAsync(copy.VersionId!.Value);
        (stored.DefinitionJson, stored.InputParametersJson, stored.OutputParametersJson)
            .Should().Be((target.DefinitionJson, target.InputParametersJson, target.OutputParametersJson));
    }

    [Fact]
    public async Task Rr_t17r1_jsonb_reads_back_as_itself_once_stored()
    {
        await using var desk = await DeskAsync("gate_jsonb_idempotent");
        await using var connection = new NpgsqlConnection(desk.ConnectionString);
        await connection.OpenAsync();
        var moved = new List<string>();

        foreach (var text in JsonbCorpus())
        {
            var once = await StoreAsync(connection, text);
            if (await StoreAsync(connection, once) != once)
                moved.Add(text);
        }

        moved.Should().BeEmpty();
    }

    [Fact]
    public async Task Q1_1_and_Q1_4a_a_save_that_commits_between_the_gates_read_and_its_promote_makes_the_promote_conflict()
    {
        await using var desk = await DeskAsync("gate_q1_1");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        desk.Hooks.BeforeBegin(1, () => desk.SaveAsync(created.Workflow.Id, Unknown));

        var publish = () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersionId.Should().Be(draft.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(Unknown));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Q1_5_and_B3c_a_save_that_changes_one_parameter_list_makes_the_promote_conflict(bool inputs)
    {
        await using var desk = await DeskAsync("gate_q1_5_" + inputs.ToString().ToLowerInvariant());
        var created = await desk.CreateAsync(Valid);
        await desk.SaveAsync(created.Workflow.Id, ValidB);
        desk.Hooks.BeforeBegin(1, () => desk.SaveAsync(created.Workflow.Id, ValidB,
            inputs: inputs ? OneInput : null, outputs: inputs ? null : OneInput));

        var publish = () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        (inputs ? row.DraftVersion!.InputParametersJson : row.DraftVersion!.OutputParametersJson).Should().NotBeNull();
    }

    [Fact]
    public async Task Q1_6_a_save_that_commits_after_the_promote_began_and_before_its_swap_makes_it_conflict()
    {
        await using var desk = await DeskAsync("gate_q1_6");
        var created = await desk.CreateAsync(Valid);
        await desk.SaveAsync(created.Workflow.Id, ValidB);
        desk.Hooks.BeforeCommand(UpdatesWorkflows, () => desk.SaveAsync(created.Workflow.Id, Unknown));

        var publish = () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        desk.Hooks.Fired.Should().Be(1);
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(Unknown));
    }

    [Fact]
    public async Task Q5_and_B3a_a_save_that_read_the_draft_before_a_publish_committed_writes_nothing()
    {
        await using var desk = await DeskAsync("gate_q5");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        desk.Hooks.BeforeCommand(UpdatesWorkflows, () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id)));

        var save = () => desk.SaveAsync(created.Workflow.Id, Unknown);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(draft.VersionId);
        row.CurrentVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(ValidB), "the published row keeps what was compiled");
        row.DraftVersionId.Should().BeNull();
    }

    [Fact]
    public async Task Auto_then_stale_save_a_save_that_read_the_draft_before_an_auto_publish_committed_writes_nothing()
    {
        await using var desk = await DeskAsync("gate_auto_then_stale");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        desk.Hooks.BeforeCommand(UpdatesWorkflows, () => desk.SaveAsync(created.Workflow.Id, ValidC, autoPublish: true));

        var save = () => desk.SaveAsync(created.Workflow.Id, Unknown);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(draft.VersionId);
        row.CurrentVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(ValidC));
        row.DraftVersionId.Should().BeNull();
    }

    [Fact]
    public async Task A_save_that_read_the_draft_before_a_rollback_committed_writes_nothing()
    {
        await using var desk = await DeskAsync("gate_s1_rollback");
        var created = await desk.CreateAsync(Valid);
        await desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidC);
        desk.Hooks.BeforeCommand(UpdatesWorkflows, () => desk.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1)));

        var save = () => desk.SaveAsync(created.Workflow.Id, Unknown);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersion!.VersionNumber.Should().Be(4);
        row.DraftVersionId.Should().BeNull();
        var orphan = await desk.VersionAsync(draft.VersionId!.Value);
        orphan.DefinitionJson.Should().Be(await desk.AsStoredAsync(ValidC), "the stale save wrote nothing into it");
        orphan.IsPublished.Should().BeFalse();
    }

    [Fact]
    public async Task Rollback_vs_save_a_save_that_commits_after_the_rollbacks_read_makes_the_rollback_conflict()
    {
        await using var desk = await DeskAsync("gate_rollback_vs_save");
        var created = await desk.CreateAsync(Valid);
        await desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);
        desk.Hooks.BeforeBegin(1, () => desk.SaveAsync(created.Workflow.Id, ValidC));

        var rollback = () => desk.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        await rollback.Should().ThrowAsync<WorkflowDraftChangedException>();
        (await desk.NumbersAsync(created.Workflow.Id)).Should().Equal(1, 2, 3);
        (await desk.RowAsync(created.Workflow.Id)).CurrentVersion!.VersionNumber.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A1_a_new_current_version_and_a_new_draft_between_the_gates_read_and_its_promote_make_it_conflict(bool viaRollback)
    {
        await using var desk = await DeskAsync("gate_a1_" + viaRollback.ToString().ToLowerInvariant());
        var created = await desk.CreateAsync(Valid);
        if (viaRollback)
            await desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);
        var first = await desk.SaveAsync(created.Workflow.Id, ValidC);
        desk.Hooks.BeforeBegin(1, async () =>
        {
            if (viaRollback)
                await desk.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));
            else
                await desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);
            await desk.SaveAsync(created.Workflow.Id, Unknown);
        });

        var publish = () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(viaRollback ? Valid : ValidB));
        row.DraftVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(Unknown));
        row.DraftVersion.IsPublished.Should().BeFalse();
        if (viaRollback)
            (await desk.VersionAsync(first.VersionId!.Value)).IsPublished.Should().BeFalse();
    }

    [Fact]
    public async Task B2_a_save_that_rewrites_3_as_3_0_between_the_compile_and_the_promote_makes_it_conflict()
    {
        await using var desk = await DeskAsync("gate_b2");
        var created = await desk.CreateAsync(Valid);
        await desk.SaveAsync(created.Workflow.Id, Retry3);
        desk.Hooks.BeforeBegin(1, () => desk.SaveAsync(created.Workflow.Id, Retry30));

        var publish = () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        (await desk.RowAsync(created.Workflow.Id)).CurrentVersionId.Should().Be(created.VersionId);
        var again = () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));
        await again.Should().ThrowAsync<WorkflowDoesNotCompileException>("jsonb keeps 3.0, and the compiler refuses it");
    }

    [Fact]
    public async Task Auto_vs_publish_an_auto_publish_inside_the_publishs_window_makes_the_publish_conflict()
    {
        await using var desk = await DeskAsync("gate_auto_in_publish");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        desk.Hooks.BeforeBegin(1, () => desk.SaveAsync(created.Workflow.Id, ValidC, autoPublish: true));

        var publish = () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(draft.VersionId);
        row.CurrentVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(ValidC));
    }

    [Fact]
    public async Task Auto_vs_publish_a_publish_between_an_auto_publishs_commit_and_its_read_makes_the_auto_publish_conflict()
    {
        await using var desk = await DeskAsync("gate_publish_in_auto");
        var created = await desk.CreateAsync(Valid);
        desk.Hooks.AfterCommit(1, () => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id)));

        var save = () => desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        (await desk.RowAsync(created.Workflow.Id)).CurrentVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(ValidB));
    }

    [Fact]
    public async Task Two_default_saves_the_second_to_commit_overtakes_the_first_which_gets_a_conflict()
    {
        await using var desk = await DeskAsync("gate_two_default_saves");
        var created = await desk.CreateAsync(Valid);
        WorkflowEditResult? second = null;
        desk.Hooks.AfterCommit(1, async () => second = await desk.SaveAsync(created.Workflow.Id, ValidC, autoPublish: true));

        var first = () => desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);

        await first.Should().ThrowAsync<WorkflowDraftChangedException>();
        second!.VersionPublished.Should().BeTrue();
        (await desk.RowAsync(created.Workflow.Id)).CurrentVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(ValidC));
    }

    [Fact]
    public async Task P8_publish_compiles_the_draft_as_stored_and_not_as_its_context_tracked_it()
    {
        await using var desk = await DeskAsync("gate_p8");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);

        var publish = async () =>
        {
            await using var scope = desk.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            await context.WorkflowVersions.SingleAsync(v => v.Id == draft.VersionId);
            await desk.SaveAsync(created.Workflow.Id, Unknown);
            return await scope.ServiceProvider.GetRequiredService<IWorkflowEditor>().PublishAsync(Owner, created.Workflow.Id);
        };

        await publish.Should().ThrowAsync<WorkflowDoesNotCompileException>();
        (await desk.RowAsync(created.Workflow.Id)).CurrentVersionId.Should().Be(created.VersionId);
    }

    [Fact]
    public async Task Create_visibility_a_peers_bump_of_a_workflow_being_created_changes_no_row()
    {
        await using var desk = await DeskAsync("gate_create_visibility");
        var bumped = -1;
        desk.Hooks.BeforeCommand(
            sql => sql.StartsWith("INSERT INTO automation.workflow_versions", StringComparison.Ordinal),
            async () => bumped = await desk.ExecuteAsync("UPDATE automation.workflows SET revision = revision + 1"));

        var created = await desk.CreateAsync(Valid);

        bumped.Should().Be(0, "no other transaction sees the row before the create commits");
        (await desk.RevisionAsync(created.Workflow.Id)).Should().Be(1, "only the promote at 0 moved it");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Q1_2_Q1_3_Q1_4b_and_B2_held_a_save_holding_its_lock_at_the_promote_makes_it_conflict(bool rewritesToThreePointZero)
    {
        await using var desk = await DeskAsync("gate_q1_2_" + rewritesToThreePointZero.ToString().ToLowerInvariant());
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, rewritesToThreePointZero ? Retry3 : ValidB);
        await using var save = await desk.HoldAsync(
            "UPDATE automation.workflows SET revision = revision + 1 WHERE id = @w",
            "UPDATE automation.workflow_versions SET definition_json = @d::jsonb WHERE id = @v AND NOT is_published",
            ("w", created.Workflow.Id), ("v", draft.VersionId!.Value), ("d", rewritesToThreePointZero ? Retry30 : Unknown));

        var publish = Task.Run(() => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id)));
        (await desk.WaitsInAsync("UPDATE automation.workflows")).Should().BeTrue("Q1.3: the gate's read did not wait for the held save; its promote does");
        await save.CommitAsync();

        await publish.Awaiting(p => p).Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersion!.IsPublished.Should().BeFalse();
    }

    [Fact]
    public async Task B3b_a_save_across_a_publish_holding_its_lock_conflicts()
    {
        await using var desk = await DeskAsync("gate_b3b");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        await using var publish = await desk.HoldAsync(
            "UPDATE automation.workflows SET current_version_id = @v, draft_version_id = NULL, revision = revision + 1 WHERE id = @w",
            "UPDATE automation.workflow_versions SET is_published = true, published_at_utc = now() WHERE id = @v AND NOT is_published",
            ("w", created.Workflow.Id), ("v", draft.VersionId!.Value));

        var save = Task.Run(() => desk.SaveAsync(created.Workflow.Id, Unknown));
        (await desk.WaitsInAsync("UPDATE automation.workflows")).Should().BeTrue();
        await publish.CommitAsync();

        await save.Awaiting(s => s).Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await desk.RowAsync(created.Workflow.Id);
        row.CurrentVersionId.Should().Be(draft.VersionId);
        row.CurrentVersion!.DefinitionJson.Should().Be(await desk.AsStoredAsync(ValidB));
    }

    [Fact]
    public async Task B3d_a_publish_across_a_parameters_only_save_holding_its_lock_conflicts()
    {
        await using var desk = await DeskAsync("gate_b3d");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        await using var save = await desk.HoldAsync(
            "UPDATE automation.workflows SET revision = revision + 1 WHERE id = @w",
            "UPDATE automation.workflow_versions SET input_parameters_json = @d::jsonb WHERE id = @v AND NOT is_published",
            ("w", created.Workflow.Id), ("v", draft.VersionId!.Value), ("d", OneInput));

        var publish = Task.Run(() => desk.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id)));
        (await desk.WaitsInAsync("UPDATE automation.workflows")).Should().BeTrue();
        await save.CommitAsync();

        await publish.Awaiting(p => p).Should().ThrowAsync<WorkflowDraftChangedException>();
        (await desk.RowAsync(created.Workflow.Id)).CurrentVersionId.Should().Be(created.VersionId);
    }

    [Fact]
    public async Task A_settings_only_save_racing_a_delete_is_told_the_workflow_is_not_there()
    {
        await using var desk = await DeskAsync("gate_settings_vs_delete");
        var created = await desk.CreateAsync(Valid);
        desk.Hooks.BeforeCommand(UpdatesWorkflows, () => desk.EditAsync(e => e.DeleteAsync(Owner, created.Workflow.Id)));

        var save = () => desk.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Renamed", null, Valid)));

        await save.Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task Host_transaction_a_create_and_a_default_save_defer_the_publish_and_publish_refuses_until_the_commit()
    {
        var connectionString = await backends.CreateDatabaseAsync("dispatch_gate_host_tx");
        await using var services = BuildBorrowingServices(connectionString);
        await using (var migrate = services.CreateAsyncScope())
            await migrate.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();

        WorkflowEditResult created, saved;
        await using (var scope = services.CreateAsyncScope())
        {
            var host = scope.ServiceProvider.GetRequiredService<HostConnection>();
            await host.Connection.OpenAsync();
            host.Transaction = await host.Connection.BeginTransactionAsync();
            var editor = scope.ServiceProvider.GetRequiredService<IWorkflowEditor>();
            created = await editor.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid));
            saved = await editor.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, ValidB));
            var again = await editor.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, ValidB));
            again.PublishDeferred.Should().BeTrue("a default save equal to the draft defers too");
            again.VersionId.Should().Be(created.VersionId);
            var publish = () => editor.PublishAsync(Owner, created.Workflow.Id);
            await publish.Should().ThrowAsync<WorkflowPublishInHostTransactionException>();
            await host.Transaction.CommitAsync();
            host.Transaction = null;
        }

        created.PublishDeferred.Should().BeTrue();
        created.Fault.Should().BeNull();
        saved.PublishDeferred.Should().BeTrue();
        saved.Fault.Should().BeNull();
        saved.VersionId.Should().Be(created.VersionId, "the second save rewrites the first's draft");
        await using (var after = services.CreateAsyncScope())
        {
            var published = await after.ServiceProvider.GetRequiredService<IWorkflowEditor>().UpdateAsync(
                Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, ValidB));
            published.VersionPublished.Should().BeTrue();
            published.Workflow.DefinitionJson.Should().Be(await StoredTextAsync(connectionString, ValidB));
        }
    }

    [Fact]
    public async Task A3_the_list_reads_a_draft_only_workflows_input_and_output_lists_from_its_draft()
    {
        await using var desk = await DeskAsync("gate_a3_list");
        var inputs = await desk.CreateAsync(Unknown);
        await desk.SaveAsync(inputs.Workflow.Id, Unknown, inputs: OneInput);
        var outputs = await desk.CreateAsync(Unknown);
        await desk.SaveAsync(outputs.Workflow.Id, Unknown, outputs: OneInput);

        await using var scope = desk.Services.CreateAsyncScope();
        var listed = await scope.ServiceProvider.GetRequiredService<IWorkflowCatalog>().GetWorkflowsAsync(Owner);

        var (byInputs, byOutputs) = (listed.Single(w => w.Id == inputs.Workflow.Id), listed.Single(w => w.Id == outputs.Workflow.Id));
        (byInputs.HasInputParameters, byInputs.HasOutputParameters).Should().Be((true, false));
        (byOutputs.HasInputParameters, byOutputs.HasOutputParameters).Should().Be((false, true));
        byInputs.CurrentVersionNumber.Should().BeNull();
    }

    [Fact]
    public async Task The_revision_column_this_hosts_migration_adds_is_the_one_EnsureCreated_makes()
    {
        await using var migrated = await DeskAsync("gate_revision_migrated");
        await using var created = await DeskAsync("gate_revision_created", migrate: false);
        await using (var scope = created.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreatedAsync();

        (await RevisionColumnAsync(migrated.ConnectionString)).Should().Be(await RevisionColumnAsync(created.ConnectionString))
            .And.Be("integer|NO|0");
    }

    [Fact]
    public async Task Rollbacks_one_statement_reads_the_target_by_the_one_workflows_versions_among_ten_thousand()
    {
        await using var desk = await DeskAsync("gate_rollback_plan");
        var created = await desk.CreateAsync(Valid);
        await desk.SaveAsync(created.Workflow.Id, ValidB, autoPublish: true);
        await desk.SaveAsync(created.Workflow.Id, ValidC, autoPublish: true);
        await FreightDesk.ExecuteSqlAsync(desk.ConnectionString, $$"""
            INSERT INTO automation.workflows
            SELECT (jsonb_populate_record(NULL::automation.workflows, to_jsonb(w) || jsonb_build_object(
                'id', gen_random_uuid(), 'current_version_id', NULL, 'draft_version_id', NULL))).*
            FROM automation.workflows w, generate_series(1, 10000) g WHERE w.id = '{{created.Workflow.Id}}';
            INSERT INTO automation.workflow_versions
                (id, workflow_id, version_number, definition_json, created_at_utc, created_by_user_id, is_published)
            SELECT gen_random_uuid(), w.id, v, '{"nodes":[],"edges":[]}', now(), '{{Owner}}', true
            FROM (SELECT id, row_number() OVER (ORDER BY id) AS r FROM automation.workflows
                  WHERE id <> '{{created.Workflow.Id}}') w
            CROSS JOIN LATERAL generate_series(1, CASE WHEN w.r % 500 = 0 THEN 200 ELSE 1 + (w.r % 20)::int END) v;
            ANALYZE automation.workflows;
            ANALYZE automation.workflow_versions;
            """);
        var commands = new QueryPlans.RecordingCommands();
        await using var recorded = await desk.WithInterceptorAsync(commands);
        await using (var scope = recorded.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IWorkflowEditor>().RollbackAsync(Owner, created.Workflow.Id, 2);

        var read = commands.Readers.First(c => c.Text.Contains("workflow_versions", StringComparison.Ordinal)
            && c.Text.Contains("version_number", StringComparison.Ordinal));
        await using var connection = new NpgsqlConnection(desk.ConnectionString);
        await connection.OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + read.Text, connection);
        foreach (var parameter in read.Parameters)
            explain.Parameters.Add(parameter.Clone());
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        if (Environment.GetEnvironmentVariable("ROLLBACK_PLAN_OUT") is { Length: > 0 } planFile)
            await File.WriteAllTextAsync(planFile, read.Text + "\n\n" + string.Join('\n', plan));

        plan.Should().NotContain(line => line.Contains("Seq Scan", StringComparison.Ordinal),
            "the target is found by the unique index, not among every workflow's versions of that number");
        plan.Should().NotContain(line => line.Contains("WindowAgg", StringComparison.Ordinal));
        plan.Should().Contain(line => line.Contains("Index Cond", StringComparison.Ordinal)
            && line.Contains("version_number", StringComparison.Ordinal));
    }

    private static bool UpdatesWorkflows(string sql) => sql.StartsWith("UPDATE automation.workflows", StringComparison.Ordinal);

    private static string Statement(string sql) =>
        Regex.Replace(Regex.Replace(sql, @"\A(\s*--[^\n]*\n)*", "").TrimStart(), @"\s+", " ");

    private static WorkflowVersion Copy(WorkflowVersion stored, string? definition = null) => new(
        Guid.NewGuid(), stored.WorkflowId, stored.VersionNumber, definition ?? stored.DefinitionJson,
        stored.InputParametersJson, stored.OutputParametersJson, Owner, DateTimeOffset.UtcNow);

    private static async Task<string> StoreAsync(NpgsqlConnection connection, string text)
    {
        await using var command = new NpgsqlCommand("SELECT $1::jsonb::text", connection) { Parameters = { new() { Value = text } } };
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> RevisionColumnAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT data_type || '|' || is_nullable || '|' || column_default FROM information_schema.columns "
            + "WHERE table_schema = 'automation' AND table_name = 'workflows' AND column_name = 'revision'", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static IEnumerable<string> JsonbCorpus()
    {
        var random = new Random(17);
        string[] numbers = ["0", "-0", "0.0", "-0.0", "1", "-1", "1.0", "1.10", "1e0", "1E0", "1e+2", "1E-2", "-1.5e-3", "3E1",
            "12345678901234567890123", "-99999999999999999999999999999999.000001", "1e308", "1e-308", "2.5e400", "1e-400",
            "9007199254740993", "0.1", "1.000000000000000000001e5", "4.9e-324", "1.7976931348623157e308", "123e-10", "0e10",
            "-0e-5", "100.00e-2"];
        string[] strings = ["\"a\"", "\"\\u00e9\"", "\"é\"", "\"\\ud83d\\ude00\"", "\"😀\"", "\"\\/\"", "\"\\b\\f\\n\\r\\t\"",
            "\"\\u0001\\u001f\"", "\"\\u007f\"", "\"\\u2028\\u2029\"", "\"\\\"\\\\\"", "\"\"", "\"\\uFEFF\"", "\"日本\"", "\"x\"",
            "\"\\u00A0\"", "\"<>&'\"", "\"\\u0041\""];
        string[] keys = ["\"a\"", "\"b\"", "\"aa\"", "\"B\"", "\"é\"", "\"\\u00e9\"", "\"id\"", "\"nodes\"", "\"\"", "\"z\"", "\"10\"",
            "\"9\"", "\"a b\"", "\"😀\""];
        string Pick(string[] pool) => pool[random.Next(pool.Length)];
        string Value(int depth)
        {
            var roll = random.Next(10);
            if (depth > 3 || roll < 3)
                return random.Next(5) switch { 0 => Pick(numbers), 1 => Pick(strings), 2 => "true", 3 => "false", _ => "null" };
            if (roll < 6)
            {
                var space = new[] { "", " ", "\n  ", "\t" }[random.Next(4)];
                return "[" + space + string.Join("," + space, Enumerable.Range(0, random.Next(4)).Select(_ => Value(depth + 1))) + space + "]";
            }
            var members = Enumerable.Range(0, random.Next(5)).Select(_ => Pick(keys) + new[] { ":", " : ", ":\n" }[random.Next(3)] + Value(depth + 1));
            return "{" + (random.Next(2) == 0 ? "" : " ") + string.Join(",", members) + "}";
        }

        for (var i = 0; i < 3000; i++)
            yield return Value(0);
        foreach (var fixedText in new[] { "{}", "[]", "[[]]", "[{}]", """{"a":{}}""", """{"a":[[1,[2,[]]],{}]}""", " 1 ", "\"x\"", "null" })
            yield return fixedText;
    }

    private async Task<Desk> DeskAsync(string database, bool migrate = true)
    {
        var connectionString = await backends.CreateDatabaseAsync("dispatch_" + database);
        var desk = new Desk(connectionString);
        if (migrate)
        {
            await using var scope = desk.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.MigrateAsync();
        }
        return desk;
    }

    private static ServiceProvider BuildServices(string connectionString, Interleaving? hooks, IInterceptor? extra = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<IWorkflowBlockFactory>(new PortlessBlock("noop"));
        collection.AddWorkflows(workflows =>
        {
            workflows.ConfigureDbContext(context =>
            {
                context.UseNpgsql(connectionString);
                if (hooks is not null)
                    context.AddInterceptors(hooks.Commands);
                if (extra is not null)
                    context.AddInterceptors(extra);
            });
        })
        .AddPostgreSqlSchema()
        .AddWorkflowEditor(options =>
        {
            if (hooks is not null)
            {
                options.Transaction.Use(
                    sp => new InterleavedTransaction(sp.GetRequiredService<WorkflowDbContext>(), hooks), ServiceLifetime.Scoped);
            }
        });
        return collection.BuildServiceProvider();
    }

    private static ServiceProvider BuildBorrowingServices(string connectionString)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddScoped(_ => new HostConnection(connectionString));
        collection.AddSingleton<IWorkflowBlockFactory>(new PortlessBlock("noop"));
        collection.AddWorkflows(workflows =>
        {
            workflows.ConfigureDbContext(context => context.UseNpgsql(connectionString));
        })
        .AddPostgreSqlSchema()
        .AddWorkflowEditor(options => options.Transaction.Use<BorrowingSeam>(ServiceLifetime.Scoped));
        return collection.BuildServiceProvider();
    }

    private static async Task<string> StoredTextAsync(string connectionString, string text)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return await StoreAsync(connection, text);
    }

    private sealed class HostConnection(string connectionString) : IAsyncDisposable
    {
        public NpgsqlConnection Connection { get; } = new(connectionString);

        public NpgsqlTransaction? Transaction { get; set; }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed class BorrowingSeam(WorkflowDbContext context, HostConnection host) : IWorkflowEditorTransaction
    {
        public bool JoinsAnotherTransaction => host.Transaction is not null;

        public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default) =>
            BeginAsync(cancellationToken);

        public async Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default)
        {
            if (host.Transaction is null)
                return await new WorkflowContextEditorTransaction(context).BeginAsync(cancellationToken);

            var own = context.Database.GetConnectionString();
            context.Database.SetDbConnection(host.Connection, contextOwnsConnection: false);
            await context.Database.UseTransactionAsync(host.Transaction, cancellationToken);
            return new Scope(context, own);
        }

        private sealed class Scope(WorkflowDbContext context, string? own) : IWorkflowEditorTransactionScope
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await context.Database.UseTransactionAsync(null);
                }
                finally
                {
                    context.Database.SetDbConnection(null, contextOwnsConnection: false);
                    context.Database.SetConnectionString(own);
                }
                context.ChangeTracker.Clear();
            }
        }
    }

    internal sealed class Desk : IAsyncDisposable
    {
        public Desk(string connectionString)
        {
            ConnectionString = connectionString;
            Services = BuildServices(connectionString, Hooks);
        }

        public string ConnectionString { get; }

        public Interleaving Hooks { get; } = new();

        public ServiceProvider Services { get; }

        public async Task<T> EditAsync<T>(Func<IWorkflowEditor, Task<T>> work)
        {
            await using var scope = Services.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<IWorkflowEditor>());
        }

        public Task<WorkflowEditResult> CreateAsync(string definition, string? inputs = null, string? outputs = null) =>
            EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, definition, inputs, outputs)));

        public Task<WorkflowEditResult> SaveAsync(
            Guid workflowId, string definition, bool autoPublish = false, string? inputs = null, string? outputs = null) =>
            EditAsync(e => e.UpdateAsync(Owner, workflowId,
                new UpdateWorkflowRequest("Ship", null, definition, inputs, outputs, AutoPublish: autoPublish)));

        public async Task<T> ReadAsync<T>(Func<WorkflowDbContext, Task<T>> read)
        {
            await using var scope = Services.CreateAsyncScope();
            return await read(scope.ServiceProvider.GetRequiredService<WorkflowDbContext>());
        }

        public Task<Workflow> RowAsync(Guid workflowId) =>
            ReadAsync(c => c.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
                .SingleAsync(w => w.Id == workflowId));

        public Task<WorkflowVersion> VersionAsync(Guid versionId) =>
            ReadAsync(c => c.WorkflowVersions.AsNoTracking().SingleAsync(v => v.Id == versionId));

        public Task<int> RevisionAsync(Guid workflowId) =>
            ReadAsync(c => c.Workflows.AsNoTracking().Where(w => w.Id == workflowId).Select(w => w.Revision).SingleAsync());

        public Task<List<int>> NumbersAsync(Guid workflowId) =>
            ReadAsync(c => c.WorkflowVersions.Where(v => v.WorkflowId == workflowId)
                .OrderBy(v => v.VersionNumber).Select(v => v.VersionNumber).ToListAsync());

        public async Task<List<(string Digest, bool Valid)>> VerdictsAsync(Guid versionId)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT definition_digest, is_valid FROM automation.workflow_compilations WHERE workflow_version_id = $1", connection)
            {
                Parameters = { new() { Value = versionId } },
            };
            var rows = new List<(string, bool)>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetBoolean(1)));
            return rows;
        }

        public async Task<string> AsStoredAsync(string text)
        {
            await using var connection = await OpenAsync();
            return await StoreAsync(connection, text);
        }

        public async Task<int> ExecuteAsync(string sql)
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return await command.ExecuteNonQueryAsync();
        }

        public async Task<HeldTransaction> HoldAsync(string first, string second, params (string Name, object Value)[] parameters)
        {
            var held = new HeldTransaction(await OpenAsync());
            await held.BeginAsync();
            await held.RunAsync(first, parameters);
            await held.RunAsync(second, parameters);
            return held;
        }

        public async Task<bool> WaitsInAsync(string prefix)
        {
            await using var connection = await OpenAsync();
            await using var look = new NpgsqlCommand(
                "SELECT query FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", connection);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                await using (var reader = await look.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        if (Statement(reader.GetString(0)).StartsWith(prefix, StringComparison.Ordinal))
                            return true;
                    }
                }
                await Task.Yield();
            }
            return false;
        }

        public Task<ServiceProvider> WithInterceptorAsync(IInterceptor interceptor) =>
            Task.FromResult(BuildServices(ConnectionString, hooks: null, interceptor));

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await using var connection = new NpgsqlConnection(ConnectionString);
            NpgsqlConnection.ClearPool(connection);
        }

        private async Task<NpgsqlConnection> OpenAsync()
        {
            var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            return connection;
        }
    }

    internal sealed class HeldTransaction(NpgsqlConnection connection) : IAsyncDisposable
    {
        private NpgsqlTransaction? transaction;

        public async Task BeginAsync() => transaction = await connection.BeginTransactionAsync();

        public async Task RunAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var (name, value) in parameters)
            {
                if (sql.Contains("@" + name, StringComparison.Ordinal))
                    command.Parameters.AddWithValue(name, value);
            }
            await command.ExecuteNonQueryAsync();
        }

        public Task CommitAsync() => transaction!.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class Interleaving
    {
        private readonly Dictionary<int, Func<Task>> beforeBegin = [];
        private readonly Dictionary<int, Func<Task>> afterCommit = [];
        private int begins;
        private bool running;

        public Interleaving() => Commands = new CommandHook(this);

        public CommandHook Commands { get; }

        public int Fired => Commands.Fired;

        public void BeforeBegin(int begin, Func<Task> peer) => beforeBegin[begins + begin] = peer;

        public void AfterCommit(int begin, Func<Task> peer) => afterCommit[begins + begin] = peer;

        public void BeforeCommand(Func<string, bool> match, Func<Task> peer) => Commands.Arm(match, peer);

        public void Clear()
        {
            beforeBegin.Clear();
            afterCommit.Clear();
            Commands.Disarm();
        }

        public bool Running => running;

        public async Task<int?> OnBeginAsync()
        {
            if (running)
                return null;
            var begin = ++begins;
            if (beforeBegin.Remove(begin, out var peer))
                await RunAsync(peer);
            return begin;
        }

        public async Task OnClosedAsync(int? begin)
        {
            if (begin is { } n && afterCommit.Remove(n, out var peer))
                await RunAsync(peer);
        }

        public async Task RunAsync(Func<Task> peer)
        {
            running = true;
            try
            {
                await peer();
            }
            finally
            {
                running = false;
            }
        }
    }

    internal sealed class CommandHook(Interleaving owner) : DbCommandInterceptor
    {
        private (Func<string, bool> Match, Func<Task> Peer)? armed;

        public int Fired { get; private set; }

        public void Arm(Func<string, bool> match, Func<Task> peer) => armed = (match, peer);

        public void Disarm() => armed = null;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RunAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunAsync(command);
            return result;
        }

        private async Task RunAsync(DbCommand command)
        {
            if (owner.Running || armed is not { } hook || !hook.Match(Statement(command.CommandText)))
                return;
            armed = null;
            Fired++;
            await owner.RunAsync(hook.Peer);
        }
    }

    private sealed class InterleavedTransaction(WorkflowDbContext context, Interleaving hooks) : IWorkflowEditorTransaction
    {
        private readonly WorkflowContextEditorTransaction inner = new(context);

        public bool JoinsAnotherTransaction => inner.JoinsAnotherTransaction;

        public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default) =>
            BeginAsync(cancellationToken);

        public async Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default)
        {
            var begin = await hooks.OnBeginAsync();
            return new Scope(await inner.BeginAsync(cancellationToken), hooks, begin);
        }

        private sealed class Scope(IWorkflowEditorTransactionScope inner, Interleaving hooks, int? begin) : IWorkflowEditorTransactionScope
        {
            private bool committed;

            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await inner.CommitAsync(cancellationToken);
                committed = true;
            }

            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (committed)
                    await hooks.OnClosedAsync(begin);
            }
        }
    }

    private sealed class PortlessBlock(string blockTypeId) : IWorkflowBlockFactory
    {
        public string BlockTypeId => blockTypeId;
        public string DisplayName => blockTypeId;
        public string Description => string.Empty;
        public string Category => "test";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }
}
