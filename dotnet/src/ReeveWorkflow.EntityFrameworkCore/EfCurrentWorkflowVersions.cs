using System.Runtime.CompilerServices;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>Every workflow's current version, which is the version admission runs for a root run: what
/// <c>WorkflowCompilationSweeper.SweepAsync</c> is given. A workflow with no current version lists nothing, and a
/// draft or an earlier version is never listed.</summary>
/// <remarks>Read in pages ordered by workflow id, each page on a scope of its own with <c>AsNoTracking</c>, so no
/// connection is held while the caller compiles what a page returned. Each page is read with the ambient
/// <see cref="TransactionScope"/> suppressed, as <c>EfWorkflowCompilationStore</c> suppresses it: a caller's
/// transaction is neither joined nor read from. A version is read as stored, the text a run reads. Pages are
/// separate reads: a workflow whose current version moves between them is listed as whichever its page saw, and
/// one that gets its first current version after the page its id falls in was read is not listed at all.</remarks>
/// <param name="scopeFactory">Where each page's workflow context comes from.</param>
/// <param name="pageSize">How many workflows one read takes.</param>
public sealed class EfCurrentWorkflowVersions(IServiceScopeFactory scopeFactory, int pageSize = 500)
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly int pageSize = pageSize > 0
        ? pageSize
        : throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "A page takes at least one workflow.");

    /// <summary>Each workflow's current version, once per workflow, in workflow id order as the database orders it.
    /// A version two workflows name is listed for each.</summary>
    public async IAsyncEnumerable<CurrentWorkflowVersion> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guid? after = null;
        while (true)
        {
            var page = await ReadPageAsync(after, cancellationToken).ConfigureAwait(false);
            foreach (var (workflowId, version) in page)
                yield return new CurrentWorkflowVersion(workflowId, version);

            if (page.Count < pageSize)
                yield break;

            after = page[^1].WorkflowId;
        }
    }

    private async Task<List<(Guid WorkflowId, WorkflowVersion Version)>> ReadPageAsync(Guid? after, CancellationToken cancellationToken)
    {
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        await using var scope = scopeFactory.CreateAsyncScope();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>().Workflows
            .AsNoTracking()
            .Where(w => w.CurrentVersionId != null);
        if (after is { } last)
            workflows = workflows.Where(w => w.Id.CompareTo(last) > 0);

        var rows = await workflows
            .OrderBy(w => w.Id)
            .Take(pageSize)
            .Select(w => new { w.Id, Version = w.CurrentVersion! })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(r => (r.Id, r.Version))];
    }
}
