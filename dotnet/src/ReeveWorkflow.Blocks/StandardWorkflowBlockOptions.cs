using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Blocks;

/// <summary>Which of the standard blocks this host replaces with one of its own. Anything not named here is
/// registered as it ships.</summary>
public sealed class StandardWorkflowBlockOptions
{
    private readonly IReadOnlySet<string> knownBlockTypeIds;
    private readonly Dictionary<string, Type> replacements = new(StringComparer.OrdinalIgnoreCase);

    internal StandardWorkflowBlockOptions(IReadOnlySet<string> knownBlockTypeIds) =>
        this.knownBlockTypeIds = knownBlockTypeIds;

    /// <summary>Registers <typeparamref name="TFactory"/> under <paramref name="blockTypeId"/> INSTEAD of the
    /// standard factory for that id. It is built through the container, so it may take dependencies.</summary>
    /// <param name="blockTypeId">An id this package carries. One it does not carry throws here, at the call:
    /// a mistyped id must not leave the standard block quietly in place beside a replacement nothing reaches.</param>
    /// <exception cref="ArgumentException">The id is not one of the standard blocks.</exception>
    public StandardWorkflowBlockOptions Override<TFactory>(string blockTypeId)
        where TFactory : class, IWorkflowBlockFactory
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockTypeId);

        if (!knownBlockTypeIds.Contains(blockTypeId))
        {
            throw new ArgumentException(
                $"'{blockTypeId}' is not a standard block, so there is nothing to replace. "
                + $"The standard blocks are: {string.Join(", ", knownBlockTypeIds.Order(StringComparer.Ordinal))}.",
                nameof(blockTypeId));
        }

        replacements[blockTypeId] = typeof(TFactory);
        return this;
    }

    internal bool TryGetReplacement(string blockTypeId, out Type replacement) =>
        replacements.TryGetValue(blockTypeId, out replacement!);
}
