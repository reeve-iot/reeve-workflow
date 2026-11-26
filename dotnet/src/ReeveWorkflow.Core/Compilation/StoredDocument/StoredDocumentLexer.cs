using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace ReeveWorkflow.Core.Compilation;

internal sealed record StoredDocumentFault(
    string Rule, string Location, string Detail, string? NodeId = null, IReadOnlyList<CanvasPathSegment>? CanvasPath = null)
{
    public IReadOnlyList<CanvasPathSegment> CanvasPath { get; init; } = CanvasPath ?? [];

    public string Describe() => $"{Detail} (rule {Rule}, at '{Location}')";

    public StoredDocumentFault Inside(string outer, string? nodeId, IReadOnlyList<CanvasPathSegment> canvasPath) =>
        this with { Location = outer + "#" + Location, NodeId = nodeId, CanvasPath = canvasPath };
}

internal static class JsonPointer
{
    public static string Escape(string key) =>
        key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    public static string Append(string pointer, string key) => pointer + "/" + Escape(key);

    public static string Append(string pointer, int index) => pointer + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal static class StoredDocumentLexer
{
    public const int MaxNesting = 64;

    private static readonly JsonReaderOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = MaxNesting + 1,
    };

    public static bool IsBlank([NotNullWhen(false)] string? text)
    {
        foreach (var c in text ?? "")
        {
            if (c is not (' ' or '\t' or '\n' or '\r'))
                return false;
        }

        return true;
    }

    public static byte[]? Lex(string text, out StoredDocumentFault? fault)
    {
        var (normalised, loneAt) = EscapeLoneSurrogates(text);
        var bytes = Encoding.UTF8.GetBytes(normalised);
        fault = Scan(bytes);
        if (fault is null && loneAt >= 0)
            fault = new StoredDocumentFault(StoredDocumentRules.L4, "", $"an unpaired surrogate at character {loneAt}");
        return fault is null ? bytes : null;
    }

    private static (string Text, int FirstAt) EscapeLoneSurrogates(string text)
    {
        StringBuilder? builder = null;
        var first = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (!char.IsSurrogate(c))
            {
                builder?.Append(c);
                continue;
            }

            if (first < 0)
            {
                first = i;
                builder = new StringBuilder(text.Length + 16).Append(text, 0, i);
            }

            if (FollowsAnEscapingBackslash(text, i))
                builder!.Append('x');
            else
                builder!.Append("\\udc00\\udc00");
        }

        return (builder?.ToString() ?? text, first);
    }

    private static bool FollowsAnEscapingBackslash(string text, int at)
    {
        var run = 0;
        while (at - run - 1 >= 0 && text[at - run - 1] == '\\')
            run++;
        return run % 2 == 1;
    }

    private sealed class Container(bool isArray, string pointer)
    {
        public bool IsArray { get; } = isArray;
        public string Pointer { get; } = pointer;
        public int Index { get; set; }
        public string? Key { get; set; }
        public HashSet<string> Keys { get; } = new(StringComparer.Ordinal);
    }

    private static StoredDocumentFault? Scan(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, Options);
        var stack = new List<Container>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        var at = ValuePointer(stack);
                        if (reader.CurrentDepth + 1 > MaxNesting)
                            return new StoredDocumentFault(StoredDocumentRules.L2, at, $"more than {MaxNesting} nested arrays and objects");
                        stack.Add(new Container(reader.TokenType == JsonTokenType.StartArray, at));
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        stack.RemoveAt(stack.Count - 1);
                        AfterValue(stack);
                        break;
                    case JsonTokenType.PropertyName:
                        var owner = stack[^1];
                        if (!IsWellFormed(ref reader))
                            return new StoredDocumentFault(StoredDocumentRules.L4, owner.Pointer, "a property name with an unpaired surrogate");
                        var name = reader.GetString()!;
                        if (!owner.Keys.Add(name))
                            return new StoredDocumentFault(StoredDocumentRules.L3, JsonPointer.Append(owner.Pointer, name), $"a second property named '{name}'");
                        owner.Key = name;
                        break;
                    case JsonTokenType.String:
                        if (!IsWellFormed(ref reader))
                            return new StoredDocumentFault(StoredDocumentRules.L4, ValuePointer(stack), "a string with an unpaired surrogate");
                        AfterValue(stack);
                        break;
                    default:
                        AfterValue(stack);
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            return new StoredDocumentFault(
                StoredDocumentRules.L1, "", $"not one JSON value (line {ex.LineNumber + 1}, byte {ex.BytePositionInLine + 1})");
        }

        return null;
    }

    private static string ValuePointer(List<Container> stack)
    {
        if (stack.Count == 0)
            return "";
        var top = stack[^1];
        return top.IsArray ? JsonPointer.Append(top.Pointer, top.Index) : JsonPointer.Append(top.Pointer, top.Key!);
    }

    private static void AfterValue(List<Container> stack)
    {
        if (stack.Count > 0 && stack[^1].IsArray)
            stack[^1].Index++;
    }

    private static bool IsWellFormed(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped)
            return true;

        var raw = reader.ValueSpan;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'\\')
                continue;
            if (raw[i + 1] != (byte)'u')
            {
                i++;
                continue;
            }

            var unit = Hex(raw.Slice(i + 2, 4));
            i += 5;
            if (unit is < 0xD800 or > 0xDFFF)
                continue;
            if (unit >= 0xDC00)
                return false;
            if (i + 6 >= raw.Length || raw[i + 1] != (byte)'\\' || raw[i + 2] != (byte)'u')
                return false;
            var low = Hex(raw.Slice(i + 3, 4));
            if (low is < 0xDC00 or > 0xDFFF)
                return false;
            i += 6;
        }

        return true;
    }

    private static int Hex(ReadOnlySpan<byte> digits)
    {
        var value = 0;
        foreach (var d in digits)
            value = (value << 4) | (d <= '9' ? d - '0' : (d | 0x20) - 'a' + 10);
        return value;
    }
}
