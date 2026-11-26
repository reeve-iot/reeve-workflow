using System.Text.Json;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

public static partial class WorkflowCompiler
{
    private static readonly IReadOnlyDictionary<string, bool> ResilienceNumberKeyIsInteger =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [WorkflowGraphExecutor.ConfigTimeoutSeconds] = true,
            [WorkflowGraphExecutor.ConfigRetryCount] = true,
            [WorkflowGraphExecutor.ConfigRetryBackoffSeconds] = false,
        };

    private static void CheckResilienceConfig(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context)
    {
        if (IsExemptFromResilienceChecks(node))
            return;

        foreach (var key in WorkflowGraphExecutor.PollyConfigKeys)
        {
            if (ResilienceNumberKeyIsInteger.TryGetValue(key, out var asInt))
                CheckResilienceNumber(frame, node, shape, context, key, asInt);
            else
                CheckResilienceBoolean(frame, node, shape, context, key);
        }

        CheckTimeoutRange(frame, node, shape, context);
    }

    private static void CheckResilienceBoolean(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context, string key)
    {
        if (IsConfiguredResilienceValue(node, key, out var value)
            && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            context.Diagnostics.Add(new CompilationDiagnostic(
                CompilationRules.ResilienceConfigInvalid,
                $"Node {context.NodeName(frame.Canvas, node.Id)} has '{key}' set to {value.ValueKind}, which is not a boolean.",
                node.Id, key, frame.Path.Segments, shape.Source));
        }
    }

    private static void CheckResilienceNumber(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context, string key, bool asInt)
    {
        if (!IsConfiguredResilienceValue(node, key, out var element))
            return;

        try
        {
            if (element.ValueKind != JsonValueKind.Number)
                throw new FormatException($"{element.ValueKind} is not a number");
            _ = asInt ? element.GetInt32() : element.GetDouble();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(
                CompilationRules.ResilienceConfigInvalid,
                $"Node {context.NodeName(frame.Canvas, node.Id)} has '{key}' set to a value the engine cannot read: {ex.Message}",
                node.Id, key, frame.Path.Segments, shape.Source));
        }
    }

    private static void CheckTimeoutRange(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context)
    {
        if (!IsConfiguredResilienceValue(node, WorkflowGraphExecutor.ConfigTimeoutSeconds, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var seconds)
            || seconds <= WorkflowGraphExecutorOptions.MaxResilienceSeconds)
        {
            return;
        }

        context.Diagnostics.Add(new CompilationDiagnostic(
            CompilationRules.ResilienceConfigInvalid,
            $"Node {context.NodeName(frame.Canvas, node.Id)} sets '{WorkflowGraphExecutor.ConfigTimeoutSeconds}' to {seconds}, and the "
            + $"longest deadline this engine can apply is {WorkflowGraphExecutorOptions.MaxResilienceSeconds} "
            + "seconds (one day).",
            node.Id, WorkflowGraphExecutor.ConfigTimeoutSeconds, frame.Path.Segments, shape.Source));
    }

    internal static bool IsExemptFromResilienceChecks(WorkflowNodeDefinition node) =>
        BoundaryBlockShapes.IsBoundary(node.BlockTypeId);

    private static bool IsConfiguredResilienceValue(WorkflowNodeDefinition node, string key, out JsonElement element)
    {
        if (node.Config is not null && node.Config.TryGetValue(key, out element) && element.ValueKind != JsonValueKind.Null)
            return true;

        element = default;
        return false;
    }
}
