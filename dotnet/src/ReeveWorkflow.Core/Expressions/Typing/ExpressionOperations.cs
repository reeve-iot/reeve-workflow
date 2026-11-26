using System.Globalization;

namespace ReeveWorkflow.Core.Expressions;

internal sealed class ExpressionOperationException(string category, string message) : Exception(message)
{
    public string Category { get; } = category;
}

internal static class ExpressionOperations
{
    public const string Overflow = "overflow";
    public const string DivisionByZero = "division-by-zero";
    public const string JsonParse = "json-parse";

    private static ExpressionOperationException Fail(string category, string operation) =>
        new(category, $"'{operation}' failed ({category})");

    internal static long IntAdd(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            throw Fail(Overflow, "Int arithmetic");
        }
    }

    internal static double NumberAdd(double left, double right) => Finite(left + right);

    internal static string Concat(string left, string right) => left + right;

    internal static long IntSubtract(long left, long right)
    {
        try
        {
            return checked(left - right);
        }
        catch (OverflowException)
        {
            throw Fail(Overflow, "Int arithmetic");
        }
    }

    internal static double NumberSubtract(double left, double right) => Finite(left - right);

    internal static long IntMultiply(long left, long right)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException)
        {
            throw Fail(Overflow, "Int arithmetic");
        }
    }

    internal static double NumberMultiply(double left, double right) => Finite(left * right);

    internal static long IntRemainder(long left, long right)
    {
        if (right == 0)
            throw Fail(DivisionByZero, "%");
        return left == long.MinValue && right == -1 ? 0L : left % right;
    }

    internal static double NumberRemainder(double left, double right) =>
        right == 0d ? throw Fail(DivisionByZero, "%") : Finite(left % right);

    internal static double NumberDivide(double left, double right) =>
        right == 0d ? throw Fail(DivisionByZero, "/") : Finite(left / right);

    internal static long IntNegate(long value) => value == long.MinValue ? throw Fail(Overflow, "-") : -value;

    internal static double NumberNegate(double value) => -value;

    internal static bool EqualInt(long left, long right) => left.Equals(right);

    internal static bool EqualNumber(double left, double right) => left.Equals(right);

    internal static bool EqualBoolean(bool left, bool right) => left.Equals(right);

    internal static bool EqualString(string left, string right) => string.Equals(left, right, StringComparison.Ordinal);

    internal static bool Not(bool value) => !value;

    internal static long MinInt(long current, long next) => next < current ? next : current;

    internal static double MinNumber(double current, double next) => next < current ? next : current;

    internal static long MaxInt(long current, long next) => next > current ? next : current;

    internal static double MaxNumber(double current, double next) => next > current ? next : current;

    internal static long AbsInt(long value) => value == long.MinValue ? throw Fail(Overflow, "abs") : Math.Abs(value);

    internal static double AbsNumber(double value) => Math.Abs(value);

    internal static long RoundNumber(double value) => ToLong(Math.Round(value, 0, MidpointRounding.AwayFromZero), "round");

    internal static long RoundInt(long value) => value;

    internal static long FloorNumber(double value) => ToLong(Math.Floor(value), "floor");

    internal static long FloorInt(long value) => value;

    internal static long CeilNumber(double value) => ToLong(Math.Ceiling(value), "ceil");

    internal static long CeilInt(long value) => value;

    internal static long Length(string value) => value.Length;

    internal static bool Contains(string text, string part) => text.Contains(part, StringComparison.Ordinal);

    internal static bool StartsWith(string text, string part) => text.StartsWith(part, StringComparison.Ordinal);

    internal static bool EndsWith(string text, string part) => text.EndsWith(part, StringComparison.Ordinal);

    internal static double ToNumberInt(long value) => value;

    internal static object? ToNumberString(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    internal static string ToStringBoolean(bool value) => TermValues.BooleanText(value);

    internal static string ToStringInt(long value) => TermValues.IntText(value);

    internal static string ToStringNumber(double value) => TermValues.NumberText(value);

    internal static string ToStringString(string value) => value;

    internal static string ToStringDateTime(DateTimeOffset value) => TermValues.DateTimeText(value);

    internal static object? ParseJson(string text)
    {
        try
        {
            return JsonText.Parse(text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ExpressionOperationException(JsonParse, $"parseJson: the text is not JSON ({ex.Message})");
        }
    }

    internal static bool LessInt(long left, long right) => left.CompareTo(right) < 0;

    internal static bool LessNumber(double left, double right) => left.CompareTo(right) < 0;

    internal static bool LessString(string left, string right) => string.CompareOrdinal(left, right) < 0;

    internal static bool LessDateTime(DateTimeOffset left, DateTimeOffset right) => left.CompareTo(right) < 0;

    internal static bool LessOrEqualInt(long left, long right) => left.CompareTo(right) <= 0;

    internal static bool LessOrEqualNumber(double left, double right) => left.CompareTo(right) <= 0;

    internal static bool LessOrEqualString(string left, string right) => string.CompareOrdinal(left, right) <= 0;

    internal static bool LessOrEqualDateTime(DateTimeOffset left, DateTimeOffset right) => left.CompareTo(right) <= 0;

    internal static bool GreaterInt(long left, long right) => left.CompareTo(right) > 0;

    internal static bool GreaterNumber(double left, double right) => left.CompareTo(right) > 0;

    internal static bool GreaterString(string left, string right) => string.CompareOrdinal(left, right) > 0;

    internal static bool GreaterDateTime(DateTimeOffset left, DateTimeOffset right) => left.CompareTo(right) > 0;

    internal static bool GreaterOrEqualInt(long left, long right) => left.CompareTo(right) >= 0;

    internal static bool GreaterOrEqualNumber(double left, double right) => left.CompareTo(right) >= 0;

    internal static bool GreaterOrEqualString(string left, string right) => string.CompareOrdinal(left, right) >= 0;

    internal static bool GreaterOrEqualDateTime(DateTimeOffset left, DateTimeOffset right) => left.CompareTo(right) >= 0;

    private static double Finite(double value) =>
        double.IsFinite(value) ? value : throw Fail(Overflow, "Number arithmetic");

    private static long ToLong(double value, string operation) =>
        value >= -9.2233720368547758E18 && value < 9.2233720368547758E18 ? (long)value : throw Fail(Overflow, operation);
}
