using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

internal interface ICapabilityClaims
{
    bool Claims(PortTerm head, WorkflowCapability capability);
}

internal static class PortTermCapabilities
{
    public static bool Implements(PortTerm term, WorkflowCapability capability, ICapabilityClaims claims)
    {
        ArgumentNullException.ThrowIfNull(term);
        ArgumentNullException.ThrowIfNull(claims);
        if (term.ContainsParameter)
            throw new ArgumentException("Capability membership is defined on closed terms; a type parameter must be bound first.");

        var node = term;
        for (;;)
        {
            if (node.Kind == PortTermKind.Null)
                return false;
            if (node.IsNullable)
            {
                if (capability != WorkflowCapability.JsonSerializable)
                    return false;
                node = node.WithoutNullable();
            }

            if (!node.IsCollection && !node.IsMap)
                return claims.Claims(node, capability);
            switch (capability)
            {
                case WorkflowCapability.Iterable:
                    return node.IsCollection;
                case WorkflowCapability.Equatable or WorkflowCapability.JsonSerializable:
                    node = node.LastArgument!;
                    break;
                default:
                    return false;
            }
        }
    }

    public static PortTerm? IterableItem(PortTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (term.ContainsParameter)
            throw new ArgumentException("Capability membership is defined on closed terms; a type parameter must be bound first.");
        return term.Kind != PortTermKind.Null && !term.IsNullable && term.IsCollection ? term.Arguments[0] : null;
    }
}
