using System.Collections.Frozen;

namespace ReeveWorkflow.Core.Expressions;

internal static partial class TypedExpressionCompiler
{
    private delegate ClosureNode Binder(TypedOperation node, ClosureNode[] operands);

    internal static IEnumerable<string> BoundOverloads => Binders.Keys;

    private static readonly FrozenDictionary<string, Binder> Binders = new Dictionary<string, Binder>(StringComparer.Ordinal)
    {
        ["int-add"] = Binary<long, long, long>(ExpressionOperations.IntAdd),
        ["number-add"] = Binary<double, double, double>(ExpressionOperations.NumberAdd),
        ["string-concat"] = Binary<string, string, string>(ExpressionOperations.Concat),
        ["int-sub"] = Binary<long, long, long>(ExpressionOperations.IntSubtract),
        ["number-sub"] = Binary<double, double, double>(ExpressionOperations.NumberSubtract),
        ["int-mul"] = Binary<long, long, long>(ExpressionOperations.IntMultiply),
        ["number-mul"] = Binary<double, double, double>(ExpressionOperations.NumberMultiply),
        ["int-rem"] = Binary<long, long, long>(ExpressionOperations.IntRemainder),
        ["number-rem"] = Binary<double, double, double>(ExpressionOperations.NumberRemainder),
        ["number-div"] = Binary<double, double, double>(ExpressionOperations.NumberDivide),
        ["int-neg"] = Unary<long, long>(ExpressionOperations.IntNegate),
        ["number-neg"] = Unary<double, double>(ExpressionOperations.NumberNegate),
        ["equal"] = Equality(negated: false),
        ["not-equal"] = Equality(negated: true),
        ["not"] = Unary<bool, bool>(ExpressionOperations.Not),
        ["min-int"] = Extremum<long>(ExpressionOperations.MinInt),
        ["min-number"] = Extremum<double>(ExpressionOperations.MinNumber),
        ["max-int"] = Extremum<long>(ExpressionOperations.MaxInt),
        ["max-number"] = Extremum<double>(ExpressionOperations.MaxNumber),
        ["abs-int"] = Unary<long, long>(ExpressionOperations.AbsInt),
        ["abs-number"] = Unary<double, double>(ExpressionOperations.AbsNumber),
        ["round-number"] = Unary<double, long>(ExpressionOperations.RoundNumber),
        ["round-int"] = Unary<long, long>(ExpressionOperations.RoundInt),
        ["floor-number"] = Unary<double, long>(ExpressionOperations.FloorNumber),
        ["floor-int"] = Unary<long, long>(ExpressionOperations.FloorInt),
        ["ceil-number"] = Unary<double, long>(ExpressionOperations.CeilNumber),
        ["ceil-int"] = Unary<long, long>(ExpressionOperations.CeilInt),
        ["len"] = Unary<string, long>(ExpressionOperations.Length),
        ["contains"] = Binary<string, string, bool>(ExpressionOperations.Contains),
        ["starts-with"] = Binary<string, string, bool>(ExpressionOperations.StartsWith),
        ["ends-with"] = Binary<string, string, bool>(ExpressionOperations.EndsWith),
        ["to-number-int"] = Unary<long, double>(ExpressionOperations.ToNumberInt),
        ["to-number-string"] = Unary<string, object?>(ExpressionOperations.ToNumberString),
        ["to-string-boolean"] = Unary<bool, string>(ExpressionOperations.ToStringBoolean),
        ["to-string-int"] = Unary<long, string>(ExpressionOperations.ToStringInt),
        ["to-string-number"] = Unary<double, string>(ExpressionOperations.ToStringNumber),
        ["to-string-string"] = Unary<string, string>(ExpressionOperations.ToStringString),
        ["to-string-datetime"] = Unary<DateTimeOffset, string>(ExpressionOperations.ToStringDateTime),
        ["parse-json"] = Unary<string, object?>(ExpressionOperations.ParseJson),
        ["lt-int"] = Binary<long, long, bool>(ExpressionOperations.LessInt),
        ["lt-number"] = Binary<double, double, bool>(ExpressionOperations.LessNumber),
        ["lt-string"] = Binary<string, string, bool>(ExpressionOperations.LessString),
        ["lt-datetime"] = Binary<DateTimeOffset, DateTimeOffset, bool>(ExpressionOperations.LessDateTime),
        ["le-int"] = Binary<long, long, bool>(ExpressionOperations.LessOrEqualInt),
        ["le-number"] = Binary<double, double, bool>(ExpressionOperations.LessOrEqualNumber),
        ["le-string"] = Binary<string, string, bool>(ExpressionOperations.LessOrEqualString),
        ["le-datetime"] = Binary<DateTimeOffset, DateTimeOffset, bool>(ExpressionOperations.LessOrEqualDateTime),
        ["gt-int"] = Binary<long, long, bool>(ExpressionOperations.GreaterInt),
        ["gt-number"] = Binary<double, double, bool>(ExpressionOperations.GreaterNumber),
        ["gt-string"] = Binary<string, string, bool>(ExpressionOperations.GreaterString),
        ["gt-datetime"] = Binary<DateTimeOffset, DateTimeOffset, bool>(ExpressionOperations.GreaterDateTime),
        ["ge-int"] = Binary<long, long, bool>(ExpressionOperations.GreaterOrEqualInt),
        ["ge-number"] = Binary<double, double, bool>(ExpressionOperations.GreaterOrEqualNumber),
        ["ge-string"] = Binary<string, string, bool>(ExpressionOperations.GreaterOrEqualString),
        ["ge-datetime"] = Binary<DateTimeOffset, DateTimeOffset, bool>(ExpressionOperations.GreaterOrEqualDateTime),
        ["and"] = And,
        ["or"] = Or,
        ["conditional"] = Conditional,
        ["coalesce"] = Coalesce,
        ["to-json-text"] = ToJsonText,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static ClosureNode Operation(TypedOperation node, int depth)
    {
        if (!Binders.TryGetValue(node.Overload, out var bind))
            throw new InvalidOperationException($"Unknown expression overload '{node.Overload}'.");
        var operands = new ClosureNode[node.Operands.Count];
        for (var i = 0; i < operands.Length; i++)
            operands[i] = Build(node.Operands[i], depth + 1);
        return bind(node, operands).In(ClosureNode.ShapeOf(node.Term));
    }

    private static void Arity(TypedOperation node, ClosureNode[] operands, int expected)
    {
        if (operands.Length != expected)
            throw new InvalidOperationException($"The overload '{node.Overload}' takes {expected} operands, not {operands.Length}.");
    }

    private static ExpressionEvaluationException Failed(ExpressionOperationException failure, int position) =>
        new(failure.Message, position, category: failure.Category);

    private static Binder Unary<TOperand, TResult>(Func<TOperand, TResult> operation) => (node, operands) =>
    {
        Arity(node, operands, 1);
        var operand = operands[0].As<TOperand>();
        var position = node.Position;
        return ClosureNode.Of<TResult>(scope =>
        {
            var value = operand(scope);
            try
            {
                return operation(value);
            }
            catch (ExpressionOperationException failure)
            {
                throw Failed(failure, position);
            }
        });
    };

    private static Binder Binary<TLeft, TRight, TResult>(Func<TLeft, TRight, TResult> operation) => (node, operands) =>
    {
        Arity(node, operands, 2);
        var left = operands[0].As<TLeft>();
        var right = operands[1].As<TRight>();
        var position = node.Position;
        return ClosureNode.Of<TResult>(scope =>
        {
            var leftValue = left(scope);
            var rightValue = right(scope);
            try
            {
                return operation(leftValue, rightValue);
            }
            catch (ExpressionOperationException failure)
            {
                throw Failed(failure, position);
            }
        });
    };

    private static Binder Extremum<T>(Func<T, T, T> pick) => (node, operands) =>
    {
        if (operands.Length == 0)
            throw new InvalidOperationException($"The overload '{node.Overload}' takes at least one operand.");
        var values = new Func<ExpressionEvaluationScope, T>[operands.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = operands[i].As<T>();
        return ClosureNode.Of<T>(scope =>
        {
            var result = values[0](scope);
            for (var i = 1; i < values.Length; i++)
                result = pick(result, values[i](scope));
            return result;
        });
    };

    private static Binder Equality(bool negated) => (node, operands) =>
    {
        Arity(node, operands, 2);
        var shape = operands[0].Shape;
        if (shape != ClosureShape.Object && shape == operands[1].Shape)
        {
            return shape switch
            {
                ClosureShape.Int => Equal<long>(operands, ExpressionOperations.EqualInt, negated),
                ClosureShape.Number => Equal<double>(operands, ExpressionOperations.EqualNumber, negated),
                ClosureShape.Boolean => Equal<bool>(operands, ExpressionOperations.EqualBoolean, negated),
                _ => Equal<string>(operands, ExpressionOperations.EqualString, negated),
            };
        }

        return Equal<object?>(operands, TermValues.AreEqual, negated);
    };

    private static ClosureNode Equal<T>(ClosureNode[] operands, Func<T, T, bool> equal, bool negated)
    {
        var left = operands[0].As<T>();
        var right = operands[1].As<T>();
        return ClosureNode.Of<bool>(scope =>
        {
            var leftValue = left(scope);
            return equal(leftValue, right(scope)) != negated;
        });
    }

    private static ClosureNode And(TypedOperation node, ClosureNode[] operands)
    {
        Arity(node, operands, 2);
        var left = operands[0].As<bool>();
        var right = operands[1].As<bool>();
        return ClosureNode.Of<bool>(scope => left(scope) && right(scope));
    }

    private static ClosureNode Or(TypedOperation node, ClosureNode[] operands)
    {
        Arity(node, operands, 2);
        var left = operands[0].As<bool>();
        var right = operands[1].As<bool>();
        return ClosureNode.Of<bool>(scope => left(scope) || right(scope));
    }

    private static ClosureNode Conditional(TypedOperation node, ClosureNode[] operands)
    {
        Arity(node, operands, 3);
        var condition = operands[0].As<bool>();
        var whenTrue = operands[1];
        var whenFalse = operands[2];
        return ClosureNode.ShapeOf(node.Term) switch
        {
            ClosureShape.Int => Pick(condition, whenTrue.As<long>(), whenFalse.As<long>()),
            ClosureShape.Number => Pick(condition, whenTrue.As<double>(), whenFalse.As<double>()),
            ClosureShape.Boolean => Pick(condition, whenTrue.As<bool>(), whenFalse.As<bool>()),
            ClosureShape.Text => Pick(condition, whenTrue.As<string>(), whenFalse.As<string>()),
            _ => Pick(condition, whenTrue.As<object?>(), whenFalse.As<object?>()),
        };
    }

    private static ClosureNode Pick<T>(
        Func<ExpressionEvaluationScope, bool> condition, Func<ExpressionEvaluationScope, T> whenTrue, Func<ExpressionEvaluationScope, T> whenFalse) =>
        ClosureNode.Of<T>(scope => condition(scope) ? whenTrue(scope) : whenFalse(scope));

    private static ClosureNode Coalesce(TypedOperation node, ClosureNode[] operands)
    {
        Arity(node, operands, 2);
        var first = operands[0].As<object?>();
        var fallback = operands[1];
        return ClosureNode.ShapeOf(node.Term) switch
        {
            ClosureShape.Int => Otherwise(first, fallback.As<long>()),
            ClosureShape.Number => Otherwise(first, fallback.As<double>()),
            ClosureShape.Boolean => Otherwise(first, fallback.As<bool>()),
            ClosureShape.Text => Otherwise(first, fallback.As<string>()),
            _ => Otherwise(first, fallback.As<object?>()),
        };
    }

    private static ClosureNode Otherwise<T>(Func<ExpressionEvaluationScope, object?> first, Func<ExpressionEvaluationScope, T> fallback) =>
        ClosureNode.Of<T>(scope => first(scope) is { } value ? (T)value : fallback(scope));

    private static ClosureNode ToJsonText(TypedOperation node, ClosureNode[] operands)
    {
        Arity(node, operands, 1);
        var operand = operands[0].As<object?>();
        var term = node.Operands[0].Term;
        var position = node.Position;
        return ClosureNode.Of<string>(scope =>
        {
            var value = operand(scope);
            var type = scope.Vocabulary.ClosedTypeOf(term)
                ?? throw new ExpressionEvaluationException($"the term '{term}' has no CLR type", position);
            string? text;
            try
            {
                text = JsonText.Write(type, value!);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ExpressionEvaluationException($"writing a value of '{term}' as JSON threw {ex.GetType().Name}: {ex.Message}", position, ex);
            }

            return text ?? throw new ExpressionEvaluationException(
                $"a value of '{term}' writes JSON null, so its JsonSerializable claim does not hold", position);
        });
    }
}
