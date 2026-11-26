using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;

namespace ReeveWorkflow.Abstractions;

/// <summary>The codes a port type registration is refused with.</summary>
public static class WorkflowRegistrationCodes
{
    /// <summary>A record breaks a rule of its own: a name no one may take, an allowed argument that is not a
    /// closed term, a missing erasure type, a literal form without a JsonTypeInfo, a JsonTypeInfo that describes another
    /// type than the record's CLR type, or a reference to a type owned by neither the library, the host nor the record's
    /// own owner.</summary>
    public const string PortTypeRegistrationInvalid = "port-type-registration-invalid";

    /// <summary>A record names a type or a constructor the vocabulary already holds.</summary>
    public const string PortTypeNameClash = "port-type-name-clash";

    /// <summary>A record claims a capability it may not claim, or one its CLR type does not bear out.</summary>
    public const string CapabilityClaimInvalid = "capability-claim-invalid";
}

/// <summary>One reason a registration record is refused: the code, the record's name and the message.</summary>
public sealed record WorkflowRegistrationRefusal(string Code, string Record, string Message);

/// <summary>What a host registers to teach the vocabulary a type: one record per atom and one per constructor.
/// Only the library derives from it.</summary>
public abstract record WorkflowTypeRegistrationRecord
{
    private protected WorkflowTypeRegistrationRecord()
    {
    }
}

