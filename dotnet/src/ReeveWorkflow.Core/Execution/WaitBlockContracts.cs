using System.Text.Json.Serialization;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>What a block that parks a run throws, and what comes back to it when the run is re-entered.</summary>

/// <summary>Carried by <see cref="WorkflowSuspendException"/> instead of an output dictionary. The runner
/// parks the run through <see cref="Hosting.IWorkflowSuspensionStore"/> and stops walking the graph.</summary>
public sealed class SuspendResult
{
    public SuspendResult(
        WorkflowSuspensionKind kind,
        string configJson,
        DateTimeOffset timeoutAtUtc)
    {
        Kind = kind;
        ConfigJson = configJson ?? throw new ArgumentNullException(nameof(configJson));
        TimeoutAtUtc = timeoutAtUtc;
    }

    public WorkflowSuspensionKind Kind { get; }

    /// <summary>Serialised block config for the resumption service.</summary>
    public string ConfigJson { get; }

    public DateTimeOffset TimeoutAtUtc { get; }
}

/// <summary>The run context passed to <see cref="Hosting.IWorkflowSuspensionStore.SuspendAsync"/> — captures
/// all state needed to resume the workflow from the suspended block.</summary>
public sealed class SuspendRequest
{
    public required Guid ExecutionLogId { get; init; }
    public required Guid WorkflowId { get; init; }
    public required string BlockId { get; init; }
    public required WorkflowSuspensionKind Kind { get; init; }
    public required string ConfigJson { get; init; }
    /// <summary>Serialised run state. Must be &lt;= 256 KiB.</summary>
    public required string StatePayloadJson { get; init; }
    public required DateTimeOffset SuspendedAtUtc { get; init; }
    public required DateTimeOffset TimeoutAtUtc { get; init; }
}

/// <summary>Answered by <see cref="Hosting.IWorkflowSuspensionResumeStore.TryResolveAsync"/> when the swap
/// wins the race — the persisted state needed to re-enter the engine, and the outcome it re-enters with.</summary>
public sealed class ResumeContext
{
    public required Guid SuspensionId { get; init; }
    public required Guid ExecutionLogId { get; init; }
    public required Guid WorkflowId { get; init; }
    public required string BlockId { get; init; }
    public required WorkflowSuspensionKind Kind { get; init; }
    /// <summary>The block output that caused the resume: <c>outcome = "satisfied"</c>.</summary>
    public required string OutcomeJson { get; init; }
    /// <summary>The full state payload from the suspension row.</summary>
    public required string StatePayloadJson { get; init; }
    public required DateTimeOffset SuspendedAtUtc { get; init; }
}

/// <summary>Serialised payload stored in <c>WorkflowSuspension.StatePayloadJson</c>.</summary>
public sealed class WorkflowSuspensionStatePayload
{
    /// <summary>Serialised workflow variables at the point of suspension.</summary>
    public required string VariablesJson { get; init; }

    /// <summary>Previous-node outputs still in scope (node id → outputs JSON).</summary>
    public required string PreviousOutputsJson { get; init; }

    /// <summary>Trigger context envelope (may be empty JSON object when none).</summary>
    public required string TriggerEnvelopeJson { get; init; }

    /// <summary>Execution id for state-store linkage.</summary>
    public required Guid? ExecutionStateId { get; init; }

    /// <summary>Version-pinning: serialised {workflowId → versionId} manifest, or null.</summary>
    public string? PinnedVersionsJson { get; init; }

    /// <summary>The id of the WorkflowInput node, whose outputs in <see cref="PreviousOutputsJson"/> are the run's
    /// parameters. Null on older rows, which a waker reads from <see cref="VariablesJson"/>.</summary>
    public string? InputNodeId { get; init; }

    /// <summary>The run's call depth when it parked (see <see cref="ReeveWorkflow.Abstractions.WorkflowRunPin.CallDepth"/>),
    /// so the segment that resumes calls further down from where it was. Absent on an older payload, which reads 0.</summary>
    public int CallDepth { get; init; }

    /// <summary>The journal entry the run writes into. A parked run's row keeps its own; a checkpoint has nowhere
    /// else to. Null on a parked run's payload.</summary>
    public Guid? JournalExecutionId { get; init; }

    /// <summary><see cref="WorkflowRunCheckpoint.Format"/> on a checkpoint, and null on anything else: what tells a
    /// checkpoint from a row's variables written without one, which are whatever JSON the run's inputs were.</summary>
    public string? CheckpointFormat { get; init; }

    /// <summary>The root-canvas node the position starts at: the node a checkpoint was written before, or, on a run
    /// parked below the root, the container it was parked inside. Null on a run parked on the root canvas, and on every payload written without a checkpoint. What tells a checkpoint from a parked run's payload is
    /// <see cref="CheckpointFormat"/>, never this. Also set on a parked payload.</summary>
    public string? NodeInFlight { get; init; }

    /// <summary>The <see cref="RunStateWriteModeKind"/> the run was writing its checkpoints under, which says how far
    /// behind the run this one may be.</summary>
    public string? WriteMode { get; init; }

    /// <summary>True: a container the run was inside is run again from its start, since nothing records where in it
    /// the run was. Always true; recorded so a later reader need not guess what an old checkpoint meant.</summary>
    public bool ContainerInFlightRepeatsWhole { get; init; }

    /// <summary>Why this checkpoint cannot be re-entered, or null when it can. See the constants on
    /// <see cref="WorkflowRunCheckpoint"/>.</summary>
    public string? NotReenterableBecause { get; init; }

    /// <summary>Where the run was below the root canvas, outermost frame first; null when it was at the root, which
    /// is every payload written without a position. Ignored when null so that a root checkpoint serialises without it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContinuationJson { get; init; }

    /// <summary>This payload with the run-state row's id in it, which the walk that built it could not know, and
    /// nothing else changed.</summary>
    /// <remarks>Copies every member, so a property added to the payload always reaches the suspension row.</remarks>
    public WorkflowSuspensionStatePayload WithExecutionStateId(Guid? executionStateId) => Relinked(executionStateId, ParentExecutionId);

    /// <summary>The run that started this one and waits for its result, or null; what a resumed child with no run-state row
    /// knows its parent by. Left out of the JSON when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentExecutionId { get; init; }

    internal WorkflowSuspensionStatePayload Relinked(Guid? executionStateId, Guid? parentExecutionId) => new()
    {
        ParentExecutionId = parentExecutionId,
        VariablesJson = VariablesJson,
        PreviousOutputsJson = PreviousOutputsJson,
        TriggerEnvelopeJson = TriggerEnvelopeJson,
        ExecutionStateId = executionStateId,
        PinnedVersionsJson = PinnedVersionsJson,
        InputNodeId = InputNodeId,
        CallDepth = CallDepth,
        JournalExecutionId = JournalExecutionId,
        CheckpointFormat = CheckpointFormat,
        NodeInFlight = NodeInFlight,
        WriteMode = WriteMode,
        ContainerInFlightRepeatsWhole = ContainerInFlightRepeatsWhole,
        NotReenterableBecause = NotReenterableBecause,
        ContinuationJson = ContinuationJson
    };
}
