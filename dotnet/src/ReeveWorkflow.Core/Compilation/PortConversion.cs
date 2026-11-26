using System.Collections;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>What the input binder does to a value that crosses an accepted assignment. A null passes every conversion unchanged.</summary>
public enum PortConversionKind
{
    /// <summary>The source's CLR type is reference-assignable to the target's: the value passes as it is.</summary>
    Identity,

    /// <summary>The row <c>Int → Number</c>: a <c>long</c> becomes a <c>double</c>.</summary>
    IntToNumber,

    /// <summary>A non-nullable value into a nullable target whose CLR type is a value type, after <see cref="PortConversion.Inner"/>.</summary>
    NullLift,

    /// <summary>A collection rebuilt in the target's CLR type, <see cref="PortConversion.Inner"/> applied to each element; a map
    /// likewise, applied to each value.</summary>
    Elementwise,

    /// <summary>A value written as JSON with its source term's serializer, into a <c>Json</c> port.</summary>
    ToJson,
}

/// <summary>The conversion the compiler chose for one accepted assignment, which the input binder applies. Its text is the
/// form <c>contracts/port-type-assignability.json</c> records with each vector: <c>Identity</c>, <c>IntToNumber</c>,
/// <c>ToJson</c>, <c>NullLift</c> or <c>NullLift(…)</c>, <c>Elementwise(…)</c>.</summary>
public sealed class PortConversion
{
    private PortConversion(PortConversionKind kind, PortConversion? inner, ClosedPortType? type, bool refusesNull = false)
    {
        Kind = kind;
        Inner = inner;
        Type = type;
        this.refusesNull = refusesNull;
    }

    private readonly bool refusesNull;

    /// <summary>The identity conversion.</summary>
    public static PortConversion Identity { get; } = new(PortConversionKind.Identity, null, null);

    /// <summary>What this conversion does.</summary>
    public PortConversionKind Kind { get; }

    /// <summary>For <see cref="PortConversionKind.NullLift"/> the conversion applied before the lift; for
    /// <see cref="PortConversionKind.Elementwise"/> the conversion applied to each element; otherwise null.</summary>
    public PortConversion? Inner { get; }

    internal ClosedPortType? Type { get; }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = new System.Text.StringBuilder();
        var closing = 0;
        for (var level = this; ; level = level.Inner!)
        {
            if (level.Kind == PortConversionKind.NullLift && level.Inner!.Kind == PortConversionKind.Identity)
            {
                text.Append("NullLift");
                break;
            }

            text.Append(level.Kind);
            if (level.Inner is null)
                break;
            text.Append('(');
            closing++;
        }

