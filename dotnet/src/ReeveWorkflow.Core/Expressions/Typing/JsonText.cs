using System.Text;
using System.Text.Json;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Expressions;

internal static class JsonText
{
    public static string? Write(ClosedPortType type, object value) =>
        type.ToJson(value) is { } json ? ExpressionJsonValue.Canonical(json) : null;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static JsonElement? Parse(string text)
    {
        byte[] bytes;
        try
        {
            bytes = Strict.GetBytes(text);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException("the text holds an unpaired surrogate", ex);
        }

        using var document = JsonMembers.Parse(bytes);
        return document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : document.RootElement.Clone();
    }
}
