using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReeveWorkflow.Core.Hosting;

internal sealed class ExactNameEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } text)
        {
            foreach (var name in Enum.GetNames<TEnum>())
            {
                if (string.Equals(name, text, StringComparison.Ordinal))
                    return Enum.Parse<TEnum>(name);
            }
        }

        throw new JsonException($"Expected one of {string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"))}, spelled exactly.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value))
            throw new JsonException($"{value} is not a member of {typeof(TEnum).Name}.");
        writer.WriteStringValue(value.ToString());
    }
}
