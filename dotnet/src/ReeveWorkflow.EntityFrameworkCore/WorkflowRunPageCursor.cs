using System.Globalization;
using System.Text;

namespace ReeveWorkflow.EntityFrameworkCore;

internal static class WorkflowRunPageCursor
{
    private const string Prefix = "rws1:";

    private const int MaxLength = 256;

    internal readonly record struct Position(DateTimeOffset StartedAtUtc, int Delivered);

    internal static string Write(DateTimeOffset startedAtUtc, int delivered) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture, $"{Prefix}{startedAtUtc.UtcDateTime.Ticks}:{delivered}")));

    internal static Position? Read(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return null;
        if (cursor.Length > MaxLength)
            throw Refuse($"it is {cursor.Length} characters, and no cursor this store issues is over {MaxLength}");

        Span<byte> decoded = stackalloc byte[MaxLength / 4 * 3];
        if (!Convert.TryFromBase64String(cursor, decoded, out var written))
            throw Refuse("it is not base64");

        var text = Encoding.UTF8.GetString(decoded[..written]);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
            throw Refuse("it was not issued by this store");

        var parts = text[Prefix.Length..].Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var delivered)
            || ticks < 0 || ticks > DateTime.MaxValue.Ticks || delivered < 0)
        {
            throw Refuse("it was not issued by this store");
        }

        return new Position(new DateTimeOffset(ticks, TimeSpan.Zero), delivered);
    }

    private static ArgumentException Refuse(string why) =>
        new($"The run-list cursor cannot be read: {why}.", "request");
}
