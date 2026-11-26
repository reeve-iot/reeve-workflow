namespace ReeveWorkflow.Core.Expressions;

/// <summary>A helper path, represented by its segments after <c>helper</c>.</summary>
public sealed class HelperPath : IEquatable<HelperPath>
{
    /// <summary>Creates a path from the segments after <c>helper</c>.</summary>
    public HelperPath(IEnumerable<string> segments) { ArgumentNullException.ThrowIfNull(segments); Segments = Array.AsReadOnly(segments.ToArray()); }
    /// <summary>The immutable path segments, compared ordinally.</summary>
    public IReadOnlyList<string> Segments { get; }
    /// <summary>Answers the dotted path after <c>helper.</c>.</summary>
    public override string ToString() => string.Join('.', Segments);
    /// <summary>Whether <paramref name="other"/> has the same ordinal path segments.</summary>
    public bool Equals(HelperPath? other) => other is not null && Segments.SequenceEqual(other.Segments, StringComparer.Ordinal);
    /// <summary>Returns a hash consistent with ordinal segment equality.</summary>
    public override bool Equals(object? obj) => obj is HelperPath other && Equals(other);
    /// <summary>Returns a hash consistent with ordinal segment equality.</summary>
    public override int GetHashCode() { var hash = new HashCode(); foreach (var s in Segments) hash.Add(s, StringComparer.Ordinal); return hash.ToHashCode(); }
}

/// <summary>Answers keyed helper checks and reads in one asynchronous call per compile or evaluation unit.</summary>
/// <remarks>Missing checks mean No; missing reads mean absent, while present null is a value. A read returns each value in the CLR type of its checked term. Each node execution sees one value per helper; later nodes see earlier writes, but later items in that node do not. A throwing read fails the node through its error policy, including on resume. A node snapshot is read before its expressions run, even when a Filter evaluates no items. A changed term takes effect at each version's next compile, and changes its dependency fingerprint, so a verdict kept for the old term is not answered; a version that reads helpers costs one check per lookup, one that reads none asks nothing. A node already executing a version compiled against the old term may fail at the gate with <c>block-output-type-mismatch</c> or the read's <c>narrowing</c>, which is accepted, not prevented.</remarks>
public interface IHelperResolver
{
    /// <summary>Checks all paths needed by one compilation and returns each accepted term.</summary>
    ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken);
    /// <summary>Reads one value snapshot for all paths used by one evaluation unit.</summary>
    ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken);
}

/// <summary>Default for environments where the helper subsystem isn't wired; reads are absent and checks answer No.</summary>
public sealed class NullHelperResolver : IHelperResolver
{
    public static readonly NullHelperResolver Instance = new();
    public ValueTask<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>> CheckAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>>(new Dictionary<HelperPath, NamespacePathCheckAnswer>());
    public ValueTask<IReadOnlyDictionary<HelperPath, object?>> ReadAsync(IReadOnlyCollection<HelperPath> paths, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyDictionary<HelperPath, object?>>(new Dictionary<HelperPath, object?>());
}
