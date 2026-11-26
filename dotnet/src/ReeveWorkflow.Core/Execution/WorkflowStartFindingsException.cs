using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Admission refused a start because its arguments did not fit the workflow's parameters, or because the host's
/// validators found something wrong with them or with a node it would run. Thrown before anything of the run exists but
/// the ended row that records it, so the caller fails in the run's place.</summary>
/// <remarks><see cref="Reason"/> is the failure reason of the row; <see cref="Findings"/> are the same ones the
/// row's entries carry.</remarks>
public sealed class WorkflowStartFindingsException : InvalidOperationException
{
    /// <summary>The <c>ProblemDetails</c> extension an HTTP surface writes <see cref="Findings"/> under, as the diagnostics
    /// of a version that does not compile.</summary>
    public const string ProblemExtension = WorkflowDoesNotCompileException.ProblemExtension;

    /// <param name="workflowId">The workflow whose start was refused.</param>
    /// <param name="workflowVersionId">The version the start was judged against.</param>
    /// <param name="reason">A <see cref="Model.WorkflowFailureReasons"/> value.</param>
    /// <param name="findings">At least one finding.</param>
    /// <exception cref="ArgumentException"><paramref name="findings"/> is empty.</exception>
    public WorkflowStartFindingsException(
        Guid workflowId, Guid workflowVersionId, string reason, IReadOnlyList<WorkflowValidationFinding> findings)
        : base(Describe(reason, findings))
    {
        WorkflowId = workflowId;
        WorkflowVersionId = workflowVersionId;
        Reason = reason;
        Findings = findings;
    }

    /// <summary>The workflow whose start was refused.</summary>
    public Guid WorkflowId { get; }

    /// <summary>The version the start was judged against.</summary>
    public Guid WorkflowVersionId { get; }

    /// <summary>Why the start was refused: <see cref="Model.WorkflowFailureReasons.StartArgumentsInvalid"/> or
    /// <see cref="Model.WorkflowFailureReasons.NodeValidationRefused"/>.</summary>
    public string Reason { get; }

    /// <summary>What was found wrong, in the order it was found.</summary>
    public IReadOnlyList<WorkflowValidationFinding> Findings { get; }

    /// <summary>The findings as the diagnostics an HTTP surface answers a refusal with.</summary>
    public IReadOnlyList<CompilationDiagnostic> Diagnostics => [.. Findings.Select(f => f.ToDiagnostic())];

    private static string Describe(string reason, IReadOnlyList<WorkflowValidationFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count == 0)
            throw new ArgumentException("A refused start has at least one finding.", nameof(findings));
        return $"The start was refused ({reason}): {findings.Count} problem(s), the first is {findings[0].Code}: {findings[0].Message}";
    }
}
