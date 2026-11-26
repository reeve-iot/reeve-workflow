using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

internal sealed partial class TypingRun
{
    private TypedExpression? Unary(UnaryNode node)
    {
        if (Visit(node.Operand) is not { } operand)
            return null;
        var row = ExpressionSignatures.Bind(ExpressionSignatureKind.Operator, node.Op, [operand.Term]);
        return row is null
            ? Mismatch(ExpressionSignatureKind.Operator, node.Op, [operand], node.Position)
            : Operation(row, [operand], node.Position);
    }

    private TypedExpression? Binary(BinaryNode node)
    {
        var left = Visit(node.Left);
        var right = Visit(node.Right);
        if (left is null || right is null)
            return null;

        if (node.Op == "??")
            return Coalesce(node, left, right);

        return node.Op switch
        {
            "+" => Plus(node, left, right),
            "-" or "*" or "%" => Arithmetic(node, left, right, widenAll: false),
            "/" => Arithmetic(node, left, right, widenAll: true),
            "<" or "<=" or ">" or ">=" => Comparison(node, left, right),
            "==" or "!=" => Equality(node, left, right),
            _ => Logic(node, left, right),
        };
    }

    private TypedExpression? Coalesce(BinaryNode node, TypedExpression left, TypedExpression right)
    {
        if (!left.Term.IsNullable || left.Term.Kind == PortTermKind.Null)
            return Mismatch(ExpressionSignatureKind.Operator, node.Op, [left, right], node.Position);
        var leftValue = Terms.Strip(left.Term);
        var joined = vocabulary.LeastUpperBound([leftValue, right.Term]);
        if (joined is null)
            return Mismatch(ExpressionSignatureKind.Operator, node.Op, [left, right], node.Position);
        var nullableJoin = joined.AsNullable();
        return new TypedOperation("coalesce", [BroughtTo(left, nullableJoin), BroughtTo(right, joined)], joined, node.Position);
    }

    private TypedExpression? Logic(BinaryNode node, TypedExpression left, TypedExpression right)
    {
        var row = ExpressionSignatures.Bind(ExpressionSignatureKind.Operator, node.Op, [left.Term, right.Term]);
        return row is null
            ? Mismatch(ExpressionSignatureKind.Operator, node.Op, [left, right], node.Position)
            : Operation(row, [left, right], node.Position);
    }

    private TypedExpression? Arithmetic(BinaryNode node, TypedExpression left, TypedExpression right, bool widenAll)
    {
        if (!Terms.IsNumeric(left.Term) || !Terms.IsNumeric(right.Term))
            return Mismatch(ExpressionSignatureKind.Operator, node.Op, [left, right], node.Position);
        return Numbers(node, left, right, widenAll);
    }

    private TypedExpression? Numbers(BinaryNode node, TypedExpression left, TypedExpression right, bool widenAll)
    {
        if (!widenAll && left.Term.Equals(Terms.Int) && right.Term.Equals(Terms.Int))
            return Bound(node, left, right);
        return Bound(node, Widened(left), Widened(right));
    }

    private TypedExpression? Bound(BinaryNode node, TypedExpression left, TypedExpression right)
    {
        var row = ExpressionSignatures.Bind(ExpressionSignatureKind.Operator, node.Op, [left.Term, right.Term]);
        return row is null
            ? Mismatch(ExpressionSignatureKind.Operator, node.Op, [left, right], node.Position)
            : Operation(row, [left, right], node.Position);
    }

    private TypedExpression? Plus(BinaryNode node, TypedExpression left, TypedExpression right)
    {
        if (Terms.IsNumeric(left.Term) && Terms.IsNumeric(right.Term))
            return Numbers(node, left, right, widenAll: false);
        if ((left.Term.Equals(Terms.String) && IsText(right.Term)) || (right.Term.Equals(Terms.String) && IsText(left.Term)))
        {
            var row = ExpressionSignatures.Get("string-concat");
            return Operation(row, [AsText(left), AsText(right)], node.Position);
        }

        return Mismatch(ExpressionSignatureKind.Operator, node.Op, [left, right], node.Position);
    }

    private static bool IsText(PortTerm term) =>
        term.Equals(Terms.String) || term.Equals(Terms.Int) || term.Equals(Terms.Number)
        || term.Equals(Terms.Boolean) || term.Equals(Terms.DateTime);

    private static TypedExpression AsText(TypedExpression node) =>
        node.Term.Equals(Terms.String) ? node : new TypedConversion(TypedConversionKind.ToText, node, Terms.String);

    private TypedExpression? Comparison(BinaryNode node, TypedExpression left, TypedExpression right)
    {
        if (Terms.IsNumeric(left.Term) && Terms.IsNumeric(right.Term))
            return Numbers(node, left, right, widenAll: false);
        return Bound(node, left, right);
    }

    private TypedExpression? Equality(BinaryNode node, TypedExpression left, TypedExpression right)
    {
        var row = ExpressionSignatures.Get(node.Op == "==" ? "equal" : "not-equal");
        var leftNull = left.Term.Kind == PortTermKind.Null;
        var rightNull = right.Term.Kind == PortTermKind.Null;
        if (leftNull || rightNull)
        {
            var other = leftNull ? right.Term : left.Term;
            return leftNull && rightNull || !other.IsNullable || Terms.IsJson(other)
                ? Mismatch(ExpressionSignatureKind.Operator, node.Op, [left, right], node.Position)
                : Operation(row, [left, right], node.Position);
        }

        var leftCore = Terms.Strip(left.Term);
        var rightCore = Terms.Strip(right.Term);
        if (Terms.IsNumeric(leftCore) && Terms.IsNumeric(rightCore) && !leftCore.Equals(rightCore))
        {
            left = Widened(left);
            right = Widened(right);
            leftCore = rightCore = Terms.Number;
        }

        return leftCore.Equals(rightCore) && IsEquatable(leftCore)
            ? Operation(row, [left, right], node.Position)
            : Mismatch(ExpressionSignatureKind.Operator, node.Op, [left, right], node.Position);
    }

