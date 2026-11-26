using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using StackExchange.Redis;

namespace Dispatch.Host.Freight;

public sealed class ClearanceDesk(IConnectionMultiplexer redis)
{
    public const string ClearancesKey = "dispatch:clearances";

    public const string Granted = "granted";

    public const string GrantedChannel = "dispatch:clearance-granted";

    public static string WaitersKey(string shipment) => $"dispatch:clearance-waiters:{Slug(shipment)}";

    public Task NoteWaitingAsync(string shipment, Guid executionId) =>
        redis.GetDatabase().SetAddAsync(WaitersKey(shipment), executionId.ToString());

    public async Task<bool> IsGrantedAsync(string shipment) =>
        await redis.GetDatabase().HashGetAsync(ClearancesKey, Slug(shipment)) == Granted;

    public async Task GrantAsync(string shipment)
    {
        var db = redis.GetDatabase();
        await db.HashSetAsync(ClearancesKey, Slug(shipment), Granted);

        var waiters = await db.SetMembersAsync(WaitersKey(shipment));
        await db.KeyDeleteAsync(WaitersKey(shipment));

        var subscriber = redis.GetSubscriber();
        foreach (var waiter in waiters)
            await subscriber.PublishAsync(RedisChannel.Literal(GrantedChannel), waiter);
    }

    public async Task<IExpressionNamespaceResolver> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await redis.GetDatabase().HashGetAllAsync(ClearancesKey);
        var granted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Value == Granted && entry.Name.ToString() is { Length: > 0 } name)
                granted.Add(name);
        }

        return new ClearanceNamespace(granted);
    }

    public static string Slug(string shipment) => LoadingBay.Slug(shipment);

    private sealed class ClearanceNamespace(IReadOnlySet<string> granted) : IExpressionNamespaceResolver
    {
        public string Namespace => "depot";

        public ResolveResult Resolve(string[] segments) =>
            segments is ["clearance", var shipment] && shipment.Length > 0
                ? ResolveResult.Resolved(granted.Contains(shipment))
                : ResolveResult.Failed();
    }
}

public sealed class ClearanceStaticCheck : IExpressionNamespaceStaticCheck
{
    public string Namespace => "depot";

    public NamespacePathCheckAnswer CheckWithKeyType(IReadOnlyList<string> segments) =>
        segments is ["clearance", _] ? new(NamespacePathCheck.Yes, "Boolean") : NamespacePathCheckAnswer.No;
}

public sealed class ClearanceWakeSource(IConnectionMultiplexer redis) : IWorkflowSuspensionWakeSource
{
    public IDisposable Subscribe(Func<WorkflowSuspensionWake, CancellationToken, Task> onWake)
    {
        ArgumentNullException.ThrowIfNull(onWake);

        var channel = RedisChannel.Literal(ClearanceDesk.GrantedChannel);
        var subscriber = redis.GetSubscriber();
        void Handler(RedisChannel published, RedisValue message)
        {
            if (!Guid.TryParse(message.ToString(), out var executionId))
                return;

            _ = onWake(
                new WorkflowSuspensionWake
                {
                    ExecutionLogId = executionId,
                    Reason = WorkflowSuspensionWakeReasons.Poll
                },
                CancellationToken.None);
        }

        _ = SubscribeAsync(subscriber, channel, Handler);
        return new Unsubscribe(() => subscriber.UnsubscribeAsync(channel, Handler));
    }

    private static async Task SubscribeAsync(
        ISubscriber subscriber, RedisChannel channel, Action<RedisChannel, RedisValue> handler)
    {
        try
        {
            await subscriber.SubscribeAsync(channel, handler).ConfigureAwait(false);
        }
        catch (RedisException)
        {
        }
    }

    private sealed class Unsubscribe(Func<Task> stop) : IDisposable
    {
        private int stopped;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) == 0)
                _ = stop();
        }
    }
}

public static class ClearanceEndpoint
{
    public static RouteGroupBuilder MapClearanceDesk(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = endpoints.MapGroup(string.Empty);
        group.MapPost('/' + prefix.Trim('/'), async (ClearanceGrant grant, HttpContext context) =>
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.Shipment))
                return Results.BadRequest();

            await context.RequestServices.GetRequiredService<ClearanceDesk>().GrantAsync(grant.Shipment);
            return Results.Accepted();
        });
        return group;
    }
}

public sealed record ClearanceGrant(string Shipment);
