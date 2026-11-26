namespace ReeveWorkflow.Core.Compilation;

/// <summary>One step of a <see cref="CanvasPath"/>: the id of the node that owns the sub-canvas, and the
/// branch name that names it on that node — never the two concatenated. A node id and a branch name are
/// both authored text nothing in this repository refuses a '/' in, so keeping them as a pair rather than a
/// joined string is what makes two different canvases keying alike impossible rather than merely
/// unlikely.</summary>
public readonly record struct CanvasPathSegment(string NodeId, string BranchName);

/// <summary>Where a canvas sits in its definition's own tree: empty at the root, one <see cref="CanvasPathSegment"/>
/// per level of nesting otherwise. A value-equal type — equality and hashing are structural over the
/// segments, ordinal on each segment's two strings, the same comparison the rest of the compiler uses on
/// ids — so a phase keys its per-canvas state by the path itself rather than by a joined string a '/' in a
/// node id or a branch name could make two different canvases share.</summary>
/// <remarks><see cref="ToString"/> renders the path for a message a human reads — a log line, a diagnostic
/// rendered for a console — and is never parsed back and never used for keying or comparing two paths: use
/// the type itself (<c>==</c>, <see cref="Equals(CanvasPath?)"/>) or <see cref="Segments"/> for that.</remarks>
public sealed class CanvasPath : IEquatable<CanvasPath>
{
    /// <summary>The empty path: the root canvas of a definition, above every sub-canvas.</summary>
    public static CanvasPath Root { get; } = new([]);

    private readonly IReadOnlyList<CanvasPathSegment> segments;

    private CanvasPath(IReadOnlyList<CanvasPathSegment> segments) => this.segments = segments;

    /// <summary>This path's own segments, root-to-leaf. Never mutated after construction — <see cref="Child"/>
    /// always returns a new instance over a new, longer list.</summary>
    public IReadOnlyList<CanvasPathSegment> Segments => segments;

    /// <summary>True at <see cref="Root"/>, false everywhere else.</summary>
    public bool IsRoot => segments.Count == 0;

    /// <summary>The path one level below this one, into the sub-canvas named <paramref name="branchName"/>
    /// on the node <paramref name="nodeId"/>. The one place a child path is built — <see cref="CanvasWalk"/>
    /// and the binding pass both go through this (by way of <c>CanvasFrame.Child</c>),
    /// so a drift between what a frame is keyed by and what its signature is filed under cannot reappear.</summary>
    public CanvasPath Child(string nodeId, string branchName)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        ArgumentNullException.ThrowIfNull(branchName);
        return new CanvasPath([.. segments, new CanvasPathSegment(nodeId, branchName)]);
    }

    internal CanvasPath Parent => segments.Count == 0 ? this : new CanvasPath([.. segments.Take(segments.Count - 1)]);

    public bool Equals(CanvasPath? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        if (segments.Count != other.segments.Count)
            return false;

        for (var i = 0; i < segments.Count; i++)
        {
            var mine = segments[i];
            var theirs = other.segments[i];
            if (!string.Equals(mine.NodeId, theirs.NodeId, StringComparison.Ordinal)
                || !string.Equals(mine.BranchName, theirs.BranchName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as CanvasPath);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in segments)
        {
            hash.Add(segment.NodeId, StringComparer.Ordinal);
            hash.Add(segment.BranchName, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(CanvasPath? left, CanvasPath? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(CanvasPath? left, CanvasPath? right) => !(left == right);

    /// <summary>"root" at <see cref="Root"/>, else each segment rendered "nodeId→branchName" and joined by
    /// "/" — for reading, never for keying. See this type's own remarks.</summary>
    public override string ToString() =>
        segments.Count == 0 ? "root" : string.Join('/', segments.Select(s => $"{s.NodeId}→{s.BranchName}"));
}
