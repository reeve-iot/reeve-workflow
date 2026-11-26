using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql;

internal sealed class WorkflowPostgreSqlOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? info;

    public DbContextOptionsExtensionInfo Info => info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        services.AddSingleton<IConventionSetPlugin, WorkflowPostgreSqlConventionPlugin>();

    public void Validate(IDbContextOptions options)
    {
        if (WorkflowPostgreSqlSchema.NpgsqlOf(options) is not { } npgsql)
            return;

        var assembly = npgsql.MigrationsAssembly ?? npgsql.MigrationsAssemblyObject?.GetName().Name;
        if (assembly != WorkflowPostgreSqlSchema.MigrationsAssemblyName
            || npgsql.MigrationsHistoryTableName != WorkflowPostgreSqlSchema.MigrationsHistoryTable
            || npgsql.MigrationsHistoryTableSchema != WorkflowPostgreSqlSchema.Schema)
        {
            throw new InvalidOperationException(
                $"The workflow context resolved with migrations assembly '{assembly ?? "(default)"}' and history table "
                + $"'{npgsql.MigrationsHistoryTableSchema ?? "(default)"}.{npgsql.MigrationsHistoryTableName ?? "__EFMigrationsHistory"}', not "
                + $"'{WorkflowPostgreSqlSchema.MigrationsAssemblyName}' and '{WorkflowPostgreSqlSchema.Schema}.{WorkflowPostgreSqlSchema.MigrationsHistoryTable}'. "
                + "A ConfigureDbContext registered after AddPostgreSqlSchema() replaced them; remove its MigrationsAssembly and "
                + "MigrationsHistoryTable settings, or register it before AddPostgreSqlSchema().");
        }
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using workflow PostgreSQL schema ";

        public override int GetServiceProviderHashCode() => typeof(WorkflowPostgreSqlOptionsExtension).GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Workflow:PostgreSql"] = "true";
    }
}
