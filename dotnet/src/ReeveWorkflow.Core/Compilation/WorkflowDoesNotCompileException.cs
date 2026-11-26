namespace ReeveWorkflow.Core.Compilation;

/// <summary>A version was asked to become current, or a run of it to start, and it does not compile. Carries the
/// diagnostics, so a caller shows them without compiling again.</summary>
public sealed class WorkflowDoesNotCompileException : InvalidOperationException
{
    /// <summary>The <c>ProblemDetails</c> extension an HTTP surface writes <see cref="Diagnostics"/> under.</summary>
    public const string ProblemExtension = "diagnostics";

    /// <param name="diagnostics">The invalid verdict's diagnostics.</param>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> is empty: an invalid verdict names at least one.</exception>
    public WorkflowDoesNotCompileException(IReadOnlyList<CompilationDiagnostic> diagnostics)
        : base(Describe(diagnostics, null)) =>
        Diagnostics = diagnostics;

    /// <summary>A run's refusal: names the version that does not compile, which is the run's own or one its manifest
    /// pins, since its diagnostics point into that version's graph.</summary>
    /// <param name="diagnostics">The invalid verdict's diagnostics.</param>
    /// <param name="workflowId">The workflow whose version does not compile.</param>
    /// <param name="workflowVersionId">That version.</param>
    /// <exception cref="ArgumentException"><paramref name="diagnostics"/> is empty.</exception>
    public WorkflowDoesNotCompileException(
        IReadOnlyList<CompilationDiagnostic> diagnostics, Guid workflowId, Guid workflowVersionId)
        : base(Describe(diagnostics, (workflowId, workflowVersionId)))
    {
        Diagnostics = diagnostics;
        WorkflowId = workflowId;
        WorkflowVersionId = workflowVersionId;
    }

    /// <summary>Why the version does not compile, in the compiler's order.</summary>
    public IReadOnlyList<CompilationDiagnostic> Diagnostics { get; }

    /// <summary>The workflow whose version does not compile, when a run was refused; null from the editor's doors,
    /// whose caller named the workflow.</summary>
    public Guid? WorkflowId { get; }

    /// <summary>The version that does not compile, when a run was refused.</summary>
    public Guid? WorkflowVersionId { get; }

    private static string Describe(IReadOnlyList<CompilationDiagnostic> diagnostics, (Guid Workflow, Guid Version)? which)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (diagnostics.Count == 0)
            throw new ArgumentException("A version that does not compile has at least one diagnostic.", nameof(diagnostics));
        var subject = which is { } named
            ? $"Version '{named.Version}' of workflow '{named.Workflow}' does not compile"
            : "The workflow does not compile";
        return $"{subject}: {diagnostics.Count} problem(s), the first is {diagnostics[0].RuleId}: {diagnostics[0].Message}";
    }
}
