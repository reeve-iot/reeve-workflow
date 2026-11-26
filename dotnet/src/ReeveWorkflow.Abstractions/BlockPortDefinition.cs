using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReeveWorkflow.Abstractions;

/// <summary>Reads and writes a port type as its name, which is what it was on the wire while it was an enum
/// and what every stored descriptor already carries. On the type, so no host has to configure it.</summary>
/// <remarks>Public because the source generator constructs it in the consuming assembly: an internal converter
/// named by a public attribute leaves a source-generating host with no metadata for this type at all.
/// Strict: null, non-string, blank and malformed all refuse rather than reading as a guessed type: a blank or absent
/// port type is an error.</remarks>
public sealed class PortValueTypeJsonConverter : JsonConverter<PortValueType>
{
    public override PortValueType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            if (PortValueType.TryParse(reader.GetString(), out var type, out var error))
                return type;
            throw new JsonException(error.Message);
        }
        throw new JsonException(reader.TokenType == JsonTokenType.Null
            ? "A port type is written as its name; found null."
            : $"A port type is written as its name; found {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, PortValueType value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Name);

    /// <summary>The same shape when one is a dictionary key, so a map keyed by port type reads back too.</summary>
    public override PortValueType ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (PortValueType.TryParse(reader.GetString(), out var type, out var error))
            return type;
        throw new JsonException(error.Message);
    }

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, PortValueType value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Name);
}

/// <summary>A variable that holds a value only while a block evaluates an <c>Expression&lt;T&gt;</c> input, such as the item a
/// filter tests; the expression reads it as <c>${Name}</c>. Declared on <see cref="BlockPortDefinition.ScopeVariables"/>.</summary>
/// <param name="Name">One of the library's scope-variable names, <c>item</c> or <c>value</c>.</param>
/// <param name="Type">A declaration term, as a port's type is: it may name the block's type parameters
/// (<c>$T</c>), and it holds no <c>Json</c>. A parameter the block's sub-canvas outputs bind may not be named.</param>
public sealed record ExpressionScopeVariable(string Name, string Type);

/// <summary>One declared port. Its type is a port type, or a declaration term that names the block's type parameters
/// (<c>$T</c>, <c>Collection&lt;$T&gt;</c>). An input typed <c>Expression&lt;T&gt;</c> is handed to the block as its
/// unevaluated <c>expr:</c> text; <c>Json</c> is an output type only.</summary>
public sealed record BlockPortDefinition(
    string Name, string DisplayName, PortValueType Type, bool IsRequired,
    IReadOnlyCollection<string>? AllowedValues = null)
{
    private readonly PortValueType type = Validated(Type);

    /// <summary>The variables an <c>Expression&lt;T&gt;</c> input's expression may read while the block evaluates it
    /// (<see cref="IWorkflowBlockExpressions.Evaluate"/>), empty by default. Declare one only on an <c>Expression&lt;T&gt;</c>
    /// input, with a name the library reserves (<c>item</c>, <c>value</c>), once per input, and a type that is a declaration
    /// term without <c>Json</c>; registration refuses any other with <c>block-port-declaration-invalid</c>.</summary>
    public IReadOnlyList<ExpressionScopeVariable> ScopeVariables { get; init; } = [];

    /// <summary>True when the input's entries may be protected or referenced secrets: the library masks them on read and in a
    /// recorded input, the host's <c>IWorkflowSecretCodec</c> protects them on save, and the block reveals them through
    /// <see cref="WorkflowBlockContext.Secrets"/> when it runs. Declare it only on an input of the term <c>Map&lt;String,String&gt;</c>
    /// or <c>Map&lt;String,String&gt;?</c>; registration refuses any other port with <c>block-port-declaration-invalid</c>.</summary>
    /// <remarks>Whoever may edit a workflow may point the node that holds a secret anywhere: a secret an administrator bound to a
    /// header is sent to whatever <c>Url</c> the editing user then writes, because the node reveals it for its own request. A host
    /// that must prevent that gates who edits workflows; the library does not.</remarks>
    public bool SecretValues { get; init; }

    /// <summary>An explicit accessor, not a property initializer, so <c>with { Type = default }</c> is caught too: a
    /// property initializer only runs from the primary constructor, never from a record's copy constructor.</summary>
    public PortValueType Type
    {
        get => type;
        init => type = Validated(value);
    }

    private static PortValueType Validated(PortValueType type) => type.IsDefault
        ? throw new ArgumentException("A port's type cannot be default(PortValueType).", nameof(Type))
        : type;
}

