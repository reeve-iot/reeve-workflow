using FluentAssertions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class ParserGoldenTests
{
    public static readonly IEnumerable<object[]> GoldenParseData = new[]
    {
        new object[] { "123", "123" },
        new object[] { "1.5", "1.5" },
        new object[] { "-123", "-(123)" },
        new object[] { "-1.5", "-(1.5)" },
        new object[] { "true", "true" },
        new object[] { "false", "false" },
        new object[] { "null", "null" },
        new object[] { "\"hello\"", "\"hello\"" },
        new object[] { "\"\"", "\"\"" },
        new object[] { "\"with\\nnewline\"", "\"with\\nnewline\"" },
        new object[] { "\"with\\\\backslash\"", "\"with\\\\backslash\"" },
        new object[] { "\"with\\\"quote\"", "\"with\\\"quote\"" },
        new object[] { "\"with\\ttab\"", "\"with\\ttab\"" },

        new object[] { "${x}", "${x}" },
        new object[] { "${device}", "${device}" },
        new object[] { "${device.kitchen}", "${device.kitchen}" },
        new object[] { "${device.kitchen.temperature}", "${device.kitchen.temperature}" },
        new object[] { "${helper.foo}", "${helper.foo}" },
        new object[] { "${trigger}", "${trigger}" },
        new object[] { "${trigger.type}", "${trigger.type}" },
        new object[] { "${now}", "${now}" },

        new object[] { "!true", "!(true)" },
        new object[] { "!false", "!(false)" },
        new object[] { "!${x}", "!(${x})" },
        new object[] { "-0", "-(0)" },

        new object[] { "1 + 2 * 3", "(1 + (2 * 3))" },
        new object[] { "(1 + 2) * 3", "((1 + 2) * 3)" },
        new object[] { "2 * 3 + 1", "((2 * 3) + 1)" },
        new object[] { "10 - 3 - 2", "((10 - 3) - 2)" },
        new object[] { "20 / 4 / 2", "((20 / 4) / 2)" },
        new object[] { "2 % 3", "(2 % 3)" },
        new object[] { "3 * 4 / 2", "((3 * 4) / 2)" },
        new object[] { "3 / 4 * 2", "((3 / 4) * 2)" },
        new object[] { "5 + 2 - 3", "((5 + 2) - 3)" },

        new object[] { "1 == 1", "(1 == 1)" },
        new object[] { "1 != 2", "(1 != 2)" },
        new object[] { "1 < 2", "(1 < 2)" },
        new object[] { "1 > 0", "(1 > 0)" },
        new object[] { "1 <= 1", "(1 <= 1)" },
        new object[] { "2 >= 1", "(2 >= 1)" },

        new object[] { "1 + 2 == 3", "((1 + 2) == 3)" },
        new object[] { "1 < 2 + 1", "(1 < (2 + 1))" },
        new object[] { "5 * 2 >= 10", "((5 * 2) >= 10)" },

        new object[] { "true && false", "(true && false)" },
        new object[] { "true || false", "(true || false)" },

        new object[] { "true || false && false", "(true || (false && false))" },
        new object[] { "false && false || true", "((false && false) || true)" },

        new object[] { "1 < 2 && 3 < 4", "((1 < 2) && (3 < 4))" },
        new object[] { "1 < 2 || 3 >= 4", "((1 < 2) || (3 >= 4))" },
        new object[] { "1 + 2 * 3 == 7 && true", "(((1 + (2 * 3)) == 7) && true)" },

        new object[] { "true ? 1 : 0", "(true ? 1 : 0)" },
        new object[] { "false ? \"a\" : \"b\"", "(false ? \"a\" : \"b\")" },
        new object[] { "${x} ? 1 : 0", "(${x} ? 1 : 0)" },

        new object[] { "true ? 1 : true ? 2 : 3", "(true ? 1 : (true ? 2 : 3))" },

        new object[] { "1 < 2 ? 3 + 4 : 5 * 6", "((1 < 2) ? (3 + 4) : (5 * 6))" },

        new object[] { "${a} ?? 1", "(${a} ?? 1)" },
        new object[] { "${a}??1", "(${a} ?? 1)" },
        new object[] { "${a} ?? ${b} ?? 3", "(${a} ?? (${b} ?? 3))" },
        new object[] { "${a} ?? 1 + 2", "(${a} ?? (1 + 2))" },
        new object[] { "${a} ?? ${b} || ${c}", "(${a} ?? (${b} || ${c}))" },
        new object[] { "${a} || ${b} ?? ${c}", "((${a} || ${b}) ?? ${c})" },
        new object[] { "${a} ?? ${b} ? 1 : 2", "((${a} ?? ${b}) ? 1 : 2)" },
        new object[] { "true ? 1 : ${a} ?? 2", "(true ? 1 : (${a} ?? 2))" },

        new object[] { "min(1, 2)", "min(1, 2)" },
        new object[] { "max(1, 2, 3)", "max(1, 2, 3)" },
        new object[] { "abs(-5)", "abs(-(5))" },
        new object[] { "floor(3.7)", "floor(3.7)" },
        new object[] { "ceil(3.2)", "ceil(3.2)" },
        new object[] { "round(3.5)", "round(3.5)" },
        new object[] { "round(3.141, 2)", "round(3.141, 2)" },
        new object[] { "len(\"hello\")", "len(\"hello\")" },
        new object[] { "contains(\"hello\", \"ll\")", "contains(\"hello\", \"ll\")" },
        new object[] { "startsWith(\"hello\", \"he\")", "startsWith(\"hello\", \"he\")" },
        new object[] { "endsWith(\"hello\", \"lo\")", "endsWith(\"hello\", \"lo\")" },
        new object[] { "toNumber(\"42\")", "toNumber(\"42\")" },
        new object[] { "toString(42)", "toString(42)" },

        new object[] { "min(1 + 2, 3 * 4)", "min((1 + 2), (3 * 4))" },
        new object[] { "max(${x}, 10)", "max(${x}, 10)" },

        new object[] { "(1)", "1" },
        new object[] { "(1 + 2)", "(1 + 2)" },
        new object[] { "((1))", "1" },
        new object[] { "(((1 + 2)))", "(1 + 2)" },

        new object[] { "(1 + 2) * (3 + 4)", "((1 + 2) * (3 + 4))" },
        new object[] { "!((${x} > 5) && (${y} < 10))", "!(((${x} > 5) && (${y} < 10)))" },
    };

    [Theory]
    [MemberData(nameof(GoldenParseData))]
    public void Parser_Parses_GoldenData_Correctly(string input, string expectedAstString)
    {
        var ast = ExpressionParser.Parse(input);
        var actualString = AstToString(ast);
        actualString.Should().Be(expectedAstString);
    }

    [Theory]
    [InlineData("1 +", 3)]
    [InlineData("1 + +", 4)]
    [InlineData("1 + )", 4)]
    [InlineData("(1", 2)]
    [InlineData("min(1", 5)]
    [InlineData("${", 0)]
    [InlineData("$$", 0)]
    [InlineData("${a} ??", 7)]
    [InlineData("true ? ? 1 : 2", 7)]
    public void Parser_Reports_Error_Positions_Correctly(string input, int expectedPosition)
    {
        var ex = Assert.Throws<ExpressionSyntaxException>(() => ExpressionParser.Parse(input));
        ex.Position.Should().Be(expectedPosition);
    }

    [Fact]
    public void Parser_Rejects_Expressions_Exceeding_Recursion_Depth()
    {
        var deepExpr = "(" + string.Concat(Enumerable.Repeat("(", 33)) + "1" +
                       string.Concat(Enumerable.Repeat(")", 33)) + ")";

        var ex = Assert.Throws<ExpressionSyntaxException>(() => ExpressionParser.Parse(deepExpr));
        ex.Message.Should().Contain("exceeds maximum depth");
        ex.Position.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Parser_Accepts_32_Nested_Parens()
    {
        var input = string.Concat(Enumerable.Repeat("(", 31)) + "1" +
                    string.Concat(Enumerable.Repeat(")", 31));
        var ast = ExpressionParser.Parse(input);
        ast.Should().NotBeNull();
    }

    [Fact]
    public void Parser_Rejects_33_Nested_Parens()
    {
        var input = string.Concat(Enumerable.Repeat("(", 32)) + "1" +
                    string.Concat(Enumerable.Repeat(")", 32));
        var ex = Assert.Throws<ExpressionSyntaxException>(() =>
            ExpressionParser.Parse(input));

        ex.Message.Should().Contain("exceeds maximum depth");
    }

    [Fact]
    public void Parser_Rejects_Deep_Unary_Chain()
    {
        var input = string.Concat(Enumerable.Repeat("-", 33)) + "1";
        var ex = Assert.Throws<ExpressionSyntaxException>(() =>
            ExpressionParser.Parse(input));

        ex.Message.Should().Contain("exceeds maximum depth");
    }

    [Fact]
    public void Parser_Rejects_Long_Flat_Operator_Chain_Without_Crashing()
    {
        var input = "1" + string.Concat(Enumerable.Repeat("+1", 50_000));

        var ex = Assert.Throws<ExpressionSyntaxException>(() => ExpressionParser.Parse(input));

        ex.Message.Should().Contain("too large");
    }

    [Fact]
    public void Parser_Accepts_A_Modest_Flat_Chain_And_The_Typed_Path_Evaluates_It()
    {
        var input = "1" + string.Concat(Enumerable.Repeat("+1", 99));

        var tree = ExpressionTyper.Type(input, new ExpressionTypingScope.Builder().Build()).Tree!;

        TypedExpressionEvaluator.Evaluate(tree, new ExpressionEvaluationScope.Builder().Build()).Should().Be(100L);
    }

    [Theory]
    [InlineData("${previous.w.TriggerData.auth.user", "unterminated")]
    [InlineData("${previous.w.Items[}", "malformed variable reference")]
    [InlineData("${previous.w.Items[x]}", "malformed variable reference")]
    [InlineData("${previous.w.Items[0}", "malformed variable reference")]
    [InlineData("${previous.w.Items[0]x}", "malformed variable reference")]
    [InlineData("${[0]}", "malformed variable reference")]
    [InlineData("${a} [0]", "indexing via '[]' is not supported")]
    [InlineData("${a.[0]}", "malformed variable reference")]
    [InlineData("${a}.b", "member access via '.' is not supported outside")]
    public void A_malformed_index_is_a_syntax_error_and_a_postfix_form_still_is(string source, string why)
    {
        var parse = () => ExpressionParser.Parse(source);

        parse.Should().Throw<ExpressionSyntaxException>().Where(e => e.Message.Contains(why));
    }

    [Fact]
    public void A_well_formed_index_parses_as_part_of_the_variable_path() =>
        ExpressionParser.Parse("${previous.w.Items[0].name}").Should().Be(
            new VariableNode("previous.w.Items[0].name", 0));

    [Fact]
    public void Bf11_Lexer_Allows_Spaces_Inside_Variable_Reference()
    {
        var tokens = new ExpressionLexer("${depot.Loading Dock.free}").Tokenize();

        tokens.Should().ContainSingle(t => t.Kind == TokenKind.Variable)
            .Which.Text.Should().Be("depot.Loading Dock.free");
    }

    private static string AstToString(ExpressionNode node) => node switch
    {
        NumberLiteralNode n => n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        StringLiteralNode s => $"\"{EscapeString(s.Value)}\"",
        BooleanLiteralNode b => b.Value ? "true" : "false",
        NullLiteralNode => "null",
        VariableNode v => $"${{{v.Path}}}",
        UnaryNode u => $"{u.Op}({AstToString(u.Operand)})",
        BinaryNode b => $"({AstToString(b.Left)} {b.Op} {AstToString(b.Right)})",
        TernaryNode t => $"({AstToString(t.Condition)} ? {AstToString(t.WhenTrue)} : {AstToString(t.WhenFalse)})",
        CallNode c => $"{c.Name}({string.Join(", ", c.Args.Select(AstToString))})",
        _ => throw new InvalidOperationException($"Unknown node type: {node.GetType()}"),
    };

    private static string EscapeString(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");
}
