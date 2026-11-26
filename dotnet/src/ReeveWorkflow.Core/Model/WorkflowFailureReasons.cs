namespace ReeveWorkflow.Core.Model;

/// <summary>Why a run reached its terminal state, where the status alone does not say. Stored on
/// <see cref="WorkflowExecutionLog.FailureReason"/>. Free-form by design: a host filters on the constants the library
/// publishes and ignores the rest.</summary>
public static class WorkflowFailureReasons
{
    /// <summary>Admission refused the start: a version it would run, the workflow's own or one its manifest pins,
    /// does not compile. A start of the same version is refused again while nothing it compiles against changes.</summary>
    public const string DidNotCompile = "did-not-compile";

    /// <summary>Admission refused the start because whether a version compiles could not be answered: the compiler
    /// faulted, a resolver or the store it read through threw, or a version the manifest pins could no longer be read.
    /// No verdict was recorded, so the same start may succeed later.</summary>
    public const string CompilationUnanswered = "compilation-unanswered";

    /// <summary>Admission refused the start because an argument did not fit its parameter, or the host's argument validator
    /// found it wrong: a required parameter without an argument, an argument no parameter names, one that does not read
    /// as its parameter's type.</summary>
    public const string StartArgumentsInvalid = "start-arguments-invalid";

    /// <summary>Admission refused the start because the host's node validator found a node of a version it would run wrong.
    ///</summary>
    public const string NodeValidationRefused = "node-validation-refused";

    /// <summary>The first start after an upgrade cancelled the run because it had not ended: a run begun before the upgrade is not resumed. The run-state row and the run's journal entry both carry it.</summary>
    public const string CancelledByUpgrade = "cancelled-by-upgrade";
}
