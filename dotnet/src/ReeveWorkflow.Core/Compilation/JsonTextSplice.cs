using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ReeveWorkflow.Core.Compilation;

internal static class JsonTextSplice
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Encode(string value) => JsonSerializer.Serialize(value, Relaxed);

    public static string Apply(string text, IReadOnlyDictionary<string, string> renames, IReadOnlyDictionary<string, string> strings)
    {
        if (renames.Count == 0 && strings.Count == 0)
            return text;

        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = StoredDocumentLexer.MaxNesting });
        var output = new List<byte>(bytes.Length + 64);
        var copied = 0;
        var stack = new List<(bool IsArray, string Pointer, int Index, string? Key)>();

        void Replace(long start, int length, string replacement)
        {
            output.AddRange(bytes.AsSpan(copied, (int)start - copied));
            output.AddRange(Encoding.UTF8.GetBytes(replacement));
            copied = (int)start + length;
        }

        string ValuePointer()
        {
            if (stack.Count == 0)
                return "";
            var top = stack[^1];
            return top.IsArray ? JsonPointer.Append(top.Pointer, top.Index) : JsonPointer.Append(top.Pointer, top.Key!);
        }

        void AfterValue()
        {
            if (stack.Count > 0 && stack[^1].IsArray)
                stack[^1] = stack[^1] with { Index = stack[^1].Index + 1 };
        }

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    stack.Add((reader.TokenType == JsonTokenType.StartArray, ValuePointer(), 0, null));
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    stack.RemoveAt(stack.Count - 1);
                    AfterValue();
                    break;
                case JsonTokenType.PropertyName:
                    var key = reader.GetString()!;
                    stack[^1] = stack[^1] with { Key = key };
                    if (renames.TryGetValue(JsonPointer.Append(stack[^1].Pointer, key), out var renamed))
                        Replace(reader.TokenStartIndex, reader.ValueSpan.Length + 2, Encode(renamed));
                    break;
                case JsonTokenType.String:
                    if (strings.TryGetValue(ValuePointer(), out var replaced))
                        Replace(reader.TokenStartIndex, reader.ValueSpan.Length + 2, Encode(replaced));
                    AfterValue();
                    break;
                case JsonTokenType.Number:
                    if (strings.TryGetValue(ValuePointer(), out var respelled))
                        Replace(reader.TokenStartIndex, reader.ValueSpan.Length, Encode(respelled));
                    AfterValue();
                    break;
                default:
                    AfterValue();
                    break;
            }
        }

        output.AddRange(bytes.AsSpan(copied));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static string Edit(string text, IReadOnlyCollection<string> removals, IReadOnlyCollection<PropertySet> sets)
    {
        if (removals.Count == 0 && sets.Count == 0)
            return text;

        var bytes = Encoding.UTF8.GetBytes(text);
        var objects = Objects(bytes);
        var edits = new List<(int Start, int End, string With)>();
        var removed = new Dictionary<string, HashSet<int>>();
        foreach (var pointer in removals)
        {
            var (owner, key) = Split(pointer);
            var i = objects[owner].Members.FindIndex(m => m.Key == key);
            if (i < 0)
                throw new InvalidOperationException($"No property at {pointer}.");
            if (!removed.TryGetValue(owner, out var indexes))
                removed[owner] = indexes = [];
            indexes.Add(i);
        }

        var added = new Dictionary<string, List<string>>();
        foreach (var set in sets)
        {
            var owner = objects[set.Object];
            var member = owner.Members.Find(m => m.Key == set.Key);
            if (member is not null)
                edits.Add((member.ValueStart, member.End, set.RawValue));
            else if (added.TryGetValue(set.Object, out var list))
                list.Add(Encode(set.Key) + ":" + set.RawValue);
            else
                added[set.Object] = [Encode(set.Key) + ":" + set.RawValue];
        }

        foreach (var pointer in removed.Keys.Concat(added.Keys).Distinct())
        {
            var members = objects[pointer].Members;
            var gone = removed.GetValueOrDefault(pointer) ?? [];
            var insert = string.Join(",", added.GetValueOrDefault(pointer) ?? []);
            var lastKept = Enumerable.Range(0, members.Count).LastOrDefault(i => !gone.Contains(i), -1);
            if (lastKept < 0)
            {
                var (from, to) = members.Count > 0 ? (members[0].Start, members[^1].End) : (objects[pointer].Close, objects[pointer].Close);
                edits.Add((from, to, insert));
                continue;
            }

            if (insert.Length > 0)
                edits.Add((members[lastKept].End, members[lastKept].End, "," + insert));
            for (var i = 0; i < members.Count; i++)
            {
                if (!gone.Contains(i) || (i > 0 && gone.Contains(i - 1)))
                    continue;
                var runEnd = i;
                while (runEnd + 1 < members.Count && gone.Contains(runEnd + 1))
                    runEnd++;
                edits.Add(runEnd + 1 < members.Count ? (members[i].Start, members[runEnd + 1].Start, "") : (members[i - 1].End, members[runEnd].End, ""));
            }
        }

        var output = new List<byte>(bytes.Length + 64);
        var copied = 0;
        foreach (var (start, end, with) in edits.OrderBy(e => e.Start).ThenBy(e => e.End))
        {
            if (start < copied)
                throw new InvalidOperationException("Two edits overlap.");
            output.AddRange(bytes.AsSpan(copied, start - copied));
            output.AddRange(Encoding.UTF8.GetBytes(with));
            copied = end;
        }

        output.AddRange(bytes.AsSpan(copied));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public sealed record PropertySet(string Object, string Key, string RawValue);

    private sealed class Member(string key, int start)
    {
        public string Key { get; } = key;
        public int Start { get; } = start;
        public int ValueStart { get; set; } = -1;
        public int End { get; set; }
    }

    private sealed class ObjectSpans
    {
        public List<Member> Members { get; } = [];
        public int Close { get; set; }
    }

    private static (string Owner, string Key) Split(string pointer)
    {
        var slash = pointer.LastIndexOf('/');
        return (pointer[..slash], pointer[(slash + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
    }

    private static Dictionary<string, ObjectSpans> Objects(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = StoredDocumentLexer.MaxNesting });
        var objects = new Dictionary<string, ObjectSpans>(StringComparer.Ordinal);
        var stack = new List<(bool IsArray, string Pointer, int Index, Member? Last)>();

        string BeginValue(int start)
        {
            if (stack.Count == 0)
                return "";
            var top = stack[^1];
            if (top.IsArray)
                return JsonPointer.Append(top.Pointer, top.Index);
            top.Last!.ValueStart = start;
            return JsonPointer.Append(top.Pointer, top.Last.Key);
        }

        void EndValue(int end)
        {
            if (stack.Count == 0)
                return;
            if (stack[^1].IsArray)
                stack[^1] = stack[^1] with { Index = stack[^1].Index + 1 };
            else
                stack[^1].Last!.End = end;
        }

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    var pointer = BeginValue((int)reader.TokenStartIndex);
                    if (reader.TokenType == JsonTokenType.StartObject)
                        objects[pointer] = new ObjectSpans();
                    stack.Add((reader.TokenType == JsonTokenType.StartArray, pointer, 0, null));
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    if (reader.TokenType == JsonTokenType.EndObject)
                        objects[stack[^1].Pointer].Close = (int)reader.TokenStartIndex;
                    stack.RemoveAt(stack.Count - 1);
                    EndValue((int)reader.BytesConsumed);
                    break;
                case JsonTokenType.PropertyName:
                    var member = new Member(reader.GetString()!, (int)reader.TokenStartIndex);
                    objects[stack[^1].Pointer].Members.Add(member);
                    stack[^1] = stack[^1] with { Last = member };
                    break;
                default:
                    BeginValue((int)reader.TokenStartIndex);
                    EndValue((int)reader.BytesConsumed);
                    break;
            }
        }

        return objects;
    }
}
