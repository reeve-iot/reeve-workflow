using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Hosting;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

/// <summary>The graph walk: topological order, per-node retry and timeout, the error policies, sub-canvas
/// entry, and the log and step records a run leaves behind. What it cannot know arrives as four separate
/// things — the zone, the sandbox, the breadcrumb and the durable sleep — none of which implies the others.</summary>
public sealed class WorkflowGraphExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = WorkflowGraphHelpers.JsonOptions;

    private const string WorkflowInputBlockTypeId = WorkflowGraphHelpers.WorkflowInputBlockTypeId;
    private const string WorkflowOutputBlockTypeId = WorkflowGraphHelpers.WorkflowOutputBlockTypeId;
    private const int MaxCallDepth = 10;

    private const string NotifierLoggerCategory = "ReeveWorkflow.NodeObserver";

    private const string DefinitionLoggerCategory = "ReeveWorkflow.Definition";

    private const string BlockLoggerCategory = "Workflow.Block";

    internal const string ConfigTimeoutSeconds = "timeoutSeconds";
    internal const string ConfigRetryCount = "retryCount";
    internal const string ConfigRetryEnabled = "retryEnabled";
    internal const string ConfigRetryBackoffSeconds = "retryBackoffSeconds";
    private const int DefaultTimeoutSeconds = 30;
    private const int DefaultRetryCount = 3;
    private const double DefaultRetryBackoffSeconds = 1.0;

    internal static readonly IReadOnlySet<string> PollyConfigKeys = new HashSet<string>
    {
        ConfigTimeoutSeconds, ConfigRetryCount, ConfigRetryEnabled, ConfigRetryBackoffSeconds
    };

    internal const string DesignTimeConfigPrefix = "__";

    private static readonly IReadOnlyDictionary<HelperPath, object?> EmptyHelperValues = new Dictionary<HelperPath, object?>();

    private readonly IWorkflowBlocks _blocks;
    private readonly IWorkflowMetrics _metrics;
    private readonly IWorkflowNodeObserver _nodeObserver;
    private readonly IHelperResolver _helperResolver;
    private readonly IWorkflowBlockSandbox? _sandbox;
    private readonly WorkflowTransitionRecorder? _transitions;
    private readonly WorkflowSleepRunner? _sleepRunner;
    private readonly WorkflowGraphExecutorOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _notifierLogger;
    private readonly ILogger _definitionLogger;
    private readonly ILogger _blockLogger;
    private readonly WorkflowActivitySource _activitySource;

    private RunStateWriteMode WriteMode => WorkflowCheckpointPolicyScope.Current ?? _options.RunStateWriteMode;

    private static int HelperReadPosition(CompiledNode node)
    {
        foreach (var expression in node.Expressions.Values)
            if (HelperReadPositions(expression.Tree).FirstOrDefault(-1) is var position && position >= 0)
                return position;
        return 0;
    }

    private static IEnumerable<int> HelperReadPositions(TypedExpression tree)
    {
        switch (tree)
        {
            case TypedRead { Namespace: "helper" } read: yield return read.Position; break;
            case TypedConversion conversion:
                foreach (var position in HelperReadPositions(conversion.Operand)) yield return position;
                break;
            case TypedOperation operation:
                foreach (var child in operation.Operands)
                    foreach (var position in HelperReadPositions(child)) yield return position;
                break;
            case TypedProjection projection:
                foreach (var position in HelperReadPositions(projection.Subject)) yield return position;
                break;
        }
    }

    public WorkflowGraphExecutor(
        IWorkflowBlocks blocks,
        ILoggerFactory loggerFactory,
        IWorkflowMetrics metrics,
        IWorkflowNodeObserver nodeObserver,
        IHelperResolver helperResolver,
        WorkflowGraphExecutorOptions? options = null,
        WorkflowActivitySource? activitySource = null,
        IWorkflowBlockSandbox? sandbox = null,
        WorkflowTransitionRecorder? transitions = null,
        WorkflowSleepRunner? sleepRunner = null)
    {
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _nodeObserver = nodeObserver ?? throw new ArgumentNullException(nameof(nodeObserver));
        _helperResolver = helperResolver ?? throw new ArgumentNullException(nameof(helperResolver));
        _sandbox = sandbox;
        _transitions = transitions;
        _sleepRunner = sleepRunner;
        _options = options ?? new WorkflowGraphExecutorOptions();
        _clock = _options.TimeProvider ?? TimeProvider.System;
        _activitySource = activitySource ?? WorkflowActivitySource.Default;
        _notifierLogger = loggerFactory.CreateLogger(NotifierLoggerCategory);
        _definitionLogger = loggerFactory.CreateLogger(DefinitionLoggerCategory);
        _blockLogger = loggerFactory.CreateLogger(BlockLoggerCategory);
    }

    internal static Dictionary<string, object?> MergeConfigIntoInputs(
        IReadOnlyDictionary<string, object?> wiredInputs,
        IReadOnlyDictionary<string, JsonElement> config,
        CompiledNode compiled) =>
        MergeConfig(wiredInputs, config, (key, raw) => compiled.TryBindConfig(key, out var bound) ? bound : WorkflowGraphHelpers.JsonToValue(raw));

    internal static Dictionary<string, object?> MergeConfigBeforeCompile(
        IReadOnlyDictionary<string, object?> wiredInputs,
        IReadOnlyDictionary<string, JsonElement> config) =>
        MergeConfig(wiredInputs, config, (_, raw) => WorkflowGraphHelpers.JsonToValue(raw));

    private static Dictionary<string, object?> MergeConfig(
        IReadOnlyDictionary<string, object?> wiredInputs,
        IReadOnlyDictionary<string, JsonElement> config,
        Func<string, JsonElement, object?> valueOf)
    {
        var merged = new Dictionary<string, object?>(wiredInputs);
        foreach (var kvp in config)
        {
            if (merged.ContainsKey(kvp.Key) ||
                PollyConfigKeys.Contains(kvp.Key) ||
                kvp.Key.StartsWith(DesignTimeConfigPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            merged[kvp.Key] = valueOf(kvp.Key, kvp.Value);
        }

        return merged;
    }

    internal static void MergeCompiledConfigIntoInputs(
        Dictionary<string, object?> wiredInputs, CompiledNode compiled)
    {
        for (var index = 0; index < compiled.InputNames.Count; index++)
        {
            var key = compiled.InputNames[index];
            if (!wiredInputs.ContainsKey(key) && compiled.TryBindConfig(key, out var value)) wiredInputs[key] = value;
        }
    }

    internal static Dictionary<string, object?> ResolveInputs(
        CompiledNode compiled, Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        HashSet<string>? wired = null)
    {
        var capacity = compiled.Inputs.Count;
        for (var index = 0; index < compiled.Incoming.Count; index++)
            capacity += compiled.Incoming[index].Mappings.Count;
        var inputs = new Dictionary<string, object?>(capacity, StringComparer.Ordinal);
        for (var index = 0; index < compiled.Incoming.Count; index++)
        {
            var edge = compiled.Incoming[index];
            WorkflowExecutionHelpers.ApplyMappings(inputs, edge.SourceNodeId, edge.Mappings, executedOutputs, compiled, wired);
        }
        return inputs;
    }

    internal async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> ExecuteGraphAsync(
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
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
        IReadOnlyDictionary<string, object?>? inputParametersForRun,
        Guid? triggerIdForRun,
        TriggerEnvelope? triggerEnvelopeForRun,
        CancellationToken cancellationToken,
        CompiledWalk compiled,
        IWorkflowDebugHook? debugHook = null,
        DebugPausedCounter? debugCounter = null,
        Guid? stateExecutionId = null,
        bool mayParkSleep = false,
        CanvasSignature? parameterSignature = null)
    {
        if (depth != 0)
        {
            return await WalkAsync(executedOutputs, workflowId, executionId, userId, services, logEntries,
                executionSteps, traceMode, debugMode, iterationIndex, callStackJson, depth, visitedNodeIds,
                isInsideTryCatch, inputParametersForRun, triggerIdForRun, triggerEnvelopeForRun, cancellationToken,
                debugHook, debugCounter, stateExecutionId, mayParkSleep, parameterSignature, compiled).ConfigureAwait(false);
        }

        var captured = WorkflowRunFrames.Swap([]);
        try
        {
            return await WalkAsync(executedOutputs, workflowId, executionId, userId, services, logEntries,
                executionSteps, traceMode, debugMode, iterationIndex, callStackJson, depth, visitedNodeIds,
                isInsideTryCatch, inputParametersForRun, triggerIdForRun, triggerEnvelopeForRun, cancellationToken,
                debugHook, debugCounter, stateExecutionId, mayParkSleep, parameterSignature, compiled).ConfigureAwait(false);
        }
        finally
        {
            WorkflowRunFrames.Swap(captured);
        }
    }

    private async Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> WalkAsync(
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
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
        IReadOnlyDictionary<string, object?>? inputParametersForRun,
        Guid? triggerIdForRun,
        TriggerEnvelope? triggerEnvelopeForRun,
        CancellationToken cancellationToken,
        IWorkflowDebugHook? debugHook,
        DebugPausedCounter? debugCounter,
        Guid? stateExecutionId,
        bool mayParkSleep,
        CanvasSignature? parameterSignature,
        CompiledWalk compiled)
    {
        if (depth > MaxCallDepth)
            throw new InvalidOperationException($"Workflow execution exceeded max depth of {MaxCallDepth}. Possible recursion.");

        var homeTimeZone = await WorkflowHomeTimeZone.ResolveAsync(
            _options.TimeZone, _options.ResolveTimeZone, services, cancellationToken).ConfigureAwait(false);

        var expressionParameters = WorkflowExecutionHelpers.ParameterScope(inputParametersForRun, parameterSignature);

        var hostNamespaces = await WorkflowExpressionNamespaces.MergeResolversAsync(
            _options.ExpressionNamespaces,
            new ExpressionNamespaceScope(services, executionId, triggerIdForRun, triggerEnvelopeForRun),
            cancellationToken).ConfigureAwait(false);
        var typeVocabulary = ExpressionNamespaceValues.VocabularyOf(services);

        var nodeOrder = compiled.Canvas.NodeOrder;
        var stepSortOrder = executionSteps.Count;
        var skippedNodes = new HashSet<string>();
        var skipPolicyCaught = false;
        foreach (var nodeId in nodeOrder)
        {
            var node = compiled.Required(nodeId);

            if (node.Shape.BlockTypeId == WorkflowInputBlockTypeId || node.Shape.BlockTypeId == WorkflowOutputBlockTypeId)
                continue;

            if (skippedNodes.Contains(node.NodeId))
            {
                AddLogEntry(logEntries, executionId, node, LogEntryLevel.Warning,
                    "Node skipped due to upstream error policy.", debugMode, iterationIndex, callStackJson);
                continue;
            }

            if (executedOutputs.ContainsKey(node.NodeId))
                continue;

            if (debugHook is not null)
            {
                await WorkflowDebugDispatcher.DispatchNodeAsync(
                    debugHook, node.NodeId, executedOutputs, cancellationToken).ConfigureAwait(false);
                debugCounter?.Increment();
            }

            var factory = _blocks.Find(node.Shape.BlockTypeId);
            if (factory == null)
            {
                AddLogEntry(logEntries, executionId, node, LogEntryLevel.Error,
                    $"Block type '{node.Shape.BlockTypeId}' not found.", debugMode, iterationIndex, callStackJson);
                throw new InvalidBlockTypeException(node.Shape.BlockTypeId);
            }

            Exception? refusal = null;
            var resumedFailure = WorkflowResumedFailure.TakeFor(executionId, node.NodeId);
            IReadOnlyDictionary<HelperPath, object?> helperValues = EmptyHelperValues;
            var helperPaths = node.HelperPaths;

            var compiledNode = node;
            var secretInputs = BlockSecrets.InputsOf(compiledNode);
            var wiredInputs = secretInputs.Count == 0 ? null : new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, object?> inputs;
            try
            {
                inputs = refusal is null ? ResolveInputs(compiledNode, executedOutputs, wiredInputs) : new Dictionary<string, object?>();
            }
            catch (Exception ex) when (ex is PortConversionException or JsonProjectionException)
            {
                refusal = ex;
                inputs = new Dictionary<string, object?>();
            }

            if (refusal is null)
            {
                    MergeCompiledConfigIntoInputs(inputs, compiledNode);
            }

            if (refusal is null && resumedFailure is null && helperPaths.Count > 0)
            {
                try { helperValues = await _helperResolver.ReadAsync(helperPaths, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    refusal = new ExpressionEvaluationException(
                        $"Reading helper values for node '{node.NodeId}' failed: {ex.GetType().Name}: {ex.Message}",
                        HelperReadPosition(compiledNode), ex, null);
                }
            }

            var rawInputNames = WorkflowExecutionHelpers.ExpressionInputNames(factory);
            if (refusal is null && WorkflowExecutionHelpers.HasExpressionInputs(inputs, rawInputNames))
            {
                try
                {
                    var scope = EvaluationScope(helperValues, executedOutputs, expressionParameters, homeTimeZone, hostNamespaces, typeVocabulary);
                    EvaluateExpressionInputs(inputs, compiledNode, scope, rawInputNames);
                }
                catch (Exception ex) when (ex is ExpressionEvaluationException or PortConversionException)
                {
                    refusal = ex;
                }
            }

            if (refusal is null)
            {
                refusal = (Exception?)RefuseValueOutsideEnum(factory, compiledNode, inputs) ?? RefuseAbsentRequiredInput(compiledNode, inputs);
            }

            if (resumedFailure is not null)
                refusal = resumedFailure;

            if (debugMode)
                AddLogEntry(logEntries, executionId, node, LogEntryLevel.Debug,
                    $"Executing block '{factory.DisplayName}'", debugMode, iterationIndex, callStackJson);

            var runWrites = stateExecutionId.HasValue && _transitions is { TracksRunState: true }
                ? WorkflowRunWrites.For(stateExecutionId.Value, root: depth == 0)
                : null;
            if (runWrites is not null)
            {
                if (depth == 0)
                {
                    runWrites.Root = new WorkflowRunWrites.RootPosition(
                        executedOutputs, compiled, node.NodeId, compiled.InputNodeId, triggerIdForRun, triggerEnvelopeForRun,
                        skipPolicyCaught);
                }
                else
                {
                    NoteThisLevel(executedOutputs, node.NodeId);
                }
            }
            var repeatsNothing = depth == 0 ? WorkflowIdempotence.Whole(compiled, node, _blocks) : WorkflowIdempotence.Node(node.Shape.BlockTypeId, _blocks);
            var writeAt = DateTimeOffset.UtcNow;
            var carriesAPosition = false;
            if (runWrites is not null && runWrites.ShouldWrite(WriteMode, repeatsNothing, depth == 0, writeAt))
            {
                var checkpoint = depth == 0
                    ? WorkflowRunCheckpoint.BuildCached(
                        executedOutputs, compiled, node.NodeId, executionId, stateExecutionId!.Value, compiled.InputNodeId,
                        triggerIdForRun, triggerEnvelopeForRun, WriteMode,
                        skipPolicyCaught ? WorkflowRunCheckpoint.SkippedANode : null, _options.MaxCheckpointBytes,
                        continuationJson: null, runWrites.OutputFragments)
                    : NestedCheckpoint(runWrites, executionId, stateExecutionId!.Value, out carriesAPosition);

                var mustLand = (depth == 0 || carriesAPosition) && !repeatsNothing && runWrites.Recoverable
                    && WriteMode.Kind != RunStateWriteModeKind.Heartbeat;
                var preBlock = new WorkflowPreBlockState(
                    stateExecutionId!.Value, workflowId, node.NodeId, node.Shape.BlockTypeId,
                    checkpoint is null ? WorkflowExecutionHelpers.BuildVariablesSnapshot(executedOutputs) : NoVariables)
                {
                    CheckpointJson = checkpoint
                };
                var written = await _transitions!.WritePreBlockStateAsync(preBlock, cancellationToken).ConfigureAwait(false);
                for (var retry = 1; written == PreBlockWrite.NotWritten && mustLand && retry < PreBlockWritesBeforeRefusing; retry++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * retry), cancellationToken).ConfigureAwait(false);
                    written = await _transitions.WritePreBlockStateAsync(preBlock, cancellationToken).ConfigureAwait(false);
                }

                if (written == PreBlockWrite.Lost)
                    throw new WorkflowRunOwnershipLostException(stateExecutionId!.Value, node.NodeId,
                        await _transitions.ExplainLossAsync(
                            stateExecutionId!.Value, WorkflowRunStateStatus.Running, cancellationToken).ConfigureAwait(false));

                if (written == PreBlockWrite.Written)
                {
                    runWrites.Wrote(writeAt, depth == 0 ? checkpoint : null, namesEntry: checkpoint is not null);
                }
                else if (mustLand)
                {
                    throw new WorkflowCheckpointNotWrittenException(stateExecutionId!.Value, node.NodeId);
                }
            }

            var resilience = ReadResilience(
                node.Resilience, node.NodeId, factory, _options.RetryOnlyIdempotentBlocks, _options.DefaultContainerTimeoutSeconds);

            RefuseAWaitTheWalkCannotApply(
                resilience.EffectiveRetries, resilience.RetryBackoffSeconds, resilience.TimeoutSeconds, node.NodeId);

            if (resilience.RetryCountIgnored is { } ignoredCount)
            {
                var unused = $"Retry count {ignoredCount} is not used: block '{node.Shape.BlockTypeId}' does not "
                    + "declare itself idempotent and this node does not set 'retryEnabled'. Set it to ask for "
                    + "those retries.";
                var alreadySaid = logEntries.Any(e => e.NodeId == node.NodeId && e.Message == unused);
                if (!alreadySaid)
                    AddLogEntry(logEntries, executionId, node, LogEntryLevel.Warning, unused,
                        debugMode, iterationIndex, callStackJson);
                if (!alreadySaid && _definitionLogger.IsEnabled(LogLevel.Warning))
                    _definitionLogger.LogWarning(
                        "Workflow {WorkflowId} node '{NodeId}': retry count {RetryCount} is not used, because "
                        + "block '{BlockTypeId}' does not declare itself idempotent and the node does not set "
                        + "'retryEnabled'.", workflowId, node.NodeId, ignoredCount, node.Shape.BlockTypeId);
            }

            using var blockActivity = _activitySource.StartActivity("workflow.block.execute", ActivityKind.Internal);
            blockActivity?.SetTag("workflow.id", workflowId.ToString());
            blockActivity?.SetTag("workflow.execution.id", executionId.ToString());
            blockActivity?.SetTag("workflow.node.id", node.NodeId);
            blockActivity?.SetTag("workflow.node.block_type_id", node.Shape.BlockTypeId);
            blockActivity?.SetTag("workflow.block.display_name", factory.DisplayName);
            blockActivity?.SetTag("workflow.depth", depth);

            var stepStartedAt = DateTimeOffset.UtcNow;

            var observedInputs = MaskedInputs(services, node, inputs);
            FireAndForget(_nodeObserver.NotifyNodeStartedAsync(workflowId, executionId, node.NodeId, observedInputs, CancellationToken.None), _notifierLogger);

            var resumedWakeUpAt = _options.DurableSleepBlockTypeIds.Contains(node.Shape.BlockTypeId)
                ? WorkflowSleepResumption.Take(stateExecutionId, node.NodeId)
                : null;

            if (mayParkSleep && !skipPolicyCaught && refusal is null && SleepToPark(node, inputs, depth, debugHook, stateExecutionId, executedOutputs,
                    compiled, triggerIdForRun, triggerEnvelopeForRun, resumedWakeUpAt) is { } sleep)
            {
                AddLogEntry(logEntries, executionId, node, LogEntryLevel.Info,
                    $"Block '{factory.DisplayName}' parked the run for {sleep.Config.DurationMs} ms.",
                    debugMode, iterationIndex, callStackJson);
                var stepsBeforePark = executionSteps.Count;
                AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node, services, stepStartedAt, DateTimeOffset.UtcNow,
                    0, WorkflowExecutionStepStatus.Succeeded, inputs,
                    new Dictionary<string, object?> { ["outcome"] = "suspended" }, null, null, null, traceMode);

                throw new SuspendedWorkflowException(
                    new SuspendResult(WorkflowSuspensionKind.Sleep,
                        JsonSerializer.Serialize(sleep.Config, JsonOptions), sleep.Config.WakeAtUtc),
                    node.NodeId, executionId, workflowId, sleep.StatePayloadJson)
                {
                    RecordedStep = StepAddedSince(executionSteps, stepsBeforePark)
                };
            }

            try
            {
                if (refusal is not null)
                    throw refusal;

                var plugin = factory.Create();

                var sandbox = _sandbox is null
                    ? null
                    : await _sandbox.SandboxAsync(factory, services, cancellationToken).ConfigureAwait(false);
                var blockServices = sandbox?.BlockServices ?? services;
                var blockHostServices = sandbox?.HostServices;

                WorkflowBlockContext NewContext()
                {
                    if (WorkflowRunContinuationScope.Current is { } position
                        && position.ExecutionId == executionId && position.NodeId == node.NodeId)
                    {
                        position.Consumed = false;
                    }

                    ISubCanvasExecutor? subCanvasExecutor = null;
                    if (factory.SubCanvasNames.Count > 0)
                    {
                        ISubCanvasExecutor inlineExecutor = new InlineSubCanvasExecutor(
                            this, node, workflowId, executionId, userId, services, logEntries, executionSteps, traceMode,
                            debugMode, iterationIndex, callStackJson, depth, visitedNodeIds, isInsideTryCatch,
                            debugHook, debugCounter, stateExecutionId, mayParkSleep) { Compiled = compiled };
                        subCanvasExecutor = sandbox is not null
                            ? sandbox.WrapSubCanvasExecutor(inlineExecutor)
                            : inlineExecutor;
                    }

                    var expressions = BlockExpressionsFor(
                        compiledNode, helperValues, executedOutputs, expressionParameters, homeTimeZone, hostNamespaces, typeVocabulary);
                    var secrets = wiredInputs is null
                        ? null
                        : BlockSecrets.For(services, compiledNode, compiled.Canvas.CanvasPath, workflowId, inputs, wiredInputs, secretInputs);

                    return new WorkflowBlockContext
                    {
                        ExecutionId = executionId,
                        UserId = userId,
                        Logger = new WorkflowBlockLogger(_blockLogger, executionId, node.NodeId),
                        Services = blockServices,
                        HostServices = blockHostServices,
                        SubCanvasExecutor = subCanvasExecutor,
                        NodeId = node.NodeId,
                        Types = compiledNode.Types,
                        OutputTypes = compiledNode.OutputTypes,
                        DeclaredOutputs = [.. node.OutputPorts.Select(p => p.Name)],
                        Expressions = sandbox is not null ? sandbox.WrapExpressions(expressions) : expressions,
                        Secrets = secrets ?? WorkflowBlockContext.Unrevealed,
                    };
                }

                var (timeoutSeconds, _, retryCount, retryBackoffSeconds, _, effectiveRetries) = resilience;

                var pipeline = PipelineFor(effectiveRetries, retryBackoffSeconds, timeoutSeconds);

                IReadOnlyDictionary<string, object?> outputs;
                if (_sleepRunner is not null && _options.DurableSleepBlockTypeIds.Contains(node.Shape.BlockTypeId))
                {
                    outputs = await _sleepRunner.ExecuteAsync(
                        inputs, node.NodeId, stateExecutionId, resumedWakeUpAt, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var resilienceContext = ResilienceContextPool.Shared.Get(cancellationToken);
                    resilienceContext.Properties.Set(RetrySinkKey, new RetrySink(
                        logEntries, executionId, node.NodeId, node.Shape.BlockTypeId, factory.DisplayName, retryCount, debugMode, iterationIndex, callStackJson,
                        _options.MaxRecordedSteps));
                    try
                    {
                        outputs = await pipeline.ExecuteAsync(
                            async (ctx) => await plugin.ExecuteAsync(inputs, NewContext(), ctx.CancellationToken).ConfigureAwait(false),
                            resilienceContext).ConfigureAwait(false);
                    }
                    catch (WorkflowSuspendException park)
                        when (park.SuspendResult.Kind is WorkflowSuspensionKind.WaitTemplate or WorkflowSuspensionKind.Predicate)
                    {
                        throw WorkflowParkCheck.Checked(compiledNode, plugin, park);
                    }
                    finally
                    {
                        ResilienceContextPool.Shared.Return(resilienceContext);
                    }
                }

                if (WorkflowRunContinuationScope.Current is { Consumed: false } unspent
                    && unspent.ExecutionId == executionId && unspent.NodeId == node.NodeId)
                {
                    throw new WorkflowContinuationDivergedException(node.NodeId,
                        $"fewer calls than the checkpoint records, stopping before it reached '{unspent.Frames[0].SubCanvas}' "
                        + $"at '{unspent.Frames[0].NodeInFlight}'");
                }

                if (node.Passthrough != null && node.Passthrough.Count > 0)
                {
                    var mergedOutputs = new Dictionary<string, object?>(outputs);
                    foreach (var p in node.Passthrough)
                    {
                        if (inputs.TryGetValue(p, out var val) && !mergedOutputs.ContainsKey(p))
                        {
                            mergedOutputs[p] = val;
                        }
                    }
                    outputs = mergedOutputs;
                }

                outputs = ValueGate.Outputs(compiledNode, outputs);
                executedOutputs[node.NodeId] = outputs;
                if (depth == 0)
                    runWrites?.OutputFragments.Record(node.NodeId, outputs, compiledNode, _options.MaxCheckpointBytes);
                blockActivity?.SetTag("workflow.outputs.count", outputs.Count);

                if (debugMode)
                    AddLogEntry(logEntries, executionId, node, LogEntryLevel.Debug,
                        $"Block '{factory.DisplayName}' completed with {outputs.Count} outputs.", debugMode, iterationIndex, callStackJson);

                var endedAt = DateTimeOffset.UtcNow;
                var durationMs = (long)(endedAt - stepStartedAt).TotalMilliseconds;

                FireAndForget(_nodeObserver.NotifyNodeCompletedAsync(workflowId, executionId, node.NodeId, observedInputs, outputs, durationMs, CancellationToken.None), _notifierLogger);

                AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node, services, stepStartedAt, endedAt, durationMs,
                    WorkflowExecutionStepStatus.Succeeded, inputs, outputs, null, null, null, traceMode);
            }
            catch (WorkflowSuspendException suspEx)
            {
                string? parkedPosition = null;
                string? parkedRootNode = null;
                var parkedOutputs = executedOutputs;
                var parkedWalk = compiled;
                var parkedInputNode = compiled.InputNodeId;
                var parkedTriggerId = triggerIdForRun;
                var parkedTriggerEnvelope = triggerEnvelopeForRun;
                if (depth > 0)
                {
                    var frames = WorkflowRunFrames.Current;
                    var root = stateExecutionId.HasValue ? WorkflowRunWrites.Find(stateExecutionId.Value)?.Root : null;
                    if (WorkflowRunFrames.ContainerThatCannotBeReentered(frames) is { } refusing)
                    {
                        throw new SubCanvasRethrowException(
                            $"Block '{node.NodeId}' ({node.Shape.BlockTypeId}) requested a wait inside '{refusing}', which does "
                            + "more than drive its sub-canvases, so nothing can put the run back where it was. Move "
                            + "the wait out of that block, or have that block declare that it only drives its "
                            + "sub-canvases.");
                    }
                    if (WorkflowRunFrames.Checkpointed(frames, _options.MaxSuspendPayloadBytes) is not { } position || root is null)
                    {
                        throw new SubCanvasRethrowException(
                            $"Block '{node.NodeId}' ({node.Shape.BlockTypeId}) requested a wait inside a sub-canvas, and this run "
                            + "keeps no position to put it back into. A run whose row this host does not track is "
                            + "parked only on the root canvas.");
                    }

                    parkedPosition = position;
                    parkedRootNode = root.NodeInFlight;
                    parkedOutputs = root.ExecutedOutputs;
                    parkedWalk = root.Walk;
                    parkedInputNode = root.InputNodeId;
                    parkedTriggerId = root.TriggerId;
                    parkedTriggerEnvelope = root.TriggerEnvelope;
                }

                var endedAt = DateTimeOffset.UtcNow;
                var durationMs = (long)(endedAt - stepStartedAt).TotalMilliseconds;

                AddLogEntry(logEntries, executionId, node, LogEntryLevel.Info,
                    $"Block '{factory.DisplayName}' requested suspension.",
                    debugMode, iterationIndex, callStackJson);

                var stepsBeforePark = executionSteps.Count;
                AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node, services,
                    stepStartedAt, endedAt, durationMs,
                    WorkflowExecutionStepStatus.Succeeded, inputs,
                    new Dictionary<string, object?> { ["outcome"] = "suspended" },
                    null, null, null, traceMode);

                var statePayload = WorkflowExecutionHelpers.BuildSuspensionStatePayload(
                    parkedOutputs, parkedWalk, parkedTriggerId, parkedTriggerEnvelope, parkedInputNode,
                    parkedPosition, parkedRootNode);

                throw new SuspendedWorkflowException(
                    suspEx.SuspendResult,
                    node.NodeId,
                    executionId,
                    workflowId,
                    statePayload)
                {
                    RecordedStep = StepAddedSince(executionSteps, stepsBeforePark)
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var endedAt = DateTimeOffset.UtcNow;
                var durationMs = (long)(endedAt - stepStartedAt).TotalMilliseconds;
                var errorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

                if (EscapesErrorPolicy(ex))
                {
                    FireAndForget(_nodeObserver.NotifyNodeFailedAsync(workflowId, executionId, node.NodeId, observedInputs, errorMessage, durationMs, CancellationToken.None), _notifierLogger);
                    throw;
                }

                var errorPolicy = node.ErrorPolicy;
                var failure = WorkflowFailureCoding.Of(ex);

                var caughtBy = (string?)null;
                var caught = false;

                if (errorPolicy.Policy != NodeErrorPolicy.FailWorkflow)
                {
                    caughtBy = "errorPolicy";
                    caught = true;
                }
                else if (isInsideTryCatch)
                {
                    caughtBy = "TryCatch";
                    caught = true;
                }

                blockActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                blockActivity?.AddException(ex);
                blockActivity?.SetTag("workflow.node.error_policy", errorPolicy.Policy.ToString());
                blockActivity?.SetTag("workflow.node.error_caught", caught);

                _metrics.RecordNodeError(node.Shape.BlockTypeId, errorPolicy.Policy.ToString(), caught, ex.GetType().Name);

                var errorJsonObj = new
                {
                    type = ex.GetType().Name,
                    message = errorMessage,
                    blockTypeId = node.Shape.BlockTypeId,
                    nodeId = node.NodeId,
                    caughtBy = caughtBy,
                    policy = errorPolicy.Policy.ToString(),
                    timestampUtc = endedAt
                };
                var errorJson = JsonSerializer.Serialize(errorJsonObj, JsonOptions);

                var stackTrace = ex.StackTrace;
                if (stackTrace != null && stackTrace.Length > 8000)
                    stackTrace = stackTrace[..8000];

                switch (errorPolicy.Policy)
                {
                    case NodeErrorPolicy.FailWorkflow:
                        {
                            AddLogEntry(logEntries, executionId, node, LogEntryLevel.Error,
                                $"Block '{factory.DisplayName}' failed with error (FailWorkflow policy): {errorMessage}", debugMode, iterationIndex, callStackJson);

                            FireAndForget(_nodeObserver.NotifyNodeFailedAsync(workflowId, executionId, node.NodeId, observedInputs, errorMessage, durationMs, CancellationToken.None), _notifierLogger);

                            AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node, services, stepStartedAt, endedAt, durationMs,
                                WorkflowExecutionStepStatus.Failed, inputs, null, errorMessage, stackTrace, errorJson, traceMode);

                            throw new WorkflowNodeExecutionException(ex.Message, node.NodeId, node.Shape.BlockTypeId, ex);
                        }

                    case NodeErrorPolicy.ContinueWithDefault:
                        {
                            AddLogEntry(logEntries, executionId, node, LogEntryLevel.Warning,
                                $"Block '{factory.DisplayName}' failed with error (ContinueWithDefault policy): {errorMessage}. Using default output.", debugMode, iterationIndex, callStackJson);

                            string? unparsed = null;
                            var defaultOutputs = compiledNode.BindDefaultOutputs() ?? ReadDefaultOutputs(errorPolicy.DefaultOutputJson, out unparsed);
                            defaultOutputs = GatedDefaults(compiledNode, defaultOutputs, node);
                            if (unparsed is not null)
                            {
                                AddLogEntry(logEntries, executionId, node, LogEntryLevel.Warning,
                                    $"Failed to parse DefaultOutputJson: {unparsed}. Using empty outputs.",
                                    debugMode, iterationIndex, callStackJson);
                            }

                            executedOutputs[node.NodeId] = defaultOutputs;
                            if (depth == 0)
                                runWrites?.OutputFragments.Record(node.NodeId, defaultOutputs, compiledNode, _options.MaxCheckpointBytes);

                            FireAndForget(_nodeObserver.NotifyNodeCompletedAsync(workflowId, executionId, node.NodeId, observedInputs, defaultOutputs, durationMs, CancellationToken.None), _notifierLogger);

                            AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node, services, stepStartedAt, endedAt, durationMs,
                                WorkflowExecutionStepStatus.Succeeded, inputs, defaultOutputs, null, null, errorJson, traceMode);
                        }
                        break;

                    case NodeErrorPolicy.EmitError:
                        {
                            AddLogEntry(logEntries, executionId, node, LogEntryLevel.Warning,
                                $"Block '{factory.DisplayName}' failed with error (EmitError policy): {errorMessage}. Emitting error output.", debugMode, iterationIndex, callStackJson);

                            var errorObject = new Dictionary<string, object?>
                            {
                                ["message"] = errorMessage,
                                ["blockTypeId"] = node.Shape.BlockTypeId,
                                ["exceptionType"] = ex.GetType().Name,
                                ["nodeId"] = node.NodeId,
                                ["timestampUtc"] = DateTimeOffset.UtcNow,
                                ["code"] = failure.Code
                            };
                            if (failure.Category is { } category)
                                errorObject["category"] = category;
                            var errorOutput = new Dictionary<string, object?> { ["_error"] = errorObject };

                            executedOutputs[node.NodeId] = errorOutput;
                            if (depth == 0)
                                runWrites?.OutputFragments.Record(node.NodeId, errorOutput, compiledNode, _options.MaxCheckpointBytes);

                            FireAndForget(_nodeObserver.NotifyNodeCompletedAsync(workflowId, executionId, node.NodeId, observedInputs, errorOutput, durationMs, CancellationToken.None), _notifierLogger);

                            AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node, services, stepStartedAt, endedAt, durationMs,
                                WorkflowExecutionStepStatus.Succeeded, inputs, errorOutput, null, null, errorJson, traceMode);
                        }
                        break;

                    case NodeErrorPolicy.Skip:
                        {
                            AddLogEntry(logEntries, executionId, node, LogEntryLevel.Warning,
                                $"Block '{factory.DisplayName}' failed with error (Skip policy): {errorMessage}. Skipping node and downstream nodes.", debugMode, iterationIndex, callStackJson);

                            skipPolicyCaught = true;
                            var downstreamNodes = compiled.Downstream(node.NodeId);
                            foreach (var downstreamId in downstreamNodes)
                                skippedNodes.Add(downstreamId);

                            FireAndForget(_nodeObserver.NotifyNodeCompletedAsync(workflowId, executionId, node.NodeId, observedInputs, new Dictionary<string, object?>(), durationMs, CancellationToken.None), _notifierLogger);

                            AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node, services, stepStartedAt, endedAt, durationMs,
                                WorkflowExecutionStepStatus.Skipped, inputs, null, errorMessage, stackTrace, errorJson, traceMode);
                        }
                        break;
                }
            }
        }

        return executedOutputs;
    }

    internal async Task<IReadOnlyDictionary<string, object?>?> ResumedOutputsAsync(
        CompiledNode node, string outcomeJson, Guid executionId, Guid userId, IServiceProvider services,
        Func<(Dictionary<string, IReadOnlyDictionary<string, object?>> Outputs, IReadOnlyDictionary<string, object?>? Parameters)> scope,
        Guid? triggerId, TriggerEnvelope? envelope, CancellationToken cancellationToken)
    {
        if (_blocks.Find(node.Shape.BlockTypeId) is not { } factory || factory.Create() is not IWorkflowResumedOutputs block)
            return null;

        var (outputs, parameters) = scope();

        WorkflowBlockSandbox? sandbox;
        TimeZoneInfo homeTimeZone;
        IReadOnlyCollection<ExpressionNamespaceBinding> hostNamespaces;
        WorkflowTypeVocabulary? vocabulary;
        try
        {
            sandbox = _sandbox is null ? null : await _sandbox.SandboxAsync(factory, services, cancellationToken).ConfigureAwait(false);
            homeTimeZone = await WorkflowHomeTimeZone.ResolveAsync(
                _options.TimeZone, _options.ResolveTimeZone, services, cancellationToken).ConfigureAwait(false);
            hostNamespaces = await WorkflowExpressionNamespaces.MergeResolversAsync(
                _options.ExpressionNamespaces, new ExpressionNamespaceScope(services, executionId, triggerId, envelope),
                cancellationToken).ConfigureAwait(false);
            vocabulary = ExpressionNamespaceValues.VocabularyOf(services);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkflowHostLookupException(ex);
        }

        IReadOnlyDictionary<HelperPath, object?> helperValues;
        try
        {
            var paths = node.HelperPaths;
            helperValues = paths.Count == 0 ? EmptyHelperValues
                : await _helperResolver.ReadAsync(paths, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new WorkflowResumedFaultException(WorkflowErrorCodes.ExpressionRuntimeError, null,
                $"Reading helper values for resumed node '{node.NodeId}' failed: {ex.Message}");
        }

        var expressions = BlockExpressionsFor(node, helperValues, outputs, parameters, homeTimeZone, hostNamespaces, vocabulary);
        var context = new WorkflowBlockContext
        {
            ExecutionId = executionId,
            UserId = userId,
            Logger = new WorkflowBlockLogger(_blockLogger, executionId, node.NodeId),
            Services = sandbox?.BlockServices ?? services,
            HostServices = sandbox?.HostServices,
            NodeId = node.NodeId,
            Types = node.Types,
            OutputTypes = node.OutputTypes,
            DeclaredOutputs = [.. node.OutputPorts.Select(p => p.Name)],
            Expressions = sandbox is not null ? sandbox.WrapExpressions(expressions) : expressions,
        };

        using var outcome = JsonDocument.Parse(outcomeJson);
        IReadOnlyDictionary<string, object?>? built;
        try
        {
            built = block.ResumedOutputs(outcome.RootElement.Clone(), context);
        }
        catch (Exception ex) when (ex is not PersistedValueReadException and not OperationCanceledException)
        {
            var failure = WorkflowFailureCoding.Of(ex);
            throw new WorkflowResumedFaultException(failure.Code, failure.Category,
                $"Block '{node.Shape.BlockTypeId}' could not build its resumed outputs: {ex.Message}");
        }

        return built ?? throw new WorkflowResumedFaultException(WorkflowErrorCodes.BlockFailed, null,
            $"Block '{node.Shape.BlockTypeId}' answered no resumed outputs.");
    }

    internal void RecordResumedNode(
        WorkflowNodeDefinition node, Guid workflowId, Guid executionId, IReadOnlyDictionary<string, object?> outputs,
        DateTimeOffset startedAtUtc, List<WorkflowExecutionLogEntry> logEntries,
        List<WorkflowExecutionStep> executionSteps, WorkflowTraceMode traceMode)
    {
        var endedAt = DateTimeOffset.UtcNow;
        var durationMs = (long)Math.Max(0, (endedAt - startedAtUtc).TotalMilliseconds);
        var inputs = new Dictionary<string, object?> { ["DurationMs"] = outputs.GetValueOrDefault("DurationMs") };
        var sortOrder = executionSteps.Count;

        AddLogEntry(logEntries, executionId, node, LogEntryLevel.Info,
            $"Block '{node.BlockTypeId}' completed with {outputs.Count} outputs after its parked sleep.",
            debugMode: false, iterationIndex: null, callStackJson: null);
        FireAndForget(_nodeObserver.NotifyNodeCompletedAsync(
            workflowId, executionId, node.Id, inputs, outputs, durationMs, CancellationToken.None), _notifierLogger);
        AddExecutionStep(executionSteps, ref sortOrder, executionId, node, startedAtUtc, endedAt, durationMs,
            WorkflowExecutionStepStatus.Succeeded, inputs, outputs, null, null, null, traceMode);
    }

    internal void NotifyParkedNodeEnded(Guid workflowId, Guid executionId, string nodeId, DateTimeOffset suspendedAtUtc, string reason)
    {
        var durationMs = (long)Math.Max(0, (DateTimeOffset.UtcNow - suspendedAtUtc).TotalMilliseconds);
        FireAndForget(_nodeObserver.NotifyNodeFailedAsync(
            workflowId, executionId, nodeId, new Dictionary<string, object?>(), reason, durationMs, CancellationToken.None), _notifierLogger);
    }

    private static readonly IReadOnlyDictionary<string, object?> NoVariables = new Dictionary<string, object?>();

    internal static string? InputNodeIdOf(WorkflowDefinition definition) =>
        definition.Nodes.FirstOrDefault(n => n.BlockTypeId == WorkflowInputBlockTypeId)?.Id;

    private int _unserialisableParkLogged;

    private const int PreBlockWritesBeforeRefusing = 3;

    private const int RelinkedPayloadMarginBytes = 64;

    private (WorkflowSleepConfig Config, string StatePayloadJson)? SleepToPark(
        CompiledNode node,
        IReadOnlyDictionary<string, object?> inputs,
        int depth,
        IWorkflowDebugHook? debugHook,
        Guid? stateExecutionId,
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        CompiledWalk compiled,
        Guid? triggerId,
        TriggerEnvelope? triggerEnvelope,
        DateTimeOffset? resumedWakeUpAt)
    {
        if (_options.ParkSleepWhenAtLeast is not { } threshold
            || _sleepRunner is null
            || !_options.DurableSleepBlockTypeIds.Contains(node.Shape.BlockTypeId)
            || debugHook is not null
            || stateExecutionId is null
            || node.Passthrough is { Count: > 0 })
        {
            return null;
        }

        long durationMs;
        try
        {
            durationMs = WorkflowExecutionHelpers.ResolveSleepDurationMs(inputs);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return null;
        }

        if (durationMs < 0)
            return null;

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset wakeAtUtc;
        try
        {
            wakeAtUtc = resumedWakeUpAt ?? WorkflowExecutionHelpers.SleepEnd(now, durationMs);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (wakeAtUtc - now < threshold)
            return null;

        var root = depth == 0 ? null : WorkflowRunWrites.Find(stateExecutionId.Value)?.Root;
        string? position = null;
        if (depth != 0)
        {
            if (root is null
                || WorkflowRunFrames.Checkpointed(WorkflowRunFrames.Current, _options.MaxSuspendPayloadBytes) is not { } frames)
            {
                return null;
            }

            position = frames;
        }

        string payload;
        try
        {
            payload = root is null
                ? WorkflowExecutionHelpers.BuildSuspensionStatePayload(
                    executedOutputs, compiled, triggerId, triggerEnvelope, compiled.InputNodeId)
                : WorkflowExecutionHelpers.BuildSuspensionStatePayload(
                    root.ExecutedOutputs, root.Walk, root.TriggerId, root.TriggerEnvelope, root.InputNodeId,
                    position, root.NodeInFlight);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Interlocked.Exchange(ref _unserialisableParkLogged, 1) == 0)
                _definitionLogger.LogWarning(ex, "Node '{NodeId}' sleeps in the process: the run's outputs cannot be stored to park it. "
                    + "Later sleeps that cannot park for this reason are not logged.", node.NodeId);
            return null;
        }

        if (System.Text.Encoding.UTF8.GetByteCount(payload) + RelinkedPayloadMarginBytes > _options.MaxSuspendPayloadBytes)
            return null;

        return (new WorkflowSleepConfig(durationMs, wakeAtUtc), payload);
    }

    private static void NoteThisLevel(
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs, string nodeId)
    {
        var frames = WorkflowRunFrames.Current;
        if (frames.Count == 0)
            return;
        var noted = frames.ToArray();
        noted[^1] = noted[^1] with { PreviousOutputs = executedOutputs, NodeInFlight = nodeId };
        WorkflowRunFrames.Swap(noted);
    }

    private string? NestedCheckpoint(
        WorkflowRunWrites runWrites, Guid executionId, Guid stateExecutionId, out bool carriesAPosition)
    {
        carriesAPosition = false;
        if (WriteMode.Kind == RunStateWriteModeKind.Heartbeat
            || runWrites.Root is not { } root
            || WorkflowRunFrames.Checkpointed(WorkflowRunFrames.Current, _options.MaxCheckpointBytes) is not { } continuation)
        {
            return runWrites.LastCheckpoint;
        }

        carriesAPosition = true;
        return WorkflowRunCheckpoint.BuildCached(
            root.ExecutedOutputs, root.Walk, root.NodeInFlight, executionId, stateExecutionId, root.InputNodeId,
            root.TriggerId, root.TriggerEnvelope, WriteMode,
            root.SkipPolicyCaught ? WorkflowRunCheckpoint.SkippedANode : null, _options.MaxCheckpointBytes,
            continuation, runWrites.OutputFragments);
    }

    internal bool ContainerOnlyDrives(CompiledNode node) =>
        _blocks.Find(node.Shape.BlockTypeId)?.OnlyDrivesItsSubCanvases ?? false;

    /// <summary>What no node's error policy and no try/catch container may handle: they describe the run or
    /// the definition, not the block that happened to be executing when they surfaced from a sub-canvas.</summary>
    public static bool EscapesErrorPolicy(Exception exception) =>
        exception is SubCanvasRethrowException
            or WorkflowRunOwnershipLostException
            or WorkflowRunNotRecordedException
            or InvalidWorkflowDefinitionException
            or WorkflowContinuationDivergedException
            or SuspendedWorkflowException
            or WorkflowCheckpointNotWrittenException;

    private static Dictionary<string, object?> GatedDefaults(
        CompiledNode compiled, Dictionary<string, object?> defaults, CompiledNode node)
    {
        try
        {
            return ValueGate.Outputs(compiled, defaults);
        }
        catch (WorkflowOutputTypeMismatchException ex)
        {
            throw new WorkflowNodeExecutionException(ex.Message, node.NodeId, node.Shape.BlockTypeId, ex);
        }
    }

    internal static Dictionary<string, object?> ReadDefaultOutputs(string? json, out string? unparsed)
    {
        unparsed = null;
        var defaultOutputs = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(json))
            return defaultOutputs;

        try
        {
            using var doc = JsonMembers.Parse(Encoding.UTF8.GetBytes(json));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                object? value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    JsonValueKind.Object => prop.Value.Deserialize<Dictionary<string, object?>>(JsonOptions),
                    _ => prop.Value.Clone()
                };
                defaultOutputs[prop.Name] = value;
            }
        }
        catch (Exception parseEx)
        {
            unparsed = parseEx.Message;
            defaultOutputs.Clear();
        }

        return defaultOutputs;
    }

    internal static (int? TimeoutSeconds, bool RetryEnabled, int RetryCount, double RetryBackoffSeconds,
        int? RetryCountIgnored, int EffectiveRetries) ReadResilience(
        ConfiguredResilience configured, string nodeId, IWorkflowBlockFactory factory, bool retryOnlyIdempotentBlocks,
        int? defaultContainerTimeoutSeconds)
    {
        int? timeoutSeconds = configured.TimeoutSeconds is { } timeout
            ? timeout
            : factory.SubCanvasNames.Count > 0 ? defaultContainerTimeoutSeconds : DefaultTimeoutSeconds;
        var retryCount = configured.RetryCount ?? DefaultRetryCount;
        var retryBackoffSeconds = configured.RetryBackoffSeconds ?? DefaultRetryBackoffSeconds;

        bool retryEnabled;
        if (configured.RetryEnabled is { } configuredRetryEnabled)
        {
            retryEnabled = configuredRetryEnabled;
        }
        else
        {
            retryEnabled = !retryOnlyIdempotentBlocks || factory.IsIdempotent;
        }

        int? retryCountIgnored =
            !retryEnabled && configured.RetryEnabled is null && configured.RetryCount is not null && retryCount > 0
                ? retryCount
                : null;

        var effectiveRetries = retryEnabled && retryCount > 0 ? retryCount : 0;

        return (timeoutSeconds is { } t ? Math.Max(1, t) : null, retryEnabled, retryCount,
            Math.Max(0.0, retryBackoffSeconds), retryCountIgnored, effectiveRetries);
    }

    private static readonly ResiliencePropertyKey<RetrySink?> RetrySinkKey = new("reeve.workflow.retry-sink");
    private const int MaxCachedPipelines = 256;
    private readonly ConcurrentDictionary<(int Retries, double Backoff, int TimeoutSeconds),
        ResiliencePipeline<IReadOnlyDictionary<string, object?>>> _pipelines = new();

    private sealed record RetrySink(
        List<WorkflowExecutionLogEntry> Entries, Guid ExecutionId, string NodeId, string BlockTypeId, string DisplayName,
        int RetryCount, bool DebugMode, int? IterationIndex, string? CallStackJson, int MaxRecorded);

    private const int MaxResilienceSeconds = WorkflowGraphExecutorOptions.MaxResilienceSeconds;

    private ResiliencePipeline<IReadOnlyDictionary<string, object?>> PipelineFor(
        int retries, double backoffSeconds, int? timeoutSeconds)
    {
        var key = (retries, backoffSeconds, timeoutSeconds ?? 0);
        if (_pipelines.TryGetValue(key, out var cached))
            return cached;
        var built = BuildPipeline(retries, backoffSeconds, timeoutSeconds, _clock);
        return _pipelines.Count < MaxCachedPipelines ? _pipelines.GetOrAdd(key, built) : built;
    }

    private static void RefuseAWaitTheWalkCannotApply(
        int retries, double backoffSeconds, int? timeoutSeconds, string nodeId)
    {
        if (timeoutSeconds is { } deadline && deadline > MaxResilienceSeconds)
        {
            throw new InvalidWorkflowDefinitionException(
                $"node '{nodeId}' sets '{ConfigTimeoutSeconds}' to {deadline}, and the longest deadline this "
                + $"engine can apply is {MaxResilienceSeconds} seconds (one day). A block that must run longer "
                + $"than that carries no '{ConfigTimeoutSeconds}' at all.");
        }

        if (retries > 0 && backoffSeconds > MaxResilienceSeconds)
        {
            throw new InvalidWorkflowDefinitionException(
                $"node '{nodeId}' sets '{ConfigRetryBackoffSeconds}' to {backoffSeconds}, and the longest retry "
                + $"delay this engine can apply is {MaxResilienceSeconds} seconds (one day).");
        }
    }

    private static ResiliencePipeline<IReadOnlyDictionary<string, object?>> BuildPipeline(
        int retries, double backoffSeconds, int? timeoutSeconds, TimeProvider clock)
    {
        var pipelineBuilder = new ResiliencePipelineBuilder<IReadOnlyDictionary<string, object?>> { TimeProvider = clock };
        if (retries > 0)
        {
            pipelineBuilder.AddRetry(new RetryStrategyOptions<IReadOnlyDictionary<string, object?>>
            {
                ShouldHandle = args => new ValueTask<bool>(args.Outcome.Exception switch
                {
                    TimeoutRejectedException => true,
                    HttpRequestException => true,
                    TaskCanceledException => !args.Context.CancellationToken.IsCancellationRequested,
                    _ => false
                }),
                MaxRetryAttempts = retries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(backoffSeconds),
                OnRetry = args =>
                {
                    if (args.Context.Properties.GetValue(RetrySinkKey, null) is { } sink)
                    {
                        AddLogEntry(sink.Entries, sink.ExecutionId, sink.NodeId, sink.BlockTypeId, LogEntryLevel.Warning,
                            $"Retrying block '{sink.DisplayName}' (attempt {args.AttemptNumber + 1}/{sink.RetryCount}) due to: {args.Outcome.Exception?.Message ?? "Unknown error"}",
                            sink.DebugMode, sink.IterationIndex, sink.CallStackJson, sink.MaxRecorded);
                    }
                    return default;
                }
            });
        }

        if (timeoutSeconds is { } deadlineSeconds)
            pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(deadlineSeconds));

        return pipelineBuilder.Build();
    }

    private const int EntryMessageLimit = 4000;

    public static void AddLogEntry(List<WorkflowExecutionLogEntry> entries, Guid executionId,
        WorkflowNodeDefinition node, LogEntryLevel level, string message, bool debugMode,
        int? iterationIndex, string? callStackJson)
        => AddLogEntry(entries, executionId, node.Id, node.BlockTypeId, level, message, debugMode, iterationIndex, callStackJson);

    internal static void AddLogEntry(List<WorkflowExecutionLogEntry> entries, Guid executionId,
        string nodeId, string blockTypeId, LogEntryLevel level, string message, bool debugMode,
        int? iterationIndex, string? callStackJson, int maxRecorded = int.MaxValue)
    {
        if (!debugMode && level == LogEntryLevel.Debug)
            return;
        if (!Admits(entries.Count, level == LogEntryLevel.Error, maxRecorded))
            return;

        entries.Add(new WorkflowExecutionLogEntry(
            Guid.NewGuid(), executionId, nodeId, blockTypeId,
            level, Cut(message, EntryMessageLimit), null, iterationIndex, callStackJson, DateTimeOffset.UtcNow));
    }

    private void AddLogEntry(List<WorkflowExecutionLogEntry> entries, Guid executionId,
        CompiledNode node, LogEntryLevel level, string message, bool debugMode, int? iterationIndex, string? callStackJson) =>
        AddLogEntry(entries, executionId, node.NodeId, node.Shape.BlockTypeId, level, message, debugMode, iterationIndex, callStackJson,
            _options.MaxRecordedSteps);

    private static WorkflowExecutionStep? StepAddedSince(List<WorkflowExecutionStep> steps, int before) =>
        steps.Count > before ? steps[^1] : null;

    private static bool Admits(int recorded, bool carriesAnError, int maxRecorded) =>
        recorded < (carriesAnError ? 2L * maxRecorded : maxRecorded);

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;

    public void AddExecutionStep(
        List<WorkflowExecutionStep> executionSteps,
        ref int stepSortOrder,
        Guid executionId,
        WorkflowNodeDefinition node,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        long durationMs,
        WorkflowExecutionStepStatus status,
        IReadOnlyDictionary<string, object?>? inputs,
        IReadOnlyDictionary<string, object?>? outputs,
        string? errorMessage,
        string? stackTrace,
        string? errorJson,
        WorkflowTraceMode traceMode)
        => AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node.Id, node.BlockTypeId,
            startedAt, endedAt, durationMs, status, inputs, outputs, errorMessage, stackTrace, errorJson, traceMode);

    internal void AddExecutionStep(
        List<WorkflowExecutionStep> executionSteps, ref int stepSortOrder, Guid executionId,
        string nodeId, string blockTypeId, DateTimeOffset startedAt, DateTimeOffset endedAt,
        long durationMs, WorkflowExecutionStepStatus status, IReadOnlyDictionary<string, object?>? inputs,
        IReadOnlyDictionary<string, object?>? outputs, string? errorMessage, string? stackTrace,
        string? errorJson, WorkflowTraceMode traceMode)
    {
        if (traceMode == WorkflowTraceMode.None && string.IsNullOrEmpty(errorJson))
            return;
        if (!Admits(executionSteps.Count,
                status == WorkflowExecutionStepStatus.Failed || !string.IsNullOrEmpty(errorJson), _options.MaxRecordedSteps))
            return;

        var maxBytes = _options.MaxPayloadValueBytes;
        var inputsJson = traceMode == WorkflowTraceMode.Full && inputs != null ? PayloadSanitizer.SerializeAndSanitize(inputs, maxBytes) : null;
        var outputsJson = traceMode == WorkflowTraceMode.Full && outputs != null ? PayloadSanitizer.SerializeAndSanitize(outputs, maxBytes) : null;

        executionSteps.Add(new WorkflowExecutionStep(
            Guid.NewGuid(), executionId, nodeId, blockTypeId,
            startedAt, endedAt,
            durationMs,
            status,
            stepSortOrder++,
            inputsJson,
            outputsJson,
            errorMessage,
            stackTrace,
            errorJson));
    }

    private void AddExecutionStep(List<WorkflowExecutionStep> executionSteps, ref int stepSortOrder,
        Guid executionId, CompiledNode node, IServiceProvider services, DateTimeOffset startedAt, DateTimeOffset endedAt, long durationMs,
        WorkflowExecutionStepStatus status, IReadOnlyDictionary<string, object?>? inputs,
        IReadOnlyDictionary<string, object?>? outputs, string? errorMessage, string? stackTrace,
        string? errorJson, WorkflowTraceMode traceMode) =>
        AddExecutionStep(executionSteps, ref stepSortOrder, executionId, node.NodeId, node.Shape.BlockTypeId,
            startedAt, endedAt, durationMs, status, RecordedInputs(services, node, inputs, traceMode), outputs, errorMessage, stackTrace, errorJson, traceMode);

    private static IReadOnlyDictionary<string, object?>? RecordedInputs(
        IServiceProvider services, CompiledNode node, IReadOnlyDictionary<string, object?>? inputs, WorkflowTraceMode traceMode)
    {
        if (inputs is null || traceMode != WorkflowTraceMode.Full)
            return inputs;
        var shown = MaskedInputs(services, node, inputs);
        if (ReferenceEquals(shown, inputs))
            return inputs;

        var recorded = new Dictionary<string, object?>(shown, StringComparer.Ordinal);
        foreach (var name in BlockSecrets.InputsOf(node))
        {
            if (recorded.TryGetValue(name, out var value) && value is IReadOnlyDictionary<string, string> map)
                recorded[name] = new PayloadSanitizer.PresentedValue([.. map]);
        }

        return recorded;
    }

    internal static IReadOnlyDictionary<string, object?> MaskedInputs(
        IServiceProvider services, CompiledNode node, IReadOnlyDictionary<string, object?> inputs)
    {
        var secretInputs = BlockSecrets.InputsOf(node);
        if (secretInputs.Count == 0)
            return inputs;

        var codec = services.GetService<IWorkflowSecretCodec>();
        var shown = new Dictionary<string, object?>(inputs, StringComparer.Ordinal);
        foreach (var name in secretInputs)
        {
            if (!inputs.TryGetValue(name, out var value) || value is not IReadOnlyDictionary<string, string> map)
                continue;
            var entries = new Dictionary<string, string>(map.Count, StringComparer.Ordinal);
            foreach (var (key, text) in map)
                entries[PayloadSanitizer.CutAtColon(key)] = WorkflowSecretEntries.IsMasked(codec, key, text) ? WorkflowSecretEntries.Masked : text;

            shown[name] = entries;
        }

        return shown;
    }

    internal static ExpressionEvaluationScope EvaluationScope(
        IReadOnlyDictionary<HelperPath, object?> helperValues,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        IReadOnlyDictionary<string, object?>? parameters,
        TimeZoneInfo homeTimeZone,
        IReadOnlyCollection<ExpressionNamespaceBinding>? hostNamespaces,
        WorkflowTypeVocabulary? vocabulary,
        DateTimeOffset? now = null)
    {
        var utcNow = now ?? DateTimeOffset.UtcNow;
        var builder = new ExpressionEvaluationScope.Builder()
            .Vocabulary(vocabulary ?? WorkflowTypeVocabulary.Builtin)
            .Now(utcNow, TimeZoneInfo.ConvertTime(utcNow, homeTimeZone))
            .Helper(helperValues)
            .Previous((nodeId, output) => executedOutputs.TryGetValue(nodeId, out var outputs) && outputs.TryGetValue(output, out var value)
                ? (true, value)
                : (false, null))
            .Parameters(name => parameters is not null && parameters.TryGetValue(name, out var value) ? (true, value) : (false, null));
        foreach (var ns in hostNamespaces ?? [])
            builder.Namespace(ns);
        return builder.Build();
    }

    private static BlockExpressions BlockExpressionsFor(
        CompiledNode node,
        IReadOnlyDictionary<HelperPath, object?> helperValues,
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        IReadOnlyDictionary<string, object?>? parameters,
        TimeZoneInfo homeTimeZone,
        IReadOnlyCollection<ExpressionNamespaceBinding>? hostNamespaces,
        WorkflowTypeVocabulary? vocabulary) =>
        new(node, () => EvaluationScope(helperValues, executedOutputs, parameters, homeTimeZone, hostNamespaces, vocabulary));

    internal static void EvaluateExpressionInputs(
        Dictionary<string, object?> inputs, CompiledNode compiledNode, ExpressionEvaluationScope scope,
        IReadOnlySet<string>? rawInputs)
    {
        List<(string Name, object? Value)>? replacements = null;
        foreach (var (name, value) in inputs)
        {
            if (rawInputs?.Contains(name) == true || value is not string text || !ExpressionSource.IsExpression(text))
            {
                continue;
            }

            if (!compiledNode.Expressions.TryGetValue(name, out var expression) || !string.Equals(expression.Text, text, StringComparison.Ordinal))
            {
                continue;
            }

            var evaluated = TypedExpressionEvaluator.Evaluate(expression.Tree, scope);
            (replacements ??= []).Add((name, expression.Conversion is { } conversion ? conversion.Apply(evaluated) : evaluated));
        }

        if (replacements is not null)
            foreach (var (name, value) in replacements) inputs[name] = value;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IWorkflowBlockFactory, BlockConfigFieldDefinition[]> EnumFieldsByFactory = new();

    internal static WorkflowConfigValueNotAllowedException? RefuseValueOutsideEnum(
        IWorkflowBlockFactory factory, CompiledNode node, Dictionary<string, object?> inputs)
    {
        var fields = EnumFieldsByFactory.GetValue(factory,
            f => f.ConfigurationSchema.Where(c => c.Type == ConfigFieldType.Enum).ToArray());
        foreach (var field in fields)
        {
            if (!inputs.TryGetValue(field.Name, out var value) || value is null)
                continue;

            if (value is string { Length: 0 })
            {
                inputs.Remove(field.Name);
                continue;
            }

            var allowed = field.AllowedValues ?? [];
            if (value is string text && allowed.Contains(text, StringComparer.Ordinal))
                continue;

            return new WorkflowConfigValueNotAllowedException(node.NodeId, field.Name, allowed, value);
        }

        return null;
    }

    internal static WorkflowRequiredInputAbsentException? RefuseAbsentRequiredInput(
        CompiledNode compiled, IReadOnlyDictionary<string, object?> inputs)
    {
        for (var index = 0; index < compiled.RequiredInputs.Count; index++)
        {
            var name = compiled.RequiredInputs[index];
            if (!inputs.ContainsKey(name))
                return new WorkflowRequiredInputAbsentException(compiled.NodeId, name);
        }

        return null;
    }

    /// <summary>The notifier is told about a node, not asked. A notifier that throws must not take the run
    /// down with it, and must not make the node wait for it either.</summary>
    public static async void FireAndForget(Task task, ILogger logger)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { logger.LogWarning(ex, "Workflow execution notifier failed"); }
    }
}

