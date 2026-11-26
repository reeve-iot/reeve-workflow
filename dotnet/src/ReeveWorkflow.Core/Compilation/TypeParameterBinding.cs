using System.Text.Json;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

internal enum EvidenceKind
{
    Lower,
    Exact,
}

internal sealed record ParameterEvidence(string Parameter, EvidenceKind Kind, PortTerm Term);

internal enum ParameterResolutionKind
{
    Bound,
    Unbound,
    Conflict,
}

internal readonly record struct ParameterResolution(ParameterResolutionKind Kind, PortTerm? Term = null)
{
    public static ParameterResolution Bound(PortTerm term) => new(ParameterResolutionKind.Bound, term);

    public static ParameterResolution Unbound => new(ParameterResolutionKind.Unbound);

    public static ParameterResolution Conflict => new(ParameterResolutionKind.Conflict);
}

internal enum BindingMutant
{
    None,
    LiteralIntegralIsNumber,
    LiteralMixedArrayHasTerm,
    MatchNullableParameterKeepsNullable,
    MatchNullIntoNullableParameterBinds,
    MatchNullableCollectionIntoNonNullable,
    MatchExactAsLower,
    MatchEmptyArrayBindsNull,
    MatchConstructorRowIgnored,
    ResolveExactConflictIgnored,
    ResolveExactJoinedWithLower,
    ResolveNoJoinIsUnbound,
    ResolveNullOnlyIsConflict,
    ConstraintSkipped,
    ConstraintWidened,
    JsonBindingAccepted,
    LubNullableDropped,
    LubCollectionClauseOff,
    LubJsonCandidate,
    LubFirstBound,
}

internal static class TypeParameterBinding
{
    public static PortTerm? LiteralTerm(object? value, WorkflowTypeVocabulary vocabulary, BindingMutant mutant = BindingMutant.None)
    {
        switch (Read(value))
        {
            case LiteralShape.Null:
                return PortTerm.Null;
            case LiteralShape.String:
                return Terms.String;
            case LiteralShape.Boolean:
                return Terms.Boolean;
            case LiteralShape.Number:
                return mutant != BindingMutant.LiteralIntegralIsNumber && vocabulary.IsLiteralOf(value, PortValueType.Int)
                    ? Terms.Int
                    : Terms.Number;
            case LiteralShape.Array:
                var elements = Elements(value).Select(e => LiteralTerm(e, vocabulary, mutant)).ToList();
                if (elements.Count == 0)
                    return null;
                var join = elements.Any(e => e is null) ? null : Lub(elements!, vocabulary, mutant);
                if (join is null && mutant == BindingMutant.LiteralMixedArrayHasTerm)
                    join = elements.FirstOrDefault(e => e is { Kind: not PortTermKind.Null });
                return join is null ? null : PortTerm.CollectionOf(join);
            default:
                return null;
        }
    }

    public static void Match(
        PortTerm declared, PortTerm source, List<ParameterEvidence> into, WorkflowTypeVocabulary vocabulary,
        BindingMutant mutant = BindingMutant.None, bool exactMode = false)
    {
        var exact = exactMode;
        var d = declared;
        var s = source;
        while (true)
        {
            if (d.Kind == PortTermKind.Parameter)
            {
                Emit(d, s, exact, into, mutant);
                return;
            }

            if (!d.ContainsParameter || s.Kind == PortTermKind.Null || d.Kind != PortTermKind.Constructor)
                return;
            if (s.IsNullable && !d.IsNullable && mutant != BindingMutant.MatchNullableCollectionIntoNonNullable)
                return;

            if (d.IsCollection)
            {
                if (!s.IsCollection)
                    return;
            }
            else if (d.IsMap)
            {
                if (!s.IsMap)
                    return;
            }
            else if (s.Kind == PortTermKind.Constructor && string.Equals(s.Name, d.Name, StringComparison.OrdinalIgnoreCase))
            {
                exact = true;
            }
            else if (s.Kind == PortTermKind.Atom && mutant != BindingMutant.MatchConstructorRowIgnored
                && RowInto(s, d.Name, vocabulary) is { } row)
            {
                s = row;
                exact = true;
            }
            else
            {
                return;
            }

            d = d.LastArgument!;
            s = s.LastArgument!;
        }
    }

