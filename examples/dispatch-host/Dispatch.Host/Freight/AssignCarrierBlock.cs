using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;

namespace Dispatch.Host.Freight;

public sealed class AssignCarrierBlockFactory : IWorkflowBlockFactory
{
    public const string TypeId = "dispatch.assign-carrier";

    public string BlockTypeId => TypeId;

    public string DisplayName => "Assign carrier";

    public string Description => "Holds a loading bay for the carrier's dwell and issues the shipment's waybill.";

    public string Category => "Freight";

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new("shipment", "Shipment", FreightPorts.Shipment, IsRequired: true),
        new("carrier", "Carrier", PortValueType.String, IsRequired: true),
        new("dwellSeconds", "Loading dwell (s)", PortValueType.Number, IsRequired: false)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new("waybill", "Waybill", PortValueType.String, IsRequired: true)
    ];

    public bool IsIdempotent => true;

    public IWorkflowBlockPlugin Create() => new AssignCarrierBlock();
}

public sealed class AssignCarrierBlock : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var shipment = LoadingBay.Required(inputs, "shipment");
        var carrier = LoadingBay.Required(inputs, "carrier");
        var dwell = LoadingBay.DwellOf(inputs);

        if (dwell > TimeSpan.Zero)
        {
            context.Logger.LogInformation(
                "Holding a bay for shipment {Shipment} on carrier {Carrier} for {DwellSeconds}s.",
                shipment, carrier, dwell.TotalSeconds);
            await Task.Delay(dwell, cancellationToken).ConfigureAwait(false);
        }

        return new Dictionary<string, object?> { ["waybill"] = Waybill(shipment, carrier) };
    }

    public static string Waybill(string shipment, string carrier) =>
        string.Concat("WB-", LoadingBay.Slug(carrier), "-", LoadingBay.Slug(shipment));
}
