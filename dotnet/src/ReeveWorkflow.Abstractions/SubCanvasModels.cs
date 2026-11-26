namespace ReeveWorkflow.Abstractions;

public sealed record SubCanvasResult(IReadOnlyDictionary<string, object?> Outputs, SubCanvasError? Error);

/// <summary>The error a sub-canvas call ended with. <paramref name="Code"/> is the code the failed node's <c>_error</c> carries
/// and <paramref name="Category"/> the category of an expression failure, null where none applies.</summary>
public sealed record SubCanvasError(
    string Message, string ExceptionType, string? FailedNodeId, string? FailedBlockTypeId,
    string Code = WorkflowErrorCodes.BlockFailed, string? Category = null);
