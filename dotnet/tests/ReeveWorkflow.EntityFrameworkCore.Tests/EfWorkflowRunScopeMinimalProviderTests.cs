using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Hosting.Defaults;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public class EfWorkflowRunScopeMinimalProviderTests
{
    private sealed class LiteralProvider(WorkflowDbContext db) : IServiceProvider, IServiceScope
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(WorkflowDbContext)) return db;
            if (serviceType == typeof(IWorkflowAuditSink))
                return new LoggingWorkflowAuditSink(NullLogger<LoggingWorkflowAuditSink>.Instance);
            if (serviceType == typeof(IWorkflowAuthorization))
                return new ConfiguredWorkflowAuthorization([], NullLogger.Instance);
            return null;
        }

        public void Dispose() => db.Dispose();
    }

    [Fact]
    public void A_provider_that_knows_nothing_about_contributors_still_yields_a_run_scope()
    {
        var connection = new SqliteConnection("Filename=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<WorkflowDbContext>().UseSqlite(connection).Options;

        using var runScope = new EfWorkflowRunScope(new LiteralProvider(new WorkflowDbContext(options)));

        runScope.Journal.Should().BeOfType<EfWorkflowRunJournal>();
        connection.Dispose();
    }
}