    public static void MatchLiteral(
        PortTerm declared, object? literal, List<ParameterEvidence> into, WorkflowTypeVocabulary vocabulary,
        BindingMutant mutant = BindingMutant.None)
    {
        if (declared.IsCollection && Read(literal) == LiteralShape.Array)
        {
            var elements = Elements(literal).ToList();
            if (elements.Count == 0 && mutant == BindingMutant.MatchEmptyArrayBindsNull)
                Match(declared.Arguments[0], PortTerm.Null, into, vocabulary, mutant);
            foreach (var element in elements)
            {
                if (LiteralTerm(element, vocabulary, mutant) is { } term)
                    Match(declared.Arguments[0], term, into, vocabulary, mutant);
            }

            return;
        }

        if (declared.IsMap && Read(literal) == LiteralShape.Object)
        {
            foreach (var member in Members(literal))
            {
                if (LiteralTerm(member, vocabulary, mutant) is { } term)
                    Match(declared.Arguments[1], term, into, vocabulary, mutant);
            }

            return;
        }

        if (LiteralTerm(literal, vocabulary, mutant) is { } own)
            Match(declared, own, into, vocabulary, mutant);
    }

    public static ParameterResolution Resolve(
        IEnumerable<ParameterEvidence> evidence, WorkflowTypeVocabulary vocabulary, BindingMutant mutant = BindingMutant.None)
    {
        var all = evidence.ToList();
        var exact = new List<PortTerm>();
        foreach (var term in all.Where(e => e.Kind == EvidenceKind.Exact).Select(e => vocabulary.Table.Canonicalize(e.Term)))
            if (!exact.Contains(term))
                exact.Add(term);
        var lower = all.Where(e => e.Kind == EvidenceKind.Lower).Select(e => e.Term).ToList();

        if (exact.Count > 1 && mutant != BindingMutant.ResolveExactConflictIgnored)
            return ParameterResolution.Conflict;
        if (exact.Count > 0)
        {
            if (mutant == BindingMutant.ResolveExactJoinedWithLower && Lub([.. exact, .. lower], vocabulary, mutant) is { } joined)
                return ParameterResolution.Bound(joined);
            return ParameterResolution.Bound(exact[0]);
        }

        if (lower.Any(t => t.Kind != PortTermKind.Null))
        {
            return Lub(lower, vocabulary, mutant) is { } join
                ? ParameterResolution.Bound(join)
                : mutant == BindingMutant.ResolveNoJoinIsUnbound ? ParameterResolution.Unbound : ParameterResolution.Conflict;
        }

        return mutant == BindingMutant.ResolveNullOnlyIsConflict && lower.Count > 0
            ? ParameterResolution.Conflict
            : ParameterResolution.Unbound;
    }

    public static string? Refusal(
        WorkflowTypeParameter parameter, PortTerm bound, WorkflowTypeVocabulary vocabulary, BindingMutant mutant = BindingMutant.None)
    {
        if (PortTermGrading.JsonMisplaced(bound, TermPosition.Input) && mutant != BindingMutant.JsonBindingAccepted)
            return CompilationRules.PortTypeJsonPosition;
        if (mutant == BindingMutant.ConstraintSkipped || vocabulary.RefusalOf(bound) is not null)
            return null;
        foreach (var name in parameter.Constraints)
        {
            if (!PortCapabilities.TryParse(name, out var capability) || vocabulary.Implements(bound, capability))
                continue;
            if (mutant == BindingMutant.ConstraintWidened && Widened(bound, vocabulary).Any(t => vocabulary.Implements(t, capability)))
                continue;
            return CompilationRules.TypeParameterConstraint;
        }

        return null;
    }

    public static PortTerm? Substitute(PortTerm declared, Func<string, PortTerm?> binding)
    {
        var chain = new List<PortTerm>();
        for (var node = declared; node is not null; node = node.LastArgument)
            chain.Add(node);

        PortTerm? built = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            if (node.Kind == PortTermKind.Parameter)
            {
                if (binding(node.Name) is not { } bound)
                    return null;
                built = node.IsNullable ? bound.AsNullable() : bound;
            }
            else if (node.Kind == PortTermKind.Constructor)
            {
                built = node.IsMap ? PortTerm.MapOf(built!, node.IsNullable) : PortTerm.Constructor(node.Name, built!, node.IsNullable);
            }
            else
            {
                built = node;
            }
        }