/// <summary>A host's one-argument type constructor, <c>K&lt;T&gt;</c>: its name, the closed terms it admits as
/// its argument, and the one CLR type every <c>K&lt;T&gt;</c> erases to. A constructor is invariant, so two
/// terms with one host head are assignable only when they are the same term. <c>Collection</c> is built in and
/// is no record.</summary>
/// <remarks>Register it with <c>workflows.Seams.PortTypes.Add</c> at startup, or, from a plugin, through
/// <c>IWorkflowTypeRegistry.Register</c>.</remarks>
/// <param name="Name">An identifier that does not begin with <c>collection</c> and is not <c>Any</c>,
/// <c>Float</c> or <c>Expression</c>; it may not name a registered atom, alias or constructor.</param>
/// <param name="Erasure">The CLR type a value of any <c>Name&lt;T&gt;</c> has, independent of <c>T</c>; <c>object</c> and <see cref="ValueType"/> are refused.</param>
public sealed record WorkflowTypeConstructorRecord(
    string Name,
    [property: DynamicallyAccessedMembers(WorkflowCapabilityClaims.ClaimedMembers)]
    [param: DynamicallyAccessedMembers(WorkflowCapabilityClaims.ClaimedMembers)]
    Type Erasure) : WorkflowTypeRegistrationRecord
{
    /// <summary>The arguments the constructor admits, each a closed document term, nullable or not
    /// (<c>Boolean</c>, <c>Int?</c>, <c>Collection&lt;Int&gt;</c>). An argument is admitted when it is exactly one
    /// of these terms, so <c>Boolean</c> does not admit <c>Boolean?</c>. At least one is required, and none may
    /// hold <c>Json</c>, <c>Any</c> or <c>Float</c>. Once registered, each must name registered types, spelled canonically,
    /// owned by the library, the host or this record's own owner.</summary>
    public IReadOnlyList<string> AllowedArguments { get; init; } = [];

    /// <summary>How a value of this constructor is written as JSON; null when none is declared. A literal form and a
    /// <see cref="WorkflowCapability.JsonSerializable"/> claim each need one.</summary>
    public JsonTypeInfo? JsonTypeInfo { get; init; }

    /// <summary>How a literal of any <c>Name&lt;T&gt;</c> is graded, whatever <c>T</c> is; the rules are those of
    /// <see cref="WorkflowTypeRecord.Literal"/>.</summary>
    public WorkflowLiteralForm? Literal { get; init; }

    /// <summary>The capabilities every <c>Name&lt;T&gt;</c> claims, whatever <c>T</c> is; the rules and the checks are those of
    /// <see cref="WorkflowTypeRecord.Claims"/>, made against <see cref="Erasure"/>.</summary>
    public IReadOnlySet<WorkflowCapability> Claims { get; init; } = new HashSet<WorkflowCapability>();

    /// <summary>Every rule this record breaks on its own, in the order a host reads them; empty when the record
    /// is acceptable. Whether its name is free in a vocabulary is that vocabulary's question.</summary>
    public IReadOnlyList<WorkflowRegistrationRefusal> Validate() => [.. ValidateRules(), .. ValidateClaims()];

    internal IReadOnlyList<WorkflowRegistrationRefusal> ValidateClaims() =>
        [.. WorkflowCapabilityClaims.Verify("Port type constructor", Name, Erasure, Claims, JsonTypeInfo)];

    internal IReadOnlyList<WorkflowRegistrationRefusal> ValidateRules()
    {
        var refusals = new List<WorkflowRegistrationRefusal>();
        void Refuse(string why) => refusals.Add(new WorkflowRegistrationRefusal(
            WorkflowRegistrationCodes.PortTypeRegistrationInvalid, Name ?? "",
            $"Port type constructor '{Name}' cannot be registered: {why}{(why.EndsWith('.') ? "" : ".")}"));

        if (Name is null || !PortTypeGrammar.IsIdentifier(Name))
            Refuse("it is not a name");
        else if (Name == PortTypeGrammar.Keyword)
            Refuse("Collection is the library's built-in constructor");
        else if (Name.StartsWith(PortTypeGrammar.Keyword, StringComparison.OrdinalIgnoreCase))
            Refuse("names starting with 'collection' are reserved");
        else if (string.Equals(Name, PortTypeGrammar.MapKeyword, StringComparison.OrdinalIgnoreCase))
            Refuse("Map is the library's built-in constructor");
        else if (string.Equals(Name, "Any", StringComparison.OrdinalIgnoreCase))
            Refuse("Any is a reserved name");
        else if (string.Equals(Name, "Float", StringComparison.OrdinalIgnoreCase))
            Refuse("Float is a reserved name");
        else if (string.Equals(Name, "Expression", StringComparison.OrdinalIgnoreCase))
            Refuse("Expression is the library's and no host may register it");

        if (Erasure is null)
            Refuse("it names no erasure type");
        else if (WorkflowTypeRegistrationRecordRules.HasUniversalClrType(Erasure))
            Refuse($"CLR erasure '{Erasure.Name}' admits every value");

        if (Literal?.Refusal() is { } literalRefusal)
            Refuse(literalRefusal);
        else if (Literal is not null && JsonTypeInfo is null)
            Refuse("a literal form needs a JsonTypeInfo to read its literals");

        if (WorkflowTypeRegistrationRecordRules.JsonTypeInfoRefusal(JsonTypeInfo, Erasure) is { } mismatch)
            Refuse(mismatch);

        if (AllowedArguments is null || AllowedArguments.Count == 0)
        {
            Refuse("it lists no allowed argument");
        }
        else
        {
            foreach (var argument in AllowedArguments)
            {
                if (!PortTerm.TryParse(argument, PortTermGrammar.Document, out var term, out var error))
                    Refuse($"allowed argument '{argument}' is not a closed term: {error.Message}");
                else if (HoldsJson(term))
                    Refuse($"allowed argument '{argument}' holds Json, which no constructor argument may");
                else if (ReservedPortTypeNames.In(term) is { } reserved)
                    Refuse($"allowed argument '{argument}' names the reserved name '{reserved}'");
            }
        }

        return refusals;
    }

    internal IReadOnlyList<PortTerm> ReadAllowedArguments() =>
        [.. AllowedArguments.Select(a => PortTerm.Parse(a, PortTermGrammar.Document))];

    private static bool HoldsJson(PortTerm term)
    {
        for (var node = term; node is not null; node = node.LastArgument)
            if (node.Kind == PortTermKind.Atom && string.Equals(node.Name, "Json", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
