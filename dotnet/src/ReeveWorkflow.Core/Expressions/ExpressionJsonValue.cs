using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Expressions;

internal sealed class ExpressionJsonValue
{
    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly object gate = new();
    private string? text;
    private string? failure;

    public ExpressionJsonValue(object value) => Value = value;

    public object Value { get; }

    public bool TryText(out string rendered, out string why)
    {
        lock (gate)
        {
            if (text is null && failure is null)
                Render();
            rendered = text ?? string.Empty;
            why = failure ?? string.Empty;
            return text is not null;
        }
    }

    private void Render()
    {
        try
        {
            text = Canonical(Recorded(Value));
        }
        catch (Exception ex) when (Unwritable(ex))
        {
            failure = ex.Message;
        }
    }

    internal static bool Unwritable(Exception exception) => exception is not (OperationCanceledException or OutOfMemoryException);

    internal static JsonElement Recorded(object value) => value is JsonElement element
        ? element
        : JsonSerializer.SerializeToElement(value, value.GetType(), WorkflowGraphHelpers.JsonOptions);

    internal static string Canonical(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Writer))
            Write(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number when element.TryGetInt64(out var integer):
                writer.WriteNumberValue(integer);
                break;
            case JsonValueKind.Number:
                writer.WriteNumberValue(element.GetDouble());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
