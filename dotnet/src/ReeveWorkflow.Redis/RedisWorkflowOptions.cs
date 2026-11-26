namespace ReeveWorkflow.Redis;

/// <summary>What this package writes into Redis, and for how long. Both are cross-replica contracts rather than tuning: every replica of one deployment must agree on them, and a host adopting this package in place of its own gate must keep whatever it already uses or the running replicas stop sharing a budget.</summary>
public sealed class RedisWorkflowOptions
{
    /// <summary>Prefix of the per-workflow slot ZSET; the workflow id follows it in "N" format.</summary>
    public string RunSlotKeyPrefix { get; set; } = "wfslots:";

    /// <summary>How long a held slot survives without being released. A slot older than this is treated as a crashed owner and pruned, so the budget recovers; it must stay far longer than any healthy block run, or a live slot is pruned mid-execution and the budget over-admits.</summary>
    public TimeSpan RunSlotTtl { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>What the fire-de-duplication claim writes into Redis. Its own class, and not a third property on the one above: three seams configured by three near-identical calls would otherwise let a prefix be set on the wrong one, compile, and be discarded — which is the silent split-brain this package exists to prevent.</summary>
public sealed class RedisFireDedupOptions
{
    /// <summary>Prefix of the claim keys; the caller's fire key follows it. The default names this library, not whoever adopted it first: a host that already holds keys under another prefix states it here, and two replicas that disagree on one both claim and both fire.</summary>
    public string KeyPrefix { get; set; } = "reeveworkflow:fire-dedup:";
}
