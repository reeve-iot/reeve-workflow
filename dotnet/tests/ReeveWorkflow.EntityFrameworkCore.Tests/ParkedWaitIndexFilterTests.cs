using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class ParkedWaitIndexFilterTests : IDisposable
{
    private readonly SqliteWorkflowDatabase database = new();

    public void Dispose() => database.Dispose();

    private string Filter()
    {
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return db.Model
            .FindEntityType(typeof(WorkflowSuspension))!
            .GetIndexes()
            .Single(index => index.GetDatabaseName() == "ix_workflow_suspensions_parked_speculative")
            .GetFilter()!;
    }

    [Theory]
    [InlineData(WorkflowSuspensionKind.WaitForTrigger)]
    [InlineData(WorkflowSuspensionKind.WaitTemplate)]
    [InlineData(WorkflowSuspensionKind.SubWorkflow)]
    [InlineData(WorkflowSuspensionKind.Predicate)]
    public void The_index_holds_every_kind_a_host_can_wake(WorkflowSuspensionKind kind)
    {
        Filter().Should().MatchRegex($@"\bIN \([^)]*\b{(int)kind}\b[^)]*\)");
    }

    [Fact]
    public void The_index_holds_no_kind_a_host_cannot_wake()
    {
        Filter().Should().NotMatchRegex($@"\bIN \([^)]*\b{(int)WorkflowSuspensionKind.Sleep}\b[^)]*\)");
    }

    [Fact]
    public void The_index_holds_parked_rows_only()
    {
        Filter().Should().Contain($"'{nameof(WorkflowSuspensionStatus.Suspended)}'");
    }
}
