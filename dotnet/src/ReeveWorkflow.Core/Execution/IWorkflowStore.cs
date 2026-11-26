using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>The reads a run needs before it can start. Three, and none of them writes: what to run, which
/// version of it, and which versions its children were on when the root started.</summary>
public interface IWorkflowStore
{
    /// <summary>The workflow and the version it currently publishes, or null when nothing is stored under
    /// that id. A workflow with no published version comes back with a null <see cref="Workflow.CurrentVersion"/>.</summary>
    Task<Workflow?> LoadAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>One specific version, for a run that was pinned to it. Null means the pin cannot be
    /// honoured, which fails the run rather than falling back to current.</summary>
    Task<WorkflowVersion?> LoadVersionAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>A child workflow's current version id and definition, for the pin manifest. Null when the
    /// child has no published version — the manifest then simply does not pin it.</summary>
    Task<(Guid VersionId, string DefinitionJson)?> LoadCurrentVersionOfAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>The version this workflow published under that number, for a run whose row records the number
    /// it started on and no manifest. Null when nothing answers to it.</summary>
    /// <remarks>Default body answers null, so a store that does not override it answers null: such a
    /// run then rebuilds from whatever is current now, which is a different workflow from the one lost.</remarks>
    Task<WorkflowVersion?> LoadVersionNumberedAsync(
        Guid workflowId, int versionNumber, CancellationToken cancellationToken) =>
        Task.FromResult<WorkflowVersion?>(null);
}

/// <summary>Collapses the N-replica fan-out of one physical trigger event. Optional: null admits every fire,
/// which is the right answer on one replica and an over-fire risk on several.</summary>
/// <remarks>The engine claims the key unscoped: it must name one fire of one trigger or workflow, the same on every
/// replica. Naming only the event admits one workflow it fires; the rest, bar Single, drop as <c>fire_dedup</c>. A Single fire's refusal claims <c>reeveworkflow:refused-row:</c>, its workflow id (<c>N</c> format), <c>:</c>
/// and its key, and a host key that starts with <c>reeveworkflow:</c> is claimed as <c>reeveworkflow:fire:</c> plus that
/// whole key; any other key is claimed as given.</remarks>
public interface IWorkflowFireDedup
{
    /// <summary>Claims this fire key for <paramref name="window"/>. False means a sibling replica already
    /// has it and this run should not start.</summary>
    Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>A one-way fingerprint of a fire key, for logs. The raw key can carry a user id and an exact
    /// position, so it must never be logged.</summary>
    string Fingerprint(string fireKey);
}

/// <summary>The de-duplication a host installs nothing for: every fire is claimed, because one physical
/// event reaches one process exactly once when there is one process.</summary>
/// <remarks>A host that has declared peers and left
/// this one here does not start — see <c>Seams.FireDedup</c>.</remarks>
public sealed class SingleReplicaFireDedup : IWorkflowFireDedup
{
    /// <inheritdoc />
    public Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <inheritdoc />
    public string Fingerprint(string fireKey)
    {
        ArgumentNullException.ThrowIfNull(fireKey);
        return WorkflowFireKey.Fingerprint(fireKey);
    }
}