    private bool IsEquatable(PortTerm term) =>
        vocabulary.RefusalOf(term) is null && vocabulary.Implements(term, WorkflowCapability.Equatable);

    private TypedExpression? Conditional(TernaryNode node)
    {
        var condition = Visit(node.Condition);
        var whenTrue = Visit(node.WhenTrue);
        var whenFalse = Visit(node.WhenFalse);
        if (condition is null || whenTrue is null || whenFalse is null)
            return null;

        var row = ExpressionSignatures.Get("conditional");
        if (!condition.Term.Equals(Terms.Boolean))
        {
            return Refuse(CompilationRules.ExpressionTypeMismatch,
                $"the condition of '?:' must be a Boolean, not {condition.Term}; there is no truthiness", node.Position);
        }

        var joined = vocabulary.LeastUpperBound([whenTrue.Term, whenFalse.Term]);
        if (joined is null)
        {
            return Refuse(CompilationRules.ExpressionTypeMismatch,
                $"the branches of '?:' have no least upper bound: {whenTrue.Term} and {whenFalse.Term}", node.Position);
        }

        return new TypedOperation(
            row.Id, [condition, BroughtTo(whenTrue, joined), BroughtTo(whenFalse, joined)], joined, node.Position);
    }

    private static TypedExpression BroughtTo(TypedExpression node, PortTerm joined)
    {
        if (node.Term.Kind == PortTermKind.Null)
            return node;
        var from = Terms.Strip(node.Term);
        var to = Terms.Strip(joined);
        if (from.Equals(to))
            return node;
        return from.Equals(Terms.Int) && to.Equals(Terms.Number)
            ? new TypedConversion(TypedConversionKind.Widen, node, Terms.Like(Terms.Number, node.Term))
            : new TypedConversion(TypedConversionKind.Convert, node, Terms.Like(to, node.Term));
    }

    private TypedExpression? Call(CallNode node)
    {
        var arguments = new List<TypedExpression>();
        var poisoned = false;
        foreach (var argument in node.Args)
        {
            if (Visit(argument) is { } typed)
                arguments.Add(typed);
            else
                poisoned = true;
        }

        if (!ExpressionSignatures.IsFunction(node.Name))
            return Refuse(CompilationRules.ExpressionTypeMismatch, $"unknown function '{node.Name}'", node.Position);
        if (poisoned)
            return null;

        if (node.Name == "toJsonText")
            return ToJsonText(node, arguments);
        if (node.Name is "jsonPath" or "jsonPathText")
            return Projection(node, arguments);

        var row = ExpressionSignatures.Bind(ExpressionSignatureKind.Function, node.Name, [.. arguments.Select(a => a.Term)]);
        if (row is null && node.Name is "min" or "max" && arguments.Count > 0 && arguments.All(a => Terms.IsNumeric(a.Term)))
        {
            arguments = [.. arguments.Select(Widened)];
            row = ExpressionSignatures.Bind(ExpressionSignatureKind.Function, node.Name, [.. arguments.Select(a => a.Term)]);
        }

        return row is null
            ? Mismatch(ExpressionSignatureKind.Function, node.Name, arguments, node.Position)
            : Operation(row, arguments, node.Position);
    }

    private TypedExpression? ToJsonText(CallNode node, IReadOnlyList<TypedExpression> arguments)
    {
        if (arguments.Count != 1)
            return Mismatch(ExpressionSignatureKind.Function, node.Name, arguments, node.Position);
        var term = arguments[0].Term;
        if (Terms.IsJson(term))
            return Refuse(CompilationRules.ExpressionTypeMismatch, $"toJsonText does not take a Json value, which is read only through a projection, not {term}", node.Position);
        if (term.IsNullable)
            return Refuse(CompilationRules.ExpressionTypeMismatch, $"toJsonText takes a value that cannot be null, not {term}", node.Position);
        if (vocabulary.RefusalOf(term) is not null || !vocabulary.Implements(term, WorkflowCapability.JsonSerializable))
            return Mismatch(ExpressionSignatureKind.Function, node.Name, arguments, node.Position);
        return Operation(ExpressionSignatures.Get("to-json-text"), arguments, node.Position);
    }

    private static TypedExpression Widened(TypedExpression node) =>
        node.Term.Equals(Terms.Int) || node.Term.Equals(Terms.Int.AsNullable())
            ? new TypedConversion(TypedConversionKind.Widen, node, Terms.Like(Terms.Number, node.Term))
            : node;

    private static TypedOperation Operation(ExpressionSignature row, IReadOnlyList<TypedExpression> operands, int position) =>
        new(row.Id, operands, ExpressionSignatures.ResultOf(row), position);

    private TypedExpression? Mismatch(ExpressionSignatureKind kind, string name, IReadOnlyList<TypedExpression> operands, int position)
    {
        var what = kind == ExpressionSignatureKind.Function ? $"the function '{name}'" : $"the operator '{name}'";
        var accepted = string.Join("; ", ExpressionSignatures.Candidates(kind, name, operands.Count).Select(s => s.Render()));
        return Refuse(CompilationRules.ExpressionTypeMismatch,
            $"no overload of {what} accepts ({ExpressionSignatures.Describe(operands.Select(o => o.Term))}); it accepts {accepted}", position);
    }
}