        return text.Append(')', closing).ToString();
    }

    internal static PortConversion Between(
        PortTerm from, PortTerm to, Func<PortTerm, bool> isValueType, Func<PortTerm, ClosedPortType?>? closed = null)
    {
        if (from.Kind == PortTermKind.Null)
            return Identity;
        var same = SameBelow(from, to);
        var wrappers = new List<(PortConversionKind Kind, bool Forced, ClosedPortType? Type)>();
        var (f, t, depth) = (from, to, 0);
        var (source, target) = (closed?.Invoke(from), closed?.Invoke(to));
        PortConversion built;
        while (true)
        {
            if (f.IsNullable == t.IsNullable && same[depth])
            {
                built = Identity;
                break;
            }

            if (t.IsNullable)
            {
                var stripped = t.WithoutNullable();
                if (!f.IsNullable && isValueType(stripped))
                    wrappers.Add((PortConversionKind.NullLift, false, null));
                (f, t) = (f.IsNullable ? f.WithoutNullable() : f, stripped);
                continue;
            }

            if (t.Kind == PortTermKind.Atom && string.Equals(t.Name, "Json", StringComparison.OrdinalIgnoreCase))
            {
                built = new PortConversion(PortConversionKind.ToJson, null, source, refusesNull: !to.IsNullable);
                break;
            }

            if (f.IsCollection && t.IsCollection || f.IsMap && t.IsMap)
            {
                var (element, targetElement) = (f.LastArgument!, t.LastArgument!);
                var stripped = element.IsNullable ? element.WithoutNullable() : element;
                var nullabilityChanged = element.IsNullable != targetElement.IsNullable;
                var forced = f.IsMap
                    ? !same[depth + 1] || nullabilityChanged && isValueType(stripped)
                    : (nullabilityChanged || !same[depth + 1]) && isValueType(stripped);
                wrappers.Add((PortConversionKind.Elementwise, forced, target));
                var throughMap = f.IsMap;
                (f, t, depth) = (element, targetElement, depth + 1);
                (source, target) = (throughMap ? source?.ValueType : source?.ElementType, throughMap ? target?.ValueType : target?.ElementType);
                continue;
            }

            built = f.Kind == PortTermKind.Atom && t.Kind == PortTermKind.Atom
                && string.Equals(f.Name, "Int", StringComparison.OrdinalIgnoreCase) && string.Equals(t.Name, "Number", StringComparison.OrdinalIgnoreCase)
                ? new PortConversion(PortConversionKind.IntToNumber, null, null)
                : Identity;
            break;
        }

        for (var i = wrappers.Count - 1; i >= 0; i--)
        {
            var (kind, forced, type) = wrappers[i];
            built = kind == PortConversionKind.NullLift ? new PortConversion(kind, built, null)
                : built.Kind != PortConversionKind.Identity || forced ? new PortConversion(kind, built, type)
                : Identity;
        }

        return built;
    }

    private static bool[] SameBelow(PortTerm from, PortTerm to)
    {
        var pairs = new List<(PortTerm From, PortTerm To)>();
        for (var (f, t) = (from, to); f is not null && t is not null; (f, t) = (f.LastArgument, t.LastArgument))
            pairs.Add((f, t));
        var same = new bool[pairs.Count + 1];
        for (var d = pairs.Count - 1; d >= 0; d--)
        {
            var (f, t) = pairs[d];
            var below = f.LastArgument is null && t.LastArgument is null
                || f.LastArgument is not null && t.LastArgument is not null && f.LastArgument.IsNullable == t.LastArgument.IsNullable && same[d + 1];
            same[d] = f.Kind == t.Kind && below
                && string.Equals(f.Name, t.Name, f.Kind == PortTermKind.Parameter ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        return same;
    }

    internal object? Apply(object? value)
    {
        var conversion = Lifted(this);
        if (value is null || conversion.Kind != PortConversionKind.Elementwise)
            return conversion.ApplyLevel(value);

        var open = new Stack<(IEnumerator<KeyValuePair<string?, object?>> Items, PortConversion Conversion, List<KeyValuePair<string, object?>> Built)>();
        open.Push((conversion.Items(value), conversion, []));
        while (true)
        {
            var (items, level, built) = open.Peek();
            if (!items.MoveNext())
            {
                open.Pop();
                var rebuilt = level.Rebuilt(built);
                if (open.Count == 0)
                    return rebuilt;
                var parent = open.Peek();
                parent.Built.Add(new(parent.Items.Current.Key!, rebuilt));
                continue;
            }

            var (key, current) = items.Current;
            var inner = Lifted(level.Inner!);
            if (current is not null && inner.Kind == PortConversionKind.Elementwise)
                open.Push((inner.Items(current), inner, []));
            else
                built.Add(new(key!, inner.ApplyLevel(current)));
        }
    }

    private static PortConversion Lifted(PortConversion conversion)
    {
        while (conversion.Kind == PortConversionKind.NullLift)
            conversion = conversion.Inner!;
        return conversion;
    }

    private object? ApplyLevel(object? value)
    {
        if (value is null)
            return null;
        switch (Kind)
        {
            case PortConversionKind.IntToNumber:
                return value switch
                {
                    long l => (double)l, int i => (double)i, short s => (double)s, byte b => (double)b,
                    sbyte sb => (double)sb, ushort us => (double)us, uint ui => (double)ui,
                    _ => throw new PortConversionException($"A {value.GetType().Name} is not a value of Int, so it cannot widen to Number."),
                };
            case PortConversionKind.ToJson:
                try
                {
                    var json = Closed().ToJson(value);
                    return json is null && refusesNull
                        ? throw new PortConversionException(
                            $"A value of '{Closed().Term}' serialised to JSON null, and the Json port is not nullable.",
                            WorkflowErrorCodes.BlockOutputTypeMismatch)
                        : json;
                }
                catch (ArgumentException ex)
                {
                    throw new PortConversionException(ex.Message, ex);
                }

            default:
                return value;
        }
    }

    private IEnumerator<KeyValuePair<string?, object?>> Items(object value)
    {
        if (Closed().ValueType is not null)
        {
            return PortMaps.TryEntries(value, out var entries)
                ? entries.Select(e => new KeyValuePair<string?, object?>(e.Key, e.Value)).GetEnumerator()
                : throw new PortConversionException($"A {value.GetType().Name} is not a value of '{Closed().Term}', so it cannot be rebuilt.");
        }

        return value is IEnumerable items and not string && !PortMaps.IsMap(value)
            ? items.Cast<object?>().Select(e => new KeyValuePair<string?, object?>(null, e)).GetEnumerator()
            : throw new PortConversionException($"A {value.GetType().Name} is not a value of '{Closed().Term}', so it cannot be rebuilt.");
    }

    private object Rebuilt(List<KeyValuePair<string, object?>> members)
    {
        try
        {
            return Closed().ValueType is null ? Closed().CreateList(members.Select(m => m.Value)) : Closed().CreateMap(members);
        }
        catch (ArgumentException ex)
        {
            throw new PortConversionException(ex.Message, ex);
        }
    }

    private ClosedPortType Closed() =>
        Type ?? throw new InvalidOperationException($"The conversion {this} was read without the closed types that apply it.");
}

internal sealed class PortConversionException : Exception, IWorkflowCodedException
{
    public PortConversionException(string message)
        : base(message)
    {
    }

    public PortConversionException(string message, string code)
        : base(message)
    {
        NamedCode = code;
    }

    public PortConversionException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public string? NamedCode { get; init; }

    string IWorkflowCodedException.Code => NamedCode ?? WorkflowErrorCodes.BlockFailed;
}
