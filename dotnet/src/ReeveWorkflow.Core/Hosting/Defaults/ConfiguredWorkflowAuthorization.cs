using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>Answers every principal the same way, from a set the host declares once. The default for a host
/// with no permission model — there is no per-principal answer to give, so the deployment gives one answer.</summary>
/// <remarks>The set is empty unless the host fills it, because a default that granted everything would silently
/// remove a gate the library asked for. An empty set denies, and the first denial of each capability says in the
/// log exactly which name to add, so the deny is discoverable rather than a run that mysteriously refuses.</remarks>
public sealed class ConfiguredWorkflowAuthorization : IWorkflowAuthorization
{
    private readonly HashSet<string> granted;
    private readonly ILogger logger;

    private readonly ConcurrentDictionary<string, byte> reported = new(StringComparer.Ordinal);

    /// <param name="grantedCapabilities">The capability names this deployment grants to everyone. Empty denies
    /// every capability, which is the safe reading of a host that has not been asked the question yet.</param>
    /// <param name="logger">Where a first denial is reported.</param>
    public ConfiguredWorkflowAuthorization(IEnumerable<string> grantedCapabilities, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(grantedCapabilities);
        this.granted = new HashSet<string>(grantedCapabilities, StringComparer.Ordinal);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The capability names this deployment grants. Exposed so a host can assert what it configured
    /// rather than infer it from a run that did or did not refuse.</summary>
    public IReadOnlyCollection<string> GrantedCapabilities => granted;

    /// <inheritdoc />
    public Task<bool> HasCapabilityAsync(
        Guid principalId, string capability, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(capability);

        if (granted.Contains(capability)) return Task.FromResult(true);

        if (reported.TryAdd(capability, 0))
        {
            logger.LogWarning(
                "Workflow capability '{Capability}' is denied: this host granted none of it. Add it to the "
                + "granted capabilities, or register an IWorkflowAuthorization that answers per principal.",
                capability);
        }

        return Task.FromResult(false);
    }
}
