using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.EntityFrameworkCore;

namespace ReeveWorkflow.Editor;

internal sealed class WorkflowCompilationReads(
    WorkflowDbContext context,
    WorkflowCompilationService compiler,
    WorkflowCompilationSweeper sweeper,
    ILogger<WorkflowCompilationReads> logger)
{
    public const int MaxTake = 50;

    public async Task<CompilationReportResponse> InvalidAsync(Guid userId, int skip, int take, CancellationToken cancellationToken)
    {
        (skip, take) = WorkflowPaging.ClampSkipTake(skip, take, MaxTake);

        var owned = context.Workflows.AsNoTracking().Where(w => w.CreatedByUserId == userId && w.CurrentVersionId != null);
        var totalCount = await owned.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await owned
            .OrderBy(w => w.Id)
            .Skip(skip)
            .Take(take)
            .Select(w => new { w.Id, Version = w.CurrentVersion! })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var page = rows.Select(r => new CurrentWorkflowVersion(r.Id, r.Version)).ToList();

        var report = await sweeper.SweepAsync(page.ToAsyncEnumerable(), cancellationToken).ConfigureAwait(false);

        var workflowOf = new Dictionary<Guid, Guid>();
        foreach (var entry in page)
            workflowOf.TryAdd(entry.Version.Id, entry.WorkflowId);

        foreach (var failure in report.Failed)
            logger.LogWarning(failure.Error,
                "Whether workflow version {WorkflowVersionId} compiles could not be answered for the invalid listing.",
                failure.WorkflowVersionId);

        return new CompilationReportResponse(
            [.. report.Invalid.Select(v => new WorkflowCompilationResponse(workflowOf[v.WorkflowVersionId], v.WorkflowVersionId, v.IsValid, v.Diagnostics))],
            [.. report.Failed.Select(f => new CompilationVersionResponse(workflowOf[f.WorkflowVersionId], f.WorkflowVersionId))],
            [.. report.Unsaved.Select(id => new CompilationVersionResponse(workflowOf[id], id))],
            report.Shared,
            totalCount, skip, take);
    }

    public async Task<WorkflowCompilationResponse?> VerdictAsync(Guid userId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await context.WorkflowVersions.AsNoTracking()
            .Where(v => v.Id == versionId && context.Workflows.Any(w => w.Id == v.WorkflowId && w.CreatedByUserId == userId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
            return null;

        CompilationVerdict verdict;
        try
        {
            verdict = await compiler.EnsureAsync(version, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception fault)
        {
            logger.LogWarning(fault, "Whether workflow version {WorkflowVersionId} compiles could not be answered.", version.Id);
            throw new WorkflowCompilationUnansweredException(version.WorkflowId, version.Id,
                $"Whether version '{version.Id}' of workflow '{version.WorkflowId}' compiles could not be answered. "
                + "Asking again may succeed.", fault);
        }

        return new WorkflowCompilationResponse(version.WorkflowId, version.Id, verdict.IsValid, verdict.Diagnostics);
    }
}
