namespace ReeveWorkflow.Core.Expressions;

/// <summary>Immutable AST nodes from <see cref="ExpressionParser"/>, consumed by the expression typer.
/// Each carries its first token's source position so error messages can point at the offending span.</summary>
public abstract record ExpressionNode(int Position);

public sealed record NumberLiteralNode(double Value, int Position, string? RawText = null) : ExpressionNode(Position);
public sealed record StringLiteralNode(string Value, int Position) : ExpressionNode(Position);
public sealed record BooleanLiteralNode(bool Value, int Position) : ExpressionNode(Position);
public sealed record NullLiteralNode(int Position) : ExpressionNode(Position);

/// <summary>A <c>${path.to.value}</c> reference; <see cref="Path"/> has the <c>${</c>/<c>}</c> wrappers
/// stripped, e.g. <c>device.kitchen.temperature</c> or <c>previous.w.Items[0].name</c>. <see cref="ExpressionPath"/> splits it.</summary>
public sealed record VariableNode(string Path, int Position) : ExpressionNode(Position);

public sealed record UnaryNode(string Op, ExpressionNode Operand, int Position) : ExpressionNode(Position);

public sealed record BinaryNode(string Op, ExpressionNode Left, ExpressionNode Right, int Position) : ExpressionNode(Position);

public sealed record TernaryNode(ExpressionNode Condition, ExpressionNode WhenTrue, ExpressionNode WhenFalse, int Position) : ExpressionNode(Position);

public sealed record CallNode(string Name, IReadOnlyList<ExpressionNode> Args, int Position) : ExpressionNode(Position);
