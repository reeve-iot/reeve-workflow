using System.Collections.Immutable;
using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal sealed class InlineSubCanvasExecutor(
    WorkflowGraphExecutor engine,
    CompiledNode node,
    Guid workflowId,
    Guid executionId,
    Guid userId,
    IServiceProvider services,
    List<WorkflowExecutionLogEntry> logEntries,
    List<WorkflowExecutionStep> executionSteps,
    WorkflowTraceMode traceMode,
    bool debugMode,
    int? iterationIndex,
    string? callStackJson,
    int depth,
    HashSet<string> visitedNodeIds,
    bool isInsideTryCatch,
    IWorkflowDebugHook? debugHook = null,
    DebugPausedCounter? debugCounter = null,
    Guid? stateExecutionId = null,
    bool mayParkSleep = false) : ISubCanvasExecutor
{
    internal required CompiledWalk Compiled { get; init; }

    private readonly Dictionary<string, ImmutableList<WorkflowCompletedSubCanvasCall>> completed = new(StringComparer.Ordinal);

    private readonly Lock recordGate = new();

    private bool unrecordable;

    private bool reentered;

    private int inFlight;

    private SuspendedWorkflowException? parking;

    public Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        string subCanvasName,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken cancellationToken)
    {
        return ExecuteInternalAsync(subCanvasName, inputs, isInsideTryCatch, reportsErrors: false, cancellationToken);
    }

    public async Task<SubCanvasResult> TryExecuteAsync(
        string subCanvasName,
        IReadOnlyDictionary<string, object?> inputs,
        CancellationToken cancellationToken)
    {
        try
        {
            var outputs = await ExecuteInternalAsync(subCanvasName, inputs, true, reportsErrors: true, cancellationToken).ConfigureAwait(false);
            return new SubCanvasResult(outputs, null);
        }
        catch (ReplayedFailure replayed)
        {
            Record(subCanvasName, new SubCanvasResult(new Dictionary<string, object?>(), replayed.Error));
            return new SubCanvasResult(new Dictionary<string, object?>(), replayed.Error);
        }
        catch (Exception ex) when (WorkflowGraphExecutor.EscapesErrorPolicy(ex))
        {
            throw;
        }
        catch (WorkflowNodeExecutionException ex)
        {
            var failure = WorkflowFailureCoding.OfNode(ex);
            return Reported(subCanvasName, new SubCanvasError(
                ex.Message, ex.InnerException?.GetType().Name ?? ex.GetType().Name, ex.NodeId, ex.BlockTypeId,
                failure.Code, failure.Category));
        }
        catch (Exception ex)
        {
            var failure = WorkflowFailureCoding.Of(ex);
            return Reported(subCanvasName, new SubCanvasError(
                ex.Message, ex.GetType().Name, null, null, failure.Code, failure.Category));
        }
    }

    private SubCanvasResult Reported(string subCanvasName, SubCanvasError error)
    {
        var result = new SubCanvasResult(new Dictionary<string, object?>(), error);
        Record(subCanvasName, result);
        return result;
    }

    private void Record(string subCanvasName, SubCanvasResult result)
    {
        string outputsJson;
        try
        {
            outputsJson = PersistedValueCodec.WriteAnswer(result.Outputs, CallTypes(subCanvasName));
        }
        catch (Exception ex) when (Expressions.ExpressionJsonValue.Unwritable(ex))
        {
            lock (recordGate)
                unrecordable = true;
            return;
        }

        lock (recordGate)
        {
            var call = new WorkflowCompletedSubCanvasCall
            {
                OutputsJson = outputsJson,
                Error = result.Error is { } error ? WorkflowCompletedSubCanvasError.Of(error) : null
            };
            completed[subCanvasName] = completed.TryGetValue(subCanvasName, out var calls)
                ? calls.Add(call)
                : ImmutableList.Create(call);
        }
    }

    private IReadOnlyDictionary<string, ClosedPortType?> CallTypes(string subCanvasName)
    {
        var called = Compiled.Child(node.NodeId, subCanvasName)
            ?? throw new InvalidOperationException($"Sub-canvas '{subCanvasName}' not found on node '{node.NodeId}'.");
        var container = Compiled.Node(node.NodeId)
            ?? throw new InvalidOperationException($"Node '{node.NodeId}' is absent from the compiled program.");
        return PersistedValueCodec.CallTypes(container, subCanvasName, called.Canvas);
    }

    private WorkflowRunContinuationScope.Armed? FrameForThisNode() =>
        WorkflowRunContinuationScope.Current is { Frames.Count: > 0 } armed
        && armed.ExecutionId == executionId && armed.NodeId == node.NodeId
            ? armed
            : null;

    private IReadOnlyDictionary<string, object?> Replayed(
        string subCanvasName, WorkflowCompletedSubCanvasCall call, bool reportsErrors)
    {
        if (call.Error is { } error)
        {
            var reported = error.Reported();
            if (!reportsErrors)
            {
                throw new WorkflowContinuationDivergedException(node.NodeId,
                    $"'{subCanvasName}' without being told about failures, where the checkpoint records a failure");
            }
            throw new ReplayedFailure(reported);
        }

        var outputs = PersistedValueCodec.ReadAnswer(call.OutputsJson, CallTypes(subCanvasName), "call", node.NodeId);
        Record(subCanvasName, new SubCanvasResult(outputs, null));
        return outputs;
    }

    private IReadOnlyDictionary<string, IReadOnlyList<WorkflowCompletedSubCanvasCall>>? Snapshot()
    {
        lock (recordGate)
        {
            return unrecordable
                ? null
                : completed.ToDictionary(
                    kv => kv.Key, kv => (IReadOnlyList<WorkflowCompletedSubCanvasCall>)kv.Value,
                    StringComparer.Ordinal);
        }
    }

    private sealed class ReplayedFailure(SubCanvasError error) : Exception(error.Message)
    {
        public SubCanvasError Error { get; } = error;
    }

    private async Task<IReadOnlyDictionary<string, object?>> ExecuteInternalAsync(
        string subCanvasName,
        IReadOnlyDictionary<string, object?> inputs,
        bool currentIsInsideTryCatch,
        bool reportsErrors,
        CancellationToken cancellationToken)
    {
        if (parking is not null)
            throw parking;

        if (Interlocked.Increment(ref inFlight) > 1 && engine.ContainerOnlyDrives(node))
        {
            Interlocked.Decrement(ref inFlight);
            throw new SubCanvasRethrowException(
                $"Block '{node.NodeId}' ({node.Shape.BlockTypeId}) ran two of its sub-canvases at once and declares that it only "
                + "drives them, which a recovery reads as one call after another. A container that runs them at once "
                + "must not declare it.");
        }

        try
        {
            return await RunOneCallAsync(subCanvasName, inputs, currentIsInsideTryCatch, reportsErrors, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    private async Task<IReadOnlyDictionary<string, object?>> RunOneCallAsync(
        string subCanvasName,
        IReadOnlyDictionary<string, object?> inputs,
        bool currentIsInsideTryCatch,
        bool reportsErrors,
        CancellationToken cancellationToken)
    {
        if (visitedNodeIds.Contains(node.NodeId))
            throw new InvalidOperationException($"Circular reference detected at node '{node.NodeId}'. Sub-canvas execution aborted.");

        var body = Compiled.Child(node.NodeId, subCanvasName)
            ?? throw new InvalidOperationException($"Sub-canvas '{subCanvasName}' not found on node '{node.NodeId}'.");
        inputs = ValueGate.SubCanvasInputs(node.NodeId, subCanvasName, body.Canvas, inputs);

        var seedOutputs = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        foreach (var subNode in body.Canvas.Nodes.Where(n => n.Shape.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId))
            seedOutputs[subNode.NodeId] = inputs;

        var armedHere = reentered ? null : FrameForThisNode();
        var frame = armedHere?.Frames[0];
        if (frame is not null)
        {
            var done = frame.CompletedBySubCanvas.TryGetValue(subCanvasName, out var calls) ? calls : [];
            int ordinal;
            lock (recordGate)
                ordinal = completed.TryGetValue(subCanvasName, out var mine) ? mine.Count : 0;
            if (ordinal < done.Count)
                return Replayed(subCanvasName, done[ordinal], reportsErrors);

            if (subCanvasName != frame.SubCanvas)
            {
                throw new WorkflowContinuationDivergedException(node.NodeId,
                    $"'{subCanvasName}' where the checkpoint records it was inside '{frame.SubCanvas}'");
            }

            reentered = true;
            armedHere!.Consumed = true;
            foreach (var (nodeId, outputs) in PersistedValueCodec.ReadNodeOutputs(frame.PreviousOutputsJson, body.Canvas, "frame"))
                seedOutputs[nodeId] = outputs;
        }

        var captured = WorkflowRunFrames.Swap([
            .. WorkflowRunFrames.Current,
            new WorkflowRunFrames.LiveFrame(subCanvasName, body, Snapshot(), engine.ContainerOnlyDrives(node), node.NodeId)
        ]);
        var armed = WorkflowRunContinuationScope.Swap(
            frame is null || WorkflowRunContinuationScope.Current is not { Frames.Count: > 1 } deeper
                ? null
                : new WorkflowRunContinuationScope.Armed(executionId, frame.NodeInFlight, [.. deeper.Frames.Skip(1)]));
        Dictionary<string, IReadOnlyDictionary<string, object?>> allOutputs;
        try
        {
            allOutputs = await engine.ExecuteGraphAsync(
                seedOutputs, workflowId, executionId, userId,
                services, logEntries, executionSteps, traceMode, debugMode, iterationIndex, callStackJson,
                depth + 1, [.. visitedNodeIds, node.NodeId], currentIsInsideTryCatch,
                inputs, triggerIdForRun: null, triggerEnvelopeForRun: null, cancellationToken,
                body, debugHook, debugCounter, stateExecutionId, mayParkSleep,
                parameterSignature: BodyParameterSignature(body.Canvas.Signature, node.Passthrough)).ConfigureAwait(false);
        }
        catch (SuspendedWorkflowException park)
        {
            parking = park;
            throw;
        }
        finally
        {
            WorkflowRunFrames.Swap(captured);
            WorkflowRunContinuationScope.Swap(armed);
        }

        var outputNodes = body.Canvas.OutputNodeOrder.Select(body.Required).ToList();
        if (outputNodes.Count == 0)
            return Returned(subCanvasName, new Dictionary<string, object?>());

        var result = new Dictionary<string, object?>();
        foreach (var outputNode in outputNodes)
        {
            var resolved = new Dictionary<string, object?>();
            foreach (var edge in outputNode.Incoming)
                WorkflowExecutionHelpers.ApplyMappings(resolved, edge.SourceNodeId, edge.Mappings, allOutputs, outputNode);
            foreach (var kvp in resolved)
                result[kvp.Key] = kvp.Value;
        }

        if (node.OutputPorts.Count > 0)
        {
            foreach (var port in node.OutputPorts)
            {
                if (port.Sources != null
                    && port.Sources.TryGetValue(subCanvasName, out var srcKey)
                    && result.TryGetValue(srcKey, out var value))
                {
                    result[port.Name] = value;
                }
            }
        }

        return Returned(subCanvasName, result);
    }

    private static CanvasSignature BodyParameterSignature(CanvasSignature signature, IReadOnlyList<string> passthrough)
    {
        var names = signature.Inputs.Select(port => port.Name)
            .Concat(signature.UntypedInputs.Select(port => port.Name))
            .ToHashSet(StringComparer.Ordinal);
        var missing = passthrough.Where(names.Add).Select(name => new UntypedPort(name, false)).ToArray();
        return missing.Length == 0
            ? signature
            : new CanvasSignature(signature.Inputs, signature.Outputs)
            {
                UntypedInputs = [.. signature.UntypedInputs, .. missing],
                UntypedOutputs = signature.UntypedOutputs,
            };
    }

    private IReadOnlyDictionary<string, object?> Returned(
        string subCanvasName, IReadOnlyDictionary<string, object?> outputs)
    {
        Record(subCanvasName, new SubCanvasResult(outputs, null));
        return outputs;
    }
}
