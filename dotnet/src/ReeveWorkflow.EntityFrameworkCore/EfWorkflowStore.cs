using Microsoft.EntityFrameworkCore;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The reads a run needs before it starts, over the workflow context.</summary>
/// <remarks>All of them are <c>AsNoTracking</c>: admission never writes, and a tracked graph here would follow
/// the run into the engine's own scope and be saved by whatever wrote there next.</remarks>
public sealed class EfWorkflowStore(WorkflowDbContext db) : IWorkflowStore
{
    private readonly WorkflowDbContext db = db ?? throw new ArgumentNullException(nameof(db));

    /// <inheritdoc />
    public Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken) =>
        db.Workflows
            .AsNoTracking()
            .Include(w => w.CurrentVersion)
            .FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken);

    /// <inheritdoc />
    public Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        db.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);

    /// <inheritdoc />
    public Task<WorkflowVersion?> LoadVersionNumberedAsync(
        Guid workflowId, int versionNumber, CancellationToken cancellationToken) =>
        db.WorkflowVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.WorkflowId == workflowId && v.VersionNumber == versionNumber, cancellationToken);

    /// <inheritdoc />
    public async Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(
        Guid workflowId, CancellationToken cancellationToken)
    {
        var child = await db.Workflows
            .AsNoTracking()
            .Include(w => w.CurrentVersion)
            .FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken)
            .ConfigureAwait(false);

        return child?.CurrentVersion is null
            ? null
            : (child.CurrentVersion.Id, child.CurrentVersion.DefinitionJson);
    }
}
