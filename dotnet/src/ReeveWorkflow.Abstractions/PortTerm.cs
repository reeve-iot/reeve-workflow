using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ReeveWorkflow.Abstractions;

/// <summary>Which of the two grammars a port term is read under. Both share one lexer and admit no white space
/// and no punctuation outside <c>&lt; &gt; ? $</c>.</summary>
public enum PortTermGrammar
{
    /// <summary>A stored document, an API value or a <c>typeArguments</c> value: <c>Ident</c>,
    /// <c>Ident&lt;Term&gt;</c>, each optionally followed by <c>?</c>. A <c>$</c> is an illegal character, so
    /// no stored text can hold a type parameter.</summary>
    Document,

    /// <summary>A block or plugin declaration: the document grammar plus <c>$Ident</c>, a type parameter the
    /// block declares, which may also be nullable (<c>$T?</c>).</summary>
    Declaration
}

/// <summary>What a <see cref="PortTerm"/> is at its outermost level.</summary>
public enum PortTermKind
{
    /// <summary>The literal <c>null</c>'s term. Not writable; only <see cref="PortTerm.Null"/> has it.</summary>
    Null,

    /// <summary>A name: <c>Int</c>, <c>Shipment</c>. Whether it is registered is the vocabulary's answer.</summary>
    Atom,

    /// <summary>A type parameter, <c>$T</c>. Written only under <see cref="PortTermGrammar.Declaration"/>.</summary>
    Parameter,

    /// <summary>A constructor head over its arguments: one for <c>Collection&lt;Int&gt;</c> and
    /// <c>Gauge&lt;Boolean&gt;</c>, two for <c>Map&lt;String,Int&gt;</c>.</summary>
    Constructor
}

/// <summary>A port type read into a tree: the structure a port type name has, with the notation stripped. A
/// term is immutable. Equality is structural and compares an atom or constructor name ASCII
/// case-insensitively, as every name in this library compares, and a parameter name exactly.</summary>
/// <remarks>Parsing never consults a vocabulary: whether an atom or a constructor exists is the registry's
/// answer. Every walk over a term is iterative, since the text a term is read from is of any length.</remarks>
public sealed class PortTerm : IEquatable<PortTerm>
{
    private static readonly IReadOnlyList<PortTerm> NoArguments = [];

    private PortTerm(PortTermKind kind, string name, IReadOnlyList<PortTerm> arguments, bool isNullable)
    {
        Kind = kind;
        Name = name;
        Arguments = arguments;
        IsNullable = isNullable;
    }

    /// <summary>The term of the literal <c>null</c>: assignable to every nullable term and to nothing else.</summary>
    public static PortTerm Null { get; } = new(PortTermKind.Null, "", NoArguments, true);

    /// <summary>The outermost level of this term.</summary>
    public PortTermKind Kind { get; }

    /// <summary>The atom's or the constructor's name as written, or a parameter's name without its <c>$</c>;
    /// empty for <see cref="Null"/>.</summary>
    public string Name { get; }

    /// <summary>A constructor's arguments in order: one for <c>Collection</c> and a host constructor, two for
    /// <c>Map</c> (the key, then the value); empty for every other kind.</summary>
    public IReadOnlyList<PortTerm> Arguments { get; }

    internal PortTerm? LastArgument => Arguments.Count == 0 ? null : Arguments[^1];

    /// <summary>True when this level carries a <c>?</c>. True for <see cref="Null"/>, which admits nothing else.</summary>
    public bool IsNullable { get; }

    /// <summary>True for the built-in <c>Collection</c> constructor.</summary>
    public bool IsCollection => Kind == PortTermKind.Constructor && Name == PortTypeGrammar.Keyword;

    /// <summary>True for the built-in <c>Map</c> constructor, whose key is <c>String</c>.</summary>
    public bool IsMap => Kind == PortTermKind.Constructor && Name == PortTypeGrammar.MapKeyword;

    /// <summary>An atom. <paramref name="name"/> is an identifier that does not begin with <c>collection</c> in
    /// any ASCII case and is not <c>Map</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not such an identifier.</exception>
    public static PortTerm Atom(string name, bool isNullable = false) =>
        new(PortTermKind.Atom, RequireName(name, nameof(name)), NoArguments, isNullable);

    /// <summary>A type parameter, <paramref name="name"/> without its <c>$</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not an identifier.</exception>
    public static PortTerm Parameter(string name, bool isNullable = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        return PortTypeGrammar.IsIdentifier(name)
            ? new(PortTermKind.Parameter, name, NoArguments, isNullable)
            : throw new ArgumentException($"'{name}' is not an identifier.", nameof(name));
    }

