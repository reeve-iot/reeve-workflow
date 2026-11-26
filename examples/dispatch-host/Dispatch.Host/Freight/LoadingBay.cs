using System.Globalization;

namespace Dispatch.Host.Freight;

public static class LoadingBay
{
    public static readonly TimeSpan MaxDwell = TimeSpan.FromSeconds(20);

    public static TimeSpan DwellOf(IReadOnlyDictionary<string, object?> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (!inputs.TryGetValue("dwellSeconds", out var raw) || raw is null)
            return TimeSpan.Zero;

        var seconds = raw switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            string s when double.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ArgumentException(
                $"The 'dwellSeconds' input must be a number; got '{raw.GetType().Name}'.", nameof(inputs))
        };

        if (seconds < 0)
            throw new ArgumentException("The 'dwellSeconds' input must not be negative.", nameof(inputs));

        var dwell = TimeSpan.FromSeconds(seconds);
        return dwell > MaxDwell
            ? throw new ArgumentException(
                $"The 'dwellSeconds' input asks for {seconds}s; this desk holds a bay for at most {MaxDwell.TotalSeconds}s.",
                nameof(inputs))
            : dwell;
    }

    public static string Required(IReadOnlyDictionary<string, object?> inputs, string name)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var value = inputs.TryGetValue(name, out var raw) ? raw?.ToString() : null;
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"The '{name}' input is required and was not supplied.", nameof(inputs))
            : value;
    }

    public static string Slug(string value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
