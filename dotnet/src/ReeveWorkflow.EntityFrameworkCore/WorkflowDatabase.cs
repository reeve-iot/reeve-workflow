using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>What <see cref="WorkflowDatabaseExtensions.EnsureWorkflowsDatabaseAsync"/> is allowed to do. An enum
/// rather than a boolean because "do not migrate" and "do not check either" are different deployments.</summary>
public enum WorkflowDatabaseMode
{
    /// <summary>Neither migrate nor verify. For a host that has its own story for this schema.</summary>
    None = 0,

    /// <summary>Apply outstanding migrations, then verify. The single-process and rolling-deploy default.</summary>
    MigrateThenVerify = 1,

    /// <summary>Verify only, and refuse to start on an outstanding schema migration. The right mode when a separate
    /// deploy job applies migrations: a replica that starts against an unmigrated database fails loudly instead.</summary>
    VerifyOnly = 2,
}

/// <summary>Raised when the workflow schema is not in the state the configured mode requires.</summary>
public sealed class WorkflowDatabaseNotReadyException(string message) : InvalidOperationException(message);

/// <summary>The one operation the workflow library needs before the host starts serving.</summary>
public static class WorkflowDatabaseExtensions
{
    /// <summary>Brings the workflow schema to the state the mode requires, or throws. No leader election is
    /// needed for one outstanding migration; a database several behind needs one caller. See the remarks.</summary>
    /// <remarks>EF takes an exclusive lock on the migrations-history table, but it takes it inside the transaction of one migration and drops it at that migration's commit, and it rereads the history under the lock only on the first acquisition. Two callers that each start several migrations behind therefore interleave: one applies a migration off a list the other has already moved past, and fails on a duplicate history row or on DDL that is already there. With exactly one outstanding — what a deploy leaves — they serialise and it runs once.</remarks>
    public static async Task EnsureWorkflowsDatabaseAsync(
        this IServiceProvider services,
        WorkflowDatabaseMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (mode == WorkflowDatabaseMode.None)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        RefuseModelChangesNoMigrationCovers(dbContext);

        if (mode == WorkflowDatabaseMode.MigrateThenVerify)
        {
            await scope.ServiceProvider.GetRequiredService<IWorkflowSchemaMigrator>().MigrateAsync(cancellationToken);
        }

        await RefuseUnappliedMigrationsAsync(dbContext, cancellationToken);
    }

    internal static async Task RefuseUnlessReadyToSweepAsync(WorkflowDbContext dbContext, CancellationToken cancellationToken)
    {
        if (dbContext.Database.GetMigrations().Any())
        {
            RefuseModelChangesNoMigrationCovers(dbContext);
            await RefuseUnappliedMigrationsAsync(dbContext, cancellationToken);
        }
    }

    private static void RefuseModelChangesNoMigrationCovers(WorkflowDbContext dbContext)
    {
        if (dbContext.Database.HasPendingModelChanges())
        {
            throw new WorkflowDatabaseNotReadyException(
                "The workflow model has changes that no migration covers, so the database would be missing them. "
                + "On PostgreSQL the usual cause is a missing AddPostgreSqlSchema() after AddWorkflows(); on another provider, scaffold the migration into the assembly its options name.");
        }
    }

    private static async Task RefuseUnappliedMigrationsAsync(WorkflowDbContext dbContext, CancellationToken cancellationToken)
    {
        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (pending.Length > 0)
        {
            throw new WorkflowDatabaseNotReadyException(
                $"The workflow schema has {pending.Length} unapplied migration(s): {string.Join(", ", pending)}. "
                + "Apply them, or start with WorkflowDatabaseMode.MigrateThenVerify.");
        }
    }
}
