namespace ReeveWorkflow.Abstractions;

/// <summary>What <c>IWorkflowTypeRegistry.Register</c> answers: nothing applied and every refusal named, or the
/// registration applied.</summary>
/// <param name="Refusals">Each record that was refused with its code; empty when the registration was applied.
/// One refused record refuses the whole call.</param>
/// <param name="Generation">The registry's generation after the call: unchanged when anything was refused.</param>
public sealed record WorkflowTypeRegistrationResult(IReadOnlyList<WorkflowRegistrationRefusal> Refusals, long Generation)
{
    /// <summary>True when the call applied.</summary>
    public bool Accepted => Refusals.Count == 0;
}

/// <summary>A registration at startup that the vocabulary refused: a host defect, so the host does not start.</summary>
public sealed class WorkflowTypeRegistrationException : InvalidOperationException
{
    /// <summary>The refusals, each with its code and the record's name.</summary>
    public IReadOnlyList<WorkflowRegistrationRefusal> Refusals { get; }

    /// <summary>Wraps <paramref name="refusals"/>; the message lists each code, record and reason.</summary>
    public WorkflowTypeRegistrationException(IReadOnlyList<WorkflowRegistrationRefusal> refusals)
        : base(Describe(refusals))
    {
        Refusals = refusals;
    }

    private static string Describe(IReadOnlyList<WorkflowRegistrationRefusal> refusals) =>
        "Port type registration refused: " + string.Join(" ", refusals.Select(r => $"{r.Code} ({r.Record}): {r.Message}"));
}
