using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace ReeveWorkflow.Abstractions;

/// <summary>A closed port term as a block sees it at run time: the term, the CLR type a value of it has, and how a value
/// of it is built and written. A block reads these from <see cref="WorkflowBlockContext.Types"/> to build each output in
/// its term's CLR type: a collection through <see cref="CreateList"/>, a <c>Json</c> output through <see cref="ToJson"/>.</summary>
/// <remarks>Built by the library once per closed term when a node compiles, so a run builds values with no reflection.
/// Its CLR type is composed at run time, so only the workflow engine, which runs JIT-compiled, builds one; a block that is
/// handed one uses it without a trim or AOT warning.</remarks>
[RequiresUnreferencedCode("A closed port type reads the runtime shape of the values it is handed, a map by the dictionary interface its type implements, and builds JSON metadata for its CLR type by reflection.")]
[RequiresDynamicCode("A closed term's CLR type is composed when a node compiles, so its lists, maps and JSON metadata are instantiated over it at run time.")]
public sealed class ClosedPortType
{
    private readonly JsonTypeInfo? ownInfo;
    private readonly ClosedPortType leaf;
    private IListFactory? lists;
    private IMapFactory? maps;
    private JsonTypeInfo? written;

    internal ClosedPortType(
        PortTerm term, Type clrType, ClosedPortType? elementType, JsonTypeInfo? jsonTypeInfo,
        ClosedPortType? valueType = null, ClosedPortType? keyType = null)
    {
        Term = term;
        ClrType = clrType;
        ElementType = elementType;
        ValueType = valueType;
        KeyType = keyType;
        var inner = elementType ?? valueType;
        ownInfo = inner is null ? jsonTypeInfo : null;
        leaf = inner?.leaf ?? this;
    }

    /// <summary>The closed term, canonical spelling.</summary>
    public PortTerm Term { get; }

    /// <summary>The CLR type a value of <see cref="Term"/> has: <c>long</c> for <c>Int</c>, <c>IReadOnlyList&lt;long&gt;</c> for
    /// <c>Collection&lt;Int&gt;</c>, <c>IReadOnlyDictionary&lt;string, long&gt;</c> for <c>Map&lt;String,Int&gt;</c>,
    /// <c>Nullable&lt;long&gt;</c> for <c>Int?</c>, a host atom's registered type.</summary>
    public Type ClrType { get; }

    /// <summary>Writes a value of <see cref="ClrType"/> as JSON; null when <see cref="Term"/> is not JsonSerializable.</summary>
    public JsonTypeInfo? JsonTypeInfo => leaf.ownInfo is null ? null : !IsContainer ? ownInfo : written ??= WrittenBy(ClrType, this);

    internal bool IsJsonSerializable => leaf.ownInfo is not null;

    /// <summary>The element's type when <see cref="Term"/> is a collection, nullable or not; otherwise null.</summary>
    public ClosedPortType? ElementType { get; }

    /// <summary>The value's type when <see cref="Term"/> is a map, nullable or not; otherwise null.</summary>
    public ClosedPortType? ValueType { get; }

    /// <summary>The key's type, <c>String</c>, when <see cref="Term"/> is a map, nullable or not; otherwise null.</summary>
    public ClosedPortType? KeyType { get; }

    private bool IsContainer => ElementType is not null || ValueType is not null;

    private ClosedPortType? Inner => ElementType ?? ValueType;

    /// <summary>A new list of <see cref="Term"/>'s collection, holding <paramref name="items"/> in order, each normalised as the
    /// output gate does: on an <c>Int</c> element a boxed <c>int</c>, <c>short</c>, <c>byte</c>, <c>sbyte</c>, <c>ushort</c> or
    /// <c>uint</c> becomes <c>long</c>, and on a <c>Number</c> element a <c>float</c> becomes <c>double</c>.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Term"/> is not a collection.</exception>
    /// <exception cref="ArgumentException">An item is null on a non-nullable element, not of the element's CLR type, or a non-finite
    /// <c>double</c> on a <c>Number</c> or <c>Number?</c> element.</exception>
    public IList CreateList(IEnumerable<object?> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (ElementType is null)
            throw new InvalidOperationException($"The term '{Term}' is not a collection.");
        lists ??= (IListFactory)Activator.CreateInstance(typeof(ListFactory<>).MakeGenericType(ElementType.ClrType))!;
        var list = lists.Create();
        var index = 0;
        foreach (var item in items)
        {
            list.Add(ElementType.Admit(item, index, null));
            index++;
        }

        return list;
    }

