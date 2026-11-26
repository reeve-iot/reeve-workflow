using System.Diagnostics.CodeAnalysis;

namespace ReeveWorkflow.Abstractions;

/// <summary>Why a port type name is not a sentence of the grammar (<c>Term ::= Base "?"?</c>,
/// <c>Base ::= Ident | Ident "&lt;" Term "&gt;" | "Map&lt;" Key "," Term "&gt;"</c> (<c>Key</c> is <c>String</c> in any case, and under the declaration grammar also <c>"$" Ident</c>).
/// One member per distinct way to fail, so a reader can act on the fault rather than parse a message.</summary>
public enum PortTypeSyntaxFault
{
    /// <summary>The type field was missing (a JSON property absent, or <c>null</c>).</summary>
    Absent,
    /// <summary>The text was empty.</summary>
    Empty,
    /// <summary>A character the grammar never accepts, anywhere: white space, a control character, non-ASCII,
    /// or punctuation other than <c>&lt; &gt; ?</c>; and a <c>$</c> where a document term is read.</summary>
    IllegalCharacter,
    /// <summary>A type name was expected at this position and none was found (a bare <c>&lt;</c>, <c>&gt;</c>
    /// or <c>?</c>, or the end of the text).</summary>
    ExpectedTypeName,
    /// <summary>The keyword <c>Collection</c> was not followed by <c>&lt;</c>.</summary>
    CollectionWithoutBracket,
    /// <summary>An identifier begins with <c>collection</c> in any ASCII case but is not exactly
    /// <c>Collection&lt;</c>. The reserved prefix keeps a concatenated spelling such as <c>CollectionInt</c>
    /// from silently reading as an atom.</summary>
    ReservedCollectionPrefix,
    /// <summary>The text ended before every open <c>Name&lt;</c> found its closing <c>&gt;</c>.</summary>
    MissingClose,
    /// <summary>A <c>&lt;</c> appeared where only a closing <c>&gt;</c> or the end of text was expected.</summary>
    UnexpectedOpen,
    /// <summary>A <c>&gt;</c> appeared with no open bracket left to close.</summary>
    UnexpectedClose,
    /// <summary>A second <c>?</c> directly after one: <c>Int??</c>. A level is nullable once.</summary>
    DoubleNullable,
    /// <summary>The keyword <c>Map</c> was not followed by <c>&lt;</c>.</summary>
    MapWithoutBracket,
    /// <summary>The first argument of <c>Map</c> is not the atom <c>String</c>: another name, a nullable
    /// <c>String?</c>, a constructor, or a type parameter. Reported as <c>port-type-map-key</c>.</summary>
    MapKey,
    /// <summary>The key of <c>Map</c> was not followed by a comma and a value: <c>Map&lt;String&gt;</c>.</summary>
    MapWithoutValue
}

/// <summary>A port type name's syntax error: the fault, the position it was found at, the whole raw text, and
/// an optional hint the message folds in — the spelling to write, or which character class was illegal.</summary>
public sealed record PortTypeSyntaxError(PortTypeSyntaxFault Fault, int Position, string Text, string? Hint)
{
    /// <summary>A message a diagnostic or an exception can surface as-is.</summary>
    public string Message => Hint is null
        ? $"'{Text}' is not a port type name: {Describe(Fault)} at {Position}."
        : $"'{Text}' is not a port type name: {Describe(Fault)} at {Position}. {Hint}.";

    private static string Describe(PortTypeSyntaxFault fault) => fault switch
    {
        PortTypeSyntaxFault.Absent => "the type is missing",
        PortTypeSyntaxFault.Empty => "the type is blank",
        PortTypeSyntaxFault.IllegalCharacter => "an illegal character",
        PortTypeSyntaxFault.ExpectedTypeName => "a type name was expected",
        PortTypeSyntaxFault.CollectionWithoutBracket => "Collection needs a bracketed element",
        PortTypeSyntaxFault.ReservedCollectionPrefix => "names starting with 'collection' are reserved",
        PortTypeSyntaxFault.MissingClose => "a closing '>' was expected",
        PortTypeSyntaxFault.UnexpectedOpen => "an unexpected '<'",
        PortTypeSyntaxFault.UnexpectedClose => "an unexpected '>'",
        PortTypeSyntaxFault.DoubleNullable => "a second '?' after '?'",
        PortTypeSyntaxFault.MapWithoutBracket => "Map needs a bracketed key and value",
        PortTypeSyntaxFault.MapKey => "the key of a Map must be String",
        PortTypeSyntaxFault.MapWithoutValue => "a ',' and the value type were expected after the key",
        _ => fault.ToString()
    };
}

