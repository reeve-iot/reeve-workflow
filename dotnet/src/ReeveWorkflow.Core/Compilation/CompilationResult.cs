using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>A program or a list of refusals. Never both: a caller holding a program must not have to ask
/// whether it is also broken.</summary>
public sealed class CompilationResult
{
    private static readonly IReadOnlyDictionary<string, BlockShape> NoBoundShapes =
        ReadOnlyDictionary<string, BlockShape>.Empty;

    private CompilationResult(
        WorkflowProgram? program,
        IReadOnlyList<CompilationDiagnostic> diagnostics,
        IReadOnlyDictionary<string, BlockShape> boundShapes,
        Exception? fault = null)
    {
        Program = program;
        Diagnostics = diagnostics;
        BoundShapes = boundShapes;
        Fault = fault;
    }

    public WorkflowProgram? Program { get; }

    public IReadOnlyList<CompilationDiagnostic> Diagnostics { get; }

    /// <summary>Every block type the bind phase reached, by id — what bind resolved during this compile,
    /// not a stand-in for the walk a fingerprint needs to decide whether to compile at all. Carried on both
    /// outcomes because a stored verdict is keyed by a hash whether it records a pass or a refusal.</summary>
    public IReadOnlyDictionary<string, BlockShape> BoundShapes { get; }

    /// <summary>The exception a <c>compilation-fault</c> diagnostic in <see cref="Diagnostics"/> was made from, for the
    /// caller to log; null on every other result. A result carrying one is no verdict: store it nowhere.</summary>
    public Exception? Fault { get; }

    [MemberNotNullWhen(true, nameof(Program))]
    public bool Succeeded => Program is not null;

    /// <summary>Wraps a program that bound and type-checked cleanly. Callers should read <see cref="Program"/>
    /// without checking <see cref="Diagnostics"/> first — it is always empty here.</summary>
    public static CompilationResult Compiled(
        WorkflowProgram program, IReadOnlyDictionary<string, BlockShape>? boundShapes = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        return new CompilationResult(program, [], Wrap(boundShapes));
    }

    /// <summary>Wraps the diagnostics that stopped compilation. Throws if handed none: a refusal that does
    /// not say what it refused is worse than no refusal. Copies the list so a caller clearing its own
    /// buffer afterwards cannot empty a refusal the constructor already validated.</summary>
    public static CompilationResult Refused(
        IReadOnlyList<CompilationDiagnostic> diagnostics,
        IReadOnlyDictionary<string, BlockShape>? boundShapes = null) =>
        diagnostics.Count == 0
            ? throw new ArgumentException("A refusal must say what it refused.", nameof(diagnostics))
            : new CompilationResult(null, [.. diagnostics], Wrap(boundShapes));

    internal static CompilationResult Refused(
        IReadOnlyList<CompilationDiagnostic> diagnostics, IReadOnlyDictionary<string, BlockShape> boundShapes, Exception? fault) =>
        fault is null ? Refused(diagnostics, boundShapes) : new CompilationResult(null, [.. diagnostics], Wrap(boundShapes), fault);

    private static IReadOnlyDictionary<string, BlockShape> Wrap(IReadOnlyDictionary<string, BlockShape>? boundShapes) =>
        boundShapes is null || boundShapes.Count == 0
            ? NoBoundShapes
            : new ReadOnlyDictionary<string, BlockShape>(
                new Dictionary<string, BlockShape>(boundShapes, StringComparer.OrdinalIgnoreCase));
}
