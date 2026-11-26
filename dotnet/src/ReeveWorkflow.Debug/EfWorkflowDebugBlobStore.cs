using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Model;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Debug;

/// <summary>Snapshot bytes in the workflow context's own table, the default for a host without object
/// storage.</summary>
/// <remarks>A blob here is read by whichever replica is asked for it, which is the property a local disk
/// cannot offer. A host with object storage still fills the seam:
/// these rows are large and short-lived, and a database is a more expensive place to keep them than a bucket.</remarks>
public sealed class EfWorkflowDebugBlobStore(
    IServiceScopeFactory scopeFactory, TimeProvider? timeProvider = null) : IWorkflowDebugBlobStore
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task PutBytesAsync(string key, byte[] content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var existing = await db.WorkflowDebugBlobs
            .FirstOrDefaultAsync(blob => blob.Key == key, cancellationToken).ConfigureAwait(false);

        if (existing is null)
            db.WorkflowDebugBlobs.Add(new WorkflowDebugBlob(key, content, clock.GetUtcNow()));
        else
            existing.Replace(content, clock.GetUtcNow());

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetBytesAsync(string key, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        return await db.WorkflowDebugBlobs
            .AsNoTracking()
            .Where(blob => blob.Key == key)
            .Select(blob => blob.Content)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        await db.WorkflowDebugBlobs
            .Where(blob => blob.Key == key)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
