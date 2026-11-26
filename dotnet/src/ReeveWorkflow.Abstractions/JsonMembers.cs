using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ReeveWorkflow.Abstractions;

internal static class JsonMembers
{
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Strict = new();

    internal static JsonRepeat? FindRepeat(JsonElement root)
    {
        if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return null;
        var open = new Stack<Level>();
        open.Push(new Level(root, "$"));
        while (open.TryPeek(out var level))
        {
            if (level.Items is { } items)
            {
                if (!items.MoveNext())
                {
                    open.Pop();
                    continue;
                }

                if (items.Current.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    open.Push(new Level(items.Current, $"{level.Path}[{level.Index}]"));
                level.Index++;
                continue;
            }

            if (!level.Members!.MoveNext())
            {
                open.Pop();
                continue;
            }

            var member = level.Members.Current;
            string name;
            try
            {
                name = member.Name;
            }
            catch (InvalidOperationException)
            {
                return new JsonRepeat(null, level.Path);
            }

            if (!level.Names!.Add(name))
                return new JsonRepeat(name, level.Path);
            if (member.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                open.Push(new Level(member.Value, Child(level.Path, name)));
        }

        return null;
    }

    internal static JsonDocument Parse(ReadOnlyMemory<byte> utf8)
    {
        var document = JsonDocument.Parse(utf8);
        if (FindRepeat(document.RootElement) is not { } repeat)
            return document;
        document.Dispose();
        throw new JsonRepeatException(repeat);
    }

    internal static JsonSerializerOptions Refusing(JsonSerializerOptions options) =>
        Strict.GetValue(options, source => new JsonSerializerOptions(source) { AllowDuplicateProperties = false });

    private static string Child(string path, string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? $"{path}.{name}"
            : $"{path}['{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}']";

    private sealed class Level
    {
        public Level(JsonElement node, string path)
        {
            Path = path;
            if (node.ValueKind == JsonValueKind.Array)
            {
                Items = node.EnumerateArray().GetEnumerator();
                return;
            }

            Members = ((IEnumerable<JsonProperty>)node.EnumerateObject()).GetEnumerator();
            Names = new HashSet<string>(StringComparer.Ordinal);
        }

        public string Path { get; }

        public int Index { get; set; }

        public IEnumerator<JsonElement>? Items { get; }

        public IEnumerator<JsonProperty>? Members { get; }

        public HashSet<string>? Names { get; }
    }
}

internal readonly record struct JsonRepeat(string? Name, string Path)
{
    public string Reason => Name is null ? "a member name that does not decode" : "a repeated member name";

    public override string ToString() => Name is null
        ? "the text holds an unpaired surrogate"
        : $"the member name '{Name}' occurs more than once in the object at {Path}";
}

internal sealed class JsonRepeatException(JsonRepeat repeat) : JsonException(repeat.ToString())
{
    public JsonRepeat Repeat { get; } = repeat;
}
