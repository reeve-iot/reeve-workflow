namespace ReeveWorkflow.Abstractions;

/// <summary>Carries the per-trigger fire callback: a CLUSTER-STABLE dedup key collapsing the N-replica fan-out
/// of one event (null degrades open), plus the payload the host spreads over <c>${trigger.*}</c>.</summary>
public sealed class TriggerContext
{
    private readonly Func<string?, IReadOnlyDictionary<string, object?>?, CancellationToken, Task> _fireCallback;

    /// <summary>Creates the context a trigger fires through.</summary>
    /// <param name="ownerId">The owner the trigger belongs to.</param>
    /// <param name="fireCallback">Called by <see cref="FireAsync"/> with the dedup key, the payload and the token.</param>
    public TriggerContext(
        Guid ownerId,
        Func<string?, IReadOnlyDictionary<string, object?>?, CancellationToken, Task> fireCallback)
    {
        OwnerId = ownerId;
        _fireCallback = fireCallback;
    }

    public Guid OwnerId { get; }

    /// <summary>Fires carrying a cluster-stable dedup key and the payload the host spreads over <c>${trigger.*}</c>; both may be <see langword="null"/>. A value must be a scalar, an object or array of such values, or <see langword="null"/> — a null keeps its key and reads as "not reported" — while a date is dropped, because it returns from a suspension as a string (inside an object too); send an ISO string or a number instead.
    /// At most 32 keys per fire, and at most 1024 bytes for one key plus its serialised value; a key breaking any of these is dropped and logged, never the payload and never the fire.</summary>
    public Task FireAsync(
        string? fireKey,
        IReadOnlyDictionary<string, object?>? payload,
        CancellationToken cancellationToken)
        => _fireCallback(fireKey, payload, cancellationToken);
}
