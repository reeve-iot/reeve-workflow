using System.Globalization;

namespace ReeveWorkflow.Core.Expressions;

/// <summary>How many path segments after its name a namespace's key takes; the walker, the evaluator, the
/// compiler and the preview split a <c>${…}</c> path with the same table.</summary>
public sealed class ExpressionKeyShapes
{
    private readonly IReadOnlyDictionary<string, (int Min, int Max)> shapes;

    private ExpressionKeyShapes(IReadOnlyDictionary<string, (int Min, int Max)> shapes) => this.shapes = shapes;

    /// <summary>The four library namespaces: <c>previous</c> takes a node and an output, <c>parameter</c> a name,
    /// <c>now</c> nothing or one field, and <c>helper</c> its whole path; and the two scope-variable names, <c>item</c> and
    /// <c>value</c>, which take no key, so whatever follows one is a member or an index of it.</summary>
    public static ExpressionKeyShapes Library { get; } = BuildLibrary();

    private static ExpressionKeyShapes BuildLibrary()
    {
        var shapes = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            ["previous"] = (2, 2),
            ["parameter"] = (1, 1),
            ["now"] = (0, 1),
            ["helper"] = (1, int.MaxValue),
        };
        foreach (var name in WorkflowExpressionNamespaces.ScopeVariableNames)
            shapes[name] = (0, 0);
        return new ExpressionKeyShapes(shapes);
    }

    /// <summary>The library's namespaces and each registration's, at the key length it declares.</summary>
    public static ExpressionKeyShapes For(IEnumerable<WorkflowExpressionNamespace>? registrations) =>
        Library.With((registrations ?? []).Select(r => (r.Namespace, r.KeySegments)));

    internal ExpressionKeyShapes With(IEnumerable<(string Namespace, int KeySegments)> hosts)
    {
        var merged = new Dictionary<string, (int, int)>(shapes, StringComparer.Ordinal);
        foreach (var (name, length) in hosts)
            merged[name] = (length, length);
        return new ExpressionKeyShapes(merged);
    }

    internal bool TryGet(string name, out (int Min, int Max) shape) => shapes.TryGetValue(name, out shape);
}

/// <summary>One step past a namespace's key: a member name, or an index into an array.</summary>
public readonly record struct ExpressionFieldStep(bool IsIndex, string Name, int Index)
{
    /// <summary>The step as an author wrote it: <c>.name</c> or <c>[n]</c>.</summary>
    public override string ToString() => IsIndex ? $"[{Index.ToString(CultureInfo.InvariantCulture)}]" : "." + Name;
}

/// <summary>A <c>${…}</c> path split into its namespace, the key that namespace's resolver reads, and the field
/// path read out of the value the key names.</summary>
public sealed record ExpressionPathParts(
    string Namespace, bool NamespaceKnown, IReadOnlyList<string> Key, IReadOnlyList<ExpressionFieldStep> Fields)
{
    /// <summary>The namespace and its key, colon-separated: what a parked wait is found by. A field path is not part of it.</summary>
    public string DependencyKey => string.Join(':', Key.Prepend(Namespace));
}

