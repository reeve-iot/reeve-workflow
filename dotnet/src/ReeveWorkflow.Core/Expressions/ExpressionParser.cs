namespace ReeveWorkflow.Core.Expressions;

/// <summary>Hand-written Pratt parser for the expression grammar. Recursion depth is capped at <see cref="MaxDepth"/>.</summary>
public sealed class ExpressionParser
{
    public const int MaxDepth = 32;

    /// <summary>Hard ceiling on AST nodes per expression. <see cref="MaxDepth"/> only bounds nested
    /// recursion — a flat chain like <c>1+1+1+…</c> stays shallow but builds a spine long enough to blow the evaluator's native stack; this catches that at parse time instead.</summary>
    public const int MaxNodeCount = 1000;

    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;
    private int _depth;
    private int _nodeCount;

    public ExpressionParser(IReadOnlyList<Token> tokens)
    {
        _tokens = tokens;
        _pos = 0;
        _depth = 0;
        _nodeCount = 0;
    }

    public static ExpressionNode Parse(string source)
    {
        var lexer = new ExpressionLexer(source);
        var tokens = lexer.Tokenize();
        var parser = new ExpressionParser(tokens);
        var ast = parser.ParseExpression(0);
        var trailing = parser.Peek();
        if (trailing.Kind != TokenKind.Eof)
            throw new ExpressionSyntaxException(
                $"unexpected token '{trailing.Text}' after end of expression", trailing.Position);
        return ast;
    }

    private Token Peek() => _tokens[_pos];

    private Token Consume()
    {
        var t = _tokens[_pos];
        if (t.Kind != TokenKind.Eof) _pos++;
        return t;
    }

    private Token Expect(TokenKind kind, string label)
    {
        var t = Peek();
        if (t.Kind != kind)
        {
            var what = t.Kind == TokenKind.Eof ? "end of input" : $"'{t.Text}'";
            throw new ExpressionSyntaxException($"expected {label} but found {what}", t.Position);
        }
        return Consume();
    }

    /// <summary>Pratt loop: returns when the next token's left binding power is less than
    /// <paramref name="minBp"/>, meaning the caller's operator wins.</summary>
    public ExpressionNode ParseExpression(int minBp)
    {
        EnterDepth();
        try
        {
            var left = ParseNud();

            while (true)
            {
                var t = Peek();
                var lbp = LeftBindingPower(t.Kind);
                if (lbp < minBp || lbp == 0)
                    break;
                left = ParseLed(left, t);
            }

            return left;
        }
        finally
        {
            _depth--;
        }
    }

