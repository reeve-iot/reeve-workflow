using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Core.Hosting;
using StackExchange.Redis;

namespace ReeveWorkflow.Redis;

/// <summary>Every cross-replica signal the library sends, over Redis pub/sub: one channel per kind, one message
/// per event, and the publishing replica's id in every message so nobody acts on an echo of itself.</summary>
/// <remarks>The message body is a JSON object with <c>Origin</c>, <c>Id</c> and <c>Payload</c>. That is a wire
/// contract: during a rollout one version writes it and another parses it, so a field may be added but never
/// renamed or removed.
///
/// No transport fault reaches a caller, and nothing here waits on the broker — including the subscribe, which
/// is why it is the asynchronous overload. Failing the caller would cost the run, and the run is the thing a
/// signal is only a hint about. A blank channel and an already-cancelled token still throw: those are the
/// caller's mistakes, not the broker's.
///
/// What a dropped message costs is worth stating exactly, because "hint" is doing less work than it looks.
/// Redis pub/sub is at-most-once with no replay. A restart cancel lost while a peer is reconnecting is not
/// retried, and the superseding trigger is then Skipped rather than taking over — the newest fire is dropped
/// and the older run continues. A debugger command lost the same way is worse: the paused session waits out
/// its orphan timeout, and that timeout does not merely expire — it kills the run it was holding.</remarks>
public sealed class RedisWorkflowClusterSignal : IWorkflowClusterSignal
{
    private readonly IConnectionMultiplexer redis;
    private readonly ILogger logger;
    private readonly string origin;

    /// <summary>Takes the connection this deployment already has and the id that tells its replicas apart.</summary>
    /// <param name="redis">Required: a host that called <c>UseRedis</c> without one has said two things.</param>
    /// <param name="identity">Who this process is on the wire — see <see cref="WorkflowClusterIdentity"/>.</param>
    /// <param name="logger">Where a dropped publish or a handler that threw is reported.</param>
    public RedisWorkflowClusterSignal(
        IConnectionMultiplexer redis,
        WorkflowClusterIdentity identity,
        ILogger<RedisWorkflowClusterSignal> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);
        this.redis = redis;
        this.logger = logger;
        origin = identity.ReplicaId;
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        string channel, Guid id, string? payload = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var body = JsonSerializer.Serialize(new Envelope(origin, id.ToString("N"), payload));
            await redis.GetSubscriber()
                .PublishAsync(RedisChannel.Literal(channel), body)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cluster signal on {Channel} for {Id} was not published.", channel, id);
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string channel, Func<Guid, string?, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);

        var redisChannel = RedisChannel.Literal(channel);
        var subscriber = redis.GetSubscriber();

        void OnMessage(RedisChannel source, RedisValue message)
        {
            if (!TryRead(message, out var messageOrigin, out var id, out var payload))
            {
                logger.LogWarning("A message on {Channel} was not a cluster signal and was ignored.", channel);
                return;
            }

            if (string.Equals(messageOrigin, origin, StringComparison.Ordinal))
                return;

            _ = HandleAsync(handler, channel, id, payload);
        }

        _ = SubscribeAsync(subscriber, redisChannel, channel, OnMessage);
        return new Unsubscribe(() => subscriber.UnsubscribeAsync(redisChannel, OnMessage));
    }

    private async Task SubscribeAsync(
        ISubscriber subscriber, RedisChannel redisChannel, string channel, Action<RedisChannel, RedisValue> onMessage)
    {
        try
        {
            await subscriber.SubscribeAsync(redisChannel, onMessage).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Subscribing to cluster signals on {Channel} did not complete.", channel);
        }
    }

    private async Task HandleAsync(
        Func<Guid, string?, CancellationToken, Task> handler, string channel, Guid id, string? payload)
    {
        try
        {
            await handler(id, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A peer's cluster signal on {Channel} for {Id} was not applied.", channel, id);
        }
    }

    private static bool TryRead(RedisValue message, out string origin, out Guid id, out string? payload)
    {
        origin = string.Empty;
        id = Guid.Empty;
        payload = null;

        var text = (string?)message;
        if (string.IsNullOrEmpty(text))
            return false;

        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(text);
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.Origin) || !Guid.TryParseExact(envelope.Id, "N", out id))
            return false;

        origin = envelope.Origin;
        payload = envelope.Payload;
        return true;
    }

    private sealed record Envelope(string Origin, string Id, string? Payload);

    private sealed class Unsubscribe(Func<Task> unsubscribe) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                _ = unsubscribe();
        }
    }
}