/// <summary>The knobs the walk reads rather than asks for: how much of a step it writes down, what a local
/// wall-clock field means, and which blocks sleep durably rather than in this process.</summary>
public sealed class WorkflowGraphExecutorOptions
{
    /// <summary>The longest deadline or retry delay this engine can apply, in seconds: one day. Polly
    /// validates both <c>TimeoutStrategyOptions.Timeout</c> and <c>RetryStrategyOptions.Delay</c> against an
    /// upper bound of <c>1.00:00:00</c>, inclusive (their lower bounds differ and <c>Math.Max(1, t)</c> and
    /// <c>Math.Max(0.0, b)</c> keep the walk clear of both), so a larger number is not a longer wait —
    /// building the node's pipeline throws instead, on every execution of it. It bounds one stated wait and
    /// not the wait a run performs: the backoff is exponential with no <c>MaxDelay</c>, so a second attempt
    /// waits twice what the node says and Polly takes that. Public because a host that validates its own
    /// configuration needs the number, and because the editor's boxes mirror it.</summary>
    public const int MaxResilienceSeconds = 86_400;

    /// <summary>Caps what a traced step writes, so a block returning a megabyte does not make the log unreadable.</summary>
    public int MaxPayloadValueBytes { get; init; } = 8 * 1024;

