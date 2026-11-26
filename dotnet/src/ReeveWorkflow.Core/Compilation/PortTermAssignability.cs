using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

internal interface IPortTermVocabulary
{
    bool HasRow(PortTerm from, PortTerm to);

    bool IsJsonSerializable(PortTerm head);
}

internal static class PortTermAssignability
{
    public static bool IsAssignable(PortTerm from, PortTerm to, IPortTermVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (from.ContainsParameter || to.ContainsParameter)
            throw new ArgumentException("Assignability is defined on closed terms; a type parameter must be bound first.");
        if (to.Kind == PortTermKind.Null)
            throw new ArgumentException("The null literal's term is never a target.", nameof(to));

        var a = from;
        var b = to;
        while (true)
        {
            if (!(a.IsCollection && b.IsCollection || a.IsMap && b.IsMap) && a.Equals(b))
                return true;

            if (b.IsNullable)
            {
                if (a.Kind == PortTermKind.Null)
                    return true;
                a = a.WithoutNullable();
                b = b.WithoutNullable();
                continue;
            }

            if (a.IsNullable)
                return false;

            if (b.Kind == PortTermKind.Atom && string.Equals(b.Name, "Json", StringComparison.OrdinalIgnoreCase))
                return ImplementsJson(a, vocabulary);

            if (a.IsCollection && b.IsCollection || a.IsMap && b.IsMap)
            {
                a = a.LastArgument!;
                b = b.LastArgument!;
                continue;
            }
            if (a.Kind == PortTermKind.Constructor && b.Kind == PortTermKind.Constructor
                && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
                return false;

            return vocabulary.HasRow(a, b);
        }
    }

    private static bool ImplementsJson(PortTerm term, IPortTermVocabulary vocabulary)
    {
        while (term.IsCollection || term.IsMap)
            term = term.LastArgument!;
        return vocabulary.IsJsonSerializable(term.IsNullable ? term.WithoutNullable() : term);
    }
}