    private void EnterDepth()
    {
        _depth++;
        if (_depth > MaxDepth)
        {
            var t = _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];
            throw new ExpressionSyntaxException(
                $"expression nesting exceeds maximum depth of {MaxDepth}", t.Position);
        }
    }

    private void CountNode()
    {
        _nodeCount++;
        if (_nodeCount > MaxNodeCount)
        {
            var t = _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];
            throw new ExpressionSyntaxException(
                $"expression is too large (exceeds the maximum of {MaxNodeCount} nodes)", t.Position);
        }
    }

    private static int LeftBindingPower(TokenKind kind) => kind switch
    {
        TokenKind.Question => 1,
        TokenKind.QuestionQuestion => 2,
        TokenKind.OrOr => 3,
        TokenKind.AndAnd => 4,
        TokenKind.EqualEqual or TokenKind.BangEqual => 5,
        TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual => 6,
        TokenKind.Plus or TokenKind.Minus => 7,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 8,
        TokenKind.LeftParen or TokenKind.Dot or TokenKind.LeftBracket => 9,
        _ => 0,
    };

    private ExpressionNode ParseNud()
    {
        CountNode();
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.Number:
                Consume();
                return new NumberLiteralNode(t.Number, t.Position, t.Text);
            case TokenKind.String:
                Consume();
                return new StringLiteralNode(t.Text, t.Position);
            case TokenKind.True:
                Consume();
                return new BooleanLiteralNode(true, t.Position);
            case TokenKind.False:
                Consume();
                return new BooleanLiteralNode(false, t.Position);
            case TokenKind.Null:
                Consume();
                return new NullLiteralNode(t.Position);
            case TokenKind.Variable:
                Consume();
                return new VariableNode(t.Text, t.Position);
            case TokenKind.Identifier:
                Consume();
                if (Peek().Kind != TokenKind.LeftParen)
                    throw new ExpressionSyntaxException(
                        $"unexpected identifier '{t.Text}' (did you mean '${{{t.Text}}}'?)",
                        t.Position);
                Consume();
                var args = new List<ExpressionNode>();
                if (Peek().Kind != TokenKind.RightParen)
                {
                    args.Add(ParseExpression(0));
                    while (Peek().Kind == TokenKind.Comma)
                    {
                        Consume();
                        args.Add(ParseExpression(0));
                    }
                }
                Expect(TokenKind.RightParen, "')' to close function arguments");
                return new CallNode(t.Text, args, t.Position);
            case TokenKind.LeftParen:
                Consume();
                var inner = ParseExpression(0);
                Expect(TokenKind.RightParen, "')'");
                return inner;
            case TokenKind.Minus:
                Consume();
                return new UnaryNode("-", ParseUnaryOperand(t.Position), t.Position);
            case TokenKind.Bang:
                Consume();
                return new UnaryNode("!", ParseUnaryOperand(t.Position), t.Position);
            case TokenKind.Eof:
                throw new ExpressionSyntaxException("unexpected end of input", t.Position);
            default:
                throw new ExpressionSyntaxException(
                    $"unexpected token '{t.Text}'", t.Position);
        }
    }

    private ExpressionNode ParseUnaryOperand(int position)
    {
        EnterDepth();
        try
        {
            return ParseExpression(8);
        }
        finally
        {
            _depth--;
        }
    }

    private ExpressionNode ParseLed(ExpressionNode left, Token op)
    {
        CountNode();
        switch (op.Kind)
        {
            case TokenKind.QuestionQuestion:
                Consume();
                return new BinaryNode("??", left, ParseExpression(2), op.Position);
            case TokenKind.Question:
                Consume();
                var whenTrue = ParseExpression(0);
                Expect(TokenKind.Colon, "':' in ternary expression");
                var whenFalse = ParseExpression(1);
                return new TernaryNode(left, whenTrue, whenFalse, op.Position);

            case TokenKind.OrOr:
                Consume();
                return new BinaryNode("||", left, ParseExpression(4), op.Position);
            case TokenKind.AndAnd:
                Consume();
                return new BinaryNode("&&", left, ParseExpression(5), op.Position);
            case TokenKind.EqualEqual:
                Consume();
                return new BinaryNode("==", left, ParseExpression(6), op.Position);
            case TokenKind.BangEqual:
                Consume();
                return new BinaryNode("!=", left, ParseExpression(6), op.Position);
            case TokenKind.Less:
                Consume();
                return new BinaryNode("<", left, ParseExpression(7), op.Position);
            case TokenKind.LessEqual:
                Consume();
                return new BinaryNode("<=", left, ParseExpression(7), op.Position);
            case TokenKind.Greater:
                Consume();
                return new BinaryNode(">", left, ParseExpression(7), op.Position);
            case TokenKind.GreaterEqual:
                Consume();
                return new BinaryNode(">=", left, ParseExpression(7), op.Position);
            case TokenKind.Plus:
                Consume();
                return new BinaryNode("+", left, ParseExpression(8), op.Position);
            case TokenKind.Minus:
                Consume();
                return new BinaryNode("-", left, ParseExpression(8), op.Position);
            case TokenKind.Star:
                Consume();
                return new BinaryNode("*", left, ParseExpression(9), op.Position);
            case TokenKind.Slash:
                Consume();
                return new BinaryNode("/", left, ParseExpression(9), op.Position);
            case TokenKind.Percent:
                Consume();
                return new BinaryNode("%", left, ParseExpression(9), op.Position);

            case TokenKind.LeftParen:
                throw new ExpressionSyntaxException(
                    "function calls only apply to bare identifiers; use ${...} for variable access",
                    op.Position);
            case TokenKind.Dot:
                throw new ExpressionSyntaxException(
                    "member access via '.' is not supported outside ${...} variables",
                    op.Position);
            case TokenKind.LeftBracket:
                throw new ExpressionSyntaxException(
                    "indexing via '[]' is not supported in this grammar",
                    op.Position);

            default:
                throw new ExpressionSyntaxException(
                    $"unexpected operator '{op.Text}'", op.Position);
        }
    }
}
