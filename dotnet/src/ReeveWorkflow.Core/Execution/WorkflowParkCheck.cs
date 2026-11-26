using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Execution;

internal sealed class WorkflowSuspendConfigInvalidException(string nodeId, string reason)
    : Exception($"{WorkflowErrorCodes.SuspendConfigInvalid}: node '{nodeId}': {reason}."), IWorkflowCodedException
{
    public string Code => WorkflowErrorCodes.SuspendConfigInvalid;
}

internal static class WorkflowParkCheck
{
    private const string ConditionType = "Expression<Boolean>";

    public static WorkflowSuspendException Checked(CompiledNode node, IWorkflowBlockPlugin plugin, WorkflowSuspendException park)
    {
        if (park.SuspendResult.Kind == WorkflowSuspensionKind.Predicate)
            return CheckedPredicate(node, plugin, park);
        if (park.SuspendResult.Kind != WorkflowSuspensionKind.WaitTemplate)
            return park;

        WorkflowWaitTemplateConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<WorkflowWaitTemplateConfig>(
                park.SuspendResult.ConfigJson, JsonMembers.Refusing(WorkflowGraphHelpers.JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new WorkflowSuspendConfigInvalidException(node.NodeId, $"the template config does not read: {ex.Message}");
        }

        if (config is null)
            throw new WorkflowSuspendConfigInvalidException(node.NodeId, "the template config is null");
        var expression = ConditionOf(node, config.Input);
        var stored = new WorkflowStoredTemplateConfig(config.Input, config.PollIntervalSecondsOverride, expression.Text);
        return new WorkflowSuspendException(new SuspendResult(
            park.SuspendResult.Kind, JsonSerializer.Serialize(stored, WorkflowGraphHelpers.JsonOptions), park.SuspendResult.TimeoutAtUtc));
    }

    private static WorkflowSuspendException CheckedPredicate(CompiledNode node, IWorkflowBlockPlugin plugin, WorkflowSuspendException park)
    {
        if (plugin is not IWorkflowResumedOutputs)
            throw new WorkflowSuspendConfigInvalidException(node.NodeId, $"block '{node.Shape.BlockTypeId}' parks a predicate wait and does not implement {nameof(IWorkflowResumedOutputs)}");
        WorkflowPredicateWaitConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<WorkflowPredicateWaitConfig>(
                park.SuspendResult.ConfigJson, JsonMembers.Refusing(WorkflowGraphHelpers.JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new WorkflowSuspendConfigInvalidException(node.NodeId, $"the predicate config does not read: {ex.Message}");
        }

        if (config is null)
            throw new WorkflowSuspendConfigInvalidException(node.NodeId, "the predicate config is null");
        ConditionOf(node, config.Input);
        return new WorkflowSuspendException(new SuspendResult(
            park.SuspendResult.Kind, JsonSerializer.Serialize(config, WorkflowGraphHelpers.JsonOptions), park.SuspendResult.TimeoutAtUtc));
    }

    public static CompiledExpression ConditionOf(CompiledNode node, string input)
    {
        var port = node.Shape.Inputs.FirstOrDefault(p => string.Equals(p.Name, input, StringComparison.Ordinal));
        if (port is null || !IsCondition(node, port))
            throw new WorkflowSuspendConfigInvalidException(node.NodeId, $"'{input}' is not an {ConditionType} input of the node");
        return node.Expressions.TryGetValue(input, out var expression) && expression.Tree is not null
            ? expression
            : throw new WorkflowSuspendConfigInvalidException(node.NodeId, $"input '{input}' holds no compiled expression");
    }

    private static bool IsCondition(CompiledNode node, PortShape port)
    {
        if (!port.Type.IsExpression
            || !PortTerm.TryParse(port.Type.Name, PortTermGrammar.Declaration, out var term, out _)
            || term is not { Kind: PortTermKind.Constructor, Name: "Expression", Arguments: [var argument] })
            return false;
        var result = argument.Kind == PortTermKind.Parameter
            ? node.TypeArguments.GetValueOrDefault(argument.Name)?.Term is { } bound
                ? argument.IsNullable ? bound.AsNullable() : bound
                : null
            : argument;
        return BooleanTerm.Equals(result);
    }

    private static readonly PortTerm BooleanTerm = PortTerm.Atom("Boolean");
}
