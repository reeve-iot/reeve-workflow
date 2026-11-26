using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug.Tests;

public class ScopedAuditSinkTests : IDisposable
{
    private readonly SqliteConnection connection = new("Filename=:memory:");

    [Fact]
    public void The_session_service_resolves_from_the_root_provider()
    {
        using var provider = Build();

        var resolve = () => provider.GetRequiredService<IWorkflowDebugSessionService>();

        resolve.Should().NotThrow<InvalidOperationException>();
    }

    [Fact]
    public async Task A_debug_session_audits_through_the_scoped_sink_the_host_registered()
    {
        using var provider = Build();
        var owner = Guid.NewGuid();
        var workflowId = SeedWorkflow(provider, owner);

        await provider.GetRequiredService<IWorkflowDebugSessionService>()
            .CreateAsync(workflowId, owner);

        RecordingAuditSink.Written.Should().ContainSingle("the host's own sink is the one that was written to")
            .Which.Event.Kind.Should().Be(WorkflowAuditEventKinds.DebugSessionStarted);
    }

    [Fact]
    public async Task The_scope_a_write_ran_in_is_gone_by_the_time_the_write_returns()
    {
        using var provider = Build();
        var owner = Guid.NewGuid();
        var workflowId = SeedWorkflow(provider, owner);

        await provider.GetRequiredService<IWorkflowDebugSessionService>()
            .CreateAsync(workflowId, owner);

        RecordingAuditSink.Written.Should().OnlyContain(write => write.Scope.IsDisposed);
    }

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private ServiceProvider Build()
    {
        RecordingAuditSink.Written.Clear();
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));

        services.AddScoped<ScopeMarker>();
        services.AddScoped<IWorkflowAuditSink, RecordingAuditSink>();

        services.AddWorkflows(options =>
        {
            options.ConfigureDbContext(builder => builder.UseSqlite(connection));
            options.Seams.AuditSink.Use<RecordingAuditSink>(ServiceLifetime.Scoped);
        });

        services.AddWorkflowDebug(seams: debug => debug.BlobStore.Use<InMemoryBlobStore>());

        var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Database.EnsureCreated();
        return provider;
    }

    private static Guid SeedWorkflow(IServiceProvider provider, Guid owner)
    {
        var workflowId = Guid.NewGuid();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        db.Workflows.Add(new Workflow(
            workflowId, "test", description: null,
            createdByUserId: owner, createdAtUtc: DateTimeOffset.UtcNow));
        db.SaveChanges();
        return workflowId;
    }

    private sealed class ScopeMarker : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed record Write(WorkflowAuditEvent Event, ScopeMarker Scope);

    private sealed class RecordingAuditSink(ScopeMarker scope) : IWorkflowAuditSink
    {
        public static readonly List<Write> Written = [];

        public Task WriteAsync(WorkflowAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (Written) Written.Add(new Write(auditEvent, scope));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryBlobStore : IWorkflowDebugBlobStore
    {
        private readonly Dictionary<string, byte[]> blobs = [];

        public Task PutBytesAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        {
            blobs[key] = content;
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(blobs.TryGetValue(key, out var content) ? content : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            blobs.Remove(key);
            return Task.CompletedTask;
        }
    }
}