public enum ConfigFieldType
{
    String,
    Number,
    Boolean,
    Int,
    Code,
    /// <summary>One of the names in <see cref="BlockConfigFieldDefinition.AllowedValues"/>, stored as that name.
    /// The compiler and the editor refuse any other value, and the run refuses one that arrives from an expression.</summary>
    Enum
}

public sealed record BlockConfigFieldDefinition(
    string Name,
    string Label,
    ConfigFieldType Type,
    bool Required,
    string? DefaultValue = null,
    string? Description = null)
{
    /// <summary>The names an Enum field accepts, in editor order, compared ordinally. Required on an Enum field and
    /// absent on any other; registration refuses none, a blank or repeated name, and a default outside them.</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }
}

/// <summary>The type a port carries, as a name rather than a member of a closed set. The vocabulary is the
/// host's: the library seeds the eight that carry no domain, and everything else is declared where it is owned.</summary>
/// <remarks>A parsed value: it cannot hold a malformed name, and it cannot be constructed from a blank one.
/// <see cref="Of"/> parses the port-type grammar — one notation, <c>Collection&lt;T&gt;</c> —
/// and <see cref="Name"/> is the text it accepted, byte for byte; nothing is ever rewritten into another
/// spelling. <c>default(PortValueType)</c> names no type: see <see cref="IsDefault"/>.</remarks>
[JsonConverter(typeof(PortValueTypeJsonConverter))]
public readonly record struct PortValueType
{
    private readonly string? name;

    private PortValueType(string name) => this.name = name;

    /// <summary>The text <see cref="Of"/> accepted, exactly. Throws on <c>default(PortValueType)</c>, which
    /// names no type — there is no text to return.</summary>
    /// <exception cref="InvalidOperationException">This instance is <c>default(PortValueType)</c>.</exception>
    public string Name => name ?? throw new InvalidOperationException(
        "default(PortValueType) names no type; construct one with PortValueType.Of.");

    /// <summary>True only for <c>default(PortValueType)</c> — a port that was never given a type, as
    /// opposed to one explicitly declared a type.</summary>
    public bool IsDefault => name is null;

    /// <summary>True when this name's outermost notation is a non-nullable <c>Collection&lt;…&gt;</c>. False for
    /// <c>default(PortValueType)</c> and for <c>Collection&lt;…&gt;?</c>, which is not a list but a list or null.</summary>
    public bool IsCollection => name is not null
        && name.StartsWith(PortTypeGrammar.BracketPrefix, StringComparison.Ordinal) && name[^1] != '?';

    /// <summary>A port type by name. Parses the document grammar in full: an identifier not beginning with
    /// <c>collection</c> in any ASCII case, or <c>Name&lt;</c> a term <c>&gt;</c>, each optionally followed by
    /// <c>?</c>; nothing else. Which names exist is the vocabulary's answer, not the parser's. Read a type
    /// parameter (<c>$T</c>) or the structure of a name with <see cref="PortTerm"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="PortTypeSyntaxException"><paramref name="name"/> is not a sentence of the grammar.</exception>
    public static PortValueType Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!TryParse(name, out var type, out var error))
            throw new PortTypeSyntaxException(error);
        return type;
    }

    /// <summary>Parses <paramref name="text"/> without throwing. False for null, empty, or anything else the
    /// grammar refuses — <paramref name="error"/> carries the fault, position and a hint.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out PortValueType type, [NotNullWhen(false)] out PortTypeSyntaxError? error)
    {
        if (PortTypeGrammar.TryParse(text, out error))
        {
            type = new PortValueType(text!);
            return true;
        }
        type = default;
        return false;
    }

    /// <summary>A port type as a block declaration writes it: the document grammar plus <c>$Name</c>, a type
    /// parameter the block declares (<see cref="IWorkflowBlockFactory.TypeParameters"/>), nullable or not. Only a
    /// declaration holds one: <see cref="Of"/> and <see cref="TryParse"/> refuse a <c>$</c>, so no stored document or
    /// API value can. Read the structure of the result with <see cref="PortTerm"/> under
    /// <see cref="PortTermGrammar.Declaration"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="PortTypeSyntaxException"><paramref name="name"/> is not a sentence of the declaration grammar.</exception>
    public static PortValueType OfDeclaration(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!TryParseDeclaration(name, out var type, out var error))
            throw new PortTypeSyntaxException(error);
        return type;
    }

    /// <summary>Parses <paramref name="text"/> under the declaration grammar without throwing.</summary>
    public static bool TryParseDeclaration(
        [NotNullWhen(true)] string? text, out PortValueType type, [NotNullWhen(false)] out PortTypeSyntaxError? error)
    {
        if (PortTypeGrammar.TryParse(text, PortTermGrammar.Declaration, build: false, out _, out error))
        {
            type = new PortValueType(text!);
            return true;
        }
        type = default;
        return false;
    }

    /// <summary>True when this is an <c>Expression&lt;T&gt;</c> input type: the head <c>Expression</c> (compared exactly: any
    /// other case is a refused declaration, never this) over one argument, not nullable. The input holds the text of an
    /// <c>expr:</c> value and the engine delivers it to the block unevaluated.</summary>
    public bool IsExpression => name is not null
        && name.StartsWith("Expression<", StringComparison.Ordinal) && name[^1] == '>';

    /// <summary>The collection of <paramref name="element"/>, spelled <c>Collection&lt;element.Name&gt;</c>.
    /// Never collapses: a collection of a collection is a list of lists, not a bare <c>Collection</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="element"/> is <c>default(PortValueType)</c>.</exception>
    public static PortValueType CollectionOf(PortValueType element) => element.IsDefault
        ? throw new ArgumentException("A collection cannot be built over default(PortValueType).", nameof(element))
        : new PortValueType(PortTypeGrammar.BracketPrefix + element.Name + ">");

    /// <summary>True when this name's outermost notation is a non-nullable <c>Map&lt;String,…&gt;</c>. False for
    /// <c>default(PortValueType)</c> and for <c>Map&lt;String,…&gt;?</c>, which is a map or null.</summary>
    public bool IsMap => name is not null
        && name.StartsWith(PortTypeGrammar.MapKeyword + "<", StringComparison.Ordinal) && name[^1] != '?';

    /// <summary>The map from <c>String</c> to <paramref name="value"/>, spelled <c>Map&lt;String,value.Name&gt;</c>: a port
    /// that carries a JSON object's members as a map, whose values are of the term <paramref name="value"/> names. Like
    /// <see cref="CollectionOf"/> it composes an already valid name, so a declared <c>$T</c> value stays a <c>$T</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is <c>default(PortValueType)</c>.</exception>
    public static PortValueType MapOf(PortValueType value)
    {
        if (value.IsDefault)
            throw new ArgumentException("A map cannot be built over default(PortValueType).", nameof(value));
        return new PortValueType(PortTypeGrammar.MapKeyword + "<String," + value.Name + ">");
    }

    /// <summary>What a collection carries — the text between its outer <c>Collection&lt;</c> and the matching
    /// final <c>&gt;</c> — and the type itself for anything that is not one.</summary>
    /// <exception cref="InvalidOperationException">This instance is <c>default(PortValueType)</c>.</exception>
    public PortValueType ElementType => IsDefault
        ? throw new InvalidOperationException("default(PortValueType) names no type.")
        : IsCollection ? new PortValueType(name![PortTypeGrammar.BracketPrefix.Length..^1]) : this;

    /// <summary>Every valid name spells the keyword <c>Collection</c> exactly, so ordinal-ignore-case text
    /// equality over two well-formed names <em>is</em> "same depth and the atom equal ignoring ASCII case" —
    /// not an approximation of that structural equality. <c>default</c> equals only another <c>default</c>.</summary>
    public bool Equals(PortValueType other) => name is null || other.name is null
        ? name is null && other.name is null
        : StringComparer.OrdinalIgnoreCase.Equals(name, other.name);

    /// <inheritdoc />
    public override int GetHashCode() => name is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(name);

    /// <summary>Never throws, unlike <see cref="Name"/>: a default instance prints text that cannot itself
    /// parse, so it is never mistaken for a real type name.</summary>
    public override string ToString() => name ?? "default(PortValueType)";

    /// <summary>Text.</summary>
    public static PortValueType String { get; } = new("String");

    /// <summary>A number the host does not narrow further.</summary>
    public static PortValueType Number { get; } = new("Number");

    /// <summary>A whole number.</summary>
    public static PortValueType Int { get; } = new("Int");

    /// <summary>True or false.</summary>
    public static PortValueType Boolean { get; } = new("Boolean");

    /// <summary>An instant.</summary>
    public static PortValueType DateTime { get; } = new("DateTime");

    /// <summary>Structured data — an object literal, or anything else the vocabulary has no narrower name
    /// for. Widens to nothing: it is a producer's type, never a promise
    /// that two <c>Json</c> ports agree on shape.</summary>
    public static PortValueType Json { get; } = new("Json");

    /// <summary>Every type the library seeds, in the order the editor's built-ins declare them. None of them
    /// names a domain: a device, a room and a scene belong to whoever has those things. Six entries:
    /// <c>Collection</c> is notation, not a builtin, and <c>Float</c> and <c>Any</c> are not in the vocabulary.</summary>
    public static IReadOnlyList<PortValueType> Builtins { get; } =
        [String, Number, Int, Boolean, DateTime, Json];
}
