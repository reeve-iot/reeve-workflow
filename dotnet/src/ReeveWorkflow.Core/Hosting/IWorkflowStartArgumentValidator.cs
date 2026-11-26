namespace ReeveWorkflow.Core.Hosting;

/// <summary>A host's check of the start arguments whose type is the host's own, such as an argument declared as a
/// subtype of a device that names the wrong kind of device. Optional: with none filled, it finds nothing.</summary>
/// <remarks>Asked once per start, after each argument has been read as its parameter's type, and only when some argument's
/// type holds a host atom or constructor. A throw or a null answer refuses the start with
/// <see cref="WorkflowStartRefusalCodes.AdmissionFaulted"/>. Resolved once with the engine, so it is a singleton.
///</remarks>
public interface IWorkflowStartArgumentValidator
{
    /// <summary>Judges the arguments of one start. An empty list passes them.</summary>
    ValueTask<IReadOnlyList<WorkflowValidationFinding>> ValidateAsync(
        WorkflowStartArgumentsRequest request, CancellationToken cancellationToken);
}

/// <summary>A start and its arguments of host type, for <see cref="IWorkflowStartArgumentValidator"/>.</summary>
/// <param name="Start">The start, as <see cref="IWorkflowStartAdmission"/> sees it.</param>
/// <param name="Arguments">The arguments whose type holds a host atom or constructor.</param>
public sealed record WorkflowStartArgumentsRequest(WorkflowStartRequest Start, IReadOnlyList<WorkflowStartArgument> Arguments);

/// <summary>One argument, read as its parameter's type.</summary>
/// <param name="Parameter">The parameter's name.</param>
/// <param name="Term">The parameter's closed term, in document grammar (<c>Collection&lt;Int&gt;</c>).</param>
/// <param name="Value">The argument in the CLR type of <paramref name="Term"/>; null for a nullable term.</param>
public sealed record WorkflowStartArgument(string Parameter, string Term, object? Value);
