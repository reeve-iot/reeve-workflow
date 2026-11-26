namespace ReeveWorkflow.Core.Hosting;

/// <summary>A host's say over whether a new run may start. Optional: with none filled, every start is admitted.</summary>
/// <remarks>Asked once per start attempt that passes the fire-dedup claim, by the one admission every start passes,
/// before the scripting gate, the compile and the lease. An admit is not a run: a start that then loses the lease (Single or Drop with a run
/// in flight, an Error overflow, a queued start dropped at its turn) was asked, and a Single workflow on N replicas asks
/// on each, so do not count admissions as runs. Never asked for a run already admitted: a resume after a wait, a
/// re-entry or replay of an abandoned run, a retry of an attempt; a child that a replayed parent starts again is a new
/// start and is asked. A throw after the fire claim drops that fire on every replica. Resolved once with
/// the engine, so it is a singleton; reach scoped services through a scope of its own.</remarks>
public interface IWorkflowStartAdmission
{
    /// <summary>Decides one start. Throwing is not an admit: the start is refused with
    /// <see cref="WorkflowStartRefusalCodes.AdmissionFaulted"/>, and the exception is logged, never served.</summary>
    ValueTask<WorkflowStartDecision> DecideAsync(WorkflowStartRequest request, CancellationToken cancellationToken);
}

/// <summary>Which door a new run came in by.</summary>
public enum WorkflowStartKind
{
    /// <summary>Nothing fired it: a call to <c>ExecuteAsync</c> with no fire and no parent, such as the mounted execute
    /// endpoint or a host's own call.</summary>
    Manual,

    /// <summary>A fire started it: a call to <c>ExecuteAsync</c> that handed over a <c>WorkflowFire</c>.</summary>
    Trigger,

    /// <summary>A run a block of another run started.</summary>
    SubWorkflow
}

/// <summary>What the library knows when a run is about to start, and nothing the host did not hand it.</summary>
/// <param name="WorkflowId">The workflow to run.</param>
/// <param name="VersionId">The version it would run: the pinned one, otherwise the current one.</param>
/// <param name="Kind">The door the start came in by.</param>
public sealed record WorkflowStartRequest(Guid WorkflowId, Guid VersionId, WorkflowStartKind Kind)
{
    /// <summary>The run whose block started this one; set for <see cref="WorkflowStartKind.SubWorkflow"/>.</summary>
    public Guid? ParentExecutionId { get; init; }

    /// <summary>The automation that fired the run, when a fire named one.</summary>
    public Guid? AutomationId { get; init; }

    /// <summary>The trigger that fired the run, when a fire named one.</summary>
    public Guid? TriggerId { get; init; }

    /// <summary>How many runs called down to this one: 0 for a run nothing called.</summary>
    public int CallDepth { get; init; }
}

/// <summary>Admit, or refuse with a code the host defines and a message a caller may be shown.</summary>
public sealed record WorkflowStartDecision
{
    private WorkflowStartDecision(string? code, string? message)
    {
        Code = code;
        Message = message;
    }

    /// <summary>The start may go on.</summary>
    public static WorkflowStartDecision Admit { get; } = new(null, null);

    /// <summary>The start is refused. The message is served to whoever asked for the run, so it must carry nothing
    /// they may not read.</summary>
    /// <exception cref="ArgumentException"><paramref name="code"/> or <paramref name="message"/> is empty, or the code is
    /// the library's own <see cref="WorkflowStartRefusalCodes.AdmissionFaulted"/>, which only a fault may carry.</exception>
    public static WorkflowStartDecision Refuse(string code, string message)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("A refusal names a code.", nameof(code));
        if (code == WorkflowStartRefusalCodes.AdmissionFaulted)
            throw new ArgumentException("That code is the library's own, for a hook that failed.", nameof(code));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A refusal carries a message.", nameof(message));
        return new(code, message);
    }

    /// <summary>Whether the start may go on.</summary>
    public bool IsAdmitted => Code is null;

    /// <summary>The host's code for the refusal; null when admitted.</summary>
    public string? Code { get; }

    /// <summary>What a caller may be told; null when admitted.</summary>
    public string? Message { get; }
}

/// <summary>Codes the library itself puts on a <see cref="Execution.WorkflowStartRefusedException"/>. A host's own
/// codes are its own strings.</summary>
public static class WorkflowStartRefusalCodes
{
    /// <summary>The host's admission threw, or answered nothing, so the start was not admitted. Not the workflow's
    /// fault: the same start may succeed later.</summary>
    public const string AdmissionFaulted = "start-admission-faulted";
}