    /// <summary>The most steps, and the most node log entries, one segment of a run's walk records, 10 000 of each by
    /// default. Both lists are held in memory until the walk ends or parks and are then written in one batch, so a walk
    /// executing more nodes keeps its first steps and entries and records no later one; the run itself goes on. A
    /// step or entry carrying an error is still recorded past the bound, up to twice it, so the step that failed the run
    /// is in its history.</summary>
    /// <remarks>A segment is the walk from a start or a resume to its end or its next park: a resumed run starts a
    /// fresh count.</remarks>
    public int MaxRecordedSteps { get; init; } = 10_000;

    /// <summary>Whether a node that says nothing about retries inherits its block's answer to
    /// <see cref="IWorkflowBlockFactory.IsIdempotent"/> instead of being retried. On by default; a node's
    /// own <c>retryEnabled</c> wins either way, and a host not ready for it sets this false.</summary>
    public bool RetryOnlyIdempotentBlocks { get; init; } = true;

    /// <summary>The ceiling a container runs under when its own node names none. Null — no ceiling, the run's own
    /// deadline and nothing else — because the engine cannot size a body and a deadline it guesses kills a long
    /// loop and then repeats it whole. A host that knows its graphs are bounded may put a number here; a number on
    /// the node still wins, and leaf blocks are untouched either way.</summary>
    /// <remarks>Set by the host rather than read from the definition: the per-node number lives in a row the
    /// library owns, so a host with graphs it did not author has nowhere else to say this. A container nested in
    /// another gets the ceiling too, so an outer one has to cover everything its inner ones do. Non-positive is
    /// refused: "no ceiling" is null, and a zero would otherwise be clamped up to one second in silence.</remarks>
    public int? DefaultContainerTimeoutSeconds
    {
        get;
        init => field = value is null or (> 0 and <= MaxResilienceSeconds)
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value), value,
                $"A container ceiling is a positive number of seconds, at most {MaxResilienceSeconds} (one "
                + "day, the longest deadline this engine can apply); use null for none.");
    }

    /// <summary>The zone a workflow's local wall-clock expression fields are read in, for a deployment whose
    /// zone is a setting. UTC when the host has no notion of place.</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    /// <summary>The same zone for a host where it is a lookup rather than a setting — a home, a tenant. Called
    /// once per graph walk, with that walk's scope; set, it is what <see cref="TimeZone"/> would have answered.</summary>
    public Func<IServiceProvider, CancellationToken, Task<TimeZoneInfo>>? ResolveTimeZone { get; init; }

    /// <summary>The namespaces registered together with their compile-time checks.</summary>
    public IReadOnlyList<WorkflowExpressionNamespace> ExpressionNamespaces { get; init; } = [];

    /// <summary>The block types whose delay is parked in the run's row rather than awaited in this process, so
    /// a replica restart mid-wait resumes instead of losing the run. Empty means every block sleeps its own way.</summary>
    /// <remarks>Named by the host because the library ships no blocks. A named block whose inputs do not carry
    /// a duration is a run that fails at that node, so name only what waits.</remarks>
    public IReadOnlyCollection<string> DurableSleepBlockTypeIds { get; init; } = [];

    /// <summary>The shortest durable sleep that parks the run as a suspension row instead of waiting in this
    /// process. Null — the default — never parks. Only a root-canvas sleep of a caller that can park is eligible.</summary>
    public TimeSpan? ParkSleepWhenAtLeast { get; init; }

    /// <summary>The suspension store's cap on a parked run's payload. A sleep whose run would not fit waits in
    /// this process rather than failing at the store, which is what it did before it could park.</summary>
    public int MaxSuspendPayloadBytes { get; init; } = 256 * 1024;

    /// <summary>How often the walk writes its row. See <see cref="Execution.RunStateWriteMode"/>.</summary>
    public RunStateWriteMode RunStateWriteMode { get; init; } = RunStateWriteMode.EveryBlock;

    /// <summary>The cap on one checkpoint. Past it the write keeps a marker that says so, and a recovery replays the
    /// run from its start.</summary>
    public int MaxCheckpointBytes { get; init; } = 256 * 1024;

    /// <summary>The clock a node's deadline and its retry backoff wait on. Null is the system clock. A parked sleep is
    /// not on it: the waker wakes that against the store's clock.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>The walk's options as a deployment's settings state them. One mapping and one only: a second
    /// copy of a rule like <see cref="RetryOnlyIdempotentBlocks"/> is a second place an answer can go missing,
    /// silently, because every receiving default here is a valid value in its own right.</summary>
    public static WorkflowGraphExecutorOptions For(WorkflowEngineSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new WorkflowGraphExecutorOptions
        {
            MaxPayloadValueBytes = settings.MaxPayloadValueBytes,
            MaxRecordedSteps = settings.MaxRecordedSteps,
            TimeZone = settings.TimeZone,
            ResolveTimeZone = settings.ResolveTimeZone,
            ExpressionNamespaces = settings.ExpressionNamespaces,
            DurableSleepBlockTypeIds = settings.DurableSleepBlockTypeIds,
            ParkSleepWhenAtLeast = settings.ParkSleepWhenAtLeast,
            MaxSuspendPayloadBytes = settings.MaxSuspendPayloadBytes,
            RunStateWriteMode = settings.RunStateWriteMode,
            MaxCheckpointBytes = settings.MaxCheckpointBytes,
            RetryOnlyIdempotentBlocks = settings.RetryOnlyIdempotentBlocks,
            DefaultContainerTimeoutSeconds = settings.DefaultContainerTimeoutSeconds,
            TimeProvider = settings.TimeProvider
        };
    }
}
