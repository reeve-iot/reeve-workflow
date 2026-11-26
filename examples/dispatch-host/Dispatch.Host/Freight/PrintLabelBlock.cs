using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;

namespace Dispatch.Host.Freight;

public sealed class PrintLabelBlockFactory : IWorkflowBlockFactory
{
    public const string TypeId = "dispatch.print-label";

    public string BlockTypeId => TypeId;

    public string DisplayName => "Print label";

    public string Description => "Prints the shipment's label at the bay. Every run prints another one.";

    public string Category => "Freight";

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new("shipment", "Shipment", FreightPorts.Shipment, IsRequired: true),
        new("dwellSeconds", "Printing dwell (s)", PortValueType.Number, IsRequired: false)
    ];

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new("labelId", "Label", PortValueType.String, IsRequired: true)
    ];

    public bool IsIdempotent => false;

    public IWorkflowBlockPlugin Create() => new PrintLabelBlock();
}

public sealed class PrintLabelBlock : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var shipment = LoadingBay.Required(inputs, "shipment");
        var dwell = LoadingBay.DwellOf(inputs);

        if (dwell > TimeSpan.Zero)
        {
            context.Logger.LogInformation(
                "Printing the label for shipment {Shipment}; the bay is held for {DwellSeconds}s.",
                shipment, dwell.TotalSeconds);
            await Task.Delay(dwell, cancellationToken).ConfigureAwait(false);
        }

        return new Dictionary<string, object?>
        {
            ["labelId"] = string.Concat("LBL-", LoadingBay.Slug(shipment), "-", Guid.NewGuid().ToString("N")[..8])
        };
    }
}
