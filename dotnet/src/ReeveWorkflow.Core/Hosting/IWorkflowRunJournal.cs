using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>The record a run leaves behind: one row saying it happened and how it ended, and the log entries
/// and steps under it. The engine builds the rows; where they are written and how is the host's business.</summary>
public interface IWorkflowRunJournal
{
    /// <summary>Records that a run has started. The row must be durable before this returns — a debug session
    /// binds to it by foreign key, and the state row is opened against an execution that already exists.</summary>
    /// <remarks>It is also handed a row already ended, for a start admission refused, with
    /// <see cref="WorkflowExecutionLog.FailureReason"/> set; nothing is saved to that row afterwards but its log entries.</remarks>
    Task OpenRunAsync(WorkflowExecutionLog log, CancellationToken cancellationToken);

    /// <summary>The row for a run already under way, or null when there is none. Used by a resume, which
    /// finds its run still Running from when it suspended.</summary>
    Task<WorkflowExecutionLog?> LoadRunAsync(Guid executionLogId, CancellationToken cancellationToken);

    /// <summary>Persists whatever the engine has just written onto the row it was handed. The engine calls
    /// <see cref="WorkflowExecutionLog.Complete"/> itself; this is only the write.</summary>
    Task SaveRunAsync(CancellationToken cancellationToken);

    /// <summary>The detail under a run: the steps it took, and — only when the caller asked to record them —
    /// its log entries. Entries a host records for its own reasons are added here regardless.</summary>
    Task SaveRunDetailsAsync(
        Guid? executionId,
        bool recordLogEntries,
        IReadOnlyList<WorkflowExecutionLogEntry> logEntries,
        IReadOnlyList<WorkflowExecutionStep> executionSteps,
        CancellationToken cancellationToken);
}
