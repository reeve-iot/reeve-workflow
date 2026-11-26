using System.Collections.ObjectModel;

namespace ReeveWorkflow.Core.Execution;

/// <summary>One fire, as the run sees it: what the trigger plugin sent, and when the host observed it.
/// This is the payload half of <c>${trigger.*}</c> — the trigger's own id is passed alongside it.</summary>
public sealed record TriggerEnvelope(
    DateTimeOffset FiredAtUtc,
    IReadOnlyDictionary<string, object?> Payload)
{
    internal IReadOnlyDictionary<string, Abstractions.ClosedPortType>? Terms { get; init; }

    /// <summary>Reserved: the automation trigger's row id. Host-set, written last, never a plugin's.</summary>
    public const string IdKey = "id";

    /// <summary>Reserved: when the host observed the fire. Host-set, written last.</summary>
    public const string FiredAtKey = "firedAt";

    /// <summary>Both reserved names together, consumed only by the schema test that pins them against the
    /// plugin-side list. Adding a name here does not register an expression namespace.</summary>
    public static readonly IReadOnlyCollection<string> ReservedKeys = [IdKey, FiredAtKey];

    /// <summary>Field name of the trigger id in a persisted suspension row. Not a reserved namespace key: it
    /// is the wire shape of rows already in every installation's database, so renaming it strands them.</summary>
    public const string TriggerIdKey = "triggerId";

    /// <summary>Field name of the fire payload in a persisted suspension row. Same constraint as
    /// <see cref="TriggerIdKey"/>: a consistent rename reads green and makes existing rows unreadable.</summary>
    public const string PayloadKey = "payload";

    /// <summary>A fire that said nothing — a Cron or Sun trigger, or a plugin that opted out. Shared
    /// process-wide, so it is wrapped: a cast-and-Add through the interface would poison every such run.</summary>
    public static readonly IReadOnlyDictionary<string, object?> EmptyPayload =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));
}
