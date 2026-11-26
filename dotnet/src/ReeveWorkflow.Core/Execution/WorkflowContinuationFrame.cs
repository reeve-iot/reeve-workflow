using System.Text.Json;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Where a run was inside one container: which of its sub-canvases it was in, what the calls that finished
/// returned, what had run inside the call that had not, and the node it was about to enter.</summary>
/// <remarks>A checkpoint carries one of these per level of nesting, outermost first. The container a frame belongs
/// to is not in the frame: for the first it is the checkpoint's <see cref="WorkflowSuspensionStatePayload.NodeInFlight"/>,
/// and for the rest it is the previous frame's <see cref="NodeInFlight"/>. Said twice is said in two places that can
/// disagree.</remarks>
public sealed class WorkflowContinuationFrame
{
    /// <summary>The sub-canvas of this container the run was inside — the call in flight. Which invocation of it is
    /// <c>CompletedBySubCanvas[SubCanvas].Count</c>, for the same reason the container is not stored.</summary>
    public required string SubCanvas { get; init; }

    /// <summary>What each finished call to each of this container's sub-canvases returned, in the order it was made.
    /// A re-entry answers these from the record instead of running them.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<WorkflowCompletedSubCanvasCall>> CompletedBySubCanvas { get; init; }

    /// <summary>The outputs of the nodes already done inside the call in flight, in the shape
    /// <see cref="WorkflowSuspensionStatePayload.PreviousOutputsJson"/> has.</summary>
    public required string PreviousOutputsJson { get; init; }

    /// <summary>The node on this level the run was about to enter. The frame after this one, if there is one,
    /// describes the inside of that node.</summary>
    public required string NodeInFlight { get; init; }
}

/// <summary>One call to a sub-canvas that finished, as a re-entry replays it.</summary>
public sealed class WorkflowCompletedSubCanvasCall
{
    /// <summary>What the call returned, serialised as the parent block received it.</summary>
    public required string OutputsJson { get; init; }

    /// <summary>The error the call reported rather than threw, or null. Only a call made through
    /// <see cref="ReeveWorkflow.Abstractions.ISubCanvasExecutor.TryExecuteAsync"/> can have one: a call that threw
    /// took the run with it, so there was no checkpoint after it. Replaying it is what lets a try/catch choose the
    /// same branch again.</summary>
    public WorkflowCompletedSubCanvasError? Error { get; init; }
}

/// <summary>The error half of <see cref="WorkflowCompletedSubCanvasCall"/>, as
/// <see cref="ReeveWorkflow.Abstractions.SubCanvasError"/> reported it.</summary>
public sealed class WorkflowCompletedSubCanvasError
{
    public required string Message { get; init; }
    public required string ExceptionType { get; init; }
    public string? NodeId { get; init; }
    public string? BlockTypeId { get; init; }

    /// <summary>The code of the failure, as <see cref="ReeveWorkflow.Abstractions.SubCanvasError.Code"/> reported it. Null in a
    /// checkpoint written before the code was recorded, which a replay reads as <c>block-failed</c>.</summary>
    public string? Code { get; init; }

    /// <summary>The category of an expression failure, or null.</summary>
    public string? Category { get; init; }

    internal static WorkflowCompletedSubCanvasError Of(ReeveWorkflow.Abstractions.SubCanvasError error) => new()
    {
        Message = error.Message,
        ExceptionType = error.ExceptionType,
        NodeId = error.FailedNodeId,
        BlockTypeId = error.FailedBlockTypeId,
        Code = error.Code,
        Category = error.Category
    };

    internal ReeveWorkflow.Abstractions.SubCanvasError Reported() => new(
        Message, ExceptionType, NodeId, BlockTypeId, Code ?? ReeveWorkflow.Abstractions.WorkflowErrorCodes.BlockFailed, Category);
}

