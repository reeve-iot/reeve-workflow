using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace Dispatch.Host.Freight;

public sealed class HoldUntilBlockFactory : IWorkflowBlockFactory
{
    public const string TypeId = "dispatch.hold-until";

    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    public string BlockTypeId => TypeId;

    public string DisplayName => "Hold until";

    public string Description => "Holds the shipment at the depot until a stated moment, across restarts.";

    public string Category => "Freight";

    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
    [
        new("shipment", "Shipment", FreightPorts.Shipment, IsRequired: true),
        new("untilUtc", "Release at (UTC, ISO-8601)", PortValueType.DateTime, IsRequired: true),
        new(ReleasedInput, "Released when", PortValueType.OfDeclaration("Expression<Boolean>"), IsRequired: true)
    ];

    public const string ReleasedInput = "released";

    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
    [
        new("outcome", "Outcome (satisfied | timed_out)", PortValueType.String, IsRequired: true),
        new("triggerData", "Wake payload (when a wake released it)", PortValueType.OfDeclaration("Json?"), IsRequired: false)
    ];

    public bool IsIdempotent => true;

    public IWorkflowBlockPlugin Create() => new HoldUntilBlock();
}

public sealed class HoldUntilBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var shipment = LoadingBay.Required(inputs, "shipment");
        var until = ReleaseAt(inputs);

        context.Logger.LogInformation(
            "Holding shipment {Shipment} until {ReleaseAt:O}; the run parks and the bay is free.",
            shipment, until);

        throw new WorkflowSuspendException(new SuspendResult(
            WorkflowSuspensionKind.WaitTemplate,
            JsonSerializer.Serialize(new WorkflowWaitTemplateConfig { Input = HoldUntilBlockFactory.ReleasedInput }),
            until + HoldUntilBlockFactory.Grace));
    }

    private static DateTimeOffset ReleaseAt(IReadOnlyDictionary<string, object?> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (!inputs.TryGetValue("untilUtc", out var raw) || raw is null)
            throw new ArgumentException("The 'untilUtc' input is required and was not supplied.", nameof(inputs));

        return raw switch
        {
            DateTimeOffset value => value,
            _ => throw new ArgumentException(
                $"The 'untilUtc' input must be an instant; got '{raw.GetType().Name}'.", nameof(inputs))
        };
    }
}
