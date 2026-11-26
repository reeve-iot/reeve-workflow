using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Execution;

/// <summary>Canonical, deterministic JSON encoder for workflow variable snapshots (sorted keys, ISO-8601 UTC
/// dates). Never log the output — variables may carry plugin blobs or user secrets.</summary>
public static class WorkflowVariablesEncoder
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    /// <summary>Serialises <paramref name="variables"/> to canonical JSON; <c>{}</c> for null/empty.</summary>
    public static string Encode(IReadOnlyDictionary<string, object?>? variables)
    {
        if (variables == null || variables.Count == 0)
            return "{}";

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        WriteObject(writer, variables);
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Deserialises variables from canonical JSON; empty dictionary for <c>null</c> or <c>{}</c>.</summary>
    public static Dictionary<string, object?> Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return [];

        using var doc = JsonDocument.Parse(json);
        return ReadObject(doc.RootElement);
    }

    private static void WriteObject(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> dict)
    {
        writer.WriteStartObject();
        foreach (var key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            WriteValue(writer, dict[key]);
        }
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case DateTime dt:
                if (dt.Kind != DateTimeKind.Utc)
                    throw new ArgumentException($"A DateTime variable must have Kind Utc, not {dt.Kind}; use a DateTimeOffset.", nameof(value));
                writer.WriteStringValue(dt.ToString("o"));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.UtcDateTime.ToString("o"));
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case IReadOnlyDictionary<string, object?> nestedDict:
                WriteObject(writer, nestedDict);
                break;
            case IDictionary<string, object?> mutableDict:
                WriteObject(writer, (IReadOnlyDictionary<string, object?>)(new Dictionary<string, object?>(mutableDict)));
                break;
            case var map when PortMaps.TryEntries(map, out var entries):
                WriteObject(writer, entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
                break;
            case System.Collections.IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                    WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            case JsonElement element:
                WriteJsonElement(writer, element);
                break;
            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), SerialiserOptions);
                break;
        }
    }

    private static void WriteJsonElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var props = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal);
                foreach (var prop in props)
                {
                    writer.WritePropertyName(prop.Name);
                    WriteJsonElement(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteJsonElement(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static Dictionary<string, object?> ReadObject(JsonElement root)
    {
        var result = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject())
            result[prop.Name] = ReadValue(prop.Value);
        return result;
    }

    private static object? ReadValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => ReadObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ReadValue).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => (object?)true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
