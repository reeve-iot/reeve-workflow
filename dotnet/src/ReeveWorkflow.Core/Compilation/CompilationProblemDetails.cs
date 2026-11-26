using System.Text.Json.Serialization;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>The problem document the handlers that can refuse with the compiler's diagnostics, publish, rollback and
/// execute, answer every 400 with. <see cref="Diagnostics"/> is a top-level member, where ASP.NET writes a
/// <c>ProblemDetails</c> extension, so a generated client reads it typed.</summary>
/// <param name="Type">The problem type URI ASP.NET gives the status, as <c>ProblemDetails.Type</c> carries it.</param>
/// <param name="Title">The status's reason phrase, as <c>ProblemDetails.Title</c> carries it.</param>
/// <param name="Status">The HTTP status, as <c>ProblemDetails.Status</c> carries it.</param>
/// <param name="Detail">What was refused, as <c>ProblemDetails.Detail</c> carries it.</param>
/// <param name="Diagnostics">The compiler's diagnostics, in its order; empty when the refusal is not the compiler's,
/// such as a definition the engine's own validation refuses.</param>
/// <param name="Code">A <see cref="Hosting.WorkflowSaveRefusalCodes"/> value when a save refusal with a code is the reason, as the problem of a
/// create, update or rollback carries it; null otherwise, and then not written, so a compile refusal keeps its five members.</param>
public sealed record CompilationProblemDetails(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName(WorkflowDoesNotCompileException.ProblemExtension)] IReadOnlyList<CompilationDiagnostic> Diagnostics,
    [property: JsonPropertyName(WorkflowStartRefusedException.ProblemExtension), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);
