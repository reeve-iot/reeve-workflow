namespace ReeveWorkflow.Core.Model;

public enum WorkflowExecutionStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
    TimedOut = 4
}

public enum WorkflowExecutionStepStatus
{
    Succeeded = 0,
    Failed = 1,
    Skipped = 2
}

public enum WorkflowTraceMode
{
    None = 0,
    Errors = 1,
    Full = 2
}

public enum LogEntryLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

/// <summary>What a trigger does when a run of the same workflow is already in flight. A run holds its place
/// while it walks and gives it back when it ends or parks; a parked run takes a place again when it resumes, and
/// is left parked while there is none.</summary>
public enum WorkflowMode
{
    /// <summary>One run at a time, across every replica: a trigger that finds one running is skipped.</summary>
    Single = 0,

    /// <summary>One run at a time, and the newest trigger wins: it cancels the run in flight, here and on peers,
    /// and waits for it to let go. Of a burst, only the last starts.</summary>
    Restart = 1,

    /// <summary>Up to <c>MaxConcurrentRuns</c> at once; a trigger that finds them all taken waits for a place, up to
    /// <c>MaxQueuedRuns</c> waiting.</summary>
    /// <remarks>The queue is deliberately in memory and per replica. A waiting trigger is a caller awaiting its
    /// run on this process: nothing about it is written down, so a crash or a stop loses it and nothing replays
    /// it, and a peer's queue is not this one's — a fleet of N replicas holds up to N × <c>MaxQueuedRuns</c>
    /// waiters, each asked for a cluster place only once it reaches the front and refused as <c>OverflowBehavior</c>
    /// says if there is none then. Waiters are woken in the order the semaphore wakes them, which is not a promise
    /// of arrival order. A limit lowered under a full queue keeps the waiters it has and refuses new ones until the
    /// queue is under it. A trigger that must not be lost belongs in something durable before it reaches the
    /// engine — an outbox, a broker — not in this queue.</remarks>
    Queued = 2,

    /// <summary>Up to <c>MaxConcurrentRuns</c> at once; a trigger that finds them all taken is refused as
    /// <c>OverflowBehavior</c> says.</summary>
    Parallel = 3
}

public enum WorkflowOverflowBehavior
{
    Drop = 0,
    Log = 1,
    Error = 2
}

public sealed class WorkflowRetryPolicy
{
    public int MaxAttempts { get; set; }
    public string BackoffStrategy { get; set; } = "fixed";
    public int InitialDelayMs { get; set; }
}

public sealed class Workflow
{
    private Workflow()
    {
    }

