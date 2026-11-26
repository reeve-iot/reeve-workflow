using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>A host-side buffer of run-log entries the engine did not produce, drained into the run's record
/// when the journal saves it. Optional, and a host may register several.</summary>
/// <remarks>This exists so a host can add its own entries to a run without owning the whole journal. A host that
/// records, say, every outbound HTTP call a workflow made has a fact about the run that belongs under the run's
/// own id, but it has nothing else to say about how the journal writes — before this seam, wanting the one meant
/// reimplementing the other.</remarks>
public interface IWorkflowRunLogContributor
{
    /// <summary>Takes and clears whatever this contributor has buffered for the run. Called once per save, so an
    /// entry returned twice would be written twice — a contributor that does not clear is a bug in the host.</summary>
    IReadOnlyList<WorkflowExecutionLogEntry> DrainEntries(Guid executionId);
}
