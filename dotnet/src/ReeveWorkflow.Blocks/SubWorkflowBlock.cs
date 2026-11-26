using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Blocks;

/// <summary>Runs another workflow as one node: cycle- and depth-guarded, pinned to the version the root run
/// pinned. A host that decides who may run which workflow overrides <see cref="AuthorizeAsync"/>.</summary>
public class SubWorkflowBlock : IWorkflowBlockPlugin
{
    private static readonly AsyncLocal<ImmutableStack<Guid>> _callChain = new();

    private const int MaxSubWorkflowDepth = WorkflowRunAdmission.MaxWorkflowCallDepth;

    private const string SubWorkflowBlockTypeId = "SubWorkflow";

    private static int _silentEngineLogged;

    private static void WarnOnceThatNothingReportsBack(WorkflowBlockContext context)
    {
        if (Interlocked.Exchange(ref _silentEngineLogged, 1) != 0)
            return;

        context.Logger.LogWarning(
            "The engine behind this sub-workflow block does not report whether the run it started parked. A child "
            + "that parks answers with the outputs it has, which are empty, and this run walks on past the block. "
            + "Said once for the life of this process.");
    }

    private sealed record SubWorkflowWait(Guid ChildExecutionLogId);

    /// <summary>Called once the node's workflow id is known and before anything of it runs. Throw to refuse.</summary>
    /// <remarks>The id is block configuration rather than something the caller chose, so a host whose runs
    /// belong to users has to check here that this run's user may run that workflow. The default permits it.</remarks>
    protected virtual Task AuthorizeAsync(
        Guid workflowId, WorkflowBlockContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    private static IReadOnlyDictionary<string, ClosedPortType> AnswerTypes(WorkflowBlockContext context) =>
        context.OutputTypes ?? context.Types;

    private static Dictionary<string, object?> Materialised(IEnumerable<KeyValuePair<string, object?>> answer, WorkflowBlockContext context)
    {
        var outputs = new Dictionary<string, object?>();
        foreach (var (name, value) in answer)
        {
            outputs[name] = value is not null && AnswerTypes(context).TryGetValue(name, out var type)
                && type.JsonTypeInfo is { } info && !type.ClrType.IsInstanceOfType(value)
                    ? ReadAs(value, info)
                    : value;
        }

        return outputs;
    }

    private static object? ReadAs(object value, JsonTypeInfo info)
    {
        try
        {
            return JsonSerializer.Deserialize(JsonSerializer.SerializeToElement(value), info);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        IReadOnlyDictionary<string, object?> inputs,
        WorkflowBlockContext context,
        CancellationToken cancellationToken)
    {
        var workflowIdStr = BlockInputCoercion.String(inputs, "WorkflowId");
        if (workflowIdStr is null || !Guid.TryParse(workflowIdStr, out var workflowId))
        {
            throw new ArgumentException("WorkflowId is required and must be a valid Guid string.");
        }

        if (WorkflowSubRunResumeScope.TakeFor(context.NodeId) is { } answered)
        {
            if (!answered.Succeeded)
            {
                throw new WorkflowNodeExecutionException(
                    answered.Error ?? $"The workflow run '{answered.ExecutionLogId}' this block started did not succeed.",
                    context.NodeId ?? string.Empty, SubWorkflowBlockTypeId);
            }

            return PersistedValueCodec.ReadChildAnswer(
                answered.OutputsJson ?? throw new InvalidOperationException("The answer of the run this block started is missing."),
                AnswerTypes(context), context.NodeId);
        }

        var engine = context.Services.GetRequiredService<IWorkflowEngine>();

        var chain = _callChain.Value ?? ImmutableStack<Guid>.Empty;
        if (chain.Contains(workflowId))
            throw new InvalidOperationException(
                $"Sub-workflow cycle detected: workflow '{workflowId}' is already running on this execution chain.");
        if (chain.Count() >= MaxSubWorkflowDepth)
            throw new InvalidOperationException(
                $"Sub-workflow call chain exceeded the maximum depth of {MaxSubWorkflowDepth}. Possible runaway recursion.");

        await AuthorizeAsync(workflowId, context, cancellationToken).ConfigureAwait(false);

        var manifest = WorkflowVersionPinScope.Current;
        Guid? pinnedChildVersion = null;
        if (manifest != null)
        {
            if (!manifest.TryGetValue(workflowId, out var childVersionId))
                throw new InvalidOperationException(
                    $"Sub-workflow '{workflowId}' is not in the run's pinned version manifest. " +
                    "It was not published when the root run started.");
            pinnedChildVersion = childVersionId;
        }

        _callChain.Value = chain.Push(workflowId);
        try
        {
            using var pinScope = WorkflowRunPinScope.Push(new WorkflowRunPin
            {
                VersionId = pinnedChildVersion,
                Manifest = manifest,
                CallDepth = WorkflowCallDepthScope.Current + 1
            });
            using var observationScope = WorkflowRunObservationScope.Push(
                new WorkflowRunObservation { RecordLogEntries = true });
            var childOutcome = new WorkflowSubRunOutcome();
            using var outcomeScope = WorkflowSubRunOutcomeScope.Push(childOutcome);
            var arguments = inputs.Where(input => input.Key != "WorkflowId").ToDictionary(input => input.Key, input => input.Value);
            var outputs = await engine.ExecuteAsync(
                workflowId,
                fire: null,
                inputParameters: arguments,
                parentExecutionId: context.ExecutionId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (childOutcome is { Reported: true, Parked: true })
            {
                throw new WorkflowSuspendException(new SuspendResult(
                    WorkflowSuspensionKind.SubWorkflow,
                    JsonSerializer.Serialize(new SubWorkflowWait(childOutcome.ExecutionLogId)),
                    DateTimeOffset.MaxValue));
            }

            if (childOutcome.RefusedBecause is { } refusal)
            {
                throw new WorkflowNodeExecutionException(
                    $"Sub-workflow '{workflowId}' was not started: {refusal}.",
                    context.NodeId ?? string.Empty, SubWorkflowBlockTypeId);
            }

            if (!childOutcome.Reported)
                WarnOnceThatNothingReportsBack(context);

            return Materialised(outputs, context);
        }
        finally
        {
            _callChain.Value = chain;
        }
    }
}

/// <summary>Registers the standard <c>SubWorkflow</c> block. Derive and override <see cref="Create"/> to put a
/// subclass of <see cref="SubWorkflowBlock"/> behind the same id — that is where an authorisation check goes.</summary>
public class SubWorkflowBlockFactory : IWorkflowBlockFactory
{
    public string BlockTypeId => "SubWorkflow";
    public string DisplayName => "Run Sub-Workflow";
    public string Description => "Executes another workflow as a block.";
    public string Category => "Workflow";
    /// <summary>Runs another workflow whole; whatever its blocks send or spend would be sent or spent again.</summary>
    public bool IsIdempotent => false;

    /// <summary>A child that parks parks its parent, so this block may park the run it is in.</summary>
    public bool CanSuspend => true;

    /// <summary>Its ports are the child workflow's parameters, not <see cref="Outputs"/> below (empty) or
    /// the single input <see cref="Inputs"/> declares — a compiler with no resolver for this must refuse it.</summary>
    public bool PortsAreResolvedElsewhere => true;

    /// <summary>Mirrors the config field of the same name below: the node's <c>WorkflowId</c> value chooses
    /// the child, so a wire feeding it — deciding the child at run time — is refused rather than resolved
    /// against whichever workflow's parameters happened to be filed for this node at compile time.</summary>
    public string? RunsWorkflowFrom => "WorkflowId";

    public IReadOnlyCollection<BlockPortDefinition> Inputs => new[]
    {
        new BlockPortDefinition("WorkflowId", "Workflow ID", PortValueType.String, true)
    };

    public IReadOnlyCollection<BlockPortDefinition> Outputs => [];

    public IReadOnlyCollection<BlockConfigFieldDefinition> ConfigurationSchema => new[]
    {
        new BlockConfigFieldDefinition(
            "WorkflowId", "Workflow", ConfigFieldType.String, Required: false,
            Description: "The workflow this block runs."),
    };

    public virtual IWorkflowBlockPlugin Create() => new SubWorkflowBlock();
}