/// <summary>Thrown by <see cref="PortValueType.Of"/> for a name that is not a sentence of the grammar. An
/// <see cref="ArgumentException"/>, so an existing <c>catch (ArgumentException)</c> site still catches it.</summary>
public sealed class PortTypeSyntaxException(PortTypeSyntaxError error) : ArgumentException(error.Message, "name")
{
    public PortTypeSyntaxError Error { get; } = error;
}

internal static class PortTypeGrammar
{
    public const string Keyword = "Collection";
    public const string BracketPrefix = Keyword + "<";
    public const string MapKeyword = "Map";

    public static bool TryParse(string? text, [NotNullWhen(false)] out PortTypeSyntaxError? error) =>
        TryParse(text, PortTermGrammar.Document, build: false, out _, out error);

    public static bool TryParse(
        string? text, PortTermGrammar grammar, bool build, out PortTerm? term,
        [NotNullWhen(false)] out PortTypeSyntaxError? error)
    {
        term = null;
        if (text is null) { error = new(PortTypeSyntaxFault.Absent, 0, "", null); return false; }
        if (text.Length == 0) { error = new(PortTypeSyntaxFault.Empty, 0, text, null); return false; }

        var declaration = grammar == PortTermGrammar.Declaration;
        var heads = build ? new List<(string Name, string? Key)>() : null;
        var depth = 0;
        var i = 0;
        var leafStart = 0;
        var leafEnd = 0;
        var parameter = false;
        while (true)
        {
            if (i >= text.Length) { error = new(PortTypeSyntaxFault.ExpectedTypeName, i, text, null); return false; }
            var c = text[i];
            if (c is '<' or '>' or '?') { error = new(PortTypeSyntaxFault.ExpectedTypeName, i, text, null); return false; }
            if (c == '$')
            {
                if (!declaration) { error = IllegalChar(c, i, text); return false; }
                parameter = true;
                i++;
                if (i >= text.Length || text[i] is '<' or '>' or '?')
                {
                    error = new(PortTypeSyntaxFault.ExpectedTypeName, i, text, null);
                    return false;
                }
                c = text[i];
            }
            if (!IsIdentStart(c)) { error = IllegalChar(c, i, text); return false; }
            var j = i + 1;
            while (j < text.Length && IsIdentPart(text[j])) j++;
            if (j < text.Length && text[j] is not ('<' or '>' or '?')) { error = IllegalChar(text[j], j, text); return false; }

            var ident = text.AsSpan(i, j - i);
            if (parameter)
            {
                if (j < text.Length && text[j] == '<')
                {
                    error = new(PortTypeSyntaxFault.UnexpectedOpen, j, text, null);
                    return false;
                }
                leafStart = i;
                leafEnd = j;
                i = j;
                break;
            }
            if (ident.Equals(Keyword, StringComparison.Ordinal))
            {
                if (j >= text.Length || text[j] != '<')
                {
                    error = new(PortTypeSyntaxFault.CollectionWithoutBracket, i, text,
                        "write Collection<T> with the type of its elements");
                    return false;
                }
            }
            else if (ident.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase))
            {
                error = new(PortTypeSyntaxFault.ReservedCollectionPrefix, i, text, "write Collection<T> with the type of its elements");
                return false;
            }
            else if (ident.Equals(MapKeyword, StringComparison.Ordinal))
            {
                if (j >= text.Length || text[j] != '<')
                {
                    error = new(PortTypeSyntaxFault.MapWithoutBracket, i, text, "write Map<String,V> with the type of its values");
                    return false;
                }
                var keyAt = j + 1;
                if (!ReadMapKey(text, ref keyAt, declaration, out var key, out error)) return false;
                depth++;
                heads?.Add((MapKeyword, key));
                i = keyAt;
                continue;
            }
            if (j < text.Length && text[j] == '<')
            {
                depth++;
                heads?.Add((ident.ToString(), null));
                i = j + 1;
                continue;
            }
            leafStart = i;
            leafEnd = j;
            i = j;
            break;
        }

