using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

internal enum TermSeam
{
    Declaration,

    Stored,

    Resolver,

    Answer,
}

internal enum TermPosition
{
    Input,
    Output,
}

internal sealed class GradedTerm
{
    private GradedTerm(PortTerm term) => Term = term;

    public PortTerm Term { get; }

    internal static GradedTerm Accepted(PortTerm term) => new(term);
}

internal readonly record struct TermRefusal(string Rule, string What);

internal static class PortTermGrading
{
    public static GradedTerm? Grade(
        string? text, TermSeam seam, TermPosition position, PortTypeWideningTable? table, out TermRefusal? refusal)
    {
        var declaration = seam == TermSeam.Declaration;
        var malformed = declaration ? CompilationRules.BlockPortDeclarationInvalid : CompilationRules.PortTypeMalformed;
        if (string.IsNullOrWhiteSpace(text))
        {
            refusal = new TermRefusal(malformed, "states no port type");
            return null;
        }

        if (!PortTerm.TryParse(text, declaration ? PortTermGrammar.Declaration : PortTermGrammar.Document, out var term, out var error))
        {
            refusal = error.Fault == PortTypeSyntaxFault.MapKey && !declaration
                ? new TermRefusal(CompilationRules.PortTypeMapKey, $"has a Map type whose key is not String: {error.Message}")
                : new TermRefusal(malformed, $"has a malformed type: {error.Message}");
            return null;
        }

        if (table is not null && Unspelled(text, term, table, declaration) is { } unspelled)
        {
            refusal = new TermRefusal(unspelled.Rule, $"has the type '{text}'{unspelled.What}");
            return null;
        }

        if (JsonMisplaced(term, position))
        {
            refusal = new TermRefusal(declaration ? CompilationRules.BlockPortDeclarationInvalid : CompilationRules.PortTypeJsonPosition,
                position == TermPosition.Input
                    ? $"has the type '{text}', which holds Json; no input, binding or type argument holds Json"
                    : $"has the type '{text}', which nests Json; Json stands only as the whole type of an output");
            return null;
        }

        refusal = null;
        return GradedTerm.Accepted(term);
    }

    private static (string Rule, string What)? Unspelled(string text, PortTerm term, PortTypeWideningTable table, bool declaration)
    {
        if (!declaration)
            return PortTypeNameSites.Unspelled(text, term, table);
        var graded = BlockDeclarationRules.Graded(term);
        return PortTypeNameSites.Unspelled(graded.ToString(), graded, table);
    }

    public static bool JsonMisplaced(PortTerm term, TermPosition position)
    {
        var depth = 0;
        for (var level = term; level is not null; level = level.LastArgument, depth++)
        {
            if (level.Kind == PortTermKind.Atom && string.Equals(level.Name, "Json", StringComparison.OrdinalIgnoreCase))
                return position == TermPosition.Input || depth > 0;
        }

        return false;
    }
}
