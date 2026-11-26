namespace ReeveWorkflow.Abstractions;

public interface IWorkflowEngine
{
    /// <summary>Executes a workflow. Which version it runs and what it records about itself are not arguments — see <see cref="WorkflowRunPinScope"/> and <see cref="WorkflowRunObservationScope"/>, each consumed once at the start of the run.</summary>
    /// <param name="fire">The occurrence that made this run happen — which automation and trigger, when, what the trigger said, and how to collapse the same fire seen by several replicas; null for a run nothing fired, such as a manual invocation, a sub-workflow or a debug session.</param>
    Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(Guid workflowId,
        WorkflowFire? fire,
        IReadOnlyDictionary<string, object?>? inputParameters,
        Guid? parentExecutionId = null,
        int? iterationIndex = null,
        string? callStackJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a previously persisted execution from the state store. Called by
    /// <c>WorkflowExecutionResumeRunner</c> after claiming an execution row.</summary>
    Task ResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default);

    /// <summary>Whether the block at <paramref name="blockPath"/> in <paramref name="workflowId"/> is declared
    /// idempotent — used during recovery to decide retry-or-fail for orphaned Running rows. Default (unknown block): false.</summary>
    Task<bool> IsBlockIdempotentAsync(Guid workflowId, string blockPath, Guid? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>Resumes an abandoned run and says whether it actually started.</summary>
    /// <remarks>Admission refuses without failing — safe mode, a peer holding the workflow's one permit, a
    /// fire already claimed — and a caller that cannot tell a refusal from a finished run records a run that
    /// never ran as done, losing it. The default body answers true, which is all an engine that predates this method can say about its own <see cref="ResumeAsync"/>.</remarks>
    async Task<bool> TryResumeAsync(Guid executionId, Guid workflowId, CancellationToken cancellationToken = default)
    {
        await ResumeAsync(executionId, workflowId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Replays an abandoned run and says how the replay ended, which is what decides what becomes of the
    /// row it was replayed from.</summary>
    /// <remarks><see cref="TryResumeAsync"/> can only say whether the replay started, and throws for
    /// every other ending, so a caller reading an exception during its own stop could not tell a stopped replay from a
    /// finished one whose journal could not be written, and replayed the finished run again. The default body answers
    /// what <see cref="TryResumeAsync"/> says and lets what it throws through.</remarks>
    async Task<WorkflowReplayOutcome> ReplayAbandonedRunAsync(
        Guid executionId, Guid workflowId, CancellationToken cancellationToken = default) =>
        await TryResumeAsync(executionId, workflowId, cancellationToken).ConfigureAwait(false)
            ? WorkflowReplayOutcome.Finished
            : WorkflowReplayOutcome.NotAdmitted;

    /// <summary>Whether a run of this workflow may be recovered by running it again from the start, which is
    /// what recovery does. Every block it contains is re-executed, not only the one it died inside.</summary>
    /// <remarks>The default body answers the older, narrower question — is the block it died inside idempotent
    /// — so an engine that predates this method keeps its own answer. It is the weaker one: a run that died in an
    /// idempotent block is still replayed through every block before it.</remarks>
    Task<bool> IsRunReplayableAsync(
        Guid workflowId, string currentBlockPath, Guid? versionId = null,
        CancellationToken cancellationToken = default) =>
        IsBlockIdempotentAsync(workflowId, currentBlockPath, versionId, cancellationToken);
}

/// <summary>How a replay of an abandoned run ended, as far as the row it was replayed from is concerned.</summary>
public enum WorkflowReplayOutcome
{
    /// <summary>Admission refused it: nothing ran, and the run is still owed.</summary>
    NotAdmitted,

    /// <summary>The replay walked to its end, parked on a wait, or was stopped with its own row left running. The replay's
    /// own row carries the run from here.</summary>
    Finished,

    /// <summary>The replay started and ended some other way: failed, timed out, replaced by a newer trigger, or taken
    /// by a peer. The replay's own row says which.</summary>
    Failed,

    /// <summary>The replay was stopped before its end — its host stopping, or its workflow's budget held elsewhere —
    /// and the run is still owed.</summary>
    Stopped
}
