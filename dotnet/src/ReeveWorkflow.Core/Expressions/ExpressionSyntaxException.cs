namespace ReeveWorkflow.Core.Expressions;

/// <summary>Thrown by the lexer and parser on invalid expression text.
/// <see cref="Position"/> is a zero-based offset into the expression body.</summary>
public sealed class ExpressionSyntaxException : Exception
{
    public int Position { get; }
    public string? Suggestion { get; }

    public ExpressionSyntaxException(string message, int position, string? suggestion = null)
        : base(message)
    {
        Position = position;
        Suggestion = suggestion;
    }

    public ExpressionSyntaxException(string message, int position, Exception inner)
        : base(message, inner)
    {
        Position = position;
        Suggestion = null;
    }
}