        var nullable = build ? new List<bool>(depth + 1) : null;
        if (!ReadNullable(text, ref i, nullable, out error)) return false;
        for (var k = 0; k < depth; k++)
        {
            if (i >= text.Length) { error = new(PortTypeSyntaxFault.MissingClose, i, text, null); return false; }
            if (text[i] != '>')
            {
                error = text[i] == '<'
                    ? new(PortTypeSyntaxFault.UnexpectedOpen, i, text, null)
                    : IllegalChar(text[i], i, text);
                return false;
            }
            i++;
            if (!ReadNullable(text, ref i, nullable, out error)) return false;
        }
        if (i != text.Length)
        {
            error = text[i] == '>' ? new(PortTypeSyntaxFault.UnexpectedClose, i, text, null)
                : text[i] == '<' ? new(PortTypeSyntaxFault.UnexpectedOpen, i, text, null)
                : IllegalChar(text[i], i, text);
            return false;
        }

        error = null;
        if (build)
        {
            var node = PortTerm.Built(
                parameter ? PortTermKind.Parameter : PortTermKind.Atom, text[leafStart..leafEnd], null, nullable![0]);
            for (var k = 0; k < depth; k++)
            {
                var (head, key) = heads![depth - 1 - k];
                IReadOnlyList<PortTerm> arguments = key is null
                    ? [node]
                    : [PortTerm.Built(PortTermKind.Atom, key, null, false), node];
                node = PortTerm.Built(PortTermKind.Constructor, head, arguments, nullable[k + 1]);
            }
            term = node;
        }
        return true;
    }

    private static bool ReadMapKey(
        string text, ref int at, bool declaration, out string key, [NotNullWhen(false)] out PortTypeSyntaxError? error)
    {
        key = "";
        error = null;
        var start = at;
        if (start >= text.Length || text[start] is '<' or '>' or '?')
        {
            error = new(PortTypeSyntaxFault.ExpectedTypeName, start, text, null);
            return false;
        }
        if (text[start] == '$')
        {
            error = declaration ? new(PortTypeSyntaxFault.MapKey, start, text, null) : IllegalChar('$', start, text);
            return false;
        }
        if (!IsIdentStart(text[start])) { error = IllegalChar(text[start], start, text); return false; }
        var end = start + 1;
        while (end < text.Length && IsIdentPart(text[end])) end++;
        if (end < text.Length && text[end] is not ('<' or '>' or '?' or ','))
        {
            error = IllegalChar(text[end], end, text);
            return false;
        }
        if (!text.AsSpan(start, end - start).Equals("String", StringComparison.OrdinalIgnoreCase)
            || end < text.Length && text[end] is '<' or '?')
        {
            error = new(PortTypeSyntaxFault.MapKey, start, text, null);
            return false;
        }
        if (end >= text.Length || text[end] == '>')
        {
            error = new(PortTypeSyntaxFault.MapWithoutValue, end, text, null);
            return false;
        }
        key = text[start..end];
        at = end + 1;
        return true;
    }

    private static bool ReadNullable(
        string text, ref int i, List<bool>? levels, [NotNullWhen(false)] out PortTypeSyntaxError? error)
    {
        error = null;
        var question = i < text.Length && text[i] == '?';
        if (question)
        {
            i++;
            if (i < text.Length && text[i] == '?')
            {
                error = new(PortTypeSyntaxFault.DoubleNullable, i, text, null);
                return false;
            }
        }
        levels?.Add(question);
        return true;
    }

    private static PortTypeSyntaxError IllegalChar(char c, int position, string text) => new(
        PortTypeSyntaxFault.IllegalCharacter, position, text,
        char.IsWhiteSpace(c) ? "white space"
        : char.IsControl(c) ? "a control character"
        : char.IsSurrogate(c) || c > 0x7e ? "a non-ASCII character"
        : $"'{c}'");

    public static bool IsAtom(string text) =>
        IsIdentifier(text) && !text.StartsWith(Keyword, StringComparison.OrdinalIgnoreCase)
        && !text.Equals(MapKeyword, StringComparison.Ordinal);

    public static bool IsIdentifier(string text)
    {
        if (text.Length == 0 || !IsIdentStart(text[0]))
            return false;
        for (var k = 1; k < text.Length; k++)
            if (!IsIdentPart(text[k]))
                return false;
        return true;
    }

    private static bool IsIdentStart(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    private static bool IsIdentPart(char c) => IsIdentStart(c) || c is >= '0' and <= '9';
}
