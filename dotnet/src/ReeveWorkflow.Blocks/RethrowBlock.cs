using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Blocks;

public sealed class RethrowBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var message = BlockInputCoercion.String(inputs, "Message");

        throw new SubCanvasRethrowException(message ?? "Rethrown from workflow");
    }
}

public sealed class RethrowBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "Rethrow";
    public string DisplayName => "Rethrow";
    public string Description => "Rethrows the current exception to be handled by an outer Try/Catch or fail the workflow.";
    public string Category => "Control Flow";
    /// <summary>Its only outcome is the throw; retrying can only delay the failure it exists to deliver.</summary>
    public bool IsIdempotent => false;

    public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema =>
    [
        new BlockConfigFieldDefinition("Message", "Message", ConfigFieldType.String, 
            Required: false, Description: "Optional message to include in the rethrown exception.")
    ];

    public IWorkflowBlockPlugin Create() => new RethrowBlock();
}
