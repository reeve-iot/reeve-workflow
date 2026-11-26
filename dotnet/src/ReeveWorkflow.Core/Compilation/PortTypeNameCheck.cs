using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>Which kind of stored field a port type name was read from.</summary>
public enum PortTypeNameSite
{
    /// <summary>An entry of a node's <c>InputPorts</c> configuration string.</summary>
    DynamicInputPort,

    /// <summary>An entry of a node's <c>OutputPorts</c> configuration string.</summary>
    DynamicOutputPort,

    /// <summary>An entry of a node's declared <c>outputPorts</c>.</summary>
    DeclaredOutput,

    /// <summary>An entry of the workflow's input parameter list.</summary>
    InputParameter,

    /// <summary>An entry of the workflow's output parameter list.</summary>
    OutputParameter,

    /// <summary>A node's explicit type argument.</summary>
    TypeArgument,

    /// <summary>The <c>type</c> of a projection on a mapping into a node's input; the site names that node and input.</summary>
    ProjectionType,
}

/// <summary>One stored port type name that is malformed, blank or absent.</summary>
/// <param name="Site">The kind of field it was read from.</param>
/// <param name="Location">Where it is: a JSON pointer into its document, with <c>#</c> where it steps into a
/// port list held as a string.</param>
/// <param name="Error">The grammar's fault, its position, the text, and the hint
/// naming the one to write.</param>
/// <param name="Diagnostic">The <c>port-type-malformed</c> diagnostic the compiler reports for the same site,
/// with no <see cref="CompilationDiagnostic.BlockSource"/>, since no catalogue was read.</param>
public sealed record MalformedPortTypeName(PortTypeNameSite Site, string Location, PortTypeSyntaxError Error, CompilationDiagnostic Diagnostic);

/// <summary>What <see cref="PortTypeNameCheck.Check"/> found.</summary>
/// <param name="Malformed">Every malformed site of every readable document: the definition in document order,
/// then the input parameters, then the output parameters.</param>
/// <param name="Unreadable">One <c>definition-unreadable</c> for a definition the reader refuses, and one
/// <c>signature-unreadable</c> per parameter list it refuses. A refused document has no sites.</param>
/// <param name="Unread">Each error policy form no run reads, on every canvas (<c>error-policy-in-configuration</c>,
/// <c>error-policy-route-unread</c>, <c>error-policy-on-boundary-node</c>), as the compiler reports it. A file may still hold any.</param>
public sealed record PortTypeNameCheckResult(
    IReadOnlyList<MalformedPortTypeName> Malformed, IReadOnlyList<CompilationDiagnostic> Unreadable,
    IReadOnlyList<CompilationDiagnostic> Unread)
{
    /// <summary>True when every document was read, every name parses and no error policy form is one no run reads.</summary>
    public bool IsWellFormed => Malformed.Count == 0 && Unreadable.Count == 0 && Unread.Count == 0;

    /// <summary><see cref="Unreadable"/>, then each malformed site's diagnostic, then <see cref="Unread"/>.</summary>
    public IReadOnlyList<CompilationDiagnostic> Diagnostics => [.. Unreadable, .. Malformed.Select(m => m.Diagnostic), .. Unread];
}

/// <summary>Checks every port type name a stored workflow writes, and every node's error policy, with no block
/// catalogue: for a host that must refuse a document the compiler would refuse for either, such as an import. It reads
/// through the compiler's own reader, and reads every node's port lists and policy, as the compiler does.</summary>
public static class PortTypeNameCheck
{
    /// <summary>Checks a definition and its two parameter lists as stored. Never throws: a document the reader
    /// refuses, an absent definition included, is one diagnostic in <see cref="PortTypeNameCheckResult.Unreadable"/>.</summary>
    public static PortTypeNameCheckResult Check(string? definitionJson, string? inputParametersJson, string? outputParametersJson)
    {
        var unreadable = new List<CompilationDiagnostic>();
        var sites = new List<StoredPortTypeSite>();
        var unread = new List<CompilationDiagnostic>();
        if (StoredWorkflowDocument.Read(definitionJson, out var fault) is { } document)
        {
            sites.AddRange(document.Sites);
            foreach (var frame in CanvasWalk.Frames(document.Definition))
                unread.AddRange(frame.Canvas.Nodes.SelectMany(n => WorkflowCompiler.ErrorPolicyForms(n, frame.Path.Segments, blockSource: null)));
        }
        else
        {
            unreadable.Add(StoredWorkflowDocument.Unreadable(fault!));
        }

        var parameters = CanvasSignature.ReadParameters(inputParametersJson, outputParametersJson);
        sites.AddRange(parameters.Sites);
        unreadable.AddRange(parameters.Unreadable);

        return new PortTypeNameCheckResult(
            [.. sites.Where(s => s.Error is not null)
                .Select(s => new MalformedPortTypeName(s.Site, s.Location, s.Error!, PortTypeNameSites.Diagnostic(s, blockSource: null)))],
            unreadable, unread);
    }
}