    /// <summary>A new map of <see cref="Term"/>'s map, holding <paramref name="entries"/> in the order given, each value
    /// normalised as <see cref="CreateList"/> normalises an element. Keys compare ordinally, so <c>X-A</c> and <c>x-a</c> are two.</summary>
    /// <returns>An <c>IReadOnlyDictionary&lt;string, V&gt;</c>, <c>V</c> being the value's CLR type: cast it to that, or read it
    /// through <see cref="ClrType"/>.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Term"/> is not a map.</exception>
    /// <exception cref="ArgumentException">A key is null or repeats, or a value is null on a non-nullable value term, not of the
    /// value's CLR type, or a non-finite <c>double</c> on a <c>Number</c> or <c>Number?</c> value.</exception>
    public System.Collections.IEnumerable CreateMap(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (ValueType is null)
            throw new InvalidOperationException($"The term '{Term}' is not a map.");
        maps ??= (IMapFactory)Activator.CreateInstance(typeof(MapFactory<>).MakeGenericType(ValueType.ClrType))!;
        var admitted = new List<KeyValuePair<string, object?>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (key is null)
                throw new ArgumentException("A map key is null.");
            if (!seen.Add(key))
                throw new ArgumentException($"The map key '{key}' occurs more than once, and a map holds each key once.");
            admitted.Add(new(key, ValueType.Admit(value, 0, key)));
        }

