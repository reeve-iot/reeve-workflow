using System.Globalization;

namespace ReeveWorkflow.Core.Compilation;

internal static class StoredNumber
{
    public const int ExponentDigits = 15;

    public static string? Refusal(string text)
    {
        var written = Decimal(text);
        if (written.Digits.Length == 0)
            return null;
        if (written.Beyond != 0)
            return written.Beyond > 0 ? NotFinite(text) : NotShortest(text);

        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value))
            return NotFinite(text);

        return Decimal(value.ToString("R", CultureInfo.InvariantCulture)) == written ? null : NotShortest(text);
    }

    private static string NotFinite(string text) => $"the number {text}, which is not a finite double";

    private static string NotShortest(string text) =>
        $"the number {text}, whose value differs from its double's shortest round-trip form";

    private readonly record struct DecimalValue(bool Negative, string Digits, long Exponent, int Beyond);

    private static DecimalValue Decimal(string text)
    {
        var negative = text.StartsWith('-');
        var body = negative ? text.AsSpan(1) : text.AsSpan();
        var e = body.IndexOfAny('e', 'E');
        long exponent = 0;
        var beyond = 0;
        if (e >= 0)
        {
            var power = body[(e + 1)..];
            var negativePower = power.Length > 0 && power[0] == '-';
            if (power.Length > 0 && power[0] is '+' or '-')
                power = power[1..];
            power = power.TrimStart('0');
            if (power.Length > ExponentDigits)
                beyond = negativePower ? -1 : 1;
            else if (power.Length > 0)
                exponent = (negativePower ? -1 : 1) * long.Parse(power, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        var mantissa = e < 0 ? body : body[..e];
        var dot = mantissa.IndexOf('.');
        var digits = dot < 0 ? mantissa.ToString() : string.Concat(mantissa[..dot], mantissa[(dot + 1)..]);
        if (dot >= 0)
            exponent -= mantissa.Length - dot - 1;

        var significant = digits.AsSpan().TrimStart('0');
        var trailing = significant.Length - significant.TrimEnd('0').Length;
        significant = significant[..^trailing];
        exponent += trailing;
        return significant.Length == 0
            ? new DecimalValue(false, "", 0, 0)
            : new DecimalValue(negative, significant.ToString(), beyond == 0 ? exponent : 0, beyond);
    }
}
