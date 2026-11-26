using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore.Tests;

public sealed class SqliteWorkflowDatabase : IDisposable
{
    private readonly SqliteConnection? connection;
    private readonly string? file;
    private readonly ServiceProvider provider;

    public SqliteWorkflowDatabase(
        Action<IServiceCollection>? configure = null, bool onFile = false, Action<DbContextOptionsBuilder>? context = null,
        Action<WorkflowPersistenceOptions>? workflows = null, Action<IServiceCollection>? after = null)
    {
        if (onFile)
        {
            file = Path.Combine(Path.GetTempPath(), $"reeve-workflow-{Guid.NewGuid():N}.db");
        }
        else
        {
            connection = new SqliteConnection("Filename=:memory:");
            connection.Open();
        }

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        configure?.Invoke(services);
        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder =>
            {
                if (connection is not null)
                    builder.UseSqlite(connection);
                else
                    builder.UseSqlite($"Data Source={file};Default Timeout=30;Pooling=False");
                context?.Invoke(builder);
            });
            workflows?.Invoke(options);
        });
        after?.Invoke(services);

        provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
    }

    public IServiceProvider Services => provider;

    public Guid SeedWorkflow()
    {
        var workflowId = Guid.NewGuid();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        db.Workflows.Add(new Workflow(
            workflowId,
            "test",
            description: null,
            createdByUserId: Guid.NewGuid(),
            createdAtUtc: DateTimeOffset.UtcNow));
        db.SaveChanges();
        return workflowId;
    }

    public (Guid WorkflowId, Guid VersionId) SeedPublishedWorkflow()
    {
        var workflowId = SeedWorkflow();
        var versionId = Guid.NewGuid();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        db.WorkflowVersions.Add(new WorkflowVersion(
            versionId,
            workflowId,
            versionNumber: 1,
            definitionJson: "{\"nodes\":[],\"edges\":[]}",
            inputParametersJson: null,
            outputParametersJson: null,
            createdByUserId: Guid.NewGuid(),
            createdAtUtc: DateTimeOffset.UtcNow,
            isPublished: true));
        db.SaveChanges();
        return (workflowId, versionId);
    }

    public void Dispose()
    {
        provider.Dispose();
        connection?.Dispose();
        if (file is not null)
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
