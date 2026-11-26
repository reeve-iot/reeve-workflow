using System.Globalization;
using System.Text;

namespace ReeveWorkflow.Core.Expressions;

public enum TokenKind
{
    Number,
    String,
    True,
    False,
    Null,
    Identifier,
    /// <summary>A <c>${path.to.value}</c> variable reference; <see cref="Token.Text"/> carries the dotted
    /// path with the wrapping <c>${</c>/<c>}</c> stripped.</summary>
    Variable,
    LeftParen,
    RightParen,
    LeftBracket,
    RightBracket,
    Comma,
    Dot,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Bang,
    EqualEqual,
    BangEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    AndAnd,
    OrOr,
    Question,
    QuestionQuestion,
    Colon,
    Eof,
}

public readonly record struct Token(TokenKind Kind, string Text, int Position, double Number = 0)
{
    public override string ToString() => Kind switch
    {
        TokenKind.Number => $"Number({Number})",
        TokenKind.String => $"String(\"{Text}\")",
        TokenKind.Variable => $"Variable({Text})",
        TokenKind.Identifier => $"Identifier({Text})",
        _ => Kind.ToString(),
    };
}

/// <summary>Tokenises an expression body (the text after the <c>expr:</c> sentinel) into a flat token
/// stream terminated by <see cref="TokenKind.Eof"/>.</summary>
public sealed class ExpressionLexer
{
    private readonly string _src;
    private int _pos;

    public ExpressionLexer(string source)
    {
        _src = source ?? string.Empty;
        _pos = 0;
    }

    public IReadOnlyList<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipWhitespace();
            if (_pos >= _src.Length)
            {
                tokens.Add(new Token(TokenKind.Eof, string.Empty, _pos));
                return tokens;
            }

            var start = _pos;
            var c = _src[_pos];

