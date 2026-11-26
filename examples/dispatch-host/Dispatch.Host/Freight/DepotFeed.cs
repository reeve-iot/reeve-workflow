using Microsoft.AspNetCore.Mvc;
using ReeveWorkflow.Abstractions;

namespace Dispatch.Host.Freight;

public static class DepotFeed
{
    public const string FireKeyPrefix = "depot-event:";

    public static RouteGroupBuilder MapDepotFeed(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = endpoints.MapGroup(string.Empty);
        group.MapPost('/' + prefix.Trim('/'), ObserveAsync)
            .Produces<IReadOnlyDictionary<string, object?>>();
        return group;
    }

    private static async Task<IResult> ObserveAsync(
        DepotEvent depotEvent,
        [FromServices] IWorkflowEngine engine,
        CancellationToken cancellationToken)
    {
        if (depotEvent is null || string.IsNullOrWhiteSpace(depotEvent.EventId))
            return Results.BadRequest();

        var outputs = await engine.ExecuteAsync(
            depotEvent.WorkflowId,
            new WorkflowFire
            {
                FiredAtUtc = depotEvent.ObservedAtUtc,
                DedupKey = FireKeyPrefix + depotEvent.WorkflowId.ToString("N") + ':' + depotEvent.EventId,
                Payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["eventId"] = depotEvent.EventId
                }
            },
            inputParameters: null,
            cancellationToken: cancellationToken);

        return Results.Ok(outputs);
    }
}

public sealed record DepotEvent(Guid WorkflowId, string EventId, DateTimeOffset ObservedAtUtc);
