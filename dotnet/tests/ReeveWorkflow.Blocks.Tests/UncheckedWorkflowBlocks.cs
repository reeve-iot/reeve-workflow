using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Blocks.Tests;

internal sealed class UncheckedWorkflowBlocks(IEnumerable<IWorkflowBlockFactory> factories) : IWorkflowBlocks
{
    private readonly IWorkflowBlockFactory[] all = [.. factories];

    public IWorkflowBlockFactory? Find(string blockTypeId) =>
        all.FirstOrDefault(f => string.Equals(f.BlockTypeId, blockTypeId, StringComparison.OrdinalIgnoreCase));

    public bool ContainsPrivilegedBlock(string? definitionJson) =>
        WorkflowDefinitionBlockTypes.Collect(definitionJson).Any(id => Find(id)?.IsPrivileged == true);

    public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => all;
}
