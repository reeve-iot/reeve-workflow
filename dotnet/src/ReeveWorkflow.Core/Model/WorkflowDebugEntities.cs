namespace ReeveWorkflow.Core.Model;

/// <summary>Transitions: Idle -> Bound -> Running -> Paused (repeatable) -> Completed | Failed | Cancelled; a Bound or Paused
/// session ends Failed too when its run fails.
/// Completed, Failed and Cancelled are terminal. Illegal transitions throw InvalidOperationException.</summary>
public enum WorkflowDebugSessionStatus
{
    Idle = 0,
    Bound = 1,
    Running = 2,
    Paused = 3,
    Completed = 4,
    Cancelled = 5,

    /// <summary>The run the session was bound to ended failed or timed out before the session reached another terminal status.</summary>
    Failed = 6
}

/// <summary>Commands sent from the UI via SignalR to the engine's per-session TaskCompletionSource.</summary>
public enum WorkflowDebugCommand
{
    Step = 0,
    StepInto = 1,
    StepOver = 2,
    Resume = 3,
    Kill = 4
}

/// <summary>A step-through debug session for a single workflow execution. Thin DB row; the blob fields
/// live in IObjectStorage core:workflow-debug under key {Id}.</summary>
public sealed class WorkflowDebugSession
{
    private static readonly IReadOnlySet<(WorkflowDebugSessionStatus From, WorkflowDebugSessionStatus To)> AllowedTransitions =
        new HashSet<(WorkflowDebugSessionStatus, WorkflowDebugSessionStatus)>
        {
            (WorkflowDebugSessionStatus.Idle, WorkflowDebugSessionStatus.Bound),
            (WorkflowDebugSessionStatus.Bound, WorkflowDebugSessionStatus.Running),
            (WorkflowDebugSessionStatus.Running, WorkflowDebugSessionStatus.Paused),
            (WorkflowDebugSessionStatus.Running, WorkflowDebugSessionStatus.Completed),
            (WorkflowDebugSessionStatus.Running, WorkflowDebugSessionStatus.Cancelled),
            (WorkflowDebugSessionStatus.Running, WorkflowDebugSessionStatus.Failed),
            (WorkflowDebugSessionStatus.Paused, WorkflowDebugSessionStatus.Running),
            (WorkflowDebugSessionStatus.Paused, WorkflowDebugSessionStatus.Cancelled),
            (WorkflowDebugSessionStatus.Paused, WorkflowDebugSessionStatus.Failed),
            (WorkflowDebugSessionStatus.Bound, WorkflowDebugSessionStatus.Failed),
            (WorkflowDebugSessionStatus.Bound, WorkflowDebugSessionStatus.Cancelled),
            (WorkflowDebugSessionStatus.Idle, WorkflowDebugSessionStatus.Cancelled),
        };

    private WorkflowDebugSession()
    {
    }

    public WorkflowDebugSession(Guid id, Guid workflowId, Guid ownerUserId, DateTimeOffset createdAtUtc)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Session id is required.", nameof(id)) : id;
        WorkflowId = workflowId == Guid.Empty ? throw new ArgumentException("Workflow id is required.", nameof(workflowId)) : workflowId;
        OwnerUserId = ownerUserId == Guid.Empty ? throw new ArgumentException("Owner user id is required.", nameof(ownerUserId)) : ownerUserId;
        Status = WorkflowDebugSessionStatus.Idle;
        CreatedAtUtc = createdAtUtc;
        LastActivityUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid WorkflowId { get; private set; }

    public Workflow Workflow { get; private set; } = null!;

    /// <summary>FK to WorkflowExecutionLog — set when the session is bound to a running execution.</summary>
    public Guid? WorkflowExecutionLogId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public WorkflowDebugSessionStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset LastActivityUtc { get; private set; }

    public ICollection<WorkflowDebugSnapshot> Snapshots { get; private set; } = [];

    /// <summary>Object storage key for the session blob, in core:workflow-debug.</summary>
    public string ObjectStorageKey => $"{Id}";

    public void Transition(WorkflowDebugSessionStatus to, DateTimeOffset now)
    {
        if (!AllowedTransitions.Contains((Status, to)))
            throw new InvalidOperationException(
                $"Invalid debug session transition from {Status} to {to} (session {Id}).");

        Status = to;
        LastActivityUtc = now;
    }

    public void BindToExecution(Guid executionLogId, DateTimeOffset now)
    {
        Transition(WorkflowDebugSessionStatus.Bound, now);
        WorkflowExecutionLogId = executionLogId;
    }

    public void TouchActivity(DateTimeOffset now)
    {
        LastActivityUtc = now;
    }
}

/// <summary>A point-in-time snapshot of engine state captured at a breakpoint pause. Thin DB row; the blob
/// fields live in IObjectStorage core:workflow-debug under key {DebugSessionId}/snapshots/{Id}.</summary>
public sealed class WorkflowDebugSnapshot
{
    private WorkflowDebugSnapshot()
    {
    }

    public WorkflowDebugSnapshot(
        Guid id,
        Guid debugSessionId,
        string nodeId,
        DateTimeOffset capturedAtUtc)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Snapshot id is required.", nameof(id)) : id;
        DebugSessionId = debugSessionId == Guid.Empty ? throw new ArgumentException("Debug session id is required.", nameof(debugSessionId)) : debugSessionId;
        NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        CapturedAtUtc = capturedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid DebugSessionId { get; private set; }

    public WorkflowDebugSession DebugSession { get; private set; } = null!;

    public string NodeId { get; private set; } = null!;

    public DateTimeOffset CapturedAtUtc { get; private set; }

    /// <summary>Object storage key for the snapshot blob, in core:workflow-debug.</summary>
    public string ObjectStorageKey => $"{DebugSessionId}/snapshots/{Id}";
}

/// <summary>A debug blob, in the workflow context's own table. Snapshots are large and short-lived, so a host
/// with object storage keeps them there instead; this is what a host without one gets.</summary>
/// <remarks>Keyed by the string the debug package composes, not by a foreign key: the same store holds a
/// session's breakpoints, which belong to no snapshot, and a snapshot's captured state, which belongs to one.</remarks>
public sealed class WorkflowDebugBlob
{
    private WorkflowDebugBlob()
    {
    }

    public WorkflowDebugBlob(string key, byte[] content, DateTimeOffset writtenAtUtc)
    {
        Key = string.IsNullOrWhiteSpace(key) ? throw new ArgumentException("Blob key is required.", nameof(key)) : key;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        WrittenAtUtc = writtenAtUtc;
    }

    public string Key { get; private set; } = null!;

    public byte[] Content { get; private set; } = null!;

    public DateTimeOffset WrittenAtUtc { get; private set; }

    /// <summary>Replaces what is stored. A re-captured snapshot overwrites its predecessor rather than
    /// accumulating, which is the contract the blob store's own Put has.</summary>
    public void Replace(byte[] content, DateTimeOffset writtenAtUtc)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        WrittenAtUtc = writtenAtUtc;
    }
}
