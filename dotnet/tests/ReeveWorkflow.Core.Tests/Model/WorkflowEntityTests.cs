using FluentAssertions;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowEntityTests
{
    [Fact]
    public void Workflow_constructor_initializes_and_normalizes_properties()
    {
        var id = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var workflow = new Workflow(id, "  Test Workflow  ", "  Description  ", creatorId, now, null);

        workflow.Id.Should().Be(id);
        workflow.Name.Should().Be("Test Workflow");
        workflow.Description.Should().Be("Description");
        workflow.HostMetadata.Should().BeNull();

        workflow.CreatedByUserId.Should().Be(creatorId);
        workflow.CreatedAtUtc.Should().Be(now);
        workflow.UpdatedAtUtc.Should().Be(now);
    }

    [Fact]
    public void Workflow_Update_updates_and_normalizes_properties()
    {
        var workflow = new Workflow(Guid.NewGuid(), "Old Name", null, Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        var updateTime = DateTimeOffset.UtcNow.AddHours(1);

        workflow.Update("  New Name  ", "  New Description  ", updateTime, null);

        workflow.Name.Should().Be("New Name");
        workflow.Description.Should().Be("New Description");
        workflow.HostMetadata.Should().BeNull();
        workflow.UpdatedAtUtc.Should().Be(updateTime);
    }

    [Fact]
    public void Workflow_carries_no_enabled_flag()
    {
        typeof(Workflow).GetProperty("IsEnabled").Should().BeNull("a host keeps its own flag");
        typeof(Workflow).GetMethod("SetEnabled").Should().BeNull();
    }

    [Fact]
    public void WorkflowExecutionLog_Complete_updates_status_and_timestamps()
    {
        var log = new WorkflowExecutionLog(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var completeTime = DateTimeOffset.UtcNow.AddSeconds(5);

        log.Complete(WorkflowExecutionStatus.Succeeded, completeTime);

        log.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        log.CompletedAtUtc.Should().Be(completeTime);
        log.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void WorkflowExecutionStatus_has_expected_values_in_stable_order()
    {
        ((int)WorkflowExecutionStatus.Running).Should().Be(0);
        ((int)WorkflowExecutionStatus.Succeeded).Should().Be(1);
        ((int)WorkflowExecutionStatus.Failed).Should().Be(2);
        ((int)WorkflowExecutionStatus.Cancelled).Should().Be(3);
        ((int)WorkflowExecutionStatus.TimedOut).Should().Be(4);
    }

    [Fact]
    public void WorkflowExecutionLog_Complete_with_Cancelled_status_records_error_message()
    {
        var log = new WorkflowExecutionLog(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var completeTime = DateTimeOffset.UtcNow.AddMilliseconds(250);

        log.Complete(WorkflowExecutionStatus.Cancelled, completeTime, "Workflow execution was cancelled.");

        log.Status.Should().Be(WorkflowExecutionStatus.Cancelled);
        log.CompletedAtUtc.Should().Be(completeTime);
        log.ErrorMessage.Should().Be("Workflow execution was cancelled.");
    }

    [Fact]
    public void WorkflowExecutionLog_Complete_with_TimedOut_status_records_error_message()
    {
        var log = new WorkflowExecutionLog(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);
        var completeTime = DateTimeOffset.UtcNow.AddSeconds(1);

        log.Complete(WorkflowExecutionStatus.TimedOut, completeTime, "Workflow execution timed out after 1 seconds.");

        log.Status.Should().Be(WorkflowExecutionStatus.TimedOut);
        log.CompletedAtUtc.Should().Be(completeTime);
        log.ErrorMessage.Should().Be("Workflow execution timed out after 1 seconds.");
    }

    [Fact]
    public void WorkflowExecutionLogEntry_constructor_initializes_properties()
    {
        var id = Guid.NewGuid();
        var logId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var entry = new WorkflowExecutionLogEntry(id, logId, "node-1", "block-type", LogEntryLevel.Info, "Message", "{}", 1, "[]", now);

        entry.Id.Should().Be(id);
        entry.ExecutionLogId.Should().Be(logId);
        entry.NodeId.Should().Be("node-1");
        entry.BlockTypeId.Should().Be("block-type");
        entry.Level.Should().Be(LogEntryLevel.Info);
        entry.Message.Should().Be("Message");
        entry.DataJson.Should().Be("{}");
        entry.IterationIndex.Should().Be(1);
        entry.CallStackJson.Should().Be("[]");
        entry.Timestamp.Should().Be(now);
    }
}
