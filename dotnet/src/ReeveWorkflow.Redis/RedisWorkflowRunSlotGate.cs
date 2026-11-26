using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis;

/// <summary>Redis-backed held-slot budget: a per-workflow ZSET scored by Redis server time, admitted by one Lua script so two racing replicas can never both win. A null multiplexer or any Redis error degrades to in-process gating.</summary>
public sealed class RedisWorkflowRunSlotGate : IWorkflowRunSlotGate
{
    private const string AcquireScript = """
        local t = redis.call('TIME')
        local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
        local ttl = tonumber(ARGV[2])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - ttl)
        local n = redis.call('ZCARD', KEYS[1])
        if n < tonumber(ARGV[1]) then
            redis.call('ZADD', KEYS[1], now, ARGV[3])
            redis.call('PEXPIRE', KEYS[1], ttl * 2)
            return 1
        end
        return 0
        """;

    private const string RenewScript = """
        local t = redis.call('TIME')
        local now = (tonumber(t[1]) * 1000) + math.floor(tonumber(t[2]) / 1000)
        if redis.call('ZSCORE', KEYS[1], ARGV[2]) then
            redis.call('ZADD', KEYS[1], 'XX', now, ARGV[2])
            redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[1]) * 2)
            return 1
        end
        return 0
        """;

    private readonly IConnectionMultiplexer? redis;
    private readonly ILogger logger;
    private readonly string keyPrefix;
    private readonly long slotTtlMs;

    /// <summary>The multiplexer may be null: a host that has not configured Redis is a single-replica host, and this gate is only consulted on multi-replica ones.</summary>
    public RedisWorkflowRunSlotGate(
        IConnectionMultiplexer? redis,
        ILogger<RedisWorkflowRunSlotGate> logger,
        RedisWorkflowOptions? options = null)
    {
        this.redis = redis;
        this.logger = logger;
        var settings = options ?? new RedisWorkflowOptions();
        keyPrefix = settings.RunSlotKeyPrefix;
        slotTtlMs = (long)settings.RunSlotTtl.TotalMilliseconds;
    }

    private string SlotKey(Guid workflowId) => $"{keyPrefix}{workflowId:N}";

    /// <summary>Claims one slot of the workflow's cluster-wide budget, or reports that the budget is full.</summary>
    public async Task<bool> TryAcquireAsync(Guid workflowId, string slotToken, int maxConcurrent, CancellationToken ct)
    {
        if (redis == null)
            return true;

        ct.ThrowIfCancellationRequested();

        try
        {
            var db = redis.GetDatabase();
            var raw = await db.ScriptEvaluateAsync(
                AcquireScript,
                keys: [(RedisKey)SlotKey(workflowId)],
                values: [(RedisValue)maxConcurrent, (RedisValue)slotTtlMs, (RedisValue)slotToken])
                .ConfigureAwait(false);

            return (long)raw == 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workflow {WorkflowId}: cluster run-slot acquire failed; admitting on the in-process permit only (degrade-open).",
                workflowId);
            return true;
        }
    }

    /// <summary>Keeps a held slot from expiring under a live run. False when the slot has left the set — an acquire
    /// pruned it after its TTL — and the caller asks for a place again.</summary>
    public async Task<bool> RenewAsync(Guid workflowId, string slotToken, CancellationToken ct)
    {
        if (redis == null)
            return true;

        ct.ThrowIfCancellationRequested();

        try
        {
            var db = redis.GetDatabase();
            var raw = await db.ScriptEvaluateAsync(
                RenewScript,
                keys: [(RedisKey)SlotKey(workflowId)],
                values: [(RedisValue)slotTtlMs, (RedisValue)slotToken])
                .ConfigureAwait(false);

            return (long)raw == 1;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workflow {WorkflowId}: cluster run-slot renewal failed; the run keeps its place until the next beat.",
                workflowId);
            return true;
        }
    }

    /// <summary>Returns a held slot to the budget. Best-effort: a failure leaves it to the TTL prune.</summary>
    public async Task ReleaseAsync(Guid workflowId, string slotToken)
    {
        if (redis == null)
            return;

        try
        {
            var db = redis.GetDatabase();
            await db.SortedSetRemoveAsync((RedisKey)SlotKey(workflowId), (RedisValue)slotToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workflow {WorkflowId}: cluster run-slot release failed for token {SlotToken}; it will be reclaimed by the slot TTL.",
                workflowId, slotToken);
        }
    }
}
