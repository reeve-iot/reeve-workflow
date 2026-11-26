using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql;

/// <summary>The facts about the workflow schema that are the library's to state, in one place so the running
/// context and the design-time factory cannot disagree about them.</summary>
public static class WorkflowPostgreSqlSchema
{
    /// <summary>The table recording which workflow migrations have been applied.</summary>
    public const string MigrationsHistoryTable = "__WorkflowMigrationsHistory";

    /// <summary>The schema holding the workflow tables and <see cref="MigrationsHistoryTable"/>.</summary>
    public const string Schema = "automation";

    private const string NpgsqlProviderAssembly = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>Points a context already on Npgsql at this package's migrations and history table, switches the
    /// TimescaleDB migration generator on and registers the hypertable convention. <c>AddPostgreSqlSchema</c> calls
    /// it for the context <c>AddWorkflows</c> registers; a host that builds a <see cref="WorkflowDbContext"/> by hand
    /// calls it itself, after <c>UseNpgsql</c>.</summary>
    public static DbContextOptionsBuilder UseWorkflowPostgreSqlSchema(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        var infrastructure = (IDbContextOptionsBuilderInfrastructure)optionsBuilder;

        if (NpgsqlOf(optionsBuilder.Options) is not { } npgsql)
        {
            throw new InvalidOperationException(
                "AddPostgreSqlSchema needs the workflow context on Npgsql. Call options.ConfigureDbContext(b => b.UseNpgsql(...)) in AddWorkflows.");
        }

        infrastructure.AddOrUpdateExtension(npgsql
            .WithMigrationsAssembly(MigrationsAssemblyName)
            .WithMigrationsHistoryTableName(MigrationsHistoryTable)
            .WithMigrationsHistoryTableSchema(Schema));

        CmdScale.EntityFrameworkCore.TimescaleDB.TimescaleDbContextOptionsBuilderExtensions.UseTimescaleDb(optionsBuilder);
        infrastructure.AddOrUpdateExtension(new WorkflowPostgreSqlOptionsExtension());
        return optionsBuilder;
    }

    internal static RelationalOptionsExtension? NpgsqlOf(IDbContextOptions options) =>
        options.Extensions.OfType<RelationalOptionsExtension>().SingleOrDefault(
            extension => extension.GetType().Assembly.GetName().Name == NpgsqlProviderAssembly);

    internal static string MigrationsAssemblyName { get; } = typeof(WorkflowPostgreSqlSchema).Assembly.GetName().Name!;

    /// <inheritdoc cref="UseWorkflowPostgreSqlSchema(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseWorkflowPostgreSqlSchema<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder) where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseWorkflowPostgreSqlSchema((DbContextOptionsBuilder)optionsBuilder);
}
