namespace ReeveWorkflow.Core.Compilation;

/// <summary>What <see cref="WorkflowCompilationService.EnsureAsync"/> throws when the compiler could not answer:
/// an exception escaped one of its phases. No verdict is memoised or stored for it, so the next call compiles
/// again, and a gate that asked refuses.</summary>
public sealed class CompilationFaultException : Exception
{
    /// <param name="diagnostic">The <c>compilation-fault</c> diagnostic the compiler refused with.</param>
    /// <param name="fault">The exception that escaped the phase.</param>
    public CompilationFaultException(CompilationDiagnostic diagnostic, Exception fault)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Message, fault) =>
        Diagnostic = diagnostic;

    /// <summary>The phase, the canvas and, where host code threw at one, the node.</summary>
    public CompilationDiagnostic Diagnostic { get; }
}
