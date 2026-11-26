using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace ReeveWorkflow.EntityFrameworkCore.PostgreSql;

internal sealed class WorkflowPostgreSqlConventionPlugin : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.ModelFinalizingConventions.Add(new WorkflowPostgreSqlConvention());
        return conventionSet;
    }
}

internal sealed class WorkflowPostgreSqlConvention : IModelFinalizingConvention
{
    internal const string StepTable = "execution_node_log";
    internal const string TimeColumn = "StartedAt";

    private const string IsHypertable = "TimescaleDB:IsHypertable";
    private const string TimeColumnName = "TimescaleDB:TimeColumnName";
    private const string MigrateData = "TimescaleDB:MigrateData";

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        PostgresExtension.GetOrAddPostgresExtension(modelBuilder.Metadata, null, "timescaledb", null);
        PostgresJsonFunctions.AddTo((IMutableModel)modelBuilder.Metadata);

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            if (entityType.GetTableName() != StepTable)
            {
                continue;
            }

            var timeProperty = entityType.FindProperty(TimeColumn);
            if (timeProperty is null)
            {
                continue;
            }

            entityType.SetAnnotation(IsHypertable, true);
            entityType.SetAnnotation(TimeColumnName, TimeColumn);
            entityType.SetAnnotation(MigrateData, true);

            var key = entityType.FindPrimaryKey();
            if (key is not null && !key.Properties.Contains(timeProperty))
            {
                var widened = new List<IMutableProperty> { (IMutableProperty)timeProperty };
                widened.AddRange(key.Properties.Cast<IMutableProperty>());
                var mutable = (IMutableEntityType)entityType;
                mutable.SetPrimaryKey(widened);

                mutable.RemoveKey((IMutableKey)key);
            }
        }
    }
}