            if (c == '$' && _pos + 1 < _src.Length && _src[_pos + 1] == '{')
            {
                tokens.Add(ReadVariable(start));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && _pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1])))
            {
                tokens.Add(ReadNumber(start));
                continue;
            }

            if (c == '"')
            {
                tokens.Add(ReadString(start));
                continue;
            }

            if (IsIdentStart(c))
            {
                tokens.Add(ReadIdentifier(start));
                continue;
            }

            tokens.Add(ReadPunctuation(start));
        }
    }

    private void SkipWhitespace()
    {
        while (_pos < _src.Length)
        {
            var c = _src[_pos];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { _pos++; continue; }
            break;
        }
    }

    private Token ReadVariable(int start)
    {
        _pos += 2;
        var sb = new StringBuilder();
        while (_pos < _src.Length)
        {
            var c = _src[_pos];
            if (c == '}')
            {
                _pos++;
                if (sb.Length == 0)
                    throw new ExpressionSyntaxException("empty variable reference '${}'", start);
                var path = sb.ToString();
                if (!ExpressionPath.IsWellFormed(path, out var why))
                    throw new ExpressionSyntaxException($"malformed variable reference '${{{path}}}': {why}", start);
                return new Token(TokenKind.Variable, path, start);
            }
            if (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == ' ' || c == '[' || c == ']')
            {
                sb.Append(c);
                _pos++;
                continue;
            }
            throw new ExpressionSyntaxException(
                $"unexpected character '{c}' inside variable reference", _pos);
        }
        throw new ExpressionSyntaxException(
            "unterminated variable reference; expected '}'", start);
    }

    private Token ReadNumber(int start)
    {
        var sb = new StringBuilder();
        var sawDot = false;
        while (_pos < _src.Length)
        {
            var c = _src[_pos];
            if (char.IsDigit(c))
            {
                sb.Append(c);
                _pos++;
            }
            else if (c == '.' && !sawDot)
            {
                if (_pos + 1 < _src.Length && char.IsDigit(_src[_pos + 1]))
                {
                    sawDot = true;
                    sb.Append(c);
                    _pos++;
                }
                else break;
            }
            else break;
        }

        var text = sb.ToString();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ExpressionSyntaxException($"invalid number literal '{text}'", start);
        return new Token(TokenKind.Number, text, start, value);
    }

    private Token ReadString(int start)
    {
        _pos++;
        var sb = new StringBuilder();
        while (_pos < _src.Length)
        {
            var c = _src[_pos];
            if (c == '"')
            {
                _pos++;
                return new Token(TokenKind.String, sb.ToString(), start);
            }
            if (c == '\\')
            {
                if (_pos + 1 >= _src.Length)
                    throw new ExpressionSyntaxException("unterminated string escape", _pos);
                var esc = _src[_pos + 1];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    default:
                        throw new ExpressionSyntaxException(
                            $"invalid escape sequence '\\{esc}' (allowed: \\\", \\\\, \\n, \\t)", _pos);
                }
                _pos += 2;
                continue;
            }
            sb.Append(c);
            _pos++;
        }
        throw new ExpressionSyntaxException("unterminated string literal", start);
    }

    private Token ReadIdentifier(int start)
    {
        var sb = new StringBuilder();
        while (_pos < _src.Length && IsIdentPart(_src[_pos]))
        {
            sb.Append(_src[_pos]);
            _pos++;
        }
        var text = sb.ToString();
        return text switch
        {
            "true" => new Token(TokenKind.True, text, start),
            "false" => new Token(TokenKind.False, text, start),
            "null" => new Token(TokenKind.Null, text, start),
            _ => new Token(TokenKind.Identifier, text, start),
        };
    }

    private Token ReadPunctuation(int start)
    {
        var c = _src[_pos];
        switch (c)
        {
            case '(': _pos++; return new Token(TokenKind.LeftParen, "(", start);
            case ')': _pos++; return new Token(TokenKind.RightParen, ")", start);
            case '[': _pos++; return new Token(TokenKind.LeftBracket, "[", start);
            case ']': _pos++; return new Token(TokenKind.RightBracket, "]", start);
            case ',': _pos++; return new Token(TokenKind.Comma, ",", start);
            case '.': _pos++; return new Token(TokenKind.Dot, ".", start);
            case '+': _pos++; return new Token(TokenKind.Plus, "+", start);
            case '-': _pos++; return new Token(TokenKind.Minus, "-", start);
            case '*': _pos++; return new Token(TokenKind.Star, "*", start);
            case '/': _pos++; return new Token(TokenKind.Slash, "/", start);
            case '%': _pos++; return new Token(TokenKind.Percent, "%", start);
            case '?':
                if (_pos + 1 < _src.Length && _src[_pos + 1] == '?') { _pos += 2; return new Token(TokenKind.QuestionQuestion, "??", start); }
                _pos++; return new Token(TokenKind.Question, "?", start);
            case ':': _pos++; return new Token(TokenKind.Colon, ":", start);
            case '=':
                if (_pos + 1 < _src.Length && _src[_pos + 1] == '=') { _pos += 2; return new Token(TokenKind.EqualEqual, "==", start); }
                throw new ExpressionSyntaxException("expected '==' (single '=' is not supported)", start);
            case '!':
                if (_pos + 1 < _src.Length && _src[_pos + 1] == '=') { _pos += 2; return new Token(TokenKind.BangEqual, "!=", start); }
                _pos++; return new Token(TokenKind.Bang, "!", start);
            case '<':
                if (_pos + 1 < _src.Length && _src[_pos + 1] == '=') { _pos += 2; return new Token(TokenKind.LessEqual, "<=", start); }
                _pos++; return new Token(TokenKind.Less, "<", start);
            case '>':
                if (_pos + 1 < _src.Length && _src[_pos + 1] == '=') { _pos += 2; return new Token(TokenKind.GreaterEqual, ">=", start); }
                _pos++; return new Token(TokenKind.Greater, ">", start);
            case '&':
                if (_pos + 1 < _src.Length && _src[_pos + 1] == '&') { _pos += 2; return new Token(TokenKind.AndAnd, "&&", start); }
                throw new ExpressionSyntaxException("expected '&&' (single '&' is not supported)", start);
            case '|':
                if (_pos + 1 < _src.Length && _src[_pos + 1] == '|') { _pos += 2; return new Token(TokenKind.OrOr, "||", start); }
                throw new ExpressionSyntaxException("expected '||' (single '|' is not supported)", start);
            default:
                throw new ExpressionSyntaxException($"unexpected character '{c}'", _pos);
        }
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
