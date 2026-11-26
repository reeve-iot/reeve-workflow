using FluentAssertions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Editor;

namespace Dispatch.Host.Tests;

public sealed partial class PublishGateOnPostgresTests
{
    private const string UnreadableDraft = """{"nodes":[{"id":"a","blockTypeId":"noop","config":{},"position":{"x":0,"y":0}}],"edges":[]}""";

    [Fact]
    public async Task A_repeated_unreadable_draft_is_refused_before_commit_and_readable_text_written_into_it_publishes()
    {
        await using var desk = await DeskAsync("gate_repeated_unreadable");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        await desk.ExecuteAsync($"UPDATE automation.workflow_versions SET definition_json = '{UnreadableDraft}'::jsonb WHERE id = '{draft.VersionId}'");
        var stored = (await desk.VersionAsync(draft.VersionId!.Value)).DefinitionJson;
        var revisionBefore = await desk.RevisionAsync(created.Workflow.Id);

        var refused = () => desk.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id, new UpdateWorkflowRequest("Renamed", null, stored)));
        (await refused.Should().ThrowAsync<InvalidWorkflowDefinitionException>()).Which.Message.Should().Contain("config");
        var row = await desk.RowAsync(created.Workflow.Id);
        (row.Name, row.Revision, row.DraftVersionId).Should().Be(("Ship", revisionBefore, draft.VersionId));

        using var within = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var drafted = await desk.EditAsync(e => e.UpdateAsync(Owner, created.Workflow.Id,
            new UpdateWorkflowRequest("Renamed", null, stored, AutoPublish: false), within.Token));
        (drafted.Fault, drafted.VersionPublished, drafted.Workflow.Name).Should().Be((null, false, "Renamed"));

        var fixedUp = await desk.SaveAsync(created.Workflow.Id, ValidC, autoPublish: true);
        (fixedUp.VersionPublished, fixedUp.VersionId).Should().Be((true, draft.VersionId), "written into the draft it read");
    }
}
