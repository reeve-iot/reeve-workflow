using System.Globalization;
using System.Text;

namespace ReeveWorkflow.Core.JsonPath;

internal sealed class JsonPathParser
{
    private const long MaxInteger = 9_007_199_254_740_991;
    private readonly string _text;
    private int _pos;

    private JsonPathParser(string text) => _text = text;

    public static CompiledQuery Parse(string text)
    {
        var parser = new JsonPathParser(text);
        if (parser.Peek() != '$') throw parser.Error("a query starts with '$'");
        parser._pos++;
        var query = parser.ParseSegments();
        if (parser._pos < text.Length)
        {
            var rest = parser._pos;
            parser.SkipBlank();
            if (parser._pos == text.Length) throw new JsonPathSyntaxException("whitespace is not allowed after the query", rest);
            throw parser.Error($"unexpected '{text[parser._pos]}'");
        }
        return query;
    }

    private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';
    private char PeekAt(int offset) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';
    private bool AtEnd => _pos >= _text.Length;
    private JsonPathSyntaxException Error(string reason) => Error(reason, _pos);
    private JsonPathSyntaxException Error(string reason, int position) =>
        new(position >= _text.Length ? reason + " (the query ended)" : reason, position);

    private JsonPathSyntaxException Unsupported(string what) => new(_text, what, _pos);

    private void SkipBlank()
    {
        while (_pos < _text.Length && _text[_pos] is ' ' or '\t' or '\n' or '\r') _pos++;
    }

    private CompiledQuery ParseSegments()
    {
        var segments = new List<Segment>();
        while (true)
        {
            var save = _pos;
            SkipBlank();
            if (AtEnd)
            {
                _pos = save;
                break;
            }
            var c = Peek();
            if (c == '[')
                segments.Add(new Segment(ParseBracketedSelection()));
            else if (c == '.' && PeekAt(1) == '.')
                throw Unsupported("a descendant segment");
            else if (c == '.')
            {
                _pos++;
                if (Peek() == '*' && !AtEnd)
                {
                    _pos++;
                    segments.Add(new Segment([WildcardSelector.Instance]));
                }
                else if (IsNameFirst())
                    segments.Add(new Segment([new NameSelector(ReadMemberName())]));
                else throw Error("expected '*' or a member name after '.'");
            }
            else
            {
                _pos = save;
                break;
            }
        }
        return new CompiledQuery([.. segments]);
    }

    private bool IsNameFirst()
    {
        if (AtEnd) return false;
        var c = _text[_pos];
        if (char.IsAsciiLetter(c) || c == '_') return true;
        if (c < 0x80) return false;
        if (char.IsHighSurrogate(c)) return char.IsLowSurrogate(PeekAt(1));
        return !char.IsLowSurrogate(c);
    }

    private string ReadMemberName()
    {
        var start = _pos;
        while (!AtEnd)
        {
            var c = _text[_pos];
            if (char.IsAsciiDigit(c) || IsNameFirst()) _pos += char.IsHighSurrogate(c) ? 2 : 1;
            else break;
        }
        if (!AtEnd && char.IsSurrogate(_text[_pos])) throw Error("an unpaired surrogate is not a character");
        return _text[start.._pos];
    }

    private Selector[] ParseBracketedSelection()
    {
        Expect('[');
        var selectors = new List<Selector>();
        SkipBlank();
        selectors.Add(ParseSelector());
        while (true)
        {
            SkipBlank();
            if (Peek() == ']' && !AtEnd)
            {
                _pos++;
                return [.. selectors];
            }
            if (Peek() != ',' || AtEnd) throw Error("expected ',' or ']'");
            _pos++;
            SkipBlank();
            selectors.Add(ParseSelector());
        }
    }

    private Selector ParseSelector()
    {
        if (AtEnd) throw Error("expected a selector");
        var c = _text[_pos];
        switch (c)
        {
            case '\'' or '"': return new NameSelector(ParseStringLiteral());
            case '*': _pos++; return WildcardSelector.Instance;
            case '?': throw Unsupported("a filter selector");
            case '-' or ':' or (>= '0' and <= '9'): return ParseIndexOrSlice();
            default: throw Error($"expected a selector, found '{c}'");
        }
    }

