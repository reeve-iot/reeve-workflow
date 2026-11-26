using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class WorkflowIndexBuildTests
{
    [Fact]
    public void No_index_asks_to_be_built_outside_the_migration_s_transaction()
    {
        using var database = new SqliteWorkflowDatabase();
        using var scope = database.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Model;

        var concurrent = model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes())
            .Where(index => index.FindAnnotation("Npgsql:CreatedConcurrently") is not null)
            .Select(index => index.GetDatabaseName())
            .ToArray();

        concurrent.Should().BeEmpty(
            "the dispatch host's own two-replica test deadlocks on exactly this, and the annotation is how it "
            + "reaches a migration nobody hand-wrote");
    }
}
