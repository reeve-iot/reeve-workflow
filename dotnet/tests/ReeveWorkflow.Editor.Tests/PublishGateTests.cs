using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class PublishGateTests
{
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string ValidB = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{}}]}""";
    private const string ValidC = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"c","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"c","mappings":{}}]}""";
    private const string Unknown = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n1","blockTypeId":"gone.block","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n1","mappings":{}}]}""";
    private const string Retry3 = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"noop","configuration":{"retryCount":3},"position":{"x":0,"y":0}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""";
    private const string Retry30 = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"noop","configuration":{"retryCount":3.0},"position":{"x":0,"y":0}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""";
    private const string OneInput = """[{"name":"n","type":"Int"}]""";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static bool UpdatesWorkflows(string sql) => SqlCommandText.WithoutLeadingComments(sql).StartsWith("UPDATE \"workflows\"", StringComparison.Ordinal);

    private static bool UpdatesVersions(string sql) => SqlCommandText.WithoutLeadingComments(sql).StartsWith("UPDATE \"workflow_versions\"", StringComparison.Ordinal);

    private static bool ReadsWorkflows(string sql) => sql.StartsWith("SELECT", StringComparison.Ordinal) && sql.Contains("FROM \"workflows\"", StringComparison.Ordinal);

    [Fact]
    public async Task Publish_refuses_a_draft_that_does_not_compile_and_the_current_version_stays()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, Unknown);

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        (await publish.Should().ThrowAsync<WorkflowDoesNotCompileException>())
            .Which.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersionId.Should().Be(draft.VersionId);
        row.DraftVersion!.IsPublished.Should().BeFalse();
        row.Revision.Should().Be(2, "the create's promote and the draft save moved it, and the refused publish did not");
    }

    [Fact]
    public async Task Q1_4c_a_publish_with_nothing_in_between_promotes_the_draft_it_compiled()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);

        var published = await db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        published.VersionPublished.Should().BeTrue();
        published.VersionId.Should().Be(draft.VersionId);
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(draft.VersionId);
        row.CurrentVersion!.IsPublished.Should().BeTrue();
        row.CurrentVersion.DefinitionJson.Should().Be(ValidB);
        row.DraftVersionId.Should().BeNull();
    }

    [Fact]
    public async Task Rollback_refuses_a_target_that_no_longer_compiles_and_copies_nothing()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var second = await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);
        await db.ReadAsync(c => c.WorkflowVersions.Where(v => v.Id == created.VersionId)
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.DefinitionJson, Unknown)));

        var rollback = () => db.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        (await rollback.Should().ThrowAsync<WorkflowDoesNotCompileException>())
            .Which.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        (await NumbersAsync(db, created.Workflow.Id)).Should().Equal(1, 2);
        (await RowAsync(db, created.Workflow.Id)).CurrentVersionId.Should().Be(second.VersionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_auto_published_save_that_does_not_compile_keeps_the_current_version_and_stores_the_draft(bool draftFirst)
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var earlier = draftFirst ? await SaveAsync(db, created.Workflow.Id, ValidB) : null;

        var saved = await SaveAsync(db, created.Workflow.Id, Unknown, autoPublish: true);

        saved.VersionPublished.Should().BeFalse();
        saved.Fault.Should().BeNull();
        saved.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersionId.Should().Be(saved.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(Unknown);
        row.DraftVersion.IsPublished.Should().BeFalse();
        if (earlier is not null)
            saved.VersionId.Should().Be(earlier.VersionId, "an existing draft is rewritten in place");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_auto_published_save_that_compiles_is_published(bool draftFirst)
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var earlier = draftFirst ? await SaveAsync(db, created.Workflow.Id, ValidB) : null;

        var saved = await SaveAsync(db, created.Workflow.Id, ValidC, autoPublish: true);

        saved.VersionPublished.Should().BeTrue();
        saved.Diagnostics.Should().BeEmpty();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(saved.VersionId);
        row.CurrentVersion!.DefinitionJson.Should().Be(ValidC);
        row.CurrentVersion.IsPublished.Should().BeTrue();
        row.DraftVersionId.Should().BeNull();
        if (earlier is not null)
            saved.VersionId.Should().Be(earlier.VersionId, "the draft is rewritten and then promoted, not copied");
        (await NumbersAsync(db, created.Workflow.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_auto_published_draft_keeps_its_first_save_time_and_change_message()
    {
        var clock = new FakeTimeProvider(SqliteEditorDatabase.Clock.GetUtcNow());
        using var db = new SqliteEditorDatabase(clock: clock);
        var created = await CreateAsync(db, Valid);
        var first = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB, ChangeMessage: "first", AutoPublish: false)));
        var firstSavedAt = (await VersionAsync(db, first.VersionId!.Value)).CreatedAtUtc;
        clock.Advance(TimeSpan.FromMinutes(1));

        await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidC, ChangeMessage: "second")));

        var promoted = await VersionAsync(db, first.VersionId!.Value);
        promoted.IsPublished.Should().BeTrue();
        promoted.CreatedAtUtc.Should().Be(firstSavedAt);
        promoted.ChangeMessage.Should().Be("first", "the promoting save's message is dropped");
        promoted.PublishedAtUtc.Should().BeAfter(firstSavedAt);
    }

    [Fact]
    public async Task A_create_that_does_not_compile_has_a_draft_and_no_current_version_and_does_not_run()
    {
        using var db = new SqliteEditorDatabase(configureEngine: _ => { });

        var created = await CreateAsync(db, Unknown);

        created.VersionPublished.Should().BeFalse();
        created.VersionNumber.Should().Be(1);
        created.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().BeNull();
        row.DraftVersionId.Should().Be(created.VersionId);
        row.DraftVersion!.IsPublished.Should().BeFalse();
        row.Revision.Should().Be(0, "the insert does not bump and nothing was promoted");

        var run = () => db.InScopeAsync(sp => sp.GetRequiredService<IWorkflowEngine>()
            .ExecuteAsync(created.Workflow.Id, null, null));
        (await run.Should().ThrowAsync<Exception>()).WithMessage("*no published version*");
    }

    [Fact]
    public async Task Create_does_not_bump_the_row_it_inserts_and_promotes_version_1_at_revision_0()
    {
        using var db = new SqliteEditorDatabase();

        var created = await CreateAsync(db, Valid);

        created.VersionPublished.Should().BeTrue();
        var row = await RowAsync(db, created.Workflow.Id);
        row.Revision.Should().Be(1, "only the promote moved it");
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersionId.Should().BeNull();
    }

    [Fact]
    public async Task A_compile_that_could_not_answer_refuses_the_publish_and_is_the_saves_fault_and_is_asked_again()
    {
        var check = new FaultingCheck();
        using var db = new SqliteEditorDatabase(configureHost: options => options.Seams.ExpressionNamespaces.Add(HostNamespaceRegistration.For(check)));
        var created = await CreateAsync(db, Valid);

        check.Throws = true;
        var draft = await SaveAsync(db, created.Workflow.Id, AsksTheHost);
        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        draft.Fault.Should().BeOfType<WorkflowCompilationUnansweredException>()
            .Which.InnerException.Should().BeOfType<CompilationFaultException>();
        draft.VersionPublished.Should().BeFalse();
        var refusal = (await publish.Should().ThrowAsync<WorkflowCompilationUnansweredException>()).Which;
        (refusal.WorkflowId, refusal.WorkflowVersionId).Should().Be((created.Workflow.Id, draft.VersionId!.Value));
        refusal.InnerException.Should().BeOfType<CompilationFaultException>().Which.InnerException.Should().BeSameAs(check.Error);
        (await RowAsync(db, created.Workflow.Id)).CurrentVersionId.Should().Be(created.VersionId);

        check.Throws = false;
        (await publish()).VersionPublished.Should().BeTrue("the fault was not recorded as a verdict");
    }

    [Fact]
    public async Task A_rollback_whose_compile_could_not_answer_throws_the_fault_and_copies_nothing()
    {
        var check = new FaultingCheck();
        using var db = new SqliteEditorDatabase(configureHost: options => options.Seams.ExpressionNamespaces.Add(HostNamespaceRegistration.For(check)));
        var created = await CreateAsync(db, AsksTheHost);
        var second = await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);

        check.Throws = true;
        var rollback = () => db.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        var refusal = (await rollback.Should().ThrowAsync<WorkflowCompilationUnansweredException>()).Which;
        (refusal.WorkflowId, refusal.WorkflowVersionId).Should().Be((created.Workflow.Id, created.VersionId!.Value),
            "a rollback names the version whose text it would copy");
        refusal.InnerException.Should().BeOfType<CompilationFaultException>().Which.InnerException.Should().BeSameAs(check.Error);
        (await NumbersAsync(db, created.Workflow.Id)).Should().Equal(1, 2);
        (await RowAsync(db, created.Workflow.Id)).CurrentVersionId.Should().Be(second.VersionId);
    }

    [Fact]
    public async Task A_publish_whose_own_token_is_cancelled_during_the_compile_throws_the_cancellation_not_the_unanswered()
    {
        var check = new CancellingCheck();
        using var db = new SqliteEditorDatabase(configureHost: options => options.Seams.ExpressionNamespaces.Add(HostNamespaceRegistration.For(check)));
        var created = await CreateAsync(db, Valid);

        check.Cancels = new CancellationTokenSource();
        var draft = await SaveAsync(db, created.Workflow.Id, AsksTheHost);
        draft.Fault.Should().BeOfType<WorkflowCompilationUnansweredException>();

        using var caller = new CancellationTokenSource();
        check.Cancels = caller;
        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id, caller.Token));

        (await publish.Should().ThrowAsync<OperationCanceledException>()).Which.Should().NotBeOfType<WorkflowCompilationUnansweredException>();
    }

    private sealed class CancellingCheck : Core.Expressions.IExpressionNamespaceStaticCheck
    {
        public CancellationTokenSource? Cancels { get; set; }

        public string Namespace => "host";

        public Core.Expressions.NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments)
        {
            Cancels!.Cancel();
            throw new OperationCanceledException(Cancels.Token);
        }
    }

    private const string AsksTheHost = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n","blockTypeId":"noop","configuration":{"v":"expr:${host.q}"},"position":{"x":0,"y":0}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n","mappings":{}}]}""";

    private sealed class FaultingCheck : Core.Expressions.IExpressionNamespaceStaticCheck
    {
        public bool Throws { get; set; }

        public InvalidOperationException Error { get; } = new("the check is down");

        public string Namespace => "host";

        public Core.Expressions.NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
            Throws ? throw Error : new(Core.Expressions.NamespacePathCheck.Yes, "String");
    }

    [Fact]
    public async Task A_draft_save_equal_to_the_draft_writes_nothing_and_leaves_the_draft()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        var revision = await RevisionAsync(db, created.Workflow.Id);

        var again = await SaveAsync(db, created.Workflow.Id, ValidB);

        again.VersionId.Should().BeNull();
        again.VersionPublished.Should().BeFalse();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersionId.Should().Be(draft.VersionId);
        (await RevisionAsync(db, created.Workflow.Id)).Should().Be(revision + 1, "every update moves it by one, one that writes no text included");
        (await NumbersAsync(db, created.Workflow.Id)).Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_default_save_equal_to_a_draft_that_compiles_writes_no_text_and_publishes_the_draft()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        var revision = await RevisionAsync(db, created.Workflow.Id);

        var again = await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);

        again.VersionPublished.Should().BeTrue();
        again.VersionId.Should().Be(draft.VersionId);
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(draft.VersionId);
        row.DraftVersionId.Should().BeNull();
        (await RevisionAsync(db, created.Workflow.Id)).Should().Be(revision + 2, "the update's own move, then the promote");
        (await NumbersAsync(db, created.Workflow.Id)).Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_default_save_equal_to_a_draft_that_does_not_compile_reports_the_diagnostics_and_keeps_it()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, Unknown);
        var revision = await RevisionAsync(db, created.Workflow.Id);

        var again = await SaveAsync(db, created.Workflow.Id, Unknown, autoPublish: true);

        again.VersionPublished.Should().BeFalse();
        again.VersionId.Should().Be(draft.VersionId);
        again.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.BlockTypeUnknown);
        var row = await RowAsync(db, created.Workflow.Id);
        (row.CurrentVersionId, row.DraftVersionId).Should().Be((created.VersionId, draft.VersionId));
        (await RevisionAsync(db, created.Workflow.Id)).Should().Be(revision + 1, "the update's own move; nothing was promoted");
    }

    [Fact]
    public async Task A_default_save_of_the_current_text_after_one_that_did_not_compile_discards_that_draft()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var invalid = await SaveAsync(db, created.Workflow.Id, Unknown, autoPublish: true);
        var revision = await RevisionAsync(db, created.Workflow.Id);

        var undone = await SaveAsync(db, created.Workflow.Id, Valid, autoPublish: true);

        invalid.VersionPublished.Should().BeFalse();
        undone.VersionId.Should().BeNull();
        undone.VersionPublished.Should().BeFalse();
        undone.Workflow.DraftVersionId.Should().BeNull();
        undone.Workflow.DefinitionJson.Should().Be(Valid);
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersionId.Should().BeNull("the author's last save is the current version");
        (await RevisionAsync(db, created.Workflow.Id)).Should().Be(revision + 1, "the update's one move, which discarding the draft needs anyway");
    }

    [Fact]
    public async Task A_draft_save_then_a_save_of_the_current_text_leaves_nothing_to_publish()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        await SaveAsync(db, created.Workflow.Id, ValidB);
        await SaveAsync(db, created.Workflow.Id, Valid);

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<NoDraftVersionToPublishException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.CurrentVersion!.DefinitionJson.Should().Be(Valid);
    }

    [Fact]
    public async Task A_save_that_would_discard_a_draft_another_write_replaced_conflicts_and_writes_nothing()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeCommand(UpdatesWorkflows,
            "UPDATE workflows SET draft_version_id = NULL, revision = revision + 1 WHERE id = @p0", created.Workflow.Id);

        var save = () => SaveAsync(db, created.Workflow.Id, Valid);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_save_that_changes_only_the_output_list_writes_it(bool draftFirst)
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = draftFirst ? await SaveAsync(db, created.Workflow.Id, ValidB) : null;

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, draftFirst ? ValidB : Valid, null, OneInput, AutoPublish: false)));

        saved.VersionId.Should().NotBeNull();
        if (draft is not null)
            saved.VersionId.Should().Be(draft.VersionId);
        (await RowAsync(db, created.Workflow.Id)).DraftVersion!.OutputParametersJson.Should().Be(OneInput);
    }

    [Fact]
    public async Task A_save_equal_to_the_current_text_but_for_the_output_list_rewrites_the_draft_and_keeps_it()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, Valid, null, OneInput, AutoPublish: false)));

        saved.VersionId.Should().Be(draft.VersionId);
        var row = await RowAsync(db, created.Workflow.Id);
        row.DraftVersionId.Should().Be(draft.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(Valid);
        row.DraftVersion.OutputParametersJson.Should().Be(OneInput);
    }

    [Fact]
    public async Task Each_door_moves_the_revision_as_the_writers_table_says()
    {
        using var db = new SqliteEditorDatabase();
        var id = (await CreateAsync(db, Valid)).Workflow.Id;
        (await RevisionAsync(db, id)).Should().Be(1, "create: the promote at 0");

        await db.EditAsync(e => e.UpdateAsync(Owner, id, new UpdateWorkflowRequest("Renamed", null, Valid)));
        (await RevisionAsync(db, id)).Should().Be(2, "every update moves it by one, a rename alone included");

        await SaveAsync(db, id, ValidB);
        (await RevisionAsync(db, id)).Should().Be(3, "a new draft: the update's one move");

        await SaveAsync(db, id, ValidC);
        (await RevisionAsync(db, id)).Should().Be(4, "an existing draft rewritten: the update's one move");

        await db.EditAsync(e => e.PublishAsync(Owner, id));
        (await RevisionAsync(db, id)).Should().Be(5, "publish: the promote");

        await db.EditAsync(e => e.RollbackAsync(Owner, id, 1));
        (await RevisionAsync(db, id)).Should().Be(7, "rollback: the copy's compare-and-swap, then the promote");

        await SaveAsync(db, id, ValidB, autoPublish: true);
        (await RevisionAsync(db, id)).Should().Be(9, "an auto-published save: the update's one move, then the promote");
    }

    [Fact]
    public async Task Q1_1_and_Q1_4a_a_save_that_commits_between_the_gates_read_and_its_promote_makes_the_promote_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeBegin(1, () => SaveAsync(db, created.Workflow.Id, Unknown));

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId, "what the gate compiled is no longer what the draft holds");
        row.DraftVersionId.Should().Be(draft.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(Unknown);
        row.DraftVersion.IsPublished.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Q1_5_and_B3c_a_save_that_changes_one_parameter_list_makes_the_promote_conflict(bool inputs)
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeBegin(1, () => db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB, inputs ? OneInput : null, inputs ? null : OneInput, AutoPublish: false))));

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        (inputs ? row.DraftVersion!.InputParametersJson : row.DraftVersion!.OutputParametersJson).Should().Be(OneInput);
        row.DraftVersionId.Should().Be(draft.VersionId);
    }

    [Fact]
    public async Task Q1_6_a_save_that_lands_after_the_promote_began_and_before_its_swap_makes_it_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeCommand(UpdatesWorkflows,
            "UPDATE workflow_versions SET definition_json = @p0 WHERE id = @p1; "
            + "UPDATE workflows SET revision = revision + 1 WHERE id = @p2",
            Unknown, draft.VersionId!.Value, created.Workflow.Id);

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        db.Interleave.Commands.Fired.Should().ContainSingle();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersion!.IsPublished.Should().BeFalse();
        row.Revision.Should().Be(2, "the promote changed nothing");
    }

    [Fact]
    public async Task Q5_B3a_and_auto_then_stale_save_a_save_that_read_the_draft_before_a_publish_committed_writes_nothing()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeCommand(UpdatesWorkflows,
            "UPDATE workflows SET current_version_id = @p0, draft_version_id = NULL, revision = revision + 1 WHERE id = @p1; "
            + "UPDATE workflow_versions SET is_published = 1 WHERE id = @p0",
            draft.VersionId!.Value, created.Workflow.Id);

        var save = () => SaveAsync(db, created.Workflow.Id, Unknown);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        db.Interleave.Commands.Fired.Should().ContainSingle();
        (await VersionAsync(db, draft.VersionId!.Value)).DefinitionJson.Should().Be(ValidB, "the save wrote nothing");
    }

    [Fact]
    public async Task Q5_guard_a_draft_published_after_the_saves_bump_is_not_written_over()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeCommand(UpdatesVersions,
            "UPDATE workflow_versions SET is_published = 1 WHERE id = @p0", draft.VersionId!.Value);

        var save = () => SaveAsync(db, created.Workflow.Id, Unknown);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        (await VersionAsync(db, draft.VersionId!.Value)).DefinitionJson.Should().Be(ValidB);
    }

    [Fact]
    public async Task A_save_that_read_the_draft_before_a_rollback_committed_writes_nothing()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        var copy = Guid.NewGuid();
        db.Interleave.BeforeCommand(UpdatesWorkflows,
            "INSERT INTO workflow_versions (id, workflow_id, version_number, definition_json, created_by_user_id, "
            + "created_at_utc, is_published) VALUES (@p0, @p1, 3, @p2, @p3, @p4, 1); "
            + "UPDATE workflows SET current_version_id = @p0, draft_version_id = NULL, revision = revision + 1 WHERE id = @p1",
            copy, created.Workflow.Id, Valid, Owner, SqliteEditorDatabase.Clock.GetUtcNow());

        var save = () => SaveAsync(db, created.Workflow.Id, ValidC);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        db.Interleave.Commands.Fired.Should().ContainSingle();
        var orphan = await VersionAsync(db, draft.VersionId!.Value);
        orphan.DefinitionJson.Should().Be(ValidB, "the stale save wrote nothing into the draft the rollback left behind");
        orphan.IsPublished.Should().BeFalse();
    }

    [Fact]
    public async Task Rollback_vs_save_a_save_that_commits_after_the_rollbacks_read_makes_the_rollback_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);
        db.Interleave.BeforeBegin(1, () => SaveAsync(db, created.Workflow.Id, ValidC));

        var rollback = () => db.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));

        await rollback.Should().ThrowAsync<WorkflowDraftChangedException>();
        (await NumbersAsync(db, created.Workflow.Id)).Should().Equal(1, 2, 3);
        (await RowAsync(db, created.Workflow.Id)).CurrentVersion!.VersionNumber.Should().Be(2);
    }

    [Fact]
    public async Task A1_an_auto_publish_and_a_new_draft_between_the_gates_read_and_its_promote_make_it_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var first = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeBegin(1, async () =>
        {
            await SaveAsync(db, created.Workflow.Id, ValidC, autoPublish: true);
            await SaveAsync(db, created.Workflow.Id, Unknown);
        });

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(first.VersionId, "the auto-publish promoted the draft it rewrote");
        row.CurrentVersion!.DefinitionJson.Should().Be(ValidC);
        row.DraftVersion!.DefinitionJson.Should().Be(Unknown);
        row.DraftVersion.IsPublished.Should().BeFalse();
    }

    [Fact]
    public async Task A1_rollback_a_rollback_and_a_new_draft_between_the_gates_read_and_its_promote_make_it_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);
        var first = await SaveAsync(db, created.Workflow.Id, ValidC);
        db.Interleave.BeforeBegin(1, async () =>
        {
            await db.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 1));
            await SaveAsync(db, created.Workflow.Id, Unknown);
        });

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersion!.VersionNumber.Should().Be(4);
        row.CurrentVersion.DefinitionJson.Should().Be(Valid);
        row.DraftVersion!.DefinitionJson.Should().Be(Unknown);
        (await VersionAsync(db, first.VersionId!.Value)).IsPublished.Should().BeFalse("the first draft stays unpublished");
    }

    [Fact]
    public async Task B2_a_save_that_rewrites_3_as_3_0_between_the_compile_and_the_promote_makes_it_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        await SaveAsync(db, created.Workflow.Id, Retry3);
        db.Interleave.BeforeBegin(1, () => SaveAsync(db, created.Workflow.Id, Retry30));

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        (await RowAsync(db, created.Workflow.Id)).CurrentVersionId.Should().Be(created.VersionId);
        var again = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));
        (await again.Should().ThrowAsync<WorkflowDoesNotCompileException>("the compiler refuses 3.0"))
            .Which.Diagnostics.Should().Contain(d => d.RuleId == CompilationRules.ResilienceConfigInvalid);
    }

    [Fact]
    public async Task Auto_vs_publish_an_auto_publish_inside_the_publishs_window_makes_the_publish_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeBegin(1, () => SaveAsync(db, created.Workflow.Id, ValidC, autoPublish: true));

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(draft.VersionId);
        row.CurrentVersion!.DefinitionJson.Should().Be(ValidC, "what the auto-publish compiled");
    }

    [Fact]
    public async Task Auto_vs_publish_a_publish_between_an_auto_publishs_commit_and_its_read_makes_the_auto_publish_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        db.Interleave.AfterCommit(1, () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id)));

        var save = () => SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersion!.DefinitionJson.Should().Be(ValidB, "the publish compiled and promoted the saved draft");
        row.DraftVersionId.Should().BeNull();
    }

    [Fact]
    public async Task A_save_between_an_auto_publishs_commit_and_its_read_makes_it_conflict_and_not_report_that_saves_diagnostics()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        db.Interleave.AfterCommit(1, () => SaveAsync(db, created.Workflow.Id, Unknown));

        var auto = () => SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);

        await auto.Should().ThrowAsync<WorkflowDraftChangedException>("the draft it read back is another save's");
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(Unknown);
    }

    [Fact]
    public async Task Two_default_saves_the_second_to_commit_overtakes_the_first_which_gets_a_conflict()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        WorkflowEditResult? second = null;
        db.Interleave.AfterCommit(1, async () => second = await SaveAsync(db, created.Workflow.Id, ValidC, autoPublish: true));

        var first = () => SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);

        await first.Should().ThrowAsync<WorkflowDraftChangedException>();
        second!.VersionPublished.Should().BeTrue();
        (await RowAsync(db, created.Workflow.Id)).CurrentVersion!.DefinitionJson.Should().Be(ValidC);
    }

    [Fact]
    public async Task Two_draft_saves_into_one_draft_do_not_conflict_and_the_later_one_stands()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        db.Interleave.AfterCommit(1, () => SaveAsync(db, created.Workflow.Id, ValidC));

        var first = await SaveAsync(db, created.Workflow.Id, ValidB);

        first.VersionPublished.Should().BeFalse();
        first.Fault.Should().BeNull();
        var row = await RowAsync(db, created.Workflow.Id);
        row.DraftVersionId.Should().Be(first.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(ValidC);
    }

    [Fact]
    public async Task P8_publish_compiles_the_draft_as_stored_and_not_as_its_context_tracked_it()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);

        var publish = () => db.InScopeAsync(async services =>
        {
            var context = services.GetRequiredService<WorkflowDbContext>();
            (await context.WorkflowVersions.SingleAsync(v => v.Id == draft.VersionId)).DefinitionJson.Should().Be(ValidB);
            await SaveAsync(db, created.Workflow.Id, Unknown);
            return await services.GetRequiredService<IWorkflowEditor>().PublishAsync(Owner, created.Workflow.Id);
        });

        await publish.Should().ThrowAsync<WorkflowDoesNotCompileException>();
        (await RowAsync(db, created.Workflow.Id)).CurrentVersionId.Should().Be(created.VersionId);
    }

    [Fact]
    public async Task A3_a_draft_only_workflows_response_shows_the_draft_and_says_so()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Unknown);
        created.Workflow.DefinitionJson.Should().Be(Unknown);
        created.Workflow.DocumentsFrom.Should().Be(WorkflowDocumentsSource.Draft);
        const string rewritten = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"n2","blockTypeId":"gone.again","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"n2","mappings":{}}]}""";

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, rewritten, OneInput)));

        saved.VersionPublished.Should().BeFalse();
        saved.Workflow.DefinitionJson.Should().Be(rewritten, "the draft was rewritten; the row's own columns were not");
        saved.Workflow.InputParametersJson.Should().Be(OneInput);
        saved.Workflow.DocumentsFrom.Should().Be(WorkflowDocumentsSource.Draft);
        var read = await db.InScopeAsync(sp => sp.GetRequiredService<IWorkflowCatalog>().GetWorkflowAsync(Owner, created.Workflow.Id));
        read.DefinitionJson.Should().Be(rewritten);
        read.DocumentsFrom.Should().Be(WorkflowDocumentsSource.Draft);
    }

    [Fact]
    public async Task A3_a_published_response_takes_all_three_documents_from_the_current_version_not_the_row()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid, OneInput)));
        created.Workflow.DocumentsFrom.Should().Be(WorkflowDocumentsSource.Current);

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Ship", null, ValidB)));

        saved.VersionPublished.Should().BeTrue();
        saved.Workflow.DefinitionJson.Should().Be(ValidB);
        saved.Workflow.InputParametersJson.Should().BeNull("the current version declares none; the row's columns still hold the create's");
        saved.Workflow.DocumentsFrom.Should().Be(WorkflowDocumentsSource.Current);
    }

    [Fact]
    public async Task A3_a_response_with_a_current_version_and_a_draft_shows_the_current_versions_documents()
    {
        using var db = new SqliteEditorDatabase();
        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid, OneInput)));

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB, null, OneInput, AutoPublish: false)));
        var read = await db.InScopeAsync(sp => sp.GetRequiredService<IWorkflowCatalog>().GetWorkflowAsync(Owner, created.Workflow.Id));

        foreach (var response in new[] { saved.Workflow, read })
        {
            response.DocumentsFrom.Should().Be(WorkflowDocumentsSource.Current);
            response.DefinitionJson.Should().Be(Valid);
            response.InputParametersJson.Should().Be(OneInput);
            response.OutputParametersJson.Should().BeNull();
            response.DraftVersionId.Should().Be(saved.VersionId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_save_whose_workflow_is_deleted_under_it_is_told_it_is_not_there(bool draftFirst)
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        if (draftFirst)
            await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeCommand(UpdatesWorkflows,
            "UPDATE workflows SET current_version_id = NULL, draft_version_id = NULL WHERE id = @p0; "
            + "DELETE FROM workflow_versions WHERE workflow_id = @p0; DELETE FROM workflows WHERE id = @p0",
            created.Workflow.Id);

        var save = () => SaveAsync(db, created.Workflow.Id, ValidC);

        await save.Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task A_publish_whose_workflow_is_deleted_before_its_promote_is_told_it_is_not_there()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeBegin(1, () => db.EditAsync(e => e.DeleteAsync(Owner, created.Workflow.Id)));

        var publish = () => db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        await publish.Should().ThrowAsync<WorkflowNotFoundException>();
    }

    [Fact]
    public async Task A_save_cancelled_after_its_commit_stands_as_the_draft_and_carries_the_fault()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        using var cancel = new CancellationTokenSource();
        db.Interleave.AfterCommit(1, () => { cancel.Cancel(); return Task.CompletedTask; });

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Ship", null, ValidB), cancel.Token));

        saved.VersionPublished.Should().BeFalse();
        saved.Fault.Should().BeAssignableTo<OperationCanceledException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(created.VersionId);
        row.DraftVersionId.Should().Be(saved.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(ValidB);
    }

    [Fact]
    public async Task A_create_cancelled_after_its_commit_leaves_a_draft_only_workflow_and_carries_the_fault()
    {
        using var db = new SqliteEditorDatabase();
        using var cancel = new CancellationTokenSource();
        db.Interleave.AfterCommit(1, () => { cancel.Cancel(); return Task.CompletedTask; });

        var created = await db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid), cancel.Token));

        created.VersionPublished.Should().BeFalse();
        created.Fault.Should().BeAssignableTo<OperationCanceledException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().BeNull();
        row.DraftVersionId.Should().Be(created.VersionId);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("save")]
    [InlineData("publish")]
    [InlineData("rollback")]
    public async Task A_fault_after_the_promote_commits_is_reported_beside_a_published_result_showing_what_was_committed(string door)
    {
        using var db = new SqliteEditorDatabase();
        var created = door == "create" ? null : await CreateAsync(db, Valid);
        if (door == "publish")
            await SaveAsync(db, created!.Workflow.Id, ValidB);
        if (door == "rollback")
            await SaveAsync(db, created!.Workflow.Id, ValidB, autoPublish: true);
        using var cancel = new CancellationTokenSource();
        db.Interleave.AfterCommit(door is "create" or "save" or "rollback" ? 2 : 1, () => { cancel.Cancel(); return Task.CompletedTask; });

        var result = await db.EditAsync(e => door switch
        {
            "create" => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, ValidB), cancel.Token),
            "save" => e.UpdateAsync(Owner, created!.Workflow.Id, new UpdateWorkflowRequest("Ship", null, ValidB), cancel.Token),
            "publish" => e.PublishAsync(Owner, created!.Workflow.Id, cancel.Token),
            _ => e.RollbackAsync(Owner, created!.Workflow.Id, 1, cancel.Token),
        });

        result.VersionPublished.Should().BeTrue();
        result.Fault.Should().BeAssignableTo<OperationCanceledException>();
        var row = await RowAsync(db, result.Workflow.Id);
        row.CurrentVersionId.Should().Be(result.VersionId);
        result.Workflow.CurrentVersionId.Should().Be(result.VersionId);
        result.Workflow.DraftVersionId.Should().BeNull();
        result.Workflow.DocumentsFrom.Should().Be(WorkflowDocumentsSource.Current);
        result.Workflow.DefinitionJson.Should().Be(row.CurrentVersion!.DefinitionJson);
        result.Workflow.CurrentVersionNumber.Should().Be(row.CurrentVersion.VersionNumber);
        result.Workflow.UpdatedAtUtc.Should().Be(row.UpdatedAtUtc);
    }

    [Fact]
    public async Task A_database_fault_reading_back_after_the_promote_commits_is_reported_beside_a_published_result()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.AfterCommit(1, () =>
        {
            db.Interleave.BeforeCommand(ReadsWorkflows, "SELECT * FROM no_such_table");
            return Task.CompletedTask;
        });

        var published = await db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));

        published.VersionPublished.Should().BeTrue();
        published.Fault.Should().BeOfType<Microsoft.Data.Sqlite.SqliteException>();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(published.VersionId);
        published.Workflow.Revision.Should().Be(row.Revision, "the answer carries the revision the promote wrote, not the one it read");
    }

    [Fact]
    public async Task Publish_and_rollback_by_another_user_answer_not_found_and_change_nothing()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);
        await SaveAsync(db, created.Workflow.Id, ValidC);
        var before = await RowAsync(db, created.Workflow.Id);
        var stranger = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var publish = () => db.EditAsync(e => e.PublishAsync(stranger, created.Workflow.Id));
        var rollback = () => db.EditAsync(e => e.RollbackAsync(stranger, created.Workflow.Id, 1));

        await publish.Should().ThrowAsync<WorkflowNotFoundException>();
        await rollback.Should().ThrowAsync<WorkflowNotFoundException>();
        var after = await RowAsync(db, created.Workflow.Id);
        (after.CurrentVersionId, after.DraftVersionId, after.Revision).Should().Be((before.CurrentVersionId, before.DraftVersionId, before.Revision));
        (await NumbersAsync(db, created.Workflow.Id)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task A_fault_free_promote_carries_no_fault_and_reads_the_workflow_back()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);

        var saved = await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);

        saved.VersionPublished.Should().BeTrue();
        saved.Fault.Should().BeNull();
        saved.Workflow.CurrentVersionId.Should().Be(saved.VersionId);
    }

    [Fact]
    public async Task A_draft_saves_read_back_does_not_report_a_draft_another_write_put_in_its_place()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        db.Interleave.AfterCommit(1, async () =>
        {
            await db.EditAsync(e => e.PublishAsync(Owner, created.Workflow.Id));
            await SaveAsync(db, created.Workflow.Id, Unknown);
        });

        var saved = await SaveAsync(db, created.Workflow.Id, ValidB);

        saved.Diagnostics.Should().BeEmpty();
        saved.Fault.Should().BeNull();
        var row = await RowAsync(db, created.Workflow.Id);
        row.CurrentVersionId.Should().Be(saved.VersionId);
        row.DraftVersion!.DefinitionJson.Should().Be(Unknown);
    }

    [Fact]
    public async Task A_default_save_equal_to_the_draft_conflicts_when_another_write_rewrote_the_draft_after_its_read()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeCommand(UpdatesWorkflows,
            "UPDATE workflow_versions SET definition_json = @p0 WHERE id = @p1; UPDATE workflows SET revision = revision + 1 WHERE id = @p2",
            ValidC, draft.VersionId!.Value, created.Workflow.Id);

        var save = () => db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Renamed", null, ValidB)));

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        db.Interleave.Commands.Fired.Should().ContainSingle();
        var row = await RowAsync(db, created.Workflow.Id);
        (row.CurrentVersionId, row.DraftVersionId, row.DraftVersion!.DefinitionJson).Should().Be((created.VersionId, draft.VersionId, ValidC));
    }

    [Fact]
    public async Task A_default_save_equal_to_the_draft_publishes_it_when_another_write_moved_only_the_revision_after_its_read()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeCommand(UpdatesWorkflows, "UPDATE workflows SET revision = revision + 1 WHERE id = @p0", created.Workflow.Id);

        var saved = await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Renamed", null, ValidB)));

        db.Interleave.Commands.Fired.Should().ContainSingle();
        saved.VersionPublished.Should().BeTrue("a peer's settings write moves the revision but leaves the draft the save read, so it is still the draft to publish");
        var row = await RowAsync(db, created.Workflow.Id);
        (row.Name, row.CurrentVersionId, row.DraftVersionId).Should().Be(("Renamed", draft.VersionId, null));
        row.Revision.Should().Be(5, "the create's promote, the draft save, the peer's move, this save's move, then its promote");
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("inputs")]
    [InlineData("outputs")]
    public async Task A_default_save_in_a_scope_that_tracked_a_stale_draft_reads_the_stored_draft_and_writes_into_it(
        string peerChanges)
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        var peer = peerChanges switch
        {
            "definition" => new UpdateWorkflowRequest("Ship", null, ValidC, AutoPublish: false),
            "inputs" => new UpdateWorkflowRequest("Ship", null, ValidB, OneInput, null, AutoPublish: false),
            _ => new UpdateWorkflowRequest("Ship", null, ValidB, null, OneInput, AutoPublish: false)
        };

        var (saved, hostCopy) = await db.InScopeAsync(async sp =>
        {
            var context = sp.GetRequiredService<WorkflowDbContext>();
            var tracked = await context.WorkflowVersions.SingleAsync(v => v.Id == draft.VersionId);
            await db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, peer));
            var result = await sp.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, created.Workflow.Id,
                new UpdateWorkflowRequest("Ship", null, ValidB));
            return (result, (context.Entry(tracked).State, tracked.DefinitionJson, tracked.InputParametersJson, tracked.OutputParametersJson));
        });

        (saved.VersionPublished, saved.VersionId, saved.Fault).Should().Be((true, draft.VersionId, null));
        hostCopy.Should().Be((EntityState.Unchanged, ValidB, (string?)null, (string?)null));
        var row = await RowAsync(db, created.Workflow.Id);
        (row.CurrentVersionId, row.DraftVersionId).Should().Be((draft.VersionId, null));
        (row.CurrentVersion!.DefinitionJson, row.CurrentVersion.InputParametersJson, row.CurrentVersion.OutputParametersJson)
            .Should().Be((ValidB, null, null));
    }

    [Theory]
    [InlineData("definition_json")]
    [InlineData("input_parameters_json")]
    [InlineData("output_parameters_json")]
    public async Task A_default_save_equal_to_the_draft_conflicts_when_a_document_changed_under_an_unmoved_revision(string column)
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        var written = column == "definition_json" ? ValidC : OneInput;
        db.Interleave.BeforeCommand(UpdatesWorkflows, $"UPDATE workflow_versions SET {column} = @p0 WHERE id = @p1",
            written, draft.VersionId!.Value);

        var save = () => db.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Renamed", null, ValidB)));

        await save.Should().ThrowAsync<WorkflowDraftChangedException>();
        db.Interleave.Commands.Fired.Should().ContainSingle();
        (await RowAsync(db, created.Workflow.Id)).CurrentVersionId.Should().Be(created.VersionId);
    }

    [Fact]
    public async Task A_default_save_into_the_draft_promotes_at_the_revision_its_own_bump_wrote()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidC);
        db.Interleave.BeforeCommand(UpdatesWorkflows,
            "UPDATE workflow_versions SET definition_json = @p0 WHERE id = @p1; UPDATE workflows SET revision = revision + 1 WHERE id = @p2",
            Unknown, draft.VersionId!.Value, created.Workflow.Id);

        var saved = await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);

        saved.VersionPublished.Should().BeTrue();
        var row = await RowAsync(db, created.Workflow.Id);
        (row.CurrentVersionId, row.CurrentVersion!.DefinitionJson).Should().Be((draft.VersionId, ValidB));
    }

    [Fact]
    public async Task A_default_save_of_a_new_draft_promotes_at_the_revision_its_own_bump_wrote()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        db.Interleave.BeforeCommand(UpdatesWorkflows,
            "UPDATE workflows SET revision = revision + 1 WHERE id = @p0", created.Workflow.Id);

        var saved = await SaveAsync(db, created.Workflow.Id, ValidB, autoPublish: true);

        saved.VersionPublished.Should().BeTrue();
        var row = await RowAsync(db, created.Workflow.Id);
        (row.CurrentVersionId, row.CurrentVersion!.DefinitionJson).Should().Be((saved.VersionId, ValidB));
        row.Revision.Should().Be(4, "the create's promote, the peer's bump, the save's own bump, then its promote");
    }

    [Fact]
    public async Task A_draft_saves_read_back_does_not_report_another_writes_text_in_the_same_draft()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidC);
        db.Interleave.AfterCommit(1, () => SaveAsync(db, created.Workflow.Id, Unknown));

        var saved = await SaveAsync(db, created.Workflow.Id, ValidB);

        saved.VersionId.Should().Be(draft.VersionId);
        saved.Diagnostics.Should().BeEmpty("the text in the draft at the read-back is another write's");
        (await RowAsync(db, created.Workflow.Id)).DraftVersion!.DefinitionJson.Should().Be(Unknown);
    }

    [Fact]
    public async Task A_rollback_to_the_draft_copies_the_text_it_read_with_the_revision()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db, Valid);
        var draft = await SaveAsync(db, created.Workflow.Id, ValidB);
        db.Interleave.BeforeCommand(ReadsWorkflows,
            "UPDATE workflow_versions SET definition_json = @p0 WHERE id = @p1; UPDATE workflows SET revision = revision + 1 WHERE id = @p2",
            ValidC, draft.VersionId!.Value, created.Workflow.Id);

        var copy = await db.EditAsync(e => e.RollbackAsync(Owner, created.Workflow.Id, 2));

        (await RowAsync(db, created.Workflow.Id)).CurrentVersion!.DefinitionJson.Should().Be(ValidC);
        copy.VersionNumber.Should().Be(3);
    }

    [Fact]
    public async Task A_door_inside_an_ambient_transaction_scope_is_refused_naming_it()
    {
        using var db = new SqliteEditorDatabase();
        using var ambient = new System.Transactions.TransactionScope(System.Transactions.TransactionScopeAsyncFlowOption.Enabled);

        var create = () => CreateAsync(db, Valid);

        (await create.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ambient TransactionScope*Editor.Transaction seam*");
    }

    private static Task<WorkflowEditResult> CreateAsync(SqliteEditorDatabase db, string definition) =>
        db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, definition)));

    private static Task<WorkflowEditResult> SaveAsync(
        SqliteEditorDatabase db, Guid workflowId, string definition, bool autoPublish = false) =>
        db.EditAsync(e => e.UpdateAsync(Owner, workflowId,
            new UpdateWorkflowRequest("Ship", null, definition, AutoPublish: autoPublish)));

    private static Task<Workflow> RowAsync(SqliteEditorDatabase db, Guid workflowId) =>
        db.ReadAsync(c => c.Workflows.AsNoTracking().Include(w => w.CurrentVersion).Include(w => w.DraftVersion)
            .SingleAsync(w => w.Id == workflowId));

    private static Task<int> RevisionAsync(SqliteEditorDatabase db, Guid workflowId) =>
        db.ReadAsync(c => c.Workflows.AsNoTracking().Where(w => w.Id == workflowId).Select(w => w.Revision).SingleAsync());

    private static Task<WorkflowVersion> VersionAsync(SqliteEditorDatabase db, Guid versionId) =>
        db.ReadAsync(c => c.WorkflowVersions.AsNoTracking().SingleAsync(v => v.Id == versionId));

    private static Task<List<int>> NumbersAsync(SqliteEditorDatabase db, Guid workflowId) =>
        db.ReadAsync(c => c.WorkflowVersions.Where(v => v.WorkflowId == workflowId)
            .OrderBy(v => v.VersionNumber).Select(v => v.VersionNumber).ToListAsync());
}
