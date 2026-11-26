using FluentAssertions;
using ReeveWorkflow.Core.Model;
using Xunit;

namespace ReeveWorkflow.Core.Tests.Model;

public sealed class WorkflowExecutionLogTests
{
    [Fact]
    public void Complete_persists_outputs_json()
    {
        var log = new WorkflowExecutionLog(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(),
            null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);

        log.Complete(WorkflowExecutionStatus.Succeeded, DateTimeOffset.UtcNow, null, "{\"result\":42}");

        log.Status.Should().Be(WorkflowExecutionStatus.Succeeded);
        log.OutputsJson.Should().Be("{\"result\":42}");
    }

    [Fact]
    public void Complete_without_outputs_leaves_outputs_null()
    {
        var log = new WorkflowExecutionLog(Guid.NewGuid(), null, Guid.NewGuid(), Guid.NewGuid(),
            null, WorkflowExecutionStatus.Running, DateTimeOffset.UtcNow);

        log.Complete(WorkflowExecutionStatus.Failed, DateTimeOffset.UtcNow, "boom");

        log.OutputsJson.Should().BeNull();
    }
}
