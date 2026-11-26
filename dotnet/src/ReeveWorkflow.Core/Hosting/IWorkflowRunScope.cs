using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>Opens the per-run world the engine works inside. One run, one scope: the reads, the writes and
/// the block container all come from it, and disposing it ends the run's hold on the host.</summary>
public interface IWorkflowRunScopeFactory
{
    /// <summary>A fresh scope. The caller disposes it; nothing inside it outlives that.</summary>
    IWorkflowRunScope CreateScope();
}

/// <summary>Everything a run needs for as long as the run lasts. Grouped rather than resolved one at a time,
/// so the library never has to know what a service container is.</summary>
public interface IWorkflowRunScope : IDisposable
{
    /// <summary>The container blocks are built from. Opaque to the engine, which only passes it along.</summary>
    IServiceProvider Services { get; }

    /// <summary>What to run, and which version of it.</summary>
    IWorkflowStore Store { get; }

    /// <summary>Where the run's own record goes.</summary>
    IWorkflowRunJournal Journal { get; }

    /// <summary>Where the run says what it did, for someone who was not watching.</summary>
    IWorkflowAuditSink Audit { get; }

    /// <summary>Whether the workflow's owner may run what it contains. Scoped because the answer is a query,
    /// and a run that lasts an hour must not hold the connection that answered it.</summary>
    IWorkflowAuthorization Authorization { get; }
}
