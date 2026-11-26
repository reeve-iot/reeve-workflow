using Microsoft.EntityFrameworkCore;
using ReeveWorkflow.DependencyInjection;

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql;

/// <summary>How a host turns this package on: one call, hung on the builder <c>AddWorkflows</c> returned.</summary>
public static class WorkflowPostgreSqlBuilderExtensions
{
    /// <summary>Gives the workflow context this package's migrations, its history table
    /// (<c>automation.__WorkflowMigrationsHistory</c>) and the TimescaleDB hypertable on the execution-step table.
    /// Requires TimescaleDB on the server and an Npgsql provider named in <c>AddWorkflows</c>. Written after
    /// <c>AddWorkflows(...)</c> it still applies: options are composed when the container builds the context, and
    /// this runs after the provider the host named. A later <c>ConfigureDbContext</c> that sets another migrations
    /// assembly or history table is refused when the context is first built.</summary>
    public static IWorkflowBuilder AddPostgreSqlSchema(this IWorkflowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigureDbContext(options => options.UseWorkflowPostgreSqlSchema());
    }
}
