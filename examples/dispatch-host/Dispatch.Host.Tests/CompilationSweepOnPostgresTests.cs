using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;
using ReeveWorkflow.EntityFrameworkCore.PostgreSql;

namespace Dispatch.Host.Tests;

[Collection(TwoInstanceCollection.Name), DropTestDatabases]
[Trait("Category", "Docker")]
public sealed class CompilationSweepOnPostgresTests(ClusterBackends backends)
{
    [Fact]
    public async Task A_database_is_swept_only_once_its_migrations_are_applied()
    {
        var connectionString = await backends.CreateDatabaseAsync("dispatch_compilation_sweep_readiness");
        var warnings = new Warnings();
        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder.AddProvider(warnings));
        collection.AddWorkflows(workflows =>
        {
            workflows.ConfigureDbContext(context => context.UseNpgsql(connectionString));
        }).AddPostgreSqlSchema();
        await using var services = collection.BuildServiceProvider();
        var runner = services.GetRequiredService<WorkflowCompilationSweepRunner>();

        var unmigrated = await runner.RunOnceAsync(default);
        await services.EnsureWorkflowsDatabaseAsync(WorkflowDatabaseMode.MigrateThenVerify);
        var versionId = await SeedAsync(services);
        var ready = await runner.RunOnceAsync(default);

        unmigrated.Should().BeNull();
        warnings.Messages.Should().ContainSingle().Which.Should().Contain("unapplied migration(s)");
        ready!.Report.Current.Should().ContainSingle().Which.WorkflowVersionId.Should().Be(versionId);
    }

    private sealed class Warnings : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) =>
            categoryName == typeof(WorkflowCompilationSweepRunner).FullName ? new Logger(Messages) : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class Logger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                    lock (messages) messages.Add(formatter(state, exception));
            }
        }
    }

    private static async Task<Guid> SeedAsync(IServiceProvider services)
    {
        const string definition = """{"nodes":[],"edges":[]}""";
        var now = DateTimeOffset.UtcNow;
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        var workflow = new Workflow(Guid.NewGuid(), "wf", null, Guid.NewGuid(), now);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var version = new WorkflowVersion(Guid.NewGuid(), workflow.Id, 1, definition, null, null, Guid.NewGuid(), now, isPublished: true);
        db.WorkflowVersions.Add(version);
        await db.SaveChangesAsync();
        workflow.SetCurrentVersion(version, now);
        await db.SaveChangesAsync();
        return version.Id;
    }
}
