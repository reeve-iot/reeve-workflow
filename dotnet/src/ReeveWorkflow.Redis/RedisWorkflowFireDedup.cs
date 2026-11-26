using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Execution;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis;

/// <summary>Collapses one physical trigger event that reached every replica into a single run, by letting
/// exactly one of them claim its key.</summary>
/// <remarks>The claim and its expiry are one <c>SET</c> with <c>NX</c>, so no key can be claimed and never expire.
/// Every failure admits the fire: over-firing is recoverable and losing a trigger is not, and the same choice
/// is why deriving a key that cannot be deceived belongs to the caller.
///
/// A claim is not released when the winner then declines the run — the budget was full, the process died —
/// because there is nothing on the seam to release it with. The event is lost for the rest of the window, and
/// so is a retry of it. No such case has been observed; the seam has no release by decision.</remarks>
public sealed class RedisWorkflowFireDedup : IWorkflowFireDedup
{
    private readonly IConnectionMultiplexer redis;
    private readonly ILogger logger;
    private readonly string keyPrefix;

    /// <summary>Takes the connection this deployment already has, and the key prefix its replicas agree on.</summary>
    /// <param name="redis">Required: a host that called <c>UseRedis</c> without one has said two things.</param>
    /// <param name="logger">Where a degraded claim is reported, as a fingerprint and a fault type. Neither the
    /// raw key nor the exception carrying it is written: the client puts the failed command, prefix and key
    /// included, into both the message and <c>Data</c> of what it throws.</param>
    /// <param name="options">The key prefix — a cross-replica contract, see <see cref="RedisFireDedupOptions"/>.</param>
    public RedisWorkflowFireDedup(
        IConnectionMultiplexer redis,
        ILogger<RedisWorkflowFireDedup> logger,
        RedisFireDedupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(logger);
        this.redis = redis;
        this.logger = logger;
        keyPrefix = (options ?? new RedisFireDedupOptions()).KeyPrefix;
    }

    /// <summary>Claims the key for the window, or reports that a sibling replica already has it.</summary>
    public async Task<bool> TryClaimFireAsync(string fireKey, TimeSpan window, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(fireKey))
            return true;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await redis.GetDatabase()
                .StringSetAsync(keyPrefix + fireKey, "1", window, false, When.NotExists, CommandFlags.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Fire de-duplication failed for key {FireKeyFingerprint} ({FaultType}); admitting the fire (degrade-open), which may run it once per replica.",
                Fingerprint(fireKey), ex.GetType().Name);
            return true;
        }
    }

    /// <inheritdoc />
    public string Fingerprint(string fireKey) => WorkflowFireKey.Fingerprint(fireKey);
}
