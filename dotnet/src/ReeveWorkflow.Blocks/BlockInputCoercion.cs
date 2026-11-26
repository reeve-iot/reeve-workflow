using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;

namespace ReeveWorkflow.Blocks;

internal static class BlockInputCoercion
{
    public static long? Int(IReadOnlyDictionary<string, object?> inputs, string port)
    {
        if (!inputs.TryGetValue(port, out var raw)) return null;
        if (RunTimeValueReading.TryInt(raw, out var integer))
            return integer;
        throw new ArgumentException($"Input '{port}' cannot be read as Int.");
    }

    public static long Within(long value, long max, string port)
    {
        if (value > max)
            throw new OverflowException($"Input '{port}' is {value}, which is beyond the {max} this block can use (overflow).");
        return value;
    }

    public static double? Number(IReadOnlyDictionary<string, object?> inputs, string port)
    {
        if (!inputs.TryGetValue(port, out var raw)) return null;
        if (raw is double number)
            return number;
        throw new ArgumentException($"Input '{port}' cannot be read as Number.");
    }

    public static bool? Boolean(IReadOnlyDictionary<string, object?> inputs, string port)
    {
        if (!inputs.TryGetValue(port, out var raw)) return null;
        if (raw is bool boolean)
            return boolean;
        throw new ArgumentException($"Input '{port}' cannot be read as Boolean.");
    }

    public static string? String(IReadOnlyDictionary<string, object?> inputs, string port)
    {
        if (!inputs.TryGetValue(port, out var raw)) return null;
        if (raw is null or JsonElement { ValueKind: JsonValueKind.Null })
            throw new ArgumentException($"Input '{port}' cannot be read as String.");
        return raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => throw new ArgumentException($"Input '{port}' cannot be read as String: the value is {KindOf(raw)}, not text."),
        };
    }

    private static string KindOf(object value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } => "a Number",
        JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } => "a Boolean",
        JsonElement { ValueKind: JsonValueKind.Array } => "an Array",
        JsonElement { ValueKind: JsonValueKind.Object } => "an Object",
        JsonElement => "an undefined value",
        bool => "a Boolean",
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "a Number",
        System.Collections.IDictionary => "an Object",
        System.Collections.IEnumerable when IsReadOnlyMap(value.GetType()) => "an Object",
        System.Collections.IEnumerable => "an Array",
        _ => $"something else ({value.GetType().Name})",
    };

    private static bool IsReadOnlyMap(Type type) => type.GetInterfaces()
        .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
}
