using System.Globalization;

namespace ReeveWorkflow.Blocks;

internal static class BlockValueReading
{
    private static readonly char[] Whitespace = ['\t', '\n', '\v', '\f', '\r', ' '];

    public static bool TryInt(string text, out long value)
    {
        value = 0;
        return !text.EndsWith('\0') && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryNumber(string text, out double value)
    {
        value = 0;
        return !text.EndsWith('\0') && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    public static bool TryBoolean(string text, out bool value)
    {
        value = false;
        var trimmed = text.Trim(Whitespace);
        value = string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase);
        return value || string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase);
    }
}
