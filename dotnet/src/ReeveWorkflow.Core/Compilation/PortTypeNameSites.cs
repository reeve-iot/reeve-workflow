using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal static class PortTypeNameSites
{
    internal static CompilationDiagnostic Diagnostic(StoredPortTypeSite site, string? blockSource) =>
        site.Error!.Fault == PortTypeSyntaxFault.MapKey
            ? At(site, CompilationRules.PortTypeMapKey, $"has a Map type whose key is not String: {site.Error.Message}", blockSource)
            : At(site, CompilationRules.PortTypeMalformed, $"has a malformed type: {site.Error.Message}", blockSource);

    internal static string? ReservedName(string? rawType) => ReservedPortTypeNames.In(rawType);

    internal static CompilationDiagnostic? Graded(StoredPortTypeSite site, PortTypeWideningTable table, string? blockSource)
    {
        if (site.Error is not null || site.RawType is null)
            return null;
        PortTermGrading.Grade(site.RawType, TermSeam.Stored, PositionOf(site.Site), table, out var refusal);
        return refusal is { } refused ? At(site, refused.Rule, refused.What, blockSource) : null;
    }

    internal static TermPosition PositionOf(PortTypeNameSite site) => site switch
    {
        PortTypeNameSite.DynamicInputPort or PortTypeNameSite.InputParameter or PortTypeNameSite.TypeArgument
            or PortTypeNameSite.ProjectionType => TermPosition.Input,
        _ => TermPosition.Output,
    };

    internal static (string Rule, string What)? Unspelled(string rawType, PortTerm term, PortTypeWideningTable table)
    {
        if (ReservedName(rawType) is { } reserved)
            return (CompilationRules.PortTypeAlias, $", which holds {reserved}, a name that is not a port type");

        if (table.RefusalOf(term) is { } refusal)
        {
            return (CompilationRules.PortTypeUnregistered,
                $", which the host's port type vocabulary does not register ({refusal.Message})");
        }

        var canonical = table.Canonicalize(term).ToString();
        return string.Equals(canonical, rawType, StringComparison.Ordinal)
            ? null
            : (CompilationRules.PortTypeAlias, $", an alias; the canonical name is '{canonical}'");
    }

    private static CompilationDiagnostic At(StoredPortTypeSite site, string rule, string what, string? blockSource)
    {
        var label = site.Site switch
        {
            PortTypeNameSite.DynamicInputPort => $"Dynamic input port '{site.PortName}' of node '{site.NodeId}'",
            PortTypeNameSite.DynamicOutputPort => $"Dynamic output port '{site.PortName}' of node '{site.NodeId}'",
            PortTypeNameSite.DeclaredOutput => $"Declared output '{site.PortName}' of node '{site.NodeId}'",
            PortTypeNameSite.TypeArgument => $"{TypeArgumentLabel}'{site.PortName}' of node '{site.NodeId}'",
            PortTypeNameSite.ProjectionType => $"The projection type of input '{site.PortName}' of node '{site.NodeId}'",
            PortTypeNameSite.InputParameter => $"Workflow input parameter '{site.PortName}'",
            _ => $"Workflow output parameter '{site.PortName}'",
        };
        var isParameter = site.Site is PortTypeNameSite.InputParameter or PortTypeNameSite.OutputParameter;
        return new CompilationDiagnostic(
            rule, $"{label} {what}",
            isParameter ? null : site.NodeId, site.PortName, isParameter ? null : site.CanvasPath, blockSource);
    }

    internal const string TypeArgumentLabel = "Type argument ";

    internal static bool HasMalformedStoredPortType(WorkflowNodeDefinition node, BlockShape shape) =>
        (node.OutputPorts ?? []).Any(p => !PortValueType.TryParse(p.Type, out _, out _))
        || (shape.Flags.HasDynamicPorts
            && (StoredWorkflowDocument.PortList(node, StoredDocumentSchema.InputPortsKey).Any(s => s.Error is not null)
                || StoredWorkflowDocument.PortList(node, StoredDocumentSchema.OutputPortsKey).Any(s => s.Error is not null)));
}
