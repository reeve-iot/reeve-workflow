using System.Collections;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks;

/// <summary>Keeps the items of <c>Collection</c> for which <c>Predicate</c>, evaluated with <c>${item}</c> bound to the item,
/// answers <c>true</c>, in order. A predicate that fails fails the node.</summary>
public sealed class FilterBlock : IWorkflowBlockPlugin
{
    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue("Collection", out var raw) || raw is not IEnumerable collection || raw is string)
            throw new ArgumentException("Input 'Collection' is required and must be a collection.");

        var writes = BlockValues.Writes(context, "Filtered", "$T");
        var kept = new List<object?>();
        foreach (var item in collection)
        {
            if (context.Expressions.Evaluate("Predicate", new Dictionary<string, object?> { ["item"] = item }) is true)
                kept.Add(item);
        }

        var written = new Dictionary<string, object?>();
        if (writes)
            written["Filtered"] = BlockValues.Collection(context, "Filtered", kept);
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(written);
    }
}

public sealed class FilterBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "Filter";
    public string DisplayName => "Filter Collection";
    public string Description => "Keeps the items for which the predicate, reading each item as ${item}, is true.";
    public string Category => "Control Flow";
    /// <summary>Filters the collection handed to it and touches nothing outside its own outputs.</summary>
    public bool IsIdempotent => true;

    /// <summary><c>$T</c> is the item type, bound by what feeds <c>Collection</c>.</summary>
    public IReadOnlyList<WorkflowTypeParameter> TypeParameters => [new("T")];

    public IReadOnlyCollection<BlockPortDefinition> Inputs => new[]
    {
        new BlockPortDefinition("Collection", "Collection", PortValueType.OfDeclaration("Collection<$T>"), true),
        new BlockPortDefinition("Predicate", "Predicate", PortValueType.OfDeclaration("Expression<Boolean>"), true)
        {
            ScopeVariables = [new ExpressionScopeVariable("item", "$T")]
        }
    };

    public IReadOnlyCollection<BlockPortDefinition> Outputs => new[]
    {
        new BlockPortDefinition("Filtered", "Filtered Collection", PortValueType.OfDeclaration("Collection<$T>"), true)
    };

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => new[]
    {
        new BlockConfigFieldDefinition("Predicate", "Predicate", ConfigFieldType.String, Required: false,
            Description: "An expression over ${item} that is true for each item to keep, for example ${item} > 2."),
    };

    public IWorkflowBlockPlugin Create() => new FilterBlock();
}
