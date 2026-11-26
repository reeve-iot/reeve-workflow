using System.Text.Json;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal static class HelperPathsInDocument
{
    internal static IReadOnlyCollection<HelperPath> Collect(WorkflowDefinition definition, ExpressionKeyShapes shapes)
    {
        var expressions = CanvasWalk.Frames(definition)
            .SelectMany(frame => frame.Canvas.Nodes.SelectMany(node => WorkflowCompiler.ExpressionSources(frame.Canvas, node).Select(source => source.Source)));
        return CollectExpressions(expressions, shapes);
    }

    internal static IReadOnlyCollection<HelperPath> CollectExpressions(IEnumerable<string> expressions, ExpressionKeyShapes shapes)
    {
        var paths = new HashSet<HelperPath>();
        foreach (var expression in expressions)
        {
            try { Visit(ExpressionSyntaxCache.ParseCached(expression)); }
            catch (ExpressionSyntaxException) { }
        }
        return paths;

        void Visit(ExpressionNode node)
        {
            switch (node)
            {
                case VariableNode variable when ExpressionPath.TrySplit(variable.Path, shapes, out var parts, out _) && parts.Namespace == "helper":
                    paths.Add(new HelperPath(parts.Key));
                    break;
                case UnaryNode unary: Visit(unary.Operand); break;
                case BinaryNode binary: Visit(binary.Left); Visit(binary.Right); break;
                case TernaryNode ternary: Visit(ternary.Condition); Visit(ternary.WhenTrue); Visit(ternary.WhenFalse); break;
                case CallNode call:
                    foreach (var argument in call.Args) Visit(argument);
                    break;
            }
        }
    }

    internal static IReadOnlyCollection<HelperPath> CollectTexts(IEnumerable<string?> texts, ExpressionKeyShapes shapes)
    {
        var expressions = new List<string>();
        foreach (var text in texts.Where(text => text is not null))
        {
            try { using var document = JsonDocument.Parse(text!); Visit(document.RootElement); }
            catch (JsonException) { }
        }
        return CollectExpressions(expressions, shapes);

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                string? value;
                try { value = element.GetString(); }
                catch (InvalidOperationException) { return; }
                if (value is not null && ExpressionSource.IsExpression(value))
                    expressions.Add(ExpressionSource.Strip(value));
            }
            else if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) Visit(property.Value);
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Visit(item);
        }
    }
}
