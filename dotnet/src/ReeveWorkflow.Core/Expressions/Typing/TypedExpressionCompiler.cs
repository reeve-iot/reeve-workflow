using System.Runtime.CompilerServices;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

internal sealed class ExpressionClosure
{
    private readonly Func<ExpressionEvaluationScope, object?> root;

    internal ExpressionClosure(PortTerm term, Func<ExpressionEvaluationScope, object?> root)
    {
        Term = term;
        this.root = root;
    }

    public PortTerm Term { get; }

    public object? Evaluate(ExpressionEvaluationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return root(scope);
    }
}

internal static partial class TypedExpressionCompiler
{
    private static readonly ConditionalWeakTable<TypedExpression, ExpressionClosure> Cache = new();

    public static ExpressionClosure For(TypedExpression tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return Cache.GetValue(tree, Compile);
    }

    public static ExpressionClosure Compile(TypedExpression tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return new ExpressionClosure(tree.Term, Build(tree, 0).As<object?>());
    }

    private static ClosureNode Build(TypedExpression node, int depth)
    {
        if (depth > TypedExpressionEvaluator.MaxEvaluationDepth)
        {
            var position = node.Position;
            return ClosureNode.Of<object?>(_ => throw new ExpressionEvaluationException("expression is too deeply nested to evaluate", position));
        }

        return node switch
        {
            TypedLiteral literal => Literal(literal),
            TypedRead read => Read(read),
            TypedConversion conversion => Conversion(conversion, depth),
            TypedOperation operation => Operation(operation, depth),
            TypedProjection projection => Projection(projection, depth),
            _ => throw new InvalidOperationException($"Unknown typed expression node '{node.GetType().Name}'."),
        };
    }

    private static ClosureNode Literal(TypedLiteral node)
    {
        var value = node.Value;
        return ClosureNode.ShapeOf(node.Term) switch
        {
            ClosureShape.Int => Constant((long)value!),
            ClosureShape.Number => Constant((double)value!),
            ClosureShape.Boolean => Constant((bool)value!),
            ClosureShape.Text => Constant((string)value!),
            _ => Constant(value),
        };
    }

    private static ClosureNode Constant<T>(T value) => ClosureNode.Of<T>(_ => value);

    private static ClosureNode Conversion(TypedConversion node, int depth)
    {
        var operand = Build(node.Operand, depth + 1);
        var converted = node.Kind switch
        {
            TypedConversionKind.Widen => Widen(operand),
            TypedConversionKind.ToText => ToText(operand),
            _ => ConvertTo(node, operand),
        };
        return converted.In(ClosureNode.ShapeOf(node.Term));
    }

    private static ClosureNode Widen(ClosureNode operand)
    {
        if (operand.Shape == ClosureShape.Int)
        {
            var whole = operand.As<long>();
            return ClosureNode.Of<double>(scope => whole(scope));
        }

        var value = operand.As<object?>();
        return ClosureNode.Of<object?>(scope => value(scope) is { } present ? (double)(long)present : null);
    }

    private static ClosureNode ToText(ClosureNode operand)
    {
        switch (operand.Shape)
        {
            case ClosureShape.Int:
                var whole = operand.As<long>();
                return ClosureNode.Of<string>(scope => TermValues.IntText(whole(scope)));
            case ClosureShape.Number:
                var number = operand.As<double>();
                return ClosureNode.Of<string>(scope => TermValues.NumberText(number(scope)));
            case ClosureShape.Boolean:
                var flag = operand.As<bool>();
                return ClosureNode.Of<string>(scope => TermValues.BooleanText(flag(scope)));
            case ClosureShape.Text:
                return operand;
            default:
                var value = operand.As<object?>();
                return ClosureNode.Of<string>(scope => TermValues.Text(value(scope)));
        }
    }

    private static ClosureNode ConvertTo(TypedConversion node, ClosureNode operand)
    {
        var value = operand.As<object?>();
        var from = node.Operand.Term;
        var to = node.Term;
        return ClosureNode.Of<object?>(scope => TermValues.Convert(value(scope), from, to, scope.Vocabulary));
    }

    private static ClosureNode Projection(TypedProjection node, int depth)
    {
        var subject = Build(node.Subject, depth + 1).As<object?>();
        var label = $"{(node.JsonText ? "jsonPathText" : "jsonPath")} at position {node.Position}";
        var projection = node.Projection;
        var position = node.Position;
        return ClosureNode.Of<object?>(scope =>
        {
            var source = subject(scope);
            try
            {
                return projection.TryBind(source, label, out var value) ? value : null;
            }
            catch (JsonProjectionException failure)
            {
                throw new ExpressionEvaluationException(failure.Message, position, failure, failure.Category);
            }
        }).In(ClosureNode.ShapeOf(node.Term));
    }
}
