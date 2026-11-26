using System.Collections;
using System.Globalization;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;

namespace ReeveWorkflow.Core.Expressions;

internal static class Terms
{
    public static readonly PortTerm Int = PortTerm.Atom("Int");
    public static readonly PortTerm Number = PortTerm.Atom("Number");
    public static readonly PortTerm String = PortTerm.Atom("String");
    public static readonly PortTerm Boolean = PortTerm.Atom("Boolean");
    public static readonly PortTerm DateTime = PortTerm.Atom("DateTime");
    public static readonly PortTerm Json = PortTerm.Atom("Json");

    public static bool IsNumeric(PortTerm term) => term.Equals(Int) || term.Equals(Number);

    public static PortTerm Strip(PortTerm term) => term.Kind == PortTermKind.Null || !term.IsNullable ? term : term.WithoutNullable();

    public static bool IsJson(PortTerm term) => Strip(term).Equals(Json);

    public static PortTerm Like(PortTerm term, PortTerm like) => like.IsNullable && like.Kind != PortTermKind.Null ? term.AsNullable() : term;
}

internal static class TermValues
{
    public static object? Convert(object? value, PortTerm from, PortTerm to, WorkflowTypeVocabulary vocabulary)
    {
        if (value is null)
            return null;
        var source = Terms.Strip(from);
        var target = Terms.Strip(to);
        if (source.Equals(target))
            return value;
        if (source.Equals(Terms.Int) && target.Equals(Terms.Number))
            return (double)(long)value;
        if (source.IsCollection && target.IsCollection)
        {
            var elementClr = vocabulary.ClrTypeOf(target.Arguments[0])
                ?? throw new InvalidOperationException($"The term '{target}' has no CLR type.");
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementClr))!;
            foreach (var item in (IEnumerable)value)
                list.Add(Convert(item, source.Arguments[0], target.Arguments[0], vocabulary));
            return list;
        }

        if (source.IsMap && target.IsMap)
        {
            var type = vocabulary.ClosedTypeOf(target) ?? throw new InvalidOperationException($"The term '{target}' has no CLR type.");
            var entries = PortMaps.TryEntries(value, out var read)
                ? read
                : throw new InvalidOperationException($"A value of '{from}' is not a map.");
            return type.CreateMap(entries.Select(e => new KeyValuePair<string, object?>(
                e.Key, Convert(e.Value, source.Arguments[1], target.Arguments[1], vocabulary))));
        }

        return vocabulary.ClrTypeOf(target)?.IsInstanceOfType(value) == true
            ? value
            : throw new InvalidOperationException($"A value of '{from}' cannot be brought to '{to}'.");
    }

    public static bool AreEqual(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (left is string a && right is string b)
            return string.Equals(a, b, StringComparison.Ordinal);
        if (PortMaps.TryEntries(left, out var leftEntries) && PortMaps.TryEntries(right, out var rightEntries))
        {
            var members = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, value) in rightEntries)
                members[key] = value;
            var count = 0;
            foreach (var (key, value) in leftEntries)
            {
                count++;
                if (!members.TryGetValue(key, out var other) || !AreEqual(value, other))
                    return false;
            }

            return count == members.Count;
        }

        if (left is IEnumerable l && right is IEnumerable r && left is not string)
        {
            var leftItems = l.Cast<object?>().ToList();
            var rightItems = r.Cast<object?>().ToList();
            return leftItems.Count == rightItems.Count && leftItems.Zip(rightItems).All(pair => AreEqual(pair.First, pair.Second));
        }

        return left.Equals(right);
    }

    public static string Text(object? value) => value switch
    {
        string s => s,
        long n => IntText(n),
        double d => NumberText(d),
        bool b => BooleanText(b),
        DateTimeOffset t => DateTimeText(t),
        _ => throw new InvalidOperationException($"A {value?.GetType().Name ?? "null"} has no text form."),
    };

    public static string IntText(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string NumberText(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string BooleanText(bool value) => value ? "true" : "false";

    public static string DateTimeText(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
