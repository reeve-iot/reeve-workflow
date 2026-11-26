using FluentAssertions;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowEntityGuardTests
{
    [Fact]
    public void Workflow_constructor_rejects_empty_id()
    {
        var action = () => new Workflow(Guid.Empty, "My Workflow", null, Guid.NewGuid(), DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>().WithParameterName("id");
    }

    [Fact]
    public void Workflow_constructor_rejects_blank_name()
    {
        var action = () => new Workflow(Guid.NewGuid(), "  ", null, Guid.NewGuid(), DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void Workflow_constructor_rejects_empty_createdByUserId()
    {
        var action = () => new Workflow(Guid.NewGuid(), "Flow", null, Guid.Empty, DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>().WithParameterName("createdByUserId");
    }

    [Fact]
    public void Workflow_constructor_sets_properties()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var workflow = new Workflow(id, " My Flow ", " desc ", userId, now);

        workflow.Id.Should().Be(id);
        workflow.Name.Should().Be("My Flow");
        workflow.Description.Should().Be("desc");
        workflow.HostMetadata.Should().BeNull();

        workflow.CreatedByUserId.Should().Be(userId);
        workflow.CreatedAtUtc.Should().Be(now);
        workflow.UpdatedAtUtc.Should().Be(now);
    }

    [Fact]
    public void Workflow_constructor_normalizes_optional_fields_to_null()
    {
        var workflow = new Workflow(Guid.NewGuid(), "Flow", "  ", Guid.NewGuid(), DateTimeOffset.UtcNow);

        workflow.Description.Should().BeNull();
        workflow.HostMetadata.Should().BeNull();

    }

    [Fact]
    public void Workflow_Update_changes_all_fields()
    {
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(Guid.NewGuid(), "Old Name", null, Guid.NewGuid(), now);
        var updateTime = now.AddMinutes(5);

        workflow.Update(" New Name ", " new desc ", updateTime);

        workflow.Name.Should().Be("New Name");
        workflow.Description.Should().Be("new desc");
        workflow.HostMetadata.Should().BeNull();
        workflow.UpdatedAtUtc.Should().Be(updateTime);
    }

    [Fact]
    public void Workflow_Update_normalizes_properties()
    {
        var workflow = new Workflow(Guid.NewGuid(), "Old", null, Guid.NewGuid(), DateTimeOffset.UtcNow);

        workflow.Update("  Updated  ", "  New Description  ", DateTimeOffset.UtcNow);

        workflow.Name.Should().Be("Updated");
        workflow.Description.Should().Be("New Description");
    }

    [Fact]
    public void WorkflowExecutionLog_constructor_rejects_empty_id()
    {
        var action = () => new WorkflowExecutionLog(Guid.Empty, null, Guid.NewGuid(), Guid.NewGuid(), null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>().WithParameterName("id");
    }

    [Fact]
    public void WorkflowExecutionLog_constructor_rejects_empty_workflowId()
    {
        var action = () => new WorkflowExecutionLog(Guid.NewGuid(), null, Guid.Empty, Guid.NewGuid(), null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>().WithParameterName("workflowId");
    }

    [Fact]
    public void WorkflowExecutionLog_constructor_rejects_empty_workflowVersionId()
    {
        var action = () => new WorkflowExecutionLog(Guid.NewGuid(), null, Guid.NewGuid(), Guid.Empty, null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>().WithParameterName("workflowVersionId");
    }

    [Fact]
    public void WorkflowExecutionLog_Complete_updates_status_and_error()
    {
        var log = new WorkflowExecutionLog(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(), null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var completedAt = DateTimeOffset.UtcNow.AddSeconds(5);

        log.Complete(WorkflowExecutionStatus.Failed, completedAt, "Something went wrong");

        log.Status.Should().Be(WorkflowExecutionStatus.Failed);
        log.CompletedAtUtc.Should().Be(completedAt);
        log.ErrorMessage.Should().Be("Something went wrong");
    }

    [Fact]
    public void WorkflowExecutionLog_Complete_succeeds_without_error()
    {
        var log = new WorkflowExecutionLog(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var completedAt = DateTimeOffset.UtcNow.AddSeconds(3);

        log.Complete(WorkflowExecutionStatus.Succeeded, completedAt);

        log.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        log.CompletedAtUtc.Should().Be(completedAt);
        log.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void WorkflowExecutionLogEntry_constructor_rejects_empty_id()
    {
        var action = () => new WorkflowExecutionLogEntry(Guid.Empty, Guid.NewGuid(), "node1", "block1", LogEntryLevel.Info, "msg", null, null, null, DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>().WithParameterName("id");
    }

    [Fact]
    public void WorkflowExecutionLogEntry_constructor_rejects_empty_executionLogId()
    {
        var action = () => new WorkflowExecutionLogEntry(Guid.NewGuid(), Guid.Empty, "node1", "block1", LogEntryLevel.Info, "msg", null, null, null, DateTimeOffset.UtcNow);

        action.Should().Throw<ArgumentException>().WithParameterName("executionLogId");
    }

    [Fact]
    public void WorkflowExecutionLogEntry_constructor_sets_properties()
    {
        var id = Guid.NewGuid();
        var logId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var entry = new WorkflowExecutionLogEntry(id, logId, "node1", "block1", LogEntryLevel.Warning, "A warning", "{}", 3, "[\"root\"]", now);

        entry.Id.Should().Be(id);
        entry.ExecutionLogId.Should().Be(logId);
        entry.NodeId.Should().Be("node1");
        entry.BlockTypeId.Should().Be("block1");
        entry.Level.Should().Be(LogEntryLevel.Warning);
        entry.Message.Should().Be("A warning");
        entry.DataJson.Should().Be("{}");
        entry.IterationIndex.Should().Be(3);
        entry.CallStackJson.Should().Be("[\"root\"]");
        entry.Timestamp.Should().Be(now);
    }
}
