using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

internal static class PortTermLub
{
    public static PortTerm? Of(
        IEnumerable<PortTerm> evidence, IPortTermVocabulary vocabulary, Func<PortTerm, IEnumerable<PortTerm>> rowTargets,
        BindingMutant mutant = BindingMutant.None)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var current = evidence.ToList();
        var levels = new List<(bool Nullable, bool IsMap)>();
        while (true)
        {
            var nullable = current.Any(e => e.Kind == PortTermKind.Null || e.IsNullable);
            var stripped = current.Where(e => e.Kind != PortTermKind.Null)
                .Select(e => e.IsNullable ? e.WithoutNullable() : e).ToList();
            if (stripped.Count == 0)
                return null;

            PortTerm? join;
            if (stripped.All(e => e.Equals(stripped[0])))
            {
                join = stripped[0];
            }
            else if ((stripped.All(e => e.IsCollection) || stripped.All(e => e.IsMap)) && mutant != BindingMutant.LubCollectionClauseOff)
            {
                levels.Add((nullable, stripped[0].IsMap));
                current = [.. stripped.Select(e => e.LastArgument!)];
                continue;
            }
            else
            {
                join = Candidate(stripped, vocabulary, rowTargets, mutant);
            }

            if (join is null)
                return null;
            if (nullable && mutant != BindingMutant.LubNullableDropped)
                join = join.AsNullable();
            for (var i = levels.Count - 1; i >= 0; i--)
            {
                join = levels[i].IsMap ? PortTerm.MapOf(join) : PortTerm.CollectionOf(join);
                if (levels[i].Nullable)
                    join = join.AsNullable();
            }

            return join;
        }
    }

    private static PortTerm? Candidate(
        List<PortTerm> elements, IPortTermVocabulary vocabulary, Func<PortTerm, IEnumerable<PortTerm>> rowTargets,
        BindingMutant mutant)
    {
        var candidates = new List<PortTerm>();
        foreach (var term in elements.Concat(elements.SelectMany(rowTargets)))
        {
            if ((!IsJson(term) || mutant == BindingMutant.LubJsonCandidate) && !candidates.Contains(term))
                candidates.Add(term);
        }

        var bounds = candidates
            .Where(c => elements.All(e => PortTermAssignability.IsAssignable(e, c, vocabulary)))
            .ToList();
        var least = bounds
            .Where(c => bounds.All(other => ReferenceEquals(other, c) || PortTermAssignability.IsAssignable(c, other, vocabulary)))
            .ToList();
        if (least.Count == 1)
            return least[0];
        return mutant == BindingMutant.LubFirstBound && bounds.Count > 0 ? bounds[0] : null;
    }

    private static bool IsJson(PortTerm term) =>
        term.Kind == PortTermKind.Atom && string.Equals(term.Name, "Json", StringComparison.OrdinalIgnoreCase);
}
