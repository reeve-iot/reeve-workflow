using Microsoft.EntityFrameworkCore;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.EntityFrameworkCore;

/// <summary>The run's record — the row saying it happened, and the steps and log entries under it — over the
/// workflow context.</summary>
/// <remarks>Contributors are drained into the same save as everything else, and drained ungated: an entry a host
/// buffered for the run is a fact about it, not a debugging convenience that the caller's
/// <c>recordLogEntries</c> flag may switch off.</remarks>
public sealed class EfWorkflowRunJournal(
    WorkflowDbContext db, IEnumerable<IWorkflowRunLogContributor>? contributors = null) : IWorkflowRunJournal
{
    private readonly WorkflowDbContext db = db ?? throw new ArgumentNullException(nameof(db));

    private readonly IWorkflowRunLogContributor[] contributors =
        contributors?.ToArray() ?? [];

    /// <inheritdoc />
    public async Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        db.WorkflowExecutionLogs.Add(log);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken) =>
        db.WorkflowExecutionLogs.FirstOrDefaultAsync(e => e.Id == executionLogId, cancellationToken);

    /// <inheritdoc />
    public Task SaveRunAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    /// <inheritdoc />
    public async Task SaveRunDetailsAsync(
        Guid? executionId,
        bool recordLogEntries,
        IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
        IReadOnlyList<WorkflowExecutionStep> executionSteps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logEntries);
        ArgumentNullException.ThrowIfNull(executionSteps);

        if (executionId is Guid execId)
        {
            foreach (var contributor in contributors)
            {
                var contributed = contributor.DrainEntries(execId);
                if (contributed.Count > 0) db.WorkflowExecutionLogEntries.AddRange(contributed);
            }
        }

        if (recordLogEntries && logEntries.Count > 0)
            db.WorkflowExecutionLogEntries.AddRange(logEntries);

        if (executionSteps.Count > 0)
            db.WorkflowExecutionSteps.AddRange(executionSteps);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
