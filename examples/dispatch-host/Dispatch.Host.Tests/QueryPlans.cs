using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ReeveWorkflow.EntityFrameworkCore;

namespace Dispatch.Host.Tests;

public static class QueryPlans
{
    public static ServiceProvider Container(string connectionString, RecordingCommands commands) =>
        new ServiceCollection()
            .AddDbContext<WorkflowDbContext>(db => db.UseNpgsql(connectionString).AddInterceptors(commands))
            .BuildServiceProvider();

    public static async Task<string> ExplainAsync(string connectionString, RecordedCommand command)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + command.Text, connection);
        foreach (var parameter in command.Parameters)
            explain.Parameters.Add(parameter.Clone());
        await using var reader = await explain.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    public sealed record RecordedCommand(string Text, IReadOnlyList<NpgsqlParameter> Parameters);

    public sealed class RecordingCommands : DbCommandInterceptor
    {
        private readonly List<RecordedCommand> readers = [];

        public IReadOnlyList<RecordedCommand> Readers
        {
            get { lock (readers) return [.. readers]; }
        }

        private readonly List<string> all = [];

        public IReadOnlyList<string> All
        {
            get { lock (all) return [.. all]; }
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            lock (all) all.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            lock (all) all.Add(command.CommandText);
            lock (readers)
                readers.Add(new RecordedCommand(
                    command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToList()));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
