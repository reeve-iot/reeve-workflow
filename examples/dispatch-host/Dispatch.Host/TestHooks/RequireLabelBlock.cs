using ReeveWorkflow.Abstractions;

namespace Dispatch.Host.TestHooks;

public sealed class RequireLabelBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "dispatch.require-label";
    public string DisplayName => "Requires Label";
    public string Description => "Test block with one required configuration field.";
    public string Category => "Freight";
    public bool IsIdempotent => true;
    public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new("Label", "Label", ConfigFieldType.String, Required: true),
    ];
    public IWorkflowBlockPlugin Create() => new RequireLabelBlock();
}

public sealed class RequireLabelBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs, WorkflowBlockContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>());
}
