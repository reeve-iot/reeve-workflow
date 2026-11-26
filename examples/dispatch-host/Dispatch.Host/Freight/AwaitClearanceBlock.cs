using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Freight;

public sealed class AwaitClearanceBlockFactory(ClearanceDesk desk) : IWorkflowBlockFactory
{
    public const string TypeId = "dispatch.await-clearance";

    public static readonly TimeSpan Patience = TimeSpan.FromHours(12);

    public string BlockTypeId => TypeId;

    public string DisplayName => "Await clearance";

    public string Description => "Parks the run until customs has cleared the shipment.";

    public string Category => "Freight";

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new("shipment", "Shipment", FreightPorts.Shipment, IsRequired: true),
        new(ClearedInput, "Cleared when", PortValueType.OfDeclaration("Expression<Boolean>"), IsRequired: true)
    ];

    public const string ClearedInput = "cleared";

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new("outcome", "Outcome (satisfied | timed_out)", PortValueType.String, IsRequired: true)
    ];

    public bool IsIdempotent => true;

    public IWorkflowBlockPlugin Create() => new AwaitClearanceBlock(desk);
}

public sealed class AwaitClearanceBlock(ClearanceDesk desk) : IWorkflowBlockPlugin
{
    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var shipment = LoadingBay.Required(inputs, "shipment");

        await desk.NoteWaitingAsync(shipment, context.ExecutionId).ConfigureAwait(false);

        if (await desk.IsGrantedAsync(shipment).ConfigureAwait(false))
        {
            context.Logger.LogInformation(
                "Shipment {Shipment} was cleared before it was lodged; the run walks on without parking.",
                shipment);
            return new Dictionary<string, object?> { ["outcome"] = WorkflowWaitOutcomes.Satisfied };
        }

        context.Logger.LogInformation(
            "Shipment {Shipment} waits for customs; the run parks and the desk will say when to look.",
            shipment);

        throw new WorkflowSuspendException(new SuspendResult(
            WorkflowSuspensionKind.WaitTemplate,
            JsonSerializer.Serialize(new WorkflowWaitTemplateConfig { Input = AwaitClearanceBlockFactory.ClearedInput }),
            DateTimeOffset.UtcNow + AwaitClearanceBlockFactory.Patience));
    }
}
