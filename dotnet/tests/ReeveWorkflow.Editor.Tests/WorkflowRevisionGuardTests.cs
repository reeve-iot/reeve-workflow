using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Editor.Tests;

public sealed class WorkflowRevisionGuardTests
{
    private const string Valid = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}}],"edges":[]}""";
    private const string ValidB = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"b","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"b","mappings":{}}]}""";
    private const string ValidC = """{"nodes":[{"id":"start","blockTypeId":"WorkflowInput","position":{"x":0,"y":0},"configuration":{}},{"id":"c","blockTypeId":"noop","position":{"x":0,"y":0},"configuration":{}}],"edges":[{"id":"e","sourceNodeId":"start","targetNodeId":"c","mappings":{}}]}""";

    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Stranger = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task A_workflow_answers_with_the_revision_it_holds()
    {
        using var db = new SqliteEditorDatabase();

        var created = await CreateAsync(db);
        var saved = await SaveAsync(db, created.Workflow.Id, "Ship", ValidB, expected: null);

        created.Workflow.Revision.Should().Be(1, "the create's promote moved it from 0");
        saved.Workflow.Revision.Should().Be(2, "the draft save moved it by one");
        (await RevisionAsync(db, created.Workflow.Id)).Should().Be(2);
    }

    [Theory]
    [InlineData(Valid)]
    [InlineData(ValidB)]
    public async Task A_save_at_the_revision_its_caller_read_moves_the_revision_by_one(string definition)
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db);

        var saved = await SaveAsync(db, created.Workflow.Id, "Renamed", definition, expected: 1);

        var row = await RowAsync(db, created.Workflow.Id);
        row.Name.Should().Be("Renamed");
        row.Revision.Should().Be(2, "a guarded save moves it once, whether or not it writes version text");
        saved.Workflow.Revision.Should().Be(2);
    }

    [Fact]
    public async Task A_rename_moves_the_revision_so_a_save_from_the_copy_read_before_it_is_refused()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db);
        await SaveAsync(db, created.Workflow.Id, "Peer", Valid, expected: null);

        var stale = () => SaveAsync(db, created.Workflow.Id, "Mine", ValidC, expected: created.Workflow.Revision);

        var refusal = (await stale.Should().ThrowAsync<WorkflowRevisionMismatchException>()).Which;
        refusal.WorkflowId.Should().Be(created.Workflow.Id);
        refusal.ExpectedRevision.Should().Be(1);
        var row = await RowAsync(db, created.Workflow.Id);
        row.Name.Should().Be("Peer", "the rename stands");
        row.Revision.Should().Be(2, "the rename's move, and none of the refused save");
        row.DraftVersionId.Should().BeNull("the refused save wrote no draft");
    }

    [Fact]
    public async Task A_save_from_a_copy_another_write_moved_is_refused_and_writes_nothing()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db);
        await SaveAsync(db, created.Workflow.Id, "Ship", ValidB, expected: null);
        var before = await RowAsync(db, created.Workflow.Id);

        var stale = () => SaveAsync(db, created.Workflow.Id, "Renamed", ValidC, expected: 1);

        await stale.Should().ThrowAsync<WorkflowRevisionMismatchException>();
        var after = await RowAsync(db, created.Workflow.Id);
        after.Name.Should().Be("Ship");
        after.Revision.Should().Be(2);
        after.DraftVersionId.Should().Be(before.DraftVersionId);
        after.DraftVersion!.DefinitionJson.Should().Be(ValidB);
    }

    [Fact]
    public async Task An_auto_published_save_answers_with_a_revision_the_next_guarded_save_is_accepted_at()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db);

        var published = await SaveAsync(db, created.Workflow.Id, "Ship", ValidB, expected: 1, autoPublish: true);
        var next = await SaveAsync(db, created.Workflow.Id, "Ship", ValidC, expected: published.Workflow.Revision);

        published.VersionPublished.Should().BeTrue();
        published.Workflow.Revision.Should().Be(3, "the update's move to 2, then the promote's to 3");
        next.Workflow.Revision.Should().Be(4);
        (await RevisionAsync(db, created.Workflow.Id)).Should().Be(4);
    }

    [Fact]
    public async Task A_guarded_save_by_someone_else_is_told_the_workflow_does_not_exist()
    {
        using var db = new SqliteEditorDatabase();
        var created = await CreateAsync(db);

        var foreign = () => db.EditAsync(e => e.UpdateAsync(Stranger, created.Workflow.Id,
            new UpdateWorkflowRequest("Mine", null, null) { ExpectedRevision = created.Workflow.Revision }));

        await foreign.Should().ThrowAsync<WorkflowNotFoundException>();
        (await RowAsync(db, created.Workflow.Id)).Revision.Should().Be(1);
    }

    private static Task<WorkflowEditResult> CreateAsync(SqliteEditorDatabase db) =>
        db.EditAsync(e => e.CreateAsync(Owner, new CreateWorkflowRequest("Ship", null, Valid)));

    private static Task<WorkflowEditResult> SaveAsync(
        SqliteEditorDatabase db, Guid workflowId, string name, string definition, int? expected, bool autoPublish = false) =>
        db.EditAsync(e => e.UpdateAsync(Owner, workflowId,
            new UpdateWorkflowRequest(name, null, definition, AutoPublish: autoPublish) { ExpectedRevision = expected }));

    private static Task<ReeveWorkflow.Core.Model.Workflow> RowAsync(SqliteEditorDatabase db, Guid workflowId) =>
        db.ReadAsync(c => c.Workflows.AsNoTracking().Include(w => w.DraftVersion).SingleAsync(w => w.Id == workflowId));

    private static Task<int> RevisionAsync(SqliteEditorDatabase db, Guid workflowId) =>
        db.ReadAsync(c => c.Workflows.AsNoTracking().Where(w => w.Id == workflowId).Select(w => w.Revision).SingleAsync());
}
