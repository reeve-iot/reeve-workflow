namespace ReeveWorkflow.Core.Compilation;

/// <summary>One refusal, anchored where an author can act on it. <see cref="CanvasPath"/> is empty on the
/// root canvas and names the sub-canvas chain below it, one <see cref="CanvasPathSegment"/> per level — a
/// node id and a branch name kept as a pair, never joined into one string a host would have to split back
/// apart (and could split wrong: see <see cref="Compilation.CanvasPath"/>'s own remarks).</summary>
/// <remarks>Equality here is reference equality on <see cref="CanvasPath"/>, not structural: a diagnostic
/// compares unequal to itself after a JSON round-trip, so <c>Distinct()</c> will not dedupe repeats.</remarks>
public sealed record CompilationDiagnostic(
    string RuleId,
    string Message,
    string? NodeId = null,
    string? PortName = null,
    IReadOnlyList<CanvasPathSegment>? CanvasPath = null,
    string? BlockSource = null)
{
    private readonly IReadOnlyList<CanvasPathSegment> _canvasPath = CanvasPath ?? [];

    /// <summary>The sub-canvas chain this points into, empty at the root. Never null, so one spelling of
    /// "root" reaches the stored JSON and the editor never has to guess which it is looking at. Each entry
    /// is a plain (node id, branch name) pair, so a host reading a stored report can print and match on it
    /// directly rather than parsing a joined string back apart.</summary>
    public IReadOnlyList<CanvasPathSegment> CanvasPath
    {
        get => _canvasPath;
        init => _canvasPath = value ?? [];
    }

}
