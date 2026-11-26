using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ReeveWorkflow.EntityFrameworkCore;

internal sealed class EfWorkflowSchemaMigrator(WorkflowDbContext dbContext, ILogger<EfWorkflowSchemaMigrator>? logger = null)
    : IWorkflowSchemaMigrator
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.HasPendingModelChanges())
        {
            throw new WorkflowDatabaseNotReadyException(
                "The workflow model has changes that no migration covers, so the database would be missing them. "
                + "On PostgreSQL the usual cause is a missing AddPostgreSqlSchema() after AddWorkflows(); on another provider, scaffold the migration into the assembly its options name.");
        }

        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (pending.Length > 0)
        {
            logger?.LogInformation("Applying {Count} workflow migration(s): {Migrations}", pending.Length, string.Join(", ", pending));
        }

        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