        return built;
    }

    public static IEnumerable<string> Parameters(PortTerm term)
    {
        for (var node = term; node is not null; node = node.LastArgument)
            if (node.Kind == PortTermKind.Parameter)
                yield return node.Name;
    }

    private static void Emit(PortTerm parameter, PortTerm source, bool exact, List<ParameterEvidence> into, BindingMutant mutant)
    {
        var kind = exact && mutant != BindingMutant.MatchExactAsLower ? EvidenceKind.Exact : EvidenceKind.Lower;
        if (!parameter.IsNullable)
        {
            into.Add(new ParameterEvidence(parameter.Name, kind, source));
            return;
        }

        if (source.Kind == PortTermKind.Null)
        {
            if (mutant == BindingMutant.MatchNullIntoNullableParameterBinds)
                into.Add(new ParameterEvidence(parameter.Name, kind, source));
            return;
        }

        var term = mutant == BindingMutant.MatchNullableParameterKeepsNullable ? source : source.WithoutNullable();
        into.Add(new ParameterEvidence(parameter.Name, kind, term));
    }

    private static PortTerm? RowInto(PortTerm atom, string constructor, WorkflowTypeVocabulary vocabulary)
    {
        if (!vocabulary.TermRows.TryGetValue(atom.Name, out var rows))
            return null;
        var matching = rows.Where(r => r.Kind == PortTermKind.Constructor
            && string.Equals(r.Name, constructor, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count == 1 ? matching[0] : null;
    }

    private static IEnumerable<PortTerm> Widened(PortTerm term, WorkflowTypeVocabulary vocabulary)
    {
        if (term.Kind != PortTermKind.Atom)
            return [];
        var plain = vocabulary.Table.WideningsOf(PortValueType.Of(term.Name)).Select(w => PortTerm.Parse(w.Name, PortTermGrammar.Document));
        return vocabulary.TermRows.TryGetValue(term.Name, out var rows) ? plain.Concat(rows) : plain;
    }

    private static PortTerm? Lub(IEnumerable<PortTerm> evidence, WorkflowTypeVocabulary vocabulary, BindingMutant mutant) =>
        mutant == BindingMutant.None
            ? vocabulary.LeastUpperBound(evidence)
            : vocabulary.LeastUpperBound(evidence, mutant);

    internal static bool MayWaitForBinding(object? literal, PortTerm declared) =>
        Read(literal) is LiteralShape.Null or LiteralShape.Array || declared.IsMap && Read(literal) == LiteralShape.Object;

    private enum LiteralShape
    {
        None,
        Null,
        String,
        Boolean,
        Number,
        Array,
        Object,
    }

    private static LiteralShape Read(object? value) => value switch
    {
        null => LiteralShape.Null,
        string => LiteralShape.String,
        bool => LiteralShape.Boolean,
        long or int or double or decimal or float => LiteralShape.Number,
        System.Collections.IList => LiteralShape.Array,
        System.Collections.IDictionary => LiteralShape.Object,
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => LiteralShape.Null,
            JsonValueKind.String => LiteralShape.String,
            JsonValueKind.True or JsonValueKind.False => LiteralShape.Boolean,
            JsonValueKind.Number => LiteralShape.Number,
            JsonValueKind.Array => LiteralShape.Array,
            JsonValueKind.Object => LiteralShape.Object,
            _ => LiteralShape.None,
        },
        _ => LiteralShape.None,
    };

    private static IEnumerable<object?> Members(object? obj) => obj switch
    {
        JsonElement element => element.EnumerateObject().Select(p => (object?)p.Value),
        System.Collections.IDictionary map => map.Values.Cast<object?>(),
        _ => [],
    };

    private static IEnumerable<object?> Elements(object? array) => array switch
    {
        JsonElement element => element.EnumerateArray().Select(e => (object?)e),
        System.Collections.IList list => list.Cast<object?>(),
        _ => [],
    };

    private static class Terms
    {
        public static readonly PortTerm Int = PortTerm.Atom("Int");
        public static readonly PortTerm Number = PortTerm.Atom("Number");
        public static readonly PortTerm String = PortTerm.Atom("String");
        public static readonly PortTerm Boolean = PortTerm.Atom("Boolean");
    }
}