    public Workflow(Guid id, string name, string? description,
        Guid createdByUserId, DateTimeOffset createdAtUtc, IReadOnlyDictionary<string, string>? hostMetadata = null,
        int? executionTimeoutSeconds = null, string? retryPolicyJson = null)
    {
        WorkflowRowSettings.Require(executionTimeoutSeconds, retryPolicyJson);
        WorkflowHostMetadata.Require(hostMetadata);
        Id = id == Guid.Empty ? throw new ArgumentException("Workflow id is required.", nameof(id)) : id;
        Name = NormalizeRequired(name, nameof(name));
        Description = NormalizeOptional(description);
        HostMetadata = WorkflowHostMetadata.Normalize(hostMetadata);
        ExecutionTimeoutSeconds = executionTimeoutSeconds;
        RetryPolicyJson = NormalizeOptional(retryPolicyJson);
        CreatedByUserId = createdByUserId == Guid.Empty ? throw new ArgumentException("Creator user id is required.", nameof(createdByUserId)) : createdByUserId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    /// <summary>Entries the host keeps on the row (<see cref="WorkflowHostMetadata"/>), which the library never reads.
    /// Null is none; an empty map is stored as null.</summary>
    public IReadOnlyDictionary<string, string>? HostMetadata { get; private set; }

    public Guid? CurrentVersionId { get; private set; }

    public WorkflowVersion? CurrentVersion { get; private set; }

    public Guid? DraftVersionId { get; private set; }

    /// <summary>Moved by one, in or before its first write to this workflow's rows, by every library write to the
    /// workflow: each update (whatever it changes, the name or the settings alone included), a publish, and a rollback;
    /// an update that also publishes moves it once more as it promotes. A caller that
    /// read it can so tell whether anything moved since, and an update that names it is refused when it moved.</summary>
    /// <remarks>A create starts it at 0 and a delete removes the row. Version retention does not move it: it deletes
    /// only versions that no workflow names current or draft in the statement that deletes them. Nothing sets it through
    /// this object: the library moves it with one statement in the database, and a host that writes pointers or
    /// documents directly moves it the same way (see docs/operations/upgrading.md).</remarks>
    public int Revision { get; private set; }

    public WorkflowVersion? DraftVersion { get; private set; }

    public bool HasImportWarnings { get; private set; }

    public string? ImportWarningsJson { get; private set; }

    public int? ExecutionTimeoutSeconds { get; private set; }

    public string? RetryPolicyJson { get; private set; }

    /// <summary>How often this workflow's runs write their row, as JSON — see <c>WorkflowCheckpointPolicy</c>.
    /// Null is the engine's own <c>RunStateWriteMode</c>.</summary>
    /// <remarks>Read at the start of a run and again at a resume, never mid-run: a run carries the mode it wrote
    /// under in every checkpoint it wrote, so a policy edited while it is in flight cannot change how it is
    /// re-entered. A policy that will not read, or that the host's settings do not allow, is the engine's mode.</remarks>
    public string? CheckpointPolicyJson { get; private set; }

    public WorkflowMode Mode { get; private set; } = WorkflowMode.Single;

    public int MaxConcurrentRuns { get; private set; } = 1;

    public int MaxQueuedRuns { get; private set; } = 10;

    public WorkflowOverflowBehavior OverflowBehavior { get; private set; } = WorkflowOverflowBehavior.Drop;

    /// <summary>Per-workflow poll interval (seconds) for wait_template blocks referencing a non-helper
    /// value. Defaults to the global setting (5s) when null; minimum enforced value is 5s.</summary>
    public int? WaitPollIntervalSeconds { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void SetImportWarnings(string? warningsJson)
    {
        HasImportWarnings = warningsJson != null;
        ImportWarningsJson = warningsJson;
    }

    public void Update(string name, string? description, DateTimeOffset updatedAtUtc,
        IReadOnlyDictionary<string, string>? hostMetadata = null, int? executionTimeoutSeconds = null, string? retryPolicyJson = null)
    {
        WorkflowRowSettings.Require(executionTimeoutSeconds, retryPolicyJson);
        WorkflowHostMetadata.Require(hostMetadata);
        Name = NormalizeRequired(name, nameof(name));
        Description = NormalizeOptional(description);
        HostMetadata = WorkflowHostMetadata.Normalize(hostMetadata);
        ExecutionTimeoutSeconds = executionTimeoutSeconds;
        RetryPolicyJson = NormalizeOptional(retryPolicyJson);
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Configures the per-workflow concurrency policy; validates positive permit count and
    /// non-negative queue to keep the engine's coordinator invariants intact.</summary>
    public void ConfigureConcurrency(WorkflowMode mode, int maxConcurrentRuns, int maxQueuedRuns,
        WorkflowOverflowBehavior overflowBehavior, DateTimeOffset updatedAtUtc)
    {
        if (maxConcurrentRuns < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentRuns), "MaxConcurrentRuns must be >= 1.");
        if (maxQueuedRuns < 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedRuns), "MaxQueuedRuns must be >= 0.");

        Mode = mode;
        MaxConcurrentRuns = maxConcurrentRuns;
        MaxQueuedRuns = maxQueuedRuns;
        OverflowBehavior = overflowBehavior;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void SetCurrentVersion(WorkflowVersion? version, DateTimeOffset updatedAtUtc)
    {
        CurrentVersion = version;
        CurrentVersionId = version?.Id;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void SetDraftVersion(WorkflowVersion? version, DateTimeOffset updatedAtUtc)
    {
        DraftVersion = version;
        DraftVersionId = version?.Id;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Sets how often this workflow's runs write their row. Null is the engine's mode. The JSON is not
    /// parsed here: what a policy is allowed to say depends on the host's own settings, which this object cannot
    /// see, and the Editor's save path is where that is refused.</summary>
    public void SetCheckpointPolicyJson(string? checkpointPolicyJson, DateTimeOffset updatedAtUtc)
    {
        CheckpointPolicyJson = NormalizeOptional(checkpointPolicyJson);
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Sets the per-workflow wait poll interval. Minimum 5 seconds.</summary>
    public void SetWaitPollIntervalSeconds(int? seconds, DateTimeOffset updatedAtUtc)
    {
        if (seconds.HasValue && seconds.Value < 5)
            throw new ArgumentOutOfRangeException(nameof(seconds), "WaitPollIntervalSeconds must be >= 5.");
        WaitPollIntervalSeconds = seconds;
        UpdatedAtUtc = updatedAtUtc;
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value is required.", paramName)
            : value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed class WorkflowVersion
{
    private WorkflowVersion()
    {
    }

    public WorkflowVersion(Guid id, Guid workflowId, int versionNumber, string definitionJson,
        string? inputParametersJson, string? outputParametersJson, Guid createdByUserId,
        DateTimeOffset createdAtUtc, string? changeMessage = null, bool isPublished = false,
        DateTimeOffset? publishedAtUtc = null)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Version id is required.", nameof(id)) : id;
        WorkflowId = workflowId == Guid.Empty ? throw new ArgumentException("Workflow id is required.", nameof(workflowId)) : workflowId;
        VersionNumber = versionNumber;
        DefinitionJson = definitionJson ?? throw new ArgumentNullException(nameof(definitionJson));
        RefuseUnreadableParameterLists(inputParametersJson, outputParametersJson);
        InputParametersJson = inputParametersJson;
        OutputParametersJson = outputParametersJson;
        CreatedByUserId = createdByUserId == Guid.Empty ? throw new ArgumentException("Creator user id is required.", nameof(createdByUserId)) : createdByUserId;
        CreatedAtUtc = createdAtUtc;
        ChangeMessage = changeMessage;
        IsPublished = isPublished;
        PublishedAtUtc = publishedAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid WorkflowId { get; private set; }

    public Workflow Workflow { get; private set; } = null!;

    public int VersionNumber { get; private set; }

    public string DefinitionJson { get; private set; } = null!;

    public string? InputParametersJson { get; private set; }

    public string? OutputParametersJson { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public string? ChangeMessage { get; private set; }

    public bool IsPublished { get; private set; }

    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public void Publish(DateTimeOffset publishedAtUtc)
    {
        IsPublished = true;
        PublishedAtUtc = publishedAtUtc;
    }

    public void UpdateDefinition(string definitionJson, string? inputParametersJson, string? outputParametersJson)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        RefuseUnreadableParameterLists(inputParametersJson, outputParametersJson);
        DefinitionJson = definitionJson;
        InputParametersJson = inputParametersJson;
        OutputParametersJson = outputParametersJson;
    }

    private static void RefuseUnreadableParameterLists(string? inputParametersJson, string? outputParametersJson)
    {
        if (WorkflowRowSettings.ParameterListRefusal(inputParametersJson, input: true) is { } inputRefusal)
            throw new ArgumentException(inputRefusal, nameof(inputParametersJson));
        if (WorkflowRowSettings.ParameterListRefusal(outputParametersJson, input: false) is { } outputRefusal)
            throw new ArgumentException(outputRefusal, nameof(outputParametersJson));
    }
}

public sealed class WorkflowExecutionLog
{
    private readonly List<WorkflowExecutionLogEntry> entries = [];
    private readonly List<WorkflowExecutionStep> steps = [];

    private WorkflowExecutionLog()
    {
    }

    public WorkflowExecutionLog(Guid id, Guid? automationId, Guid workflowId, Guid workflowVersionId,
        Guid? triggerId, WorkflowExecutionStatus status, DateTimeOffset startedAtUtc, int attemptNumber = 1)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Execution log id is required.", nameof(id)) : id;
        AutomationId = automationId;
        WorkflowId = workflowId == Guid.Empty ? throw new ArgumentException("Workflow id is required.", nameof(workflowId)) : workflowId;
        WorkflowVersionId = workflowVersionId == Guid.Empty ? throw new ArgumentException("Workflow version id is required.", nameof(workflowVersionId)) : workflowVersionId;
        TriggerId = triggerId;
        Status = status;
        StartedAtUtc = startedAtUtc;
        AttemptNumber = Math.Max(1, attemptNumber);
    }

    public Guid Id { get; private set; }

    public Guid? AutomationId { get; private set; }

    public Guid WorkflowId { get; private set; }

    public Workflow Workflow { get; private set; } = null!;

    public Guid WorkflowVersionId { get; private set; }

    public WorkflowVersion WorkflowVersion { get; private set; } = null!;

    public Guid? TriggerId { get; private set; }

    public WorkflowExecutionStatus Status { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? ErrorMessage { get; private set; }

    public int AttemptNumber { get; private set; }

    public string? OutputsJson { get; private set; }

    internal string? ResultOutputsJson { get; private set; }

    /// <summary>Why the run ended as it did, where <see cref="Status"/> alone does not say: one of
    /// <see cref="WorkflowFailureReasons"/>, or null.</summary>
    public string? FailureReason { get; private set; }

    public IReadOnlyCollection<WorkflowExecutionLogEntry> Entries => entries;

    public IReadOnlyCollection<WorkflowExecutionStep> Steps => steps;

    /// <summary>Says why the replica writing this entry stopped, leaving the entry open for the one that carries the
    /// run on under it. The ending that replica writes replaces it.</summary>
    public void NoteHandedOver(string message) => ErrorMessage = message;

    /// <summary>Ends, as <see cref="WorkflowExecutionStatus.Failed"/>, a run admission refused before its first node,
    /// and says why.</summary>
    /// <param name="failureReason">One of <see cref="WorkflowFailureReasons"/>.</param>
    /// <param name="completedAtUtc">When it was refused.</param>
    /// <param name="errorMessage">What the refusal said.</param>
    public void EndRefused(string failureReason, DateTimeOffset completedAtUtc, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        Complete(WorkflowExecutionStatus.Failed, completedAtUtc, errorMessage);
        FailureReason = failureReason;
    }

    public void Complete(WorkflowExecutionStatus status, DateTimeOffset completedAtUtc, string? errorMessage = null, string? outputsJson = null)
    {
        Status = status;
        CompletedAtUtc = completedAtUtc;
        ErrorMessage = errorMessage;
        if (outputsJson != null)
            OutputsJson = outputsJson;
    }

    internal void RecordResultForParent(string? json) => ResultOutputsJson = json;
}

public sealed class WorkflowExecutionStep
{
    private WorkflowExecutionStep()
    {
    }

    public WorkflowExecutionStep(Guid id, Guid executionLogId, string nodeId, string nodeType,
        DateTimeOffset startedAt, DateTimeOffset endedAt, long durationMs,
        WorkflowExecutionStepStatus status, int sortOrder,
        string? inputsJson, string? outputsJson,
        string? errorMessage, string? errorStackTrace, string? errorJson = null)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Step id is required.", nameof(id)) : id;
        ExecutionLogId = executionLogId == Guid.Empty ? throw new ArgumentException("Execution log id is required.", nameof(executionLogId)) : executionLogId;
        NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        NodeType = nodeType ?? throw new ArgumentNullException(nameof(nodeType));
        StartedAt = startedAt;
        EndedAt = endedAt;
        DurationMs = durationMs;
        Status = status;
        SortOrder = sortOrder;
        InputsJson = inputsJson;
        OutputsJson = outputsJson;
        ErrorMessage = errorMessage;
        ErrorStackTrace = errorStackTrace;
        ErrorJson = errorJson;
    }

    public Guid Id { get; private set; }

    public Guid ExecutionLogId { get; private set; }

    public WorkflowExecutionLog ExecutionLog { get; private set; } = null!;

    public string NodeId { get; private set; } = null!;

    public string NodeType { get; private set; } = null!;

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset EndedAt { get; private set; }

    public long DurationMs { get; private set; }

    public WorkflowExecutionStepStatus Status { get; private set; }

    public int SortOrder { get; private set; }

    public string? InputsJson { get; private set; }

    public string? OutputsJson { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? ErrorStackTrace { get; private set; }

    public string? ErrorJson { get; private set; }
}

public sealed class WorkflowExecutionLogEntry
{
    private WorkflowExecutionLogEntry()
    {
    }

    public WorkflowExecutionLogEntry(Guid id, Guid executionLogId, string nodeId, string blockTypeId,
        LogEntryLevel level, string message, string? dataJson, int? iterationIndex,
        string? callStackJson, DateTimeOffset timestamp)
    {
        Id = id == Guid.Empty ? throw new ArgumentException("Log entry id is required.", nameof(id)) : id;
        ExecutionLogId = executionLogId == Guid.Empty ? throw new ArgumentException("Execution log id is required.", nameof(executionLogId)) : executionLogId;
        NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
        BlockTypeId = blockTypeId ?? throw new ArgumentNullException(nameof(blockTypeId));
        Level = level;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        DataJson = dataJson;
        IterationIndex = iterationIndex;
        CallStackJson = callStackJson;
        Timestamp = timestamp;
    }

    public Guid Id { get; private set; }

    public Guid ExecutionLogId { get; private set; }

    public WorkflowExecutionLog ExecutionLog { get; private set; } = null!;

    public string NodeId { get; private set; } = null!;

    public string BlockTypeId { get; private set; } = null!;

    public LogEntryLevel Level { get; private set; }

    public string Message { get; private set; } = null!;

    public string? DataJson { get; private set; }

    public int? IterationIndex { get; private set; }

    public string? CallStackJson { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }
}

public enum WorkflowSuspensionKind
{
    WaitForTrigger = 0,
    WaitTemplate = 1,

    /// <summary>A durable-sleep block parked on the root canvas; its timeout is its wake time.</summary>
    Sleep = 2,

    /// <summary>A run parked on a run it started: its config names that run's execution-log id, and it resumes
    /// when that run reaches an ending.</summary>
    SubWorkflow = 3,

    /// <summary>A host's wait on a node's <c>Expression&lt;Boolean&gt;</c> input (<see cref="Execution.WorkflowPredicateWaitConfig"/>):
    /// evaluated only when the host wakes the row with the input's scope variables.</summary>
    Predicate = 4
}

public enum WorkflowSuspensionStatus
{
    Suspended = 0,
    Resumed = 1,
    TimedOut = 2,
    Cancelled = 3
}

/// <summary>Persists a workflow run paused on a wait_for_trigger/wait_template block. Transient runtime
/// state — deliberately excluded from config export and treated as operational, not configuration.</summary>
public sealed class WorkflowSuspension
{
    private WorkflowSuspension() { }

    public WorkflowSuspension(
        Guid id,
        Guid executionLogId,
        Guid workflowId,
        string blockId,
        WorkflowSuspensionKind kind,
        string configJson,
        string statePayloadJson,
        DateTimeOffset suspendedAtUtc,
        DateTimeOffset timeoutAtUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id is required.", nameof(id));
        if (executionLogId == Guid.Empty) throw new ArgumentException("ExecutionLogId is required.", nameof(executionLogId));
        if (workflowId == Guid.Empty) throw new ArgumentException("WorkflowId is required.", nameof(workflowId));
        if (string.IsNullOrWhiteSpace(blockId)) throw new ArgumentException("BlockId is required.", nameof(blockId));
        if (string.IsNullOrWhiteSpace(configJson)) throw new ArgumentException("ConfigJson is required.", nameof(configJson));
        if (string.IsNullOrWhiteSpace(statePayloadJson)) throw new ArgumentException("StatePayloadJson is required.", nameof(statePayloadJson));

        Id = id;
        ExecutionLogId = executionLogId;
        WorkflowId = workflowId;
        BlockId = blockId.Trim();
        Kind = kind;
        Status = WorkflowSuspensionStatus.Suspended;
        ConfigJson = configJson;
        StatePayloadJson = statePayloadJson;
        SuspendedAtUtc = suspendedAtUtc;
        TimeoutAtUtc = timeoutAtUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>FK to <see cref="WorkflowExecutionLog"/>.</summary>
    public Guid ExecutionLogId { get; private set; }

    public WorkflowExecutionLog ExecutionLog { get; private set; } = null!;

    /// <summary>FK to <see cref="Workflow"/> — denormalised for fast index scans.</summary>
    public Guid WorkflowId { get; private set; }

    public Workflow Workflow { get; private set; } = null!;

    /// <summary>The workflow node id of the suspended block.</summary>
    public string BlockId { get; private set; } = null!;

    public WorkflowSuspensionKind Kind { get; private set; }

    public WorkflowSuspensionStatus Status { get; private set; }

    /// <summary>Serialised block-specific config (trigger ids or expression string).</summary>
    public string ConfigJson { get; private set; } = null!;

    /// <summary>Serialised run state: workflow variables, previous-node outputs, trigger envelope.
    /// Capped at 256 KiB by the service layer.</summary>
    public string StatePayloadJson { get; private set; } = null!;

    /// <summary>When the run was suspended.</summary>
    public DateTimeOffset SuspendedAtUtc { get; private set; }

    /// <summary>When the suspension times out if not satisfied first.</summary>
    public DateTimeOffset TimeoutAtUtc { get; private set; }

    /// <summary>When the suspension was resolved (any terminal state).</summary>
    public DateTimeOffset? ResolvedAtUtc { get; private set; }

    /// <summary>Outcome written back when the row is resolved: "satisfied" or "timed_out".</summary>
    public string? OutcomeJson { get; private set; }

    /// <summary>How many times a waker has taken the row, counting a takeover of a take whose taker died. The
    /// version a takeover is fenced on, so two replicas taking over one abandoned take move it once.</summary>
    public int TakeCount { get; private set; }
}

/// <summary>EF Core entity backing workflow_executions_state: transient execution state the engine
/// regenerates continuously. Deliberately excluded from config export — operational, not configuration.</summary>
public sealed class WorkflowExecutionState
{
    private WorkflowExecutionState() { }

    /// <summary>The nine-argument shape. Kept beside the one below rather than given
    /// a tenth optional parameter: an added parameter is a new method, and the old one stops existing.</summary>
    public WorkflowExecutionState(
        Guid executionId,
        Guid workflowId,
        int workflowVersion,
        string status,
        string variablesJson,
        string? triggerSource,
        string? ownedByReplicaId,
        DateTimeOffset startedAtUtc,
        string? pinnedVersionsJson = null)
        : this(executionId, workflowId, workflowVersion, status, variablesJson, triggerSource,
            ownedByReplicaId, startedAtUtc, pinnedVersionsJson, parentExecutionId: null)
    {
    }

    public WorkflowExecutionState(
        Guid executionId,
        Guid workflowId,
        int workflowVersion,
        string status,
        string variablesJson,
        string? triggerSource,
        string? ownedByReplicaId,
        DateTimeOffset startedAtUtc,
        string? pinnedVersionsJson,
        Guid? parentExecutionId)
    {
        ExecutionId = executionId;
        WorkflowId = workflowId;
        WorkflowVersion = workflowVersion;
        Status = status;
        CurrentBlockPath = string.Empty;
        VariablesJson = variablesJson;
        TriggerSource = triggerSource;
        OwnedByReplicaId = ownedByReplicaId;
        LastTransitionUtc = startedAtUtc;
        StartedAtUtc = startedAtUtc;
        Version = 1;
        PinnedVersionsJson = pinnedVersionsJson;
        ParentExecutionId = parentExecutionId;
    }

    public Guid ExecutionId { get; private set; }
    public Guid WorkflowId { get; private set; }
    public int WorkflowVersion { get; private set; }
    /// <summary>Status stored as string; see <c>WorkflowExecutionStateStatus</c> enum in Application layer.</summary>
    public string Status { get; private set; } = "Pending";
    public string CurrentBlockPath { get; private set; } = string.Empty;
    /// <summary>Canonical JSON; NEVER logged.</summary>
    public string VariablesJson { get; private set; } = "{}";
    public DateTimeOffset? WaitUntilUtc { get; private set; }
    public string? WaitForEventKey { get; private set; }
    public string? OwnedByReplicaId { get; private set; }
    public DateTimeOffset LastTransitionUtc { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }
    public string? TriggerSource { get; private set; }
    /// <summary>Optimistic-concurrency version; incremented on every successful CAS transition.</summary>
    public int Version { get; private set; }
    /// <summary>Serialised {workflowId → versionId} manifest pinned at root start; null for none.</summary>
    public string? PinnedVersionsJson { get; private set; }
    /// <summary>The run that dispatched this one, or null when nothing did — its journal id, not a key into
    /// this table. A sub-workflow's row is not a run anything may start on its own.</summary>
    public Guid? ParentExecutionId { get; private set; }

    public Workflow Workflow { get; private set; } = null!;
}
