using ReeveWorkflow.Abstractions;

namespace Dispatch.Host.TestHooks;

public sealed class EnumFieldBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "dispatch.enum-field";
    public string DisplayName => "Sets Mode";
    public string Description => "Test block with one Enum configuration field.";
    public string Category => "Freight";
    public bool IsIdempotent => true;
    public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new("Kind", "Kind", ConfigFieldType.Enum, Required: false) { AllowedValues = ["Toggle", "Dim", "Scene"] },
    ];
    public IWorkflowBlockPlugin Create() => new EnumFieldBlock();
}

public sealed class EnumFieldBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
}
