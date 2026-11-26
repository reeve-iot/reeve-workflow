using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.Editor;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

public sealed partial class PublishGateOnPostgresTests
{
    [Fact]
    public async Task A_door_on_a_host_scope_keeps_and_saves_the_hosts_staged_version_log_and_added_version()
    {
        await using var desk = await DeskAsync("host_scope_staged");
        var created = await desk.CreateAsync(Valid);
        await desk.SaveAsync(created.Workflow.Id, ValidB);
        var id = created.Workflow.Id;
        var logId = Guid.NewGuid();
        var addedId = Guid.NewGuid();
        await using (var scope = desk.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            context.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(logId, null, id, created.VersionId!.Value, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        await using (var scope = desk.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            await context.Workflows.SingleAsync(w => w.Id == id);
            var current = await context.WorkflowVersions.SingleAsync(v => v.Id == created.VersionId);
            current.UpdateDefinition(ValidC, null, null);
            var log = await context.WorkflowExecutionLogs.SingleAsync(l => l.Id == logId);
            log.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow);
            var added = new WorkflowVersion(addedId, id, 9, ValidC, null, null, Owner, DateTimeOffset.UtcNow);
            context.WorkflowVersions.Add(added);

            await scope.ServiceProvider.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, id,
                new UpdateWorkflowRequest("Renamed", null, ValidB, AutoPublish: false));

            (context.Entry(current).State, context.Entry(log).State, context.Entry(added).State)
                .Should().Be((EntityState.Unchanged, EntityState.Unchanged, EntityState.Unchanged));
        }

        (await desk.VersionAsync(created.VersionId!.Value)).DefinitionJson.Should().Contain("\"c\"", "the staged text was saved");
        (await desk.VersionAsync(addedId)).VersionNumber.Should().Be(9);
        await using (var scope = desk.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().WorkflowExecutionLogs.AsNoTracking()
                .Where(l => l.Id == logId).Select(l => l.Status).SingleAsync()).Should().Be(WorkflowExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task A_run_log_staged_for_a_version_whose_row_is_gone_fails_the_door_rather_than_vanish()
    {
        await using var desk = await DeskAsync("host_scope_gone_row");
        var created = await desk.CreateAsync(Valid);
        var draft = await desk.SaveAsync(created.Workflow.Id, ValidB);
        var id = created.Workflow.Id;

        await using var scope = desk.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        await context.WorkflowVersions.SingleAsync(v => v.Id == draft.VersionId);
        context.WorkflowExecutionLogs.Add(new WorkflowExecutionLog(Guid.NewGuid(), null, id, draft.VersionId!.Value, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow));
        await desk.SaveAsync(id, Valid);
        await desk.ExecuteAsync($"DELETE FROM automation.workflow_versions WHERE id = '{draft.VersionId}'");

        var door = () => scope.ServiceProvider.GetRequiredService<IWorkflowEditor>().UpdateAsync(Owner, id,
            new UpdateWorkflowRequest("Renamed", null, Valid, AutoPublish: false));

        await door.Should().ThrowAsync<DbUpdateException>();
        (await desk.RowAsync(id)).Name.Should().Be("Ship");
    }
}