    /// <summary>A constructor over one argument. <paramref name="name"/> is an identifier; it may be
    /// <c>Collection</c> and otherwise neither begins with <c>collection</c> in any ASCII case nor is <c>Map</c>,
    /// which <see cref="MapOf"/> builds.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="argument"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not such an identifier, or
    /// <paramref name="argument"/> is <see cref="Null"/>, which no text writes.</exception>
    public static PortTerm Constructor(string name, PortTerm argument, bool isNullable = false)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Kind == PortTermKind.Null)
            throw new ArgumentException("A constructor's argument cannot be the null literal's term.", nameof(argument));
        return new(PortTermKind.Constructor,
            name == PortTypeGrammar.Keyword ? name : RequireName(name, nameof(name)), [argument], isNullable);
    }

    /// <summary>The term of the built-in map from <c>String</c> to <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is <see cref="Null"/>, which no text writes.</exception>
    public static PortTerm MapOf(PortTerm value, bool isNullable = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind == PortTermKind.Null)
            throw new ArgumentException("A map's value cannot be the null literal's term.", nameof(value));
        return new(PortTermKind.Constructor, PortTypeGrammar.MapKeyword,
            [new(PortTermKind.Atom, "String", NoArguments, false), value], isNullable);
    }

    /// <summary>The term of the built-in collection of <paramref name="element"/>.</summary>
    public static PortTerm CollectionOf(PortTerm element, bool isNullable = false) =>
        Constructor(PortTypeGrammar.Keyword, element, isNullable);

    /// <summary>This term with one <c>?</c> at its outermost level: <c>(X?)?</c> is <c>X?</c>. The term itself
    /// when it is nullable already, and <see cref="Null"/> for <see cref="Null"/>.</summary>
    public PortTerm AsNullable() => IsNullable ? this : new(Kind, Name, Arguments, true);

    /// <summary>This term without the <c>?</c> at its outermost level. The term itself when it has none.</summary>
    /// <exception cref="InvalidOperationException">This is <see cref="Null"/>, which has no non-null form.</exception>
    public PortTerm WithoutNullable() => Kind == PortTermKind.Null
        ? throw new InvalidOperationException("The null literal's term has no non-null form.")
        : IsNullable ? new(Kind, Name, Arguments, false) : this;

    /// <summary>Whether a parameter stands anywhere in this term.</summary>
    public bool ContainsParameter
    {
        get
        {
            for (var term = this; term is not null; term = term.LastArgument)
                if (term.Kind == PortTermKind.Parameter)
                    return true;
            return false;
        }
    }

    /// <summary>Reads <paramref name="text"/> without trimming or repair. False for anything the grammar
    /// refuses, with the fault, its position and a hint in <paramref name="error"/>.</summary>
    public static bool TryParse(
        string? text, PortTermGrammar grammar, [NotNullWhen(true)] out PortTerm? term,
        [NotNullWhen(false)] out PortTypeSyntaxError? error)
    {
        var read = PortTypeGrammar.TryParse(text, grammar, build: true, out var parsed, out error);
        term = parsed!;
        return read;
    }

    /// <summary>Reads <paramref name="text"/> under <paramref name="grammar"/>.</summary>
    /// <exception cref="PortTypeSyntaxException"><paramref name="text"/> is not a sentence of the grammar.</exception>
    public static PortTerm Parse(string? text, PortTermGrammar grammar) =>
        TryParse(text, grammar, out var term, out var error) ? term : throw new PortTypeSyntaxException(error);

    /// <summary>The one notation: constructor heads around their arguments in angle brackets, a map's key and value
    /// separated by one comma, a <c>$</c> before a
    /// parameter, a <c>?</c> after each nullable level. Reading the result gives an equal term. The null
    /// literal's term renders as <c>null</c>, which no grammar reads.</summary>
    public override string ToString()
    {
        if (Kind == PortTermKind.Null) return "null";
        var builder = new StringBuilder();
        var closing = new List<bool>();
        var term = this;
        while (term.Kind == PortTermKind.Constructor)
        {
            builder.Append(term.Name).Append('<');
            for (var k = 0; k < term.Arguments.Count - 1; k++)
                builder.Append(term.Arguments[k].Name).Append(',');
            closing.Add(term.IsNullable);
            term = term.LastArgument!;
        }
        if (term.Kind == PortTermKind.Parameter) builder.Append('$');
        builder.Append(term.Name);
        if (term.IsNullable) builder.Append('?');
        for (var i = closing.Count - 1; i >= 0; i--)
        {
            builder.Append('>');
            if (closing[i]) builder.Append('?');
        }
        return builder.ToString();
    }

    /// <inheritdoc />
    public bool Equals(PortTerm? other)
    {
        var a = this;
        var b = other;
        while (true)
        {
            if (ReferenceEquals(a, b)) return true;
            if (b is null || a is null || a.Kind != b.Kind || a.IsNullable != b.IsNullable) return false;
            if (!string.Equals(a.Name, b.Name, a.Kind == PortTermKind.Parameter ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                return false;
            if (a.Arguments.Count != b.Arguments.Count) return false;
            if (a.Arguments.Count == 0) return true;
            for (var k = 0; k < a.Arguments.Count - 1; k++)
                if (!a.Arguments[k].Equals(b.Arguments[k])) return false;
            a = a.LastArgument!;
            b = b.LastArgument;
        }
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PortTerm other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var term = this; term is not null; term = term.LastArgument)
        {
            hash.Add(term.Kind);
            hash.Add(term.IsNullable);
            hash.Add(term.Name, term.Kind == PortTermKind.Parameter ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        }
        return hash.ToHashCode();
    }

    internal static PortTerm Built(PortTermKind kind, string name, IReadOnlyList<PortTerm>? arguments, bool isNullable) =>
        new(kind, name, arguments ?? NoArguments, isNullable);

    private static string RequireName(string name, string parameter)
    {
        ArgumentNullException.ThrowIfNull(name, parameter);
        if (!PortTypeGrammar.IsAtom(name))
            throw new ArgumentException(
                $"'{name}' is not a name: it must be an identifier that does not begin with 'collection' and is not 'Map'.", parameter);
        return name;
    }
}
