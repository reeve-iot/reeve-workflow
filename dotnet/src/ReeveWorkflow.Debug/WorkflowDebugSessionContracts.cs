using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Debug;

public sealed record WorkflowDebugSessionResponse(
    Guid Id,
    Guid WorkflowId,
    Guid? WorkflowExecutionLogId,
    Guid OwnerUserId,
    string BreakpointsJson,
    string? CurrentPauseNodeId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityUtc
);

public sealed record WorkflowDebugSnapshotResponse(
    Guid Id,
    Guid DebugSessionId,
    string NodeId,
    DateTimeOffset CapturedAtUtc,
    string InputsJson,
    string? OutputsJson,
    string VariablesJson,
    string ExpressionEvalsJson
);

/// <summary>Stored in core:workflow-debug under key {sessionId}; holds the mutable fields not
/// needed for retention sweeps.</summary>
public sealed record WorkflowDebugSessionBlob(
    string BreakpointsJson,
    string? CurrentPauseNodeId
);

/// <summary>Stored in core:workflow-debug under key {sessionId}/snapshots/{snapshotId}; the
/// captured engine state at a breakpoint pause.</summary>
public sealed record WorkflowDebugSnapshotBlob(
    string InputsJson,
    string? OutputsJson,
    string VariablesJson,
    string ExpressionEvalsJson
);

/// <summary>Optional body for POST /api/workflows/{workflowId}/debug-sessions.</summary>
public sealed record CreateDebugSessionRequest(
    IReadOnlyList<string>? BreakpointNodeIds = null
);

public sealed record SetBreakpointsRequest(
    Guid SessionId,
    IReadOnlyList<string> NodeIds
);

public static class WorkflowDebugSessionMapper
{
    /// <summary>Maps a thin session row + its object-storage blob to a response DTO.</summary>
    public static WorkflowDebugSessionResponse ToResponse(
        WorkflowDebugSession session,
        WorkflowDebugSessionBlob? blob) =>
        new(
            session.Id,
            session.WorkflowId,
            session.WorkflowExecutionLogId,
            session.OwnerUserId,
            blob?.BreakpointsJson ?? "[]",
            blob?.CurrentPauseNodeId,
            session.Status.ToString(),
            session.CreatedAtUtc,
            session.LastActivityUtc
        );

    /// <summary>Maps a thin snapshot row + its object-storage blob to a response DTO.</summary>
    public static WorkflowDebugSnapshotResponse ToResponse(
        WorkflowDebugSnapshot snapshot,
        WorkflowDebugSnapshotBlob? blob) =>
        new(
            snapshot.Id,
            snapshot.DebugSessionId,
            snapshot.NodeId,
            snapshot.CapturedAtUtc,
            blob?.InputsJson ?? "{}",
            blob?.OutputsJson,
            blob?.VariablesJson ?? "{}",
            blob?.ExpressionEvalsJson ?? "{}"
        );
}
