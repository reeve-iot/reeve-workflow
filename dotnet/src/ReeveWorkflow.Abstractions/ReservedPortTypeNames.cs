namespace ReeveWorkflow.Abstractions;

internal static class ReservedPortTypeNames
{
    internal static string? In(PortTerm term)
    {
        for (var node = term; node is not null; node = node.LastArgument)
        {
            if (node.Kind is not (PortTermKind.Atom or PortTermKind.Constructor) || node.IsCollection)
                continue;
            if (string.Equals(node.Name, "Any", StringComparison.OrdinalIgnoreCase)) return "Any";
            if (string.Equals(node.Name, "Float", StringComparison.OrdinalIgnoreCase)) return "Float";
        }

        return null;
    }

    internal static string? In(string? text) =>
        text is not null && PortTerm.TryParse(text, PortTermGrammar.Document, out var term, out _) ? In(term) : null;
}
