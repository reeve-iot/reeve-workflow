using System.Globalization;
using System.Text.Json;

namespace ReeveWorkflow.Abstractions;

/// <summary>The decision on whether a literal is a literal of a port type by its form: the editor grades with it alone, and the
/// compiler also reads the literal with the record's reader, so its decision is the stricter. Both grade the vectors in
/// <c>contracts/literal-coercion.json</c>.</summary>
/// <remarks>A literal is of a term only in the form its type declares. No text is read into another type: a string is a
/// literal of <c>String</c>, of <c>DateTime</c> and of a host type whose form is graded as String, and of nothing else.</remarks>
public static class PortLiteralCoercion
{
    private const double TwoToThe63 = 9223372036854775808.0;

    /// <summary>The form the library gives one of its own atoms, or null for a name that is not one. Names compare
    /// ignoring ASCII case.</summary>
    public static WorkflowLiteralForm? LibraryFormOf(string name) => name.ToLowerInvariant() switch
    {
        "string" => new WorkflowLiteralForm(WorkflowLiteralKind.String),
        "boolean" => new WorkflowLiteralForm(WorkflowLiteralKind.Boolean),
        "int" => new WorkflowLiteralForm(WorkflowLiteralKind.Int),
        "number" => new WorkflowLiteralForm(WorkflowLiteralKind.Number),
        "datetime" => new WorkflowLiteralForm(WorkflowLiteralKind.String, WorkflowLiteralFormat.DateTime),
        "json" => new WorkflowLiteralForm(WorkflowLiteralKind.Json),
        _ => null,
    };

    /// <summary>Whether <paramref name="value"/>, a CLR value the stored-document reader makes or a <see cref="JsonElement"/>,
    /// is a literal of <paramref name="type"/>. <c>null</c> is a literal of a nullable term and of no other; a
    /// <c>Collection</c> is a JSON array of literals of its argument; a <c>Map</c> is a JSON object whose member names are
    /// unique and whose member values are literals of its value term; a <c>Json</c> literal repeats no member name at any
    /// depth; an atom or a constructor is graded by its form, and a head with no form has no literal. A term that names a type
    /// parameter has no literal here: the binding pass grades it once the parameter is bound.</summary>
    /// <param name="value">The literal; a JSON <c>null</c> and an absent value are the same thing.</param>
    /// <param name="type">The declared type of the port or field.</param>
    /// <param name="formOf">The form of a registered head, by name, or null for a head with none. It is asked for each
    /// atom and constructor other than <c>Collection</c> and <c>Map</c>, and for a library atom too.</param>
    public static bool IsLiteralOf(object? value, PortValueType type, Func<string, WorkflowLiteralForm?> formOf) =>
        IsLiteralOf(value, type, formOf, null);

    internal static bool IsLiteralOf(
        object? value, PortValueType type, Func<string, WorkflowLiteralForm?> formOf, Func<string, JsonElement, bool>? reads)
    {
        ArgumentNullException.ThrowIfNull(formOf);
        if (type.IsDefault || !PortTerm.TryParse(type.Name, PortTermGrammar.Declaration, out var term, out _) || term.ContainsParameter)
            return false;

        var work = new Stack<(object? Value, PortTerm Term)>();
        work.Push((value, term));
        while (work.TryPop(out var item))
        {
            var (current, node) = item;
            var shape = ShapeOf(current);
            if (shape == Shape.Null)
            {
                if (!node.IsNullable) return false;
                continue;
            }

            if (node.IsCollection)
            {
                if (shape != Shape.Array) return false;
                foreach (var element in ElementsOf(current))
                    work.Push((element, node.Arguments[0]));
                continue;
            }

            if (node.IsMap)
            {
                if (shape != Shape.Object || !TryMembersOf(current, out var members)) return false;
                foreach (var member in members)
                    work.Push((member, node.Arguments[1]));
                continue;
            }

            if (formOf(node.Name) is not { } form || !Fits(current, shape, form)
                || reads is not null && !reads(node.Name, AsElement(current)))
                return false;
        }

        return true;
    }

    private static JsonElement AsElement(object? value) =>
        value as JsonElement? ?? JsonSerializer.SerializeToElement(value, AbstractionsJson.AnyValue);

    private enum Shape { Null, String, Boolean, Number, Array, Object, Other }

    private static Shape ShapeOf(object? value) => value switch
    {
        null => Shape.Null,
        JsonElement element => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => Shape.Null,
            JsonValueKind.String => Shape.String,
            JsonValueKind.True or JsonValueKind.False => Shape.Boolean,
            JsonValueKind.Number => Shape.Number,
            JsonValueKind.Array => Shape.Array,
            JsonValueKind.Object => Shape.Object,
            _ => Shape.Other,
        },
        string => Shape.String,
        bool => Shape.Boolean,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => Shape.Number,
        System.Collections.IDictionary => Shape.Object,
        System.Collections.IEnumerable => Shape.Array,
        _ => Shape.Other,
    };

    private static IEnumerable<object?> ElementsOf(object? array) => array is JsonElement element
        ? element.EnumerateArray().Select(item => (object?)item)
        : ((System.Collections.IEnumerable)array!).Cast<object?>();

    private static bool TryMembersOf(object? literal, out List<object?> values)
    {
        values = [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (literal is JsonElement element)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    return false;
                values.Add(property.Value);
            }

            return true;
        }

        foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)literal!)
        {
            if (entry.Key is not string name || !names.Add(name))
                return false;
            values.Add(entry.Value);
        }

        return true;
    }

    private static bool Fits(object? value, Shape shape, WorkflowLiteralForm form) => form.GradedAs switch
    {
        WorkflowLiteralKind.String => shape == Shape.String && TextFits(TextOf(value), form.Format),
        WorkflowLiteralKind.Boolean => shape == Shape.Boolean,
        WorkflowLiteralKind.Int => shape == Shape.Number && IsIntegral(value),
        WorkflowLiteralKind.Number => shape == Shape.Number && double.IsFinite(DoubleOf(value)),
        WorkflowLiteralKind.Json => shape != Shape.Other && !(value is JsonElement json && JsonMembers.FindRepeat(json) is not null),
        _ => false,
    };

    private static string TextOf(object? value) => value is JsonElement element ? element.GetString()! : (string)value!;

    private static bool TextFits(string text, WorkflowLiteralFormat? format) => format switch
    {
        null => true,
        WorkflowLiteralFormat.Uuid => ReadsAsGuid(text),
        WorkflowLiteralFormat.DateTime => ReadsAsDateTime(text),
        _ => false,
    };

    private static bool ReadsAsGuid(string text) => text.Length == 36 && ReadAsJsonString(text).TryGetGuid(out _);

    private static bool ReadsAsDateTime(string text) => WorkflowDateTimeText.TryRead(ReadAsJsonString(text), out _);

    private static JsonElement ReadAsJsonString(string text)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            writer.WriteStringValue(text);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static double DoubleOf(object? value) => value switch
    {
        JsonElement element => element.TryGetDouble(out var number) ? number : double.NaN,
        decimal number => (double)number,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    private static bool IsIntegral(object? value)
    {
        if (value is JsonElement element && element.TryGetInt64(out _))
            return true;
        if (value is long or int or short or sbyte or byte or ushort or uint)
            return true;
        if (value is ulong unsigned)
            return unsigned <= long.MaxValue;
        if (value is decimal number)
            return Math.Truncate(number) == number && number >= long.MinValue && number <= long.MaxValue;
        var d = DoubleOf(value);
        return double.IsFinite(d) && Math.Truncate(d) == d && d >= -TwoToThe63 && d < TwoToThe63;
    }
}
