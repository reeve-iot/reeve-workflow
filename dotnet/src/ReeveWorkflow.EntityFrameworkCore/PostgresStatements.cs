using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace ReeveWorkflow.EntityFrameworkCore;

internal static class PostgresStatements
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public static bool Applies(DatabaseFacade database) => database.ProviderName == NpgsqlProviderName;

    public static MappedTable Table<TEntity>(IModel model)
    {
        var entity = model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"The workflow model does not map {typeof(TEntity).Name}.");
        return new MappedTable(entity, StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema()));
    }

    public static async Task<T> RunAsync<T>(
        DatabaseFacade database, string sql, object?[] parameters, Func<DbCommand, CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = database.CurrentTransaction?.GetDbTransaction();
            if (database.GetCommandTimeout() is { } timeout)
                command.CommandTimeout = timeout;

            foreach (var value in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }

            return await execute(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public sealed class MappedTable(IEntityType entity, StoreObjectIdentifier table)
    {
        public string Name { get; } = (table.Schema is null ? "" : Quote(table.Schema) + ".") + Quote(table.Name);

        public string Column(string property) => Quote(Property(property).GetColumnName(table)!);

        public string StoreType(string property) => Property(property).GetRelationalTypeMapping().StoreType;

        public object? ToProvider(string property, object? value) =>
            Property(property).GetTypeMapping().Converter is { } converter && value is not null
                ? converter.ConvertToProvider(value)
                : value;

        public object? FromProvider(string property, object? value) =>
            Property(property).GetTypeMapping().Converter is { } converter && value is not null and not DBNull
                ? converter.ConvertFromProvider(value)
                : value;

        private IProperty Property(string name) =>
            entity.FindProperty(name) ?? throw new InvalidOperationException($"The workflow model does not map {name}.");
    }
}
