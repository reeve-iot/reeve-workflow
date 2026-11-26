using System.Reflection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Hosting;

/// <summary>One thing a host's validator found wrong with a node or a start argument: a host code, a message a caller may
/// be shown, and where it points. Answered by <see cref="IWorkflowNodeValidator"/> and
/// <see cref="IWorkflowStartArgumentValidator"/>.</summary>
public sealed record WorkflowValidationFinding
{
    /// <summary>A finding the host defines. Its code is the host's own.</summary>
    /// <param name="code">The host's code for the finding.</param>
    /// <param name="message">What a caller may be told; it is served, so it carries nothing they may not read.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> or <paramref name="message"/> is empty, or the code is one of
    /// the library's own refusal codes, which only the library may raise.</exception>
    public WorkflowValidationFinding(string code, string message)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("A finding names a code.", nameof(code));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A finding carries a message.", nameof(message));
        if (WorkflowLibraryCodes.IsLibraryCode(code))
            throw new ArgumentException("That code is the library's own.", nameof(code));
        Code = code;
        Message = message;
    }

    private WorkflowValidationFinding(string code, string message, bool library)
    {
        Code = code;
        Message = message;
    }

    /// <summary>The host's code.</summary>
    public string Code { get; }

    /// <summary>What a caller may be told.</summary>
    public string Message { get; }

    /// <summary>The sub-canvas chain the node is in; null or empty at the root.</summary>
    public CanvasPath? CanvasPath { get; init; }

    /// <summary>The node the finding is about.</summary>
    public string? NodeId { get; init; }

    /// <summary>The port of that node the finding is about.</summary>
    public string? Port { get; init; }

    /// <summary>The start parameter the finding is about.</summary>
    public string? Parameter { get; init; }

    internal CompilationDiagnostic ToDiagnostic() => new(Code, Message, NodeId, Port) { CanvasPath = CanvasPath?.Segments ?? [] };

    internal static WorkflowValidationFinding Of(string code, string message) => new(code, message, library: true);
}

internal static class WorkflowLibraryCodes
{
    private static readonly HashSet<string> Codes = new(
        new[]
        {
            typeof(WorkflowFailureReasons), typeof(WorkflowStartRefusalCodes), typeof(WorkflowStartArgumentCodes),
            typeof(WorkflowEditorFindingCodes), typeof(WorkflowSaveRefusalCodes), typeof(WorkflowEditConflictCodes),
            typeof(WorkflowErrorCodes), typeof(WorkflowRegistrationCodes), typeof(EnumFieldRules),
            typeof(WorkflowWaitOutcomes), typeof(WorkflowAbandonmentReasons), typeof(CompilationRules),
        }.SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)),
        StringComparer.Ordinal);

    internal static bool IsLibraryCode(string code) => Codes.Contains(code);
}

/// <summary>The codes of the findings admission raises about a start's arguments.</summary>
public static class WorkflowStartArgumentCodes
{
    /// <summary>A required parameter had no argument.</summary>
    public const string Missing = "start-argument-missing";

    /// <summary>An argument named a parameter the workflow does not declare.</summary>
    public const string Unknown = "start-argument-unknown";

    /// <summary>An argument did not read as its parameter's type, or was null for a type that is not nullable.</summary>
    public const string TypeMismatch = "start-argument-type-mismatch";
}

/// <summary>The code of the finding the editor raises when a host's node validator fails.</summary>
public static class WorkflowEditorFindingCodes
{
    /// <summary>The node validator threw or answered null, so the save or publish could not be judged by the host.</summary>
    public const string HostValidatorFaulted = "host-validator-faulted";
}