/// <summary>The one function that splits a path inside <c>${…}</c>. Past a namespace's key, <c>.name</c> is a
/// member and <c>[n]</c> an index; a name with spaces is one segment, so there is no quoted form.</summary>
public static class ExpressionPath
{
    /// <summary>The namespace a path starts with: the text before its first <c>.</c> or <c>[</c>.</summary>
    public static string NamespaceOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var end = path.AsSpan().IndexOfAny('.', '[');
        return end < 0 ? path : path[..end];
    }

    /// <summary>The one formula for a dependency key: the namespace and its key, colon-separated. The walker (so the
    /// writer, the keyed scan, the index scan and the unkeyed fallback of a store) and the compiler both call it. A path the
    /// split refuses keeps its whole text, dots to colons, so it is neither lost nor invented.</summary>
    public static string DependencyKeyOf(string path, ExpressionKeyShapes shapes) =>
        TrySplit(path, shapes, out var parts, out _) ? parts.DependencyKey : path.Replace('.', ':');

    /// <summary>Splits <paramref name="path"/> by the key lengths in <paramref name="shapes"/>. False, with why, when the path
    /// is shorter than its namespace's key or an index sits where none may. A namespace the table does not know answers true
    /// with <see cref="ExpressionPathParts.NamespaceKnown"/> false, every dotted segment its key (an index after one is not
    /// part of it) and no fields. An empty segment never gets here: the grammar (<see cref="IsWellFormed"/>) refuses it.</summary>
    public static bool TrySplit(string path, ExpressionKeyShapes shapes, out ExpressionPathParts parts, out string error)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(shapes);
        parts = new ExpressionPathParts(string.Empty, false, [], []);
        error = string.Empty;

        if (!TryParts(path, out var pieces, out error))
            return false;

        var name = pieces[0].Name;
        var available = pieces.Count - 1;
        if (!shapes.TryGet(name, out var shape))
        {
            parts = new ExpressionPathParts(name, false, [.. pieces.Skip(1).Select(p => p.Name)], []);
            return true;
        }

        if (shape.Max == int.MaxValue)
        {
            if (pieces.Any(p => p.Indexes.Count > 0))
            {
                error = $"'{name}' takes its whole path as the key, so it has no index";
                return false;
            }
            return Done(pieces, available, name, ref parts, shape.Min, out error);
        }

        return Done(pieces, Math.Min(shape.Max, available), name, ref parts, shape.Min, out error);
    }

    private static bool Done(
        List<Piece> pieces, int length, string name, ref ExpressionPathParts parts, int minimum, out string error)
    {
        error = string.Empty;
        if (length < minimum)
        {
            error = minimum == 1
                ? $"'{name}' needs a key after it"
                : $"'{name}' needs a key of {minimum.ToString(CultureInfo.InvariantCulture)} segments after it";
            return false;
        }

        var fields = new List<ExpressionFieldStep>();
        if (length == 0)
        {
            AddIndexes(pieces[0].Indexes, fields);
        }
        else if (pieces[0].Indexes.Count > 0)
        {
            error = $"an index cannot come between '{name}' and its key";
            return false;
        }

        var key = new List<string>(length);
        for (var i = 1; i <= length; i++)
        {
            key.Add(pieces[i].Name);
            if (i < length && pieces[i].Indexes.Count > 0)
            {
                error = $"an index cannot come inside the key of '{name}', after '{pieces[i].Name}'";
                return false;
            }
        }
        if (length > 0)
            AddIndexes(pieces[length].Indexes, fields);

        for (var i = length + 1; i < pieces.Count; i++)
        {
            fields.Add(new ExpressionFieldStep(false, pieces[i].Name, 0));
            AddIndexes(pieces[i].Indexes, fields);
        }

        parts = new ExpressionPathParts(name, true, key, fields);
        return true;
    }

    private static void AddIndexes(List<int> indexes, List<ExpressionFieldStep> fields)
    {
        foreach (var index in indexes)
            fields.Add(new ExpressionFieldStep(true, string.Empty, index));
    }

    internal static bool IsWellFormed(string path, out string error) => TryParts(path, out _, out error);

    private sealed record Piece(string Name, List<int> Indexes);

    private static bool TryParts(string path, out List<Piece> pieces, out string error)
    {
        pieces = [];
        error = string.Empty;
        var position = 0;
        while (true)
        {
            var start = position;
            while (position < path.Length && path[position] is not ('.' or '['))
                position++;
            var piece = new Piece(path[start..position], []);
            if (piece.Name.Contains(']'))
            {
                error = "a ']' closes an index and is not part of a name";
                return false;
            }
            if (piece.Name.Length == 0 && position < path.Length && path[position] == '[')
            {
                error = "a bracket must follow a name";
                return false;
            }
            if (piece.Name.Length == 0)
            {
                error = "a path segment is empty";
                return false;
            }
            while (position < path.Length && path[position] == '[')
            {
                var close = path.IndexOf(']', position);
                var digits = close < 0 ? string.Empty : path[(position + 1)..close];
                if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
                {
                    error = "an index is one or more decimal digits in brackets";
                    return false;
                }
                if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    error = $"the index [{digits}] is larger than {int.MaxValue.ToString(CultureInfo.InvariantCulture)}";
                    return false;
                }
                piece.Indexes.Add(index);
                position = close + 1;
            }
            pieces.Add(piece);
            if (position >= path.Length)
                return true;
            if (path[position] == ']')
            {
                error = "a ']' closes an index and is not part of a name";
                return false;
            }
            if (path[position] != '.')
            {
                error = "a bracket must follow a name";
                return false;
            }
            position++;
        }
    }
}
