using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ReeveWorkflow.Core.JsonPath;

internal static class JsonText
{
    public static bool TryGetLastProperty(JsonElement element, ReadOnlySpan<byte> utf8Name, out JsonElement value)
    {
        try
        {
            return element.TryGetProperty(utf8Name, out value);
        }
        catch (InvalidOperationException)
        {
            value = default;
            return false;
        }
    }

    public static string PropertyName(JsonProperty member)
    {
        try
        {
            return member.Name;
        }
        catch (InvalidOperationException)
        {
            return Encoding.UTF8.GetString(JsonMarshal.GetRawUtf8PropertyName(member));
        }
    }
}
