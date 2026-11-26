using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;

namespace ReeveWorkflow.Abstractions;

/// <summary>What a host registers to teach the vocabulary an atom: its name, the CLR type a value of it has, the
/// spellings that read as it and the direct widening rows out of it. One record per atom; a constructor is a
/// <see cref="WorkflowTypeConstructorRecord"/>.</summary>
/// <remarks>Register it with <c>workflows.Seams.PortTypes.Add</c> at startup, or, from a plugin, through
/// <c>IWorkflowTypeRegistry.Register</c>.</remarks>
/// <param name="Name">A plain identifier that does not begin with <c>collection</c>, is not <c>Any</c>,
/// <c>Float</c> or <c>Expression</c> in any case, and names no registered atom, alias or constructor.</param>
/// <param name="ClrType">The CLR type a value of this atom has; <c>object</c> and <see cref="ValueType"/> are refused.</param>
public sealed record WorkflowTypeRecord(
    string Name,
    [property: DynamicallyAccessedMembers(WorkflowCapabilityClaims.ClaimedMembers)]
    [param: DynamicallyAccessedMembers(WorkflowCapabilityClaims.ClaimedMembers)]
    Type ClrType) : WorkflowTypeRegistrationRecord
{
    /// <summary>Further spellings that read as this atom in a stored name, each a plain identifier, none equal to
    /// <see cref="Name"/> in any case. A stored document must spell the type as <see cref="Name"/>: the compiler refuses
    /// an alias there as <c>port-type-alias</c> and names the canonical spelling, and nothing rewrites the document.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>How a value of this atom is written as JSON; null when none is declared. A literal form and a
    /// <see cref="WorkflowCapability.JsonSerializable"/> claim each need one.</summary>
    public JsonTypeInfo? JsonTypeInfo { get; init; }

    /// <summary>How a literal of this atom is graded: the JSON shape it must have and, for a string, how its text is read.
    /// Without a form no literal is a literal of this atom, and every one written for a port of it is refused with
    /// <c>literal-port-value-invalid</c>. A form outside <see cref="WorkflowLiteralKind"/> and <see cref="WorkflowLiteralFormat"/>,
    /// a format on a form not graded as String, or a form without a <see cref="JsonTypeInfo"/> is refused as
    /// <c>port-type-registration-invalid</c>.</summary>
    public WorkflowLiteralForm? Literal { get; init; }

    /// <summary>The capabilities this atom claims: <see cref="WorkflowCapability.Comparable"/>, <see cref="WorkflowCapability.Equatable"/>
    /// and <see cref="WorkflowCapability.JsonSerializable"/>, each verified against <see cref="ClrType"/> when the record is
    /// registered. <c>Iterable</c> and <c>Numeric</c> are the library's, and claiming either is refused with
    /// <c>capability-claim-invalid</c>, as is a claim the CLR type does not bear out. Nothing is inferred from the CLR
    /// type's interfaces: a type that is not claimed implements nothing.</summary>
    public IReadOnlySet<WorkflowCapability> Claims { get; init; } = new HashSet<WorkflowCapability>();

    /// <summary>The terms a value of this atom may feed directly, one hop and one way: each a closed,
    /// non-nullable document term, spelled canonically, that is registered, that no library atom, <c>Json</c>
    /// included, spells, that is not a <c>Collection</c> term (a collection widens through its element), and whose
    /// CLR type this record's <see cref="ClrType"/> is reference-assignable to. At most one row leads into any one
    /// constructor head. A target owned by neither the library, the host nor this record's own owner is refused.</summary>
    public IReadOnlyList<string> WidensTo { get; init; } = [];

    /// <summary>Every rule this record breaks on its own, in the order a host reads them; empty when the record
    /// is acceptable. Whether its names are free, and whether its rows lead anywhere, are the vocabulary's questions.</summary>
    public IReadOnlyList<WorkflowRegistrationRefusal> Validate() => [.. ValidateRules(), .. ValidateClaims()];

    internal IReadOnlyList<WorkflowRegistrationRefusal> ValidateClaims() =>
        [.. WorkflowCapabilityClaims.Verify("Port type", Name, ClrType, Claims, JsonTypeInfo)];

    internal IReadOnlyList<WorkflowRegistrationRefusal> ValidateRules()
    {
        var refusals = new List<WorkflowRegistrationRefusal>();
        void Refuse(string why) => refusals.Add(new WorkflowRegistrationRefusal(
            WorkflowRegistrationCodes.PortTypeRegistrationInvalid, Name ?? "",
            $"Port type '{Name}' cannot be registered: {why}{(why.EndsWith('.') ? "" : ".")}"));

        if (Name is null || !PortTypeGrammar.IsIdentifier(Name))
            Refuse("it is not a name");
        else if (Name.StartsWith(PortTypeGrammar.Keyword, StringComparison.OrdinalIgnoreCase))
            Refuse("names starting with 'collection' are reserved");
        else if (string.Equals(Name, PortTypeGrammar.MapKeyword, StringComparison.OrdinalIgnoreCase))
            Refuse("Map is the library's built-in constructor");
        else if (Reserved(Name) is { } reserved)
            Refuse(reserved);

        if (ClrType is null)
            Refuse("it names no CLR type");
        else if (WorkflowTypeRegistrationRecordRules.HasUniversalClrType(ClrType))
            Refuse($"CLR type '{ClrType.Name}' admits every value");

        if (Literal?.Refusal() is { } literalRefusal)
            Refuse(literalRefusal);
        else if (Literal is not null && JsonTypeInfo is null)
            Refuse("a literal form needs a JsonTypeInfo to read its literals");

        if (WorkflowTypeRegistrationRecordRules.JsonTypeInfoRefusal(JsonTypeInfo, ClrType) is { } mismatch)
            Refuse(mismatch);

        foreach (var alias in Aliases ?? [])
        {
            if (alias is not null && string.Equals(alias, PortTypeGrammar.MapKeyword, StringComparison.OrdinalIgnoreCase))
                Refuse($"alias '{alias}': Map is the library's built-in constructor");
            else if (alias is null || !PortTypeGrammar.IsAtom(alias))
                Refuse($"alias '{alias}' is not a plain name");
            else if (Reserved(alias) is { } reserved)
                Refuse($"alias '{alias}': {reserved}");
            else if (string.Equals(alias, Name, StringComparison.OrdinalIgnoreCase))
                Refuse($"alias '{alias}' is the name itself");
        }

        foreach (var row in WidensTo ?? [])
        {
            if (!PortTerm.TryParse(row, PortTermGrammar.Document, out var term, out var error))
                Refuse($"widening row '{row}' is not a closed term: {error.Message}");
            else if (term.IsNullable)
                Refuse($"widening row '{row}' is nullable; a row's target is never nullable");
            else if (ReservedPortTypeNames.In(term) is { } reserved)
                Refuse($"widening row '{row}' names the reserved name '{reserved}'");
            else if (term.IsCollection)
                Refuse($"widening row '{row}' leads into a collection; a collection widens through its element, never by a row");
            else if (term.IsMap)
                Refuse($"widening row '{row}' leads into a map; a map widens through its value, never by a row");
            else if (HoldsJson(term))
                Refuse($"widening row '{row}' holds Json, which a value reaches by being serializable, never by a row");
        }

        return refusals;
    }

    internal static string? Reserved(string name)
    {
        if (string.Equals(name, "Any", StringComparison.OrdinalIgnoreCase)) return "Any is a reserved name";
        if (string.Equals(name, "Float", StringComparison.OrdinalIgnoreCase)) return "Float is a reserved name";
        if (string.Equals(name, "Expression", StringComparison.OrdinalIgnoreCase))
            return "Expression is the library's and no host may register it";
        return null;
    }

    internal IReadOnlyList<PortTerm> ReadWidensTo() =>
        [.. WidensTo.Select(row => PortTerm.Parse(row, PortTermGrammar.Document))];

    /// <summary>Whether this term contains Json at any constructor depth.</summary>
    public static bool HoldsJson(PortTerm term)
    {
        for (var node = term; node is not null; node = node.LastArgument)
            if (node.Kind == PortTermKind.Atom && string.Equals(node.Name, "Json", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

internal static class WorkflowTypeRegistrationRecordRules
{
    internal static bool HasUniversalClrType(Type? type) => type == typeof(object) || type == typeof(ValueType);

    internal static string? JsonTypeInfoRefusal(JsonTypeInfo? info, Type? clrType)
    {
        if (info is null || clrType is null)
            return null;
        if (info.Type != clrType)
            return $"its JsonTypeInfo describes {info.Type.Name}, not {clrType.Name}";
        return info.Kind != JsonTypeInfoKind.None && info.Options.AllowDuplicateProperties
            ? $"its JsonTypeInfo for {info.Type.Name} comes from options that let a repeated member name, or one that differs only in case, resolve to the last; "
                + "build it from JsonSerializerOptions with AllowDuplicateProperties = false"
            : null;
    }
}