        return maps.Create(admitted);
    }

    /// <summary>The value written as JSON with <see cref="JsonTypeInfo"/>, a collection element by element; null for a null
    /// value and for a value that writes the JSON <c>null</c>, since a <c>Json</c> value is never a JSON null.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Term"/> is not JsonSerializable.</exception>
    /// <exception cref="ArgumentException">The value, or an element of it, is not of its term's CLR type, or is null on a
    /// non-nullable element.</exception>
    public JsonElement? ToJson(object? value)
    {
        if (leaf.ownInfo is null)
            throw new InvalidOperationException($"The term '{Term}' is not JsonSerializable.");
        if (value is null)
            return null;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, value);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.ValueKind == JsonValueKind.Null ? null : document.RootElement.Clone();
    }

    internal void WriteTo(Utf8JsonWriter writer, object? value)
    {
        if (leaf.ownInfo is null)
            throw new InvalidOperationException($"The term '{Term}' is not JsonSerializable.");
        if (value is null)
            writer.WriteNullValue();
        else
            Write(writer, value);
    }

    /// <summary>Reads one value of <see cref="Term"/> back from JSON with <see cref="JsonTypeInfo"/>, a collection element by element, into
    /// <see cref="ClrType"/>: the persisted-value codec's read. A JSON <c>null</c> is null (rule J of docs/reference/port-types.md, Json), so it reads only on a
    /// nullable term; a whole number on <c>Number</c> reads as a <c>double</c>, and a <c>Json</c> value as a <see cref="JsonElement"/>.</summary>
    /// <exception cref="PersistedValueReadException">The JSON is not a value of <see cref="Term"/>, or the term is not JsonSerializable.</exception>
    public object? FromJson(JsonElement json) =>
        TryFromJson(json, out var value, out var reason) ? value : throw new PersistedValueReadException(Term, reason!);

    internal bool TryFromJson(JsonElement json, out object? value, out string? reason)
    {
        value = null;
        reason = null;
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            reason = Term.IsNullable ? null : "null";
            return Term.IsNullable;
        }

        if (leaf.ownInfo is null)
        {
            reason = "a term that is not JsonSerializable";
            return false;
        }

        if (ElementType is { } element)
        {
            if (json.ValueKind != JsonValueKind.Array)
            {
                reason = Describe(json);
                return false;
            }

            var items = new List<object?>(json.GetArrayLength());
            var index = 0;
            foreach (var item in json.EnumerateArray())
            {
                if (!element.TryFromJson(item, out var read, out var why))
                {
                    reason = $"element {index} holding {why}";
                    return false;
                }

                items.Add(read);
                index++;
            }

            value = CreateList(items);
            return true;
        }

        if (ValueType is { } member)
        {
            if (json.ValueKind != JsonValueKind.Object)
            {
                reason = Describe(json);
                return false;
            }

            var members = new List<KeyValuePair<string, object?>>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in json.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    reason = "a repeated member name";
                    return false;
                }

                if (!member.TryFromJson(property.Value, out var read, out var why))
                {
                    reason = $"a member holding {why}";
                    return false;
                }

                members.Add(new(property.Name, read));
            }

            value = CreateMap(members);
            return true;
        }

        if (json.ValueKind is JsonValueKind.Object or JsonValueKind.Array && JsonMembers.FindRepeat(json) is { } repeat)
        {
            reason = repeat.Reason;
            return false;
        }

        if (TryLibraryScalar(json, out value, out var known))
        {
            reason = value is null ? Describe(json) : null;
            return value is not null;
        }

        if (known)
        {
            reason = Describe(json);
            return false;
        }

        if (!TryReadWith(json, ownInfo!, ClrType, out value))
        {
            reason = Describe(json);
            return false;
        }

        return true;
    }

    internal static bool TryReadWith(JsonElement json, JsonTypeInfo info, Type clr, out object? value)
    {
        try
        {
            value = JsonSerializer.Deserialize(json, info);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException or InvalidOperationException
                                       or ArgumentException or OverflowException)
        {
            value = null;
            return false;
        }

        if (value is null || !clr.IsInstanceOfType(value))
        {
            value = null;
            return false;
        }

        return true;
    }

    private bool TryLibraryScalar(JsonElement json, out object? value, out bool known)
    {
        value = null;
        known = Term.Kind == PortTermKind.Atom && Term.Name is "Int" or "Number" or "String" or "Boolean" or "Json";
        if (!known)
            return false;
        switch (Term.Name)
        {
            case "Int" when json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var whole):
                value = whole;
                break;
            case "Number" when json.ValueKind == JsonValueKind.Number && json.TryGetDouble(out var number) && double.IsFinite(number):
                value = number;
                break;
            case "String" when json.ValueKind == JsonValueKind.String:
                value = json.GetString();
                break;
            case "Boolean" when json.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = json.ValueKind == JsonValueKind.True;
                break;
            case "Json":
                value = json.Clone();
                break;
        }

        return true;
    }

    private static string Describe(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.String => "a JSON string",
        JsonValueKind.Number => "a JSON number",
        JsonValueKind.True or JsonValueKind.False => "a JSON boolean",
        JsonValueKind.Object => "a JSON object",
        JsonValueKind.Array => "a JSON array",
        _ => "null",
    };

    internal object? Copy(object? value)
    {
        if (!IsContainer || value is null || Members(this, value) is not { } top)
            return value;
        var open = new Stack<(IEnumerator<KeyValuePair<string?, object?>> Items, ClosedPortType Type, List<KeyValuePair<string, object?>> Built)>();
        open.Push((top, this, []));
        while (true)
        {
            var (items, type, built) = open.Peek();
            if (!items.MoveNext())
            {
                open.Pop();
                var rebuilt = type.ValueType is null ? type.CreateList(built.Select(e => e.Value)) : type.CreateMap(built);
                if (open.Count == 0)
                    return rebuilt;
                var parent = open.Peek();
                parent.Built.Add(new(parent.Items.Current.Key!, rebuilt));
                continue;
            }

            var (key, item) = items.Current;
            var element = type.Inner!;
            if (element.IsContainer && item is not null && Members(element, item) is { } nested)
                open.Push((nested, element, []));
            else
                built.Add(new(key!, item));
        }
    }

    private static IEnumerator<KeyValuePair<string?, object?>>? Members(ClosedPortType type, object value)
    {
        if (type.ValueType is not null)
            return PortMaps.TryEntries(value, out var entries) ? Keyed(entries) : null;
        return value is IEnumerable and not string && !PortMaps.IsMap(value) ? Unkeyed((IEnumerable)value) : null;
    }

    private static IEnumerator<KeyValuePair<string?, object?>> Keyed(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        foreach (var (key, value) in entries)
            yield return new(key, value);
    }

    private static IEnumerator<KeyValuePair<string?, object?>> Unkeyed(IEnumerable items)
    {
        foreach (var item in items)
            yield return new(null, item);
    }

    private object? Admit(object? item, int index, string? key)
    {
        if (item is null)
        {
            return Term.IsNullable
                ? null
                : throw new ArgumentException($"{Where(index, key)} is null, and the {What(key)} term '{Term}' is not nullable.");
        }

        var normalised = Normalised(item);
        if (!ClrType.IsInstanceOfType(normalised))
            throw new ArgumentException($"{Where(index, key)} is a {item.GetType().Name}, not a value of the {What(key)} term '{Term}'.");
        return IsNonFiniteNumber(normalised)
            ? throw new ArgumentException($"{Where(index, key)} is a non-finite number, not a value of the {What(key)} term '{Term}'.")
            : normalised;
    }

    private static string Where(int index, string? key) => key is null ? $"Element {index}" : $"Member '{key}'";

    private static string What(string? key) => key is null ? "element" : "value";

    internal bool IsJson => Term is { Kind: PortTermKind.Atom, Name: "Json" };

    internal JsonRepeat? FirstRepeat(object value)
    {
        if (!leaf.IsJson)
            return null;
        if (!IsContainer)
            return value is JsonElement json ? JsonMembers.FindRepeat(json) : null;
        if (Members(this, value) is not { } top)
            return null;
        var open = new Stack<(IEnumerator<KeyValuePair<string?, object?>> Items, ClosedPortType Type)>();
        open.Push((top, this));
        while (open.TryPeek(out var level))
        {
            if (!level.Items.MoveNext())
            {
                open.Pop();
                continue;
            }

            var item = level.Items.Current.Value;
            var element = level.Type.Inner!;
            if (item is null)
                continue;
            if (!element.IsContainer)
            {
                if (item is JsonElement json && JsonMembers.FindRepeat(json) is { } repeat)
                    return repeat;
            }
            else if (Members(element, item) is { } nested)
            {
                open.Push((nested, element));
            }
        }

        return null;
    }

    internal bool IsNonFiniteNumber(object value) =>
        value is double number && !double.IsFinite(number)
        && Term.Kind == PortTermKind.Atom && string.Equals(Term.Name, "Number", StringComparison.Ordinal);

    internal object Normalised(object value)
    {
        if (Term.Kind != PortTermKind.Atom)
            return value;
        if (string.Equals(Term.Name, "Int", StringComparison.Ordinal))
        {
            return value switch
            {
                int i => (long)i, short s => (long)s, byte b => (long)b, sbyte sb => (long)sb, ushort us => (long)us, uint ui => (long)ui,
                _ => value,
            };
        }

        return string.Equals(Term.Name, "Number", StringComparison.Ordinal) && value is float f ? (double)f : value;
    }

    private void Write(Utf8JsonWriter writer, object value)
    {
        if (!IsContainer)
        {
            WriteLeaf(writer, value);
            return;
        }

        var open = new Stack<(IEnumerator<KeyValuePair<string?, object?>> Items, ClosedPortType Type)>();
        Begin(writer, open, this, value);
        while (open.TryPeek(out var level))
        {
            if (!level.Items.MoveNext())
            {
                if (level.Type.ValueType is null)
                    writer.WriteEndArray();
                else
                    writer.WriteEndObject();
                open.Pop();
                continue;
            }

            var (key, item) = level.Items.Current;
            var element = level.Type.Inner!;
            if (key is not null)
                writer.WritePropertyName(key);
            if (item is null)
            {
                if (!element.Term.IsNullable)
                    throw new ArgumentException(key is null
                        ? $"An element is null, and the element term '{element.Term}' is not nullable."
                        : $"The member '{key}' is null, and the value term '{element.Term}' is not nullable.");
                writer.WriteNullValue();
            }
            else if (!element.IsContainer)
            {
                element.WriteLeaf(writer, item);
            }
            else
            {
                Begin(writer, open, element, item);
            }
        }
    }

    private static void Begin(
        Utf8JsonWriter writer, Stack<(IEnumerator<KeyValuePair<string?, object?>> Items, ClosedPortType Type)> open, ClosedPortType type, object value)
    {
        var items = Members(type, value) ?? throw new ArgumentException($"A {value.GetType().Name} is not a value of '{type.Term}'.");
        if (type.ValueType is null)
            writer.WriteStartArray();
        else
            writer.WriteStartObject();
        open.Push((items, type));
    }

    private void WriteLeaf(Utf8JsonWriter writer, object value)
    {
        var normalised = Normalised(value);
        if (!ClrType.IsInstanceOfType(normalised))
            throw new ArgumentException($"A {value.GetType().Name} is not a value of '{Term}'.");
        JsonSerializer.Serialize(writer, normalised, ownInfo!);
    }

    private static JsonTypeInfo WrittenBy(Type clrType, ClosedPortType owner)
    {
        var converter = (JsonConverter)Activator.CreateInstance(typeof(TermConverter<>).MakeGenericType(clrType), owner)!;
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(converter);
        return options.GetTypeInfo(clrType);
    }

    private interface IListFactory
    {
        IList Create();
    }

    private sealed class ListFactory<T> : IListFactory
    {
        public IList Create() => new List<T>();
    }

    private interface IMapFactory
    {
        System.Collections.IEnumerable Create(List<KeyValuePair<string, object?>> entries);
    }

    private sealed class MapFactory<T> : IMapFactory
    {
        public System.Collections.IEnumerable Create(List<KeyValuePair<string, object?>> entries)
        {
            var built = new KeyValuePair<string, T>[entries.Count];
            var positions = new Dictionary<string, int>(entries.Count, StringComparer.Ordinal);
            for (var i = 0; i < built.Length; i++)
            {
                built[i] = new(entries[i].Key, (T)entries[i].Value!);
                positions[entries[i].Key] = i;
            }

            return new MapValue<T>(built, positions);
        }
    }

    private sealed class TermConverter<T>(ClosedPortType owner) : JsonConverter<T>
    {
        public override bool HandleNull => true;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return owner.TryFromJson(document.RootElement, out var value, out var reason)
                ? (T)value!
                : throw new JsonException($"A persisted value does not read as '{owner.Term}': {reason}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value is null)
                writer.WriteNullValue();
            else
                owner.Write(writer, value);
        }
    }
}
