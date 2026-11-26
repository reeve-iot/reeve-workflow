namespace ReeveWorkflow.Core.Compilation;

/// <summary>What an import's compile found, for a document not stored anywhere yet.</summary>
/// <param name="Diagnostics">The diagnostics publish would answer; empty when the document compiles. A
/// <c>SubWorkflow</c> whose callee this host lacks files none, since a bundle imports its workflows in any order.</param>
/// <param name="UnresolvedWorkflowIds">Each callee id a <c>SubWorkflow</c> node names that is not stored here, or has no
/// current version: publish and run keep refusing the caller until it exists.</param>
public sealed record ImportCompilation(
    IReadOnlyList<CompilationDiagnostic> Diagnostics, IReadOnlyList<Guid> UnresolvedWorkflowIds);
