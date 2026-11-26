using System.Collections.ObjectModel;

namespace ReeveWorkflow.Abstractions;

/// <summary>One fire: the single occurrence that made a workflow run, as the engine receives it. The engine carries a fire, it does not interpret one — what decided the moment was worth a run stays the host's concern.</summary>
/// <remarks><c>TriggerId</c>, <c>FiredAtUtc</c> and <c>Payload</c> are already serialised under exactly those names into the <c>trigger_envelope_json</c> of every suspension row in every installation's database, so adding a field here is safe while renaming or removing one reads green in every test and makes existing rows unreadable on resume.</remarks>
public sealed record WorkflowFire
{
    /// <summary>A fire that said nothing — a cron or sun trigger, or a plugin that opted out. Shared
    /// process-wide, so it is wrapped: a cast-and-Add through the interface would poison every such fire.</summary>
    public static readonly IReadOnlyDictionary<string, object?> EmptyPayload =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>The automation that bound a trigger to this workflow, when one did. Opaque to the engine:
    /// it is recorded on the execution log and never interpreted.</summary>
    public Guid? AutomationId { get; init; }

    /// <summary>The trigger row that fired, when the fire came from one. Persisted in suspension rows as
    /// <c>triggerId</c>.</summary>
    public Guid? TriggerId { get; init; }

    /// <summary>When the host observed the fire. Null means "the engine decides", which it does once for
    /// the whole run so <c>${trigger.firedAt}</c> cannot drift between nodes.</summary>
    public DateTimeOffset? FiredAtUtc { get; init; }

    /// <summary>What the trigger said about the fire — the values exposed as <c>${trigger.*}</c>. Scalars, and
    /// objects or arrays of them; the bound is enforced where the payload is admitted, not here.</summary>
    public IReadOnlyDictionary<string, object?> Payload { get; init; } = EmptyPayload;

    /// <summary>Collapses one logical fire that reached several replicas into a single run. Null disables
    /// deduplication for this fire, which is correct for a fire only one replica can observe.</summary>
    /// <remarks>Claimed as given, so name one fire of one trigger or workflow, not only the event — two workflows
    /// one event fires would share a claim and the second is dropped. Every replica must derive the same key.</remarks>
    public string? DedupKey { get; init; }
}
