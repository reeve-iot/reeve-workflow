using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReeveWorkflow.Abstractions;

internal static class WorkflowDateTimeText
{
    public static bool TryRead(JsonElement element, out DateTimeOffset value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.String || NamesHourTwentyFour(element.GetString()))
            return false;
        if (element.TryGetDateTime(out var plain) && plain.Kind == DateTimeKind.Unspecified)
        {
            value = new DateTimeOffset(plain, TimeSpan.Zero);
            return true;
        }

        return element.TryGetDateTimeOffset(out value);
    }

    public static bool TryRead(ref Utf8JsonReader reader, out DateTimeOffset value)
    {
        value = default;
        if (reader.TokenType != JsonTokenType.String || NamesHourTwentyFour(ref reader))
            return false;
        if (reader.TryGetDateTime(out var plain) && plain.Kind == DateTimeKind.Unspecified)
        {
            value = new DateTimeOffset(plain, TimeSpan.Zero);
            return true;
        }

        return reader.TryGetDateTimeOffset(out value);
    }

    private static bool NamesHourTwentyFour(ref Utf8JsonReader reader) =>
        reader.HasValueSequence || reader.ValueIsEscaped
            ? NamesHourTwentyFour(reader.GetString())
            : reader.ValueSpan is { Length: > 12 } text && text[10] == (byte)'T' && text[11] == (byte)'2' && text[12] == (byte)'4';

    private static bool NamesHourTwentyFour(string? text) =>
        text is { Length: > 12 } && text[10] == 'T' && text[11] == '2' && text[12] == '4';

    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions { TypeInfoResolver = AbstractionsJsonContext.Default };
        options.Converters.Add(new Converter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class Converter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TryRead(ref reader, out var value) ? value : throw new JsonException("The value is not a date and time.");

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
