using ReeveWorkflow.Abstractions;

namespace Dispatch.Host.TestHooks;

public sealed class EchoJsonBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "dispatch.echo-json";
    public string DisplayName => "Echo JSON";
    public string Description => "Returns its input unchanged.";
    public string Category => "Freight";
    public bool IsIdempotent => true;
    public bool HasDynamicPorts => true;
    public IReadOnlyCollection<BlockPortDefinition> Inputs =>
        [new("Value", "Value", PortValueType.String, false)];
    public IReadOnlyCollection<BlockPortDefinition> Outputs =>
        [new("Value", "Value", PortValueType.String, false)];
    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new("Value", "Value", ConfigFieldType.String, Required: false),
        new("InputPorts", "Input Ports", ConfigFieldType.String, Required: false),
        new("OutputPorts", "Output Ports", ConfigFieldType.String, Required: false),
    ];
    public IWorkflowBlockPlugin Create() => new EchoJsonBlock();
}

public sealed class EchoJsonBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var value = inputs.TryGetValue("Value", out var input) ? input : null;
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?> { ["Value"] = value });
    }
}