internal static class WorkflowContinuation
{
    public static IReadOnlyList<WorkflowContinuationFrame> Read(string? continuationJson)
    {
        if (string.IsNullOrWhiteSpace(continuationJson))
            return [];
        try
        {
            var frames = JsonSerializer.Deserialize<List<WorkflowContinuationFrame>>(
                continuationJson, WorkflowGraphHelpers.JsonOptions);
            return frames is null || frames.Any(frame => frame is null or { SubCanvas: null } or { NodeInFlight: null }
                    or { PreviousOutputsJson: null } or { CompletedBySubCanvas: null })
                ? []
                : frames;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    public static string? Write(IReadOnlyList<WorkflowContinuationFrame> frames) =>
        frames.Count == 0 ? null : JsonSerializer.Serialize(frames, WorkflowGraphHelpers.JsonOptions);

    public static IReadOnlyList<WorkflowContinuationFrame> WithResumedNode(
        IReadOnlyList<WorkflowContinuationFrame> frames, CompiledNode node, IReadOnlyDictionary<string, object?> outputs)
    {
        if (frames.Count == 0)
            return frames;

        var innermost = frames[^1];
        var done = string.IsNullOrWhiteSpace(innermost.PreviousOutputsJson)
            ? new System.Text.Json.Nodes.JsonObject()
            : System.Text.Json.Nodes.JsonNode.Parse(innermost.PreviousOutputsJson) as System.Text.Json.Nodes.JsonObject
                ?? throw new InvalidOperationException("The level the run was parked on holds no node outputs.");
        done[node.NodeId] = System.Text.Json.Nodes.JsonNode.Parse(PersistedValueCodec.WriteOutputs(outputs, node));
        var answered = frames.ToArray();
        answered[^1] = new WorkflowContinuationFrame
        {
            SubCanvas = innermost.SubCanvas,
            CompletedBySubCanvas = innermost.CompletedBySubCanvas,
            NodeInFlight = innermost.NodeInFlight,
            PreviousOutputsJson = done.ToJsonString(WorkflowGraphHelpers.JsonOptions)
        };
        return answered;
    }
}

/// <summary>What a replay found the container asking for that the record does not describe. The container promised,
/// through <see cref="ReeveWorkflow.Abstractions.IWorkflowBlockFactory.OnlyDrivesItsSubCanvases"/>, to make the same
/// calls again; it did not, so the run is failed rather than guessed at.</summary>
/// <remarks>Escapes the error policies, like every other exception that says the walk itself cannot go on: caught by
/// a try/catch, it would look like the container's body having failed.</remarks>
public sealed class WorkflowContinuationDivergedException(string nodeId, string message)
    : Exception($"Block '{nodeId}' re-entered from a checkpoint asked for {message}. "
        + "A container that declares it re-enters its sub-canvases must make the same calls again.")
{
    /// <summary>The container that diverged.</summary>
    public string NodeId { get; } = nodeId;
}

internal static class WorkflowRunFrames
{
    private static readonly AsyncLocal<IReadOnlyList<LiveFrame>?> current = new();

    public static IReadOnlyList<LiveFrame> Current => current.Value ?? [];

    public static IReadOnlyList<LiveFrame> Swap(IReadOnlyList<LiveFrame> frames)
    {
        var was = Current;
        current.Value = frames;
        return was;
    }

    internal sealed record LiveFrame(
        string SubCanvas,
        CompiledWalk Level,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowCompletedSubCanvasCall>>? Completed,
        bool ContainerOnlyDrives,
        string ContainerNodeId)
    {
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? PreviousOutputs { get; init; }

        public string? NodeInFlight { get; init; }
    }

    public static string? ContainerThatCannotBeReentered(IReadOnlyList<LiveFrame> frames) =>
        frames.FirstOrDefault(frame => !frame.ContainerOnlyDrives)?.ContainerNodeId;

    public static string? Checkpointed(IReadOnlyList<LiveFrame> frames, int maxBytes)
    {
        if (frames.Count == 0)
            return null;

        const int PerCall = 34;
        const int PerFrame = 128;

        var recorded = 0;
        foreach (var frame in frames)
        {
            if (frame.Completed is null)
                return null;

            recorded += PerFrame;
            foreach (var calls in frame.Completed.Values)
            {
                foreach (var call in calls)
                    recorded += call.OutputsJson.Length + PerCall;
            }

            if (recorded > maxBytes)
                return null;
        }

        var written = new List<WorkflowContinuationFrame>(frames.Count);
        foreach (var frame in frames)
        {
            if (!frame.ContainerOnlyDrives || frame.NodeInFlight is null || frame.PreviousOutputs is null
                || frame.Completed is null)
                return null;
            if (Written(frame.PreviousOutputs, frame.Level) is not { } previousOutputsJson)
                return null;
            written.Add(new WorkflowContinuationFrame
            {
                SubCanvas = frame.SubCanvas,
                CompletedBySubCanvas = frame.Completed,
                PreviousOutputsJson = previousOutputsJson,
                NodeInFlight = frame.NodeInFlight
            });
        }

        return WorkflowContinuation.Write(written);
    }

    private static string? Written(IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> outputs, CompiledWalk level)
    {
        try
        {
            return PersistedValueCodec.WriteNodeOutputs(outputs, level);
        }
        catch (Exception ex) when (Expressions.ExpressionJsonValue.Unwritable(ex))
        {
            return null;
        }
    }
}

internal static class WorkflowRunContinuationScope
{
    private static readonly AsyncLocal<Armed?> current = new();

    public static Armed? Current => current.Value;

    public static Armed? Swap(Armed? armed)
    {
        var was = current.Value;
        current.Value = armed;
        return was;
    }

    internal sealed record Armed(Guid ExecutionId, string NodeId, IReadOnlyList<WorkflowContinuationFrame> Frames)
    {
        public bool Consumed { get; set; }
    }
}
