using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class SqliteEditorDatabase : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider provider;

    public SqliteEditorDatabase(
        Action<WorkflowEditorOptions>? configureEditor = null,
        Action<ReeveWorkflow.EntityFrameworkCore.WorkflowPersistenceOptions>? configureHost = null,
        Action<ReeveWorkflow.EntityFrameworkCore.WorkflowEngineOptions>? configureEngine = null,
        TimeProvider? clock = null,
        IQueryExpressionInterceptor? queryInterceptor = null)
    {
        connection = new SqliteConnection("Filename=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(clock ?? Clock);
        services.AddSingleton<IWorkflowBlockFactory>(new PortlessBlock("noop"));
        var workflows = services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder =>
            {
                builder.UseSqlite(connection).AddInterceptors(Interleave.Commands);
                if (queryInterceptor is not null)
                    builder.AddInterceptors(queryInterceptor);
            });
            configureHost?.Invoke(options);
        })
        .AddWorkflowEditor(options =>
        {
            options.Transaction.Use(
                sp => new InterleavedTransaction(sp.GetRequiredService<WorkflowDbContext>(), Interleave),
                ServiceLifetime.Scoped);
            configureEditor?.Invoke(options);
        });

        if (configureEngine is not null)
            workflows.AddWorkflowEngine(configureEngine);

        provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
    }

    public static TimeProvider Clock { get; } = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-10T12:00:00Z"));

    public IServiceProvider Services => provider;

    public Interleaving Interleave { get; } = new();

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        using var scope = provider.CreateScope();
        return await work(scope.ServiceProvider);
    }

    public Task<T> EditAsync<T>(Func<IWorkflowEditor, Task<T>> work) =>
        InScopeAsync(sp => work(sp.GetRequiredService<IWorkflowEditor>()));

    public async Task<T> ReadAsync<T>(Func<WorkflowDbContext, Task<T>> read)
    {
        using var scope = provider.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<WorkflowDbContext>());
    }

    public void Dispose()
    {
        provider.Dispose();
        connection.Dispose();
    }
}

public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset now = now;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now = now.Add(by);
}

public sealed class Interleaving
{
    private readonly Dictionary<int, Func<Task>> beforeBegin = [];
    private readonly Dictionary<int, Func<Task>> afterCommit = [];
    private int begins;
    private bool running;

    internal CommandHook Commands { get; } = new();

    public void BeforeBegin(int begin, Func<Task> action) => beforeBegin[begins + begin] = action;

    public void AfterCommit(int begin, Func<Task> action) => afterCommit[begins + begin] = action;

    public void BeforeCommand(Func<string, bool> match, string sql, params object[] arguments) =>
        Commands.Arm(match, sql, arguments);

    internal async Task<int?> OnBeginAsync()
    {
        if (running)
            return null;
        var begin = ++begins;
        if (beforeBegin.Remove(begin, out var action))
            await RunAsync(action);
        return begin;
    }

    internal async Task OnClosedAsync(int? begin)
    {
        if (begin is { } n && afterCommit.Remove(n, out var action))
            await RunAsync(action);
    }

    private async Task RunAsync(Func<Task> action)
    {
        running = true;
        try
        {
            await action();
        }
        finally
        {
            running = false;
        }
    }
}

internal sealed class InterleavedTransaction(WorkflowDbContext context, Interleaving hooks) : IWorkflowEditorTransaction
{
    private readonly WorkflowContextEditorTransaction inner = new(context);

    public bool JoinsAnotherTransaction => inner.JoinsAnotherTransaction;

    public Task<IWorkflowEditorTransactionScope> BeginAsync(WorkflowWriteIntent intent, CancellationToken cancellationToken = default) =>
        BeginAsync(cancellationToken);

    public async Task<IWorkflowEditorTransactionScope> BeginAsync(CancellationToken cancellationToken = default)
    {
        var begin = await hooks.OnBeginAsync();
        return new Scope(await inner.BeginAsync(cancellationToken), hooks, begin);
    }

    private sealed class Scope(IWorkflowEditorTransactionScope inner, Interleaving hooks, int? begin)
        : IWorkflowEditorTransactionScope
    {
        private bool committed;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(cancellationToken);
            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            if (committed)
                await hooks.OnClosedAsync(begin);
        }
    }
}

internal sealed class CommandHook : DbCommandInterceptor
{
    private (Func<string, bool> Match, string Sql, object[] Arguments)? armed;

    public List<string> Fired { get; } = [];

    public int Executed { get; private set; }

    public List<string> Commands { get; } = [];

    public void Arm(Func<string, bool> match, string sql, object[] arguments) => armed = (match, sql, arguments);

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Run(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Run(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Run(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Run(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        Run(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Run(command);
        return result;
    }

    private void Run(DbCommand command)
    {
        Executed++;
        Commands.Add(command.CommandText);
        if (armed is not { } hook || !hook.Match(command.CommandText))
            return;
        armed = null;
        Fired.Add(command.CommandText);

        using var peer = command.Connection!.CreateCommand();
        peer.Transaction = command.Transaction;
        peer.CommandText = hook.Sql;
        for (var i = 0; i < hook.Arguments.Length; i++)
        {
            var parameter = peer.CreateParameter();
            parameter.ParameterName = "@p" + i;
            parameter.Value = hook.Arguments[i] switch
            {
                Guid id => id.ToString().ToUpperInvariant(),
                DateTimeOffset at => at.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", System.Globalization.CultureInfo.InvariantCulture),
                var other => other,
            };
            peer.Parameters.Add(parameter);
        }
        peer.ExecuteNonQuery();
    }
}

internal static class SqlCommandText
{
    public static string WithoutLeadingComments(string sql)
    {
        var offset = 0;
        while (offset < sql.Length)
        {
            var lineEnd = sql.IndexOf('\n', offset);
            var end = lineEnd < 0 ? sql.Length : lineEnd;
            if (!sql[offset..end].TrimStart().StartsWith("--", StringComparison.Ordinal))
                break;
            offset = lineEnd < 0 ? sql.Length : lineEnd + 1;
        }
        return sql[offset..].TrimStart();
    }
}

internal sealed class PortlessBlock(string blockTypeId) : IWorkflowBlockFactory
{
    public string BlockTypeId => blockTypeId;
    public string DisplayName => blockTypeId;
    public string Description => string.Empty;
    public string Category => "test";
    public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
    public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
}
