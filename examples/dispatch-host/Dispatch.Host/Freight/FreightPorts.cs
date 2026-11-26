using System.Text.Json;
using ReeveWorkflow.Abstractions;

namespace Dispatch.Host.Freight;

public static class FreightPorts
{
    public static readonly PortValueType Shipment = PortValueType.Of("Shipment");

    public static readonly WorkflowTypeRecord ShipmentRecord = new("Shipment", typeof(string))
    {
        Literal = new WorkflowLiteralForm(WorkflowLiteralKind.String),
        JsonTypeInfo = JsonSerializerOptions.Default.GetTypeInfo(typeof(string)),
        Claims = new HashSet<WorkflowCapability> { WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable },
    };

    public static readonly PortValueType Shipments = PortValueType.CollectionOf(Shipment);
}
