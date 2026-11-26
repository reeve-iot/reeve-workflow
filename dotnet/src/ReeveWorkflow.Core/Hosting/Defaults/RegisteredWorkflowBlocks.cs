using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Catalog;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Hosting.Defaults;

/// <summary>The block catalogue of a host whose blocks are all known at startup: whatever factories it hands
/// over, indexed by id. The default behind both block seams for a host with no plugin model.</summary>
/// <remarks>One object behind both interfaces on purpose. Two adapters over one set of factories would agree
/// today and stop agreeing the moment either grew a cache, and then a palette would offer a block the graph
/// walk cannot find. A host that loads blocks at runtime replaces this with a registry of its own.</remarks>
public sealed class RegisteredWorkflowBlocks : IWorkflowBlocks
{
    private readonly Dictionary<string, IWorkflowBlockFactory> byId;
    private readonly IWorkflowBlockFactory[] all;

    /// <param name="factories">Every block this host offers. Duplicate or reserved ids throw at startup, and so does a
    /// declaration <see cref="BlockShape.From"/> refuses: a broken rule, a dotted port name, a null or duplicate sub-canvas name or a malformed Enum field.</param>
    public RegisteredWorkflowBlocks(IEnumerable<IWorkflowBlockFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);

        all = factories.ToArray();
        byId = new Dictionary<string, IWorkflowBlockFactory>(all.Length, StringComparer.OrdinalIgnoreCase);

        foreach (var factory in all)
        {
            _ = BlockShape.From(factory);
            if (BoundaryBlockShapes.IsReserved(factory.BlockTypeId))
                throw new InvalidOperationException(BoundaryBlockShapes.ReservedMessage(factory.BlockTypeId));

            if (!byId.TryAdd(factory.BlockTypeId, factory))
            {
                throw new InvalidOperationException(
                    $"Two block factories are registered under block type id '{factory.BlockTypeId}': "
                    + $"{byId[factory.BlockTypeId].GetType().FullName} and {factory.GetType().FullName}. "
                    + "Block type ids are matched case-insensitively and must be unique.");
            }
        }
    }

    /// <inheritdoc />
    public IWorkflowBlockFactory? Find(string blockTypeId) =>
        blockTypeId is not null && byId.TryGetValue(blockTypeId, out var factory) ? factory : null;

    /// <inheritdoc />
    public bool ContainsPrivilegedBlock(string? definitionJson) =>
        WorkflowDefinitionBlockTypes.Collect(definitionJson).Any(id => Find(id)?.IsPrivileged == true);

    /// <inheritdoc />
    public IReadOnlyCollection<IWorkflowBlockFactory> GetAll() => all;
}