    private Selector ParseIndexOrSlice()
    {
        long? start = null;
        if (Peek() != ':') start = ParseInteger();
        var afterStart = _pos;
        SkipBlank();
        if (Peek() != ':' || AtEnd)
        {
            _pos = afterStart;
            return new IndexSelector(start!.Value);
        }
        _pos++;
        SkipBlank();
        long? end = IsIntegerStart() ? ParseInteger() : null;
        SkipBlank();
        long? step = null;
        if (Peek() == ':' && !AtEnd)
        {
            _pos++;
            SkipBlank();
            if (IsIntegerStart()) step = ParseInteger();
        }
        return new SliceSelector(start, end, step);
    }

    private bool IsIntegerStart() => !AtEnd && (Peek() == '-' || char.IsAsciiDigit(Peek()));

    private long ParseInteger()
    {
        var start = _pos;
        var negative = Peek() == '-';
        if (negative) _pos++;
        if (AtEnd || !char.IsAsciiDigit(Peek())) throw Error("expected a digit");
        if (Peek() == '0')
        {
            if (negative) throw Error("-0 is not an integer here", start);
            _pos++;
            if (!AtEnd && char.IsAsciiDigit(Peek())) throw Error("an integer has no leading zero", start);
            return 0;
        }
        long value = 0;
        while (!AtEnd && char.IsAsciiDigit(Peek()))
        {
            value = value * 10 + (Peek() - '0');
            if (value > MaxInteger) throw Error("the integer is outside the range ±(2^53 - 1)", start);
            _pos++;
        }
        return negative ? -value : value;
    }

    private string ParseStringLiteral()
    {
        var start = _pos;
        var quote = _text[_pos++];
        var builder = new StringBuilder();
        while (true)
        {
            if (AtEnd) throw Error("the string literal is not closed", start);
            var c = _text[_pos];
            if (c == quote)
            {
                _pos++;
                return builder.ToString();
            }
            if (c == '\\')
            {
                ReadEscape(builder, quote);
                continue;
            }
            if (c < 0x20) throw Error("a control character must be escaped in a string literal");
            if (char.IsHighSurrogate(c))
            {
                if (!char.IsLowSurrogate(PeekAt(1))) throw Error("an unpaired surrogate is not a character");
                builder.Append(c).Append(_text[_pos + 1]);
                _pos += 2;
                continue;
            }
            if (char.IsLowSurrogate(c)) throw Error("an unpaired surrogate is not a character");
            builder.Append(c);
            _pos++;
        }
    }

    private void ReadEscape(StringBuilder builder, char quote)
    {
        var start = _pos++;
        if (AtEnd) throw Error("the escape is not complete", start);
        var c = _text[_pos++];
        switch (c)
        {
            case 'b': builder.Append('\b'); return;
            case 'f': builder.Append('\f'); return;
            case 'n': builder.Append('\n'); return;
            case 'r': builder.Append('\r'); return;
            case 't': builder.Append('\t'); return;
            case '/': builder.Append('/'); return;
            case '\\': builder.Append('\\'); return;
            case 'u': break;
            default:
                if (c == quote) { builder.Append(c); return; }
                throw Error($"'\\{c}' is not an escape", start);
        }
        var unit = ReadHex4(start);
        if (char.IsLowSurrogate(unit)) throw Error("a low surrogate escape must follow a high surrogate escape", start);
        if (char.IsHighSurrogate(unit))
        {
            if (PeekAt(0) != '\\' || PeekAt(1) != 'u') throw Error("a high surrogate escape must be followed by a low surrogate escape", start);
            var second = _pos;
            _pos += 2;
            var low = ReadHex4(second);
            if (!char.IsLowSurrogate(low)) throw Error("a high surrogate escape must be followed by a low surrogate escape", second);
            builder.Append(unit).Append(low);
            return;
        }
        builder.Append(unit);
    }

    private char ReadHex4(int escapeStart)
    {
        if (_pos + 4 > _text.Length) throw Error("a \\u escape needs four hex digits", escapeStart);
        var digits = _text.AsSpan(_pos, 4);
        foreach (var d in digits)
            if (!char.IsAsciiHexDigit(d)) throw Error("a \\u escape needs four hex digits", escapeStart);
        _pos += 4;
        return (char)int.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private void Expect(char c)
    {
        if (Peek() != c || AtEnd) throw Error($"expected '{c}'");
        _pos++;
    }
}
