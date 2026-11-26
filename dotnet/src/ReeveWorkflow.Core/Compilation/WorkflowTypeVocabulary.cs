using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>Whether a registered constructor keeps or reverses the order of its argument's assignability.</summary>
public enum WorkflowTypeVariance
{
    /// <summary>Assignable when the arguments are: <c>Collection</c> and <c>Map</c>, built in. A map's key is fixed, so only its
    /// value varies.</summary>
    Covariant,

    /// <summary>Assignable only when the terms are the same: every host constructor.</summary>
    Invariant
}

/// <summary>One atom of a vocabulary, as <c>GET port-types</c> publishes it.</summary>
/// <param name="Name">The canonical spelling.</param>
/// <param name="Owner"><c>library</c>, <c>host</c> or <c>plugin:&lt;id&gt;</c>.</param>
/// <param name="ClrType">The CLR type a value of it has; null for a name a widening table declared without one.</param>
/// <param name="Aliases">The spellings that read as it, sorted.</param>
/// <param name="WidensTo">The terms it feeds directly, sorted, in canonical spelling.</param>
/// <param name="Implements">The capabilities it implements, in <see cref="PortCapabilities.All"/> order: a library
/// atom's fixed set, a registered atom's verified claims.</param>
/// <param name="Literal">How a literal of it is graded: the library's fixed form for a library atom, the record's for a
/// registered one; null when the record declares none, and every literal of it is refused.</param>
public sealed record RegisteredPortType(
    string Name, string Owner, Type? ClrType, IReadOnlyList<string> Aliases, IReadOnlyList<string> WidensTo,
    IReadOnlyList<WorkflowCapability> Implements, WorkflowLiteralForm? Literal = null);

/// <summary>One constructor of a vocabulary, as <c>GET port-types</c> publishes it.</summary>
/// <param name="Name">The constructor's name.</param>
/// <param name="Owner"><c>library</c>, <c>host</c> or <c>plugin:&lt;id&gt;</c>.</param>
/// <param name="Variance">Covariant for the built-in <c>Collection</c> and <c>Map</c>, invariant for every other.</param>
/// <param name="Erasure">The one CLR type every term over it has; null for <c>Collection</c> and <c>Map</c>, which compute it from their arguments.</param>
/// <param name="AllowedArguments">The closed terms it admits; null for <c>Collection</c> and <c>Map</c>, which admit any.</param>
/// <param name="Implements">The capabilities every term over it implements, whatever the argument: a registered
/// constructor's verified claims, for <c>Collection</c> only <c>Iterable</c>, and for <c>Map</c> none (both are Equatable and
/// JsonSerializable when their argument or value is, which is the term's answer, not the constructor's).</param>
/// <param name="Literal">How a literal of any term over it is graded; null when none is declared, and always for
/// <c>Collection</c> (an array of literals of its argument) and <c>Map</c> (an object of literals of its value).</param>
/// <param name="Arity">How many arguments a term over it takes: 1, and 2 for <c>Map</c>.</param>
/// <param name="Key">The fixed first argument of a constructor whose key is not a choice, <c>String</c> for <c>Map</c>; null for every other.</param>
public sealed record RegisteredPortConstructor(
    string Name, string Owner, WorkflowTypeVariance Variance, Type? Erasure, IReadOnlyList<string>? AllowedArguments,
    IReadOnlyList<WorkflowCapability> Implements, WorkflowLiteralForm? Literal = null, int Arity = 1, string? Key = null);

/// <summary>One immutable state of the port type vocabulary: the atoms and constructors registered at one
/// <see cref="Generation"/>, and what is decided from them — whether a term is registered, whether one is
/// assignable to another (rules A1 to A7 of docs/reference/port-types.md, Widening and assignability), the CLR type of a closed term, and the least upper bound of evidence.</summary>
/// <remarks>Read it from <see cref="IWorkflowTypeRegistry.Current"/>. A state never changes; a registration makes a
/// new one, so a compile that holds one reads a single vocabulary from its first term to its last.</remarks>
public sealed class WorkflowTypeVocabulary : IPortTermVocabulary, ICapabilityClaims
{
    internal const string Library = "library";

    private static readonly Dictionary<string, IReadOnlySet<WorkflowCapability>> LibraryClaims = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Boolean"] = Claimed(WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable),
        ["Int"] = Claimed(WorkflowCapability.Numeric, WorkflowCapability.Comparable, WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable),
        ["Number"] = Claimed(WorkflowCapability.Numeric, WorkflowCapability.Comparable, WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable),
        ["String"] = Claimed(WorkflowCapability.Comparable, WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable),
        ["DateTime"] = Claimed(WorkflowCapability.Comparable, WorkflowCapability.Equatable, WorkflowCapability.JsonSerializable),
        ["Json"] = Claimed(WorkflowCapability.JsonSerializable),
    };

    private static readonly (string Name, Type Clr)[] LibraryAtoms =
    [
        ("String", typeof(string)), ("Number", typeof(double)), ("Int", typeof(long)),
        ("Boolean", typeof(bool)), ("DateTime", typeof(DateTimeOffset)), ("Json", typeof(JsonElement)),
    ];

    private readonly Dictionary<string, string> owners;
    private readonly Dictionary<string, Type> atomTypes;
    private readonly Dictionary<string, ImmutableList<PortTerm>> termRows;
    private readonly Dictionary<string, IReadOnlySet<WorkflowCapability>> claims;
    private readonly Dictionary<string, WorkflowLiteralForm> literals;
    private readonly IReadOnlyList<(string Name, Type Type)> registeredClr;
    private IReadOnlyList<RegisteredPortType>? types;
    private IReadOnlyList<RegisteredPortConstructor>? constructors;

    internal WorkflowTypeVocabulary(
        long generation, PortTypeWideningTable table, Dictionary<string, string> owners, Dictionary<string, Type> atomTypes,
        Dictionary<string, ImmutableList<PortTerm>> termRows, IReadOnlyList<(string Name, Type Type)> registeredClr,
        Dictionary<string, IReadOnlySet<WorkflowCapability>> claims, Dictionary<string, WorkflowLiteralForm> literals)
    {
        Generation = generation;
        Table = table;
        this.owners = owners;
        this.atomTypes = atomTypes;
        this.termRows = termRows;
        this.registeredClr = registeredClr;
        this.claims = claims;
        this.literals = literals;
    }

    internal static IReadOnlySet<WorkflowCapability> Claimed(params WorkflowCapability[] capabilities) =>
        new HashSet<WorkflowCapability>(capabilities);

    internal static Dictionary<string, IReadOnlySet<WorkflowCapability>> NewClaims() =>
        new(LibraryClaims, StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, WorkflowLiteralForm> NewLiterals() =>
        new(LibraryAtoms.ToDictionary(a => a.Name, a => PortLiteralCoercion.LibraryFormOf(a.Name)!, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The library's own vocabulary, generation 0: the six built-in atoms with their CLR types and the one
    /// row <c>Int → Number</c>, and <c>Collection</c>.</summary>
    public static WorkflowTypeVocabulary Builtin { get; } = OverTable(PortTypeWideningTable.Builtin);

    /// <summary>The generation of the registry state this is: 0 at startup, one more for each accepted plugin
    /// registration and unregistration.</summary>
    public long Generation { get; }

    /// <summary>The names, aliases, plain rows and constructors of this state as a table, for the readers that
    /// still work on a name's text.</summary>
    public PortTypeWideningTable Table { get; }

    internal static WorkflowTypeVocabulary OverTable(PortTypeWideningTable table)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, clr) in LibraryAtoms)
        {
            owners[name] = Library;
            types[name] = clr;
        }

        return new WorkflowTypeVocabulary(
            0, table, owners, types, new Dictionary<string, ImmutableList<PortTerm>>(StringComparer.OrdinalIgnoreCase), [], NewClaims(), NewLiterals());
    }

    internal static IReadOnlyList<(string Name, Type Clr)> LibraryTypes => LibraryAtoms;

    internal Dictionary<string, string> Owners => owners;

    internal Dictionary<string, Type> AtomTypes => atomTypes;

    internal Dictionary<string, ImmutableList<PortTerm>> TermRows => termRows;

    /// <summary>Every atom, canonical spelling, in the order <c>GET port-types</c> lists them.</summary>
    public IReadOnlyList<RegisteredPortType> Types => types ??= [.. Table.KnownTypes.Select(Describe)];

    /// <summary>Every constructor, the built-in <c>Collection</c> and <c>Map</c> among the host's by name, in the order <c>GET port-types</c> lists them.</summary>
    public IReadOnlyList<RegisteredPortConstructor> Constructors => constructors ??=
    [
        .. Table.Constructors
            .Select(c => new RegisteredPortConstructor(
                c.Name, OwnerOf(c.Name), WorkflowTypeVariance.Invariant, c.Erasure, [.. c.AllowedArguments], ClaimsOf(c.Name),
                literals.GetValueOrDefault(c.Name)))
            .Append(new RegisteredPortConstructor(
                PortTypeGrammar.Keyword, Library, WorkflowTypeVariance.Covariant, null, null, [WorkflowCapability.Iterable]))
            .Append(new RegisteredPortConstructor(
                PortTypeGrammar.MapKeyword, Library, WorkflowTypeVariance.Covariant, null, null, [], Arity: 2, Key: "String"))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase),
    ];

    private RegisteredPortType Describe(PortValueType type)
    {
        var rows = Table.WideningsOf(type).Select(w => w.Name).ToList();
        if (termRows.TryGetValue(type.Name, out var targets))
            rows.AddRange(targets.Select(t => Table.Canonicalize(t).ToString()));
        rows.Sort(StringComparer.OrdinalIgnoreCase);
        return new RegisteredPortType(
            type.Name, OwnerOf(type.Name), atomTypes.GetValueOrDefault(type.Name),
            [.. Table.AliasesOf(type).Select(a => a.Name)], rows, ClaimsOf(type.Name), literals.GetValueOrDefault(type.Name));
    }

    /// <summary>The form that grades a literal of the atom or constructor named <paramref name="head"/>, or null when it
    /// declares none or is not registered. Aliases are not resolved: ask with the canonical name.</summary>
    public WorkflowLiteralForm? LiteralFormOf(string head)
    {
        ArgumentNullException.ThrowIfNull(head);
        return literals.GetValueOrDefault(head);
    }

    /// <summary>Whether <paramref name="value"/> is a literal of <paramref name="type"/> in this vocabulary: the one
    /// decision <see cref="PortLiteralCoercion.IsLiteralOf"/> makes over the forms its atoms and constructors declare, and then the
    /// record's own read of the literal.
    /// A type that names a type parameter answers true: the binding pass grades it once the parameter is bound.</summary>
    /// <param name="value">The literal as the stored-document reader makes it; a JSON null and an absent value are the same.</param>
    /// <param name="type">The declared type; aliases and case variants are read as the names they spell.</param>
    public bool IsLiteralOf(object? value, PortValueType type)
    {
        if (type.IsDefault || !PortTerm.TryParse(type.Name, PortTermGrammar.Declaration, out var term, out _))
            return false;
        if (term.ContainsParameter)
            return true;
        return PortLiteralCoercion.IsLiteralOf(value, PortValueType.Of(Table.Canonicalize(term).ToString()), LiteralFormOf, ReadsLiteral);
    }

    private bool ReadsLiteral(string head, JsonElement literal) =>
        !JsonTypeInfos.TryGetValue(head, out var info) || ClosedPortType.TryReadWith(literal, info, info.Type, out _);

    private IReadOnlyList<WorkflowCapability> ClaimsOf(string name) =>
        claims.TryGetValue(name, out var set) ? [.. set.Order()] : [];

    private string OwnerOf(string name) => owners.GetValueOrDefault(name, "host");

    internal bool HoldsHostName(PortTerm term)
    {
        for (var node = Table.Canonicalize(term); node is not null; node = node.LastArgument)
        {
            if (node.Kind is PortTermKind.Atom or PortTermKind.Constructor && !node.IsCollection && !node.IsMap && OwnerOf(node.Name) != Library)
                return true;
        }

        return false;
    }

    internal IReadOnlyDictionary<string, System.Text.Json.Serialization.Metadata.JsonTypeInfo> JsonTypeInfos { get; init; } =
        new Dictionary<string, System.Text.Json.Serialization.Metadata.JsonTypeInfo>(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<PortTerm, ClosedPortType?> closedTypes = new();

    internal ClosedPortType? ClosedTypeOf(PortTerm term)
    {
        var canonical = Table.Canonicalize(term);
        return closedTypes.TryGetValue(canonical, out var known) ? known : closedTypes.GetOrAdd(canonical, BuildClosedType(canonical));
    }

    private ClosedPortType? BuildClosedType(PortTerm term)
    {
        if (term.Kind == PortTermKind.Null || term.ContainsParameter)
            return null;
        var chain = new List<PortTerm>();
        for (var node = term; node is not null; node = node.LastArgument)
            chain.Add(node);

        ClosedPortType? built = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            var core = node.IsNullable ? node.WithoutNullable() : node;
            Type? clr;
            System.Text.Json.Serialization.Metadata.JsonTypeInfo? info = null;
            if (node.IsCollection)
            {
                clr = built is null ? null : typeof(IReadOnlyList<>).MakeGenericType(built.ClrType);
            }
            else if (node.IsMap)
            {
                clr = built is null ? null : typeof(IReadOnlyDictionary<,>).MakeGenericType(typeof(string), built.ClrType);
            }
            else
            {
                clr = node.Kind == PortTermKind.Atom ? atomTypes.GetValueOrDefault(node.Name)
                    : Table.TryGetConstructor(node.Name, out var head) ? head.Erasure : null;
                if (clr is not null && Implements(core, WorkflowCapability.JsonSerializable))
                {
                    var lifted = node.IsNullable && clr.IsValueType && Nullable.GetUnderlyingType(clr) is null ? typeof(Nullable<>).MakeGenericType(clr) : clr;
                    info = owners.GetValueOrDefault(core.Name) == Library ? WorkflowDateTimeText.Options.GetTypeInfo(lifted)
                        : JsonTypeInfos.GetValueOrDefault(core.Name);
                }
            }

            if (clr is null)
                return null;
            var type = node.IsNullable && clr.IsValueType && Nullable.GetUnderlyingType(clr) is null
                ? typeof(Nullable<>).MakeGenericType(clr)
                : clr;
            built = node.IsMap
                ? new ClosedPortType(node, type, null, null, built, ClosedTypeOf(PortTerm.Atom("String")))
                : new ClosedPortType(node, type, node.IsCollection ? built : null, info);
        }

        return built;
    }

    internal PortConversion? ConversionOf(PortTerm from, PortTerm to)
    {
        return PortConversion.Between(Table.Canonicalize(from), Table.Canonicalize(to),
            core => !core.IsCollection && !core.IsMap && ClrTypeOf(core)?.IsValueType == true, ClosedTypeOf);
    }

    /// <summary>The CLR type a value of the closed <paramref name="term"/> has: <c>long</c> for <c>Int</c>, an
    /// <c>IReadOnlyList</c> of the argument's type for <c>Collection</c>, an <c>IReadOnlyDictionary</c> from <c>string</c> to
    /// the value's type for <c>Map</c>, a host atom's registered type, a host constructor's erasure; <c>Nullable&lt;T&gt;</c> for a nullable value type, else the type itself, which admits null.
    /// Null when the term names something this vocabulary has no CLR type for, or holds a type parameter or is the
    /// null literal's term.</summary>
    /// <param name="term">A closed term; aliases and case variants are read as the names they spell.</param>
    public Type? ClrTypeOf(PortTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (term.Kind == PortTermKind.Null || term.ContainsParameter)
            return null;
        var canonical = Table.Canonicalize(term);
        var chain = new List<PortTerm>();
        for (var node = canonical; node is not null; node = node.LastArgument)
            chain.Add(node);

        Type? built = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            Type? clr;
            if (node.Kind == PortTermKind.Atom)
                clr = atomTypes.GetValueOrDefault(node.Name);
            else if (node.IsCollection)
                clr = built is null ? null : typeof(IReadOnlyList<>).MakeGenericType(built);
            else if (node.IsMap)
                clr = built is null ? null : typeof(IReadOnlyDictionary<,>).MakeGenericType(typeof(string), built);
            else
                clr = Table.TryGetConstructor(node.Name, out var head) ? head.Erasure : null;
            if (clr is null)
                return null;
            built = node.IsNullable && clr.IsValueType && Nullable.GetUnderlyingType(clr) is null
                ? typeof(Nullable<>).MakeGenericType(clr)
                : clr;
        }

        return built;
    }

    /// <summary>Why <paramref name="term"/> is not made of registered types, or null when it is; see
    /// <see cref="PortTypeWideningTable.RefusalOf"/>.</summary>
    public PortTermRefusal? RefusalOf(PortTerm term) => Table.RefusalOf(term);

    /// <summary>Whether a value of <paramref name="from"/> may feed a port of <paramref name="to"/>: rules A1 to A7 of docs/reference/port-types.md, Widening and assignability, over two closed terms. <see cref="PortTerm.Null"/> is the literal <c>null</c>.</summary>
    /// <exception cref="ArgumentException">A term holds a type parameter, or <paramref name="to"/> is the null literal's term.</exception>
    public bool IsAssignable(PortTerm from, PortTerm to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return PortTermAssignability.IsAssignable(Table.Canonicalize(from), Table.Canonicalize(to), this);
    }

    /// <summary>The wire check: whether an output declared <paramref name="from"/> may feed an input declared
    /// <paramref name="to"/>, decided by <see cref="IsAssignable(PortTerm, PortTerm)"/>.</summary>
    public bool IsAssignable(PortValueType from, PortValueType to) =>
        IsAssignable(PortTerm.Parse(from.Name, PortTermGrammar.Document), PortTerm.Parse(to.Name, PortTermGrammar.Document));

    /// <summary>The least upper bound of <paramref name="evidence"/> (rules L1 to L4 of docs/reference/port-types.md, Least upper bound):
    /// the one term every element is assignable to that is assignable to every other such term, nullable when an
    /// element is, or null when it is undefined — no evidence, only <c>null</c>, or no such term. Over registered
    /// rows, so <c>ReeferShipment</c> and <c>TankerShipment</c> join as <c>Shipment</c> when both feed it; a mixed
    /// join never invents <c>Json</c>.</summary>
    /// <param name="evidence">Closed terms; <see cref="PortTerm.Null"/> is the literal <c>null</c>.</param>
    public PortTerm? LeastUpperBound(IEnumerable<PortTerm> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return PortTermLub.Of(evidence.Select(Table.Canonicalize), this, RowTargets);
    }

    internal PortTerm? LeastUpperBound(IEnumerable<PortTerm> evidence, BindingMutant mutant) =>
        PortTermLub.Of(evidence.Select(Table.Canonicalize), this, RowTargets, mutant);

    private IEnumerable<PortTerm> RowTargets(PortTerm from)
    {
        if (from.Kind != PortTermKind.Atom)
            return [];
        var plain = Table.WideningsOf(PortValueType.Of(from.Name)).Select(w => PortTerm.Parse(w.Name, PortTermGrammar.Document));
        return termRows.TryGetValue(from.Name, out var rows) ? plain.Concat(rows) : plain;
    }

    bool IPortTermVocabulary.HasRow(PortTerm from, PortTerm to)
    {
        if (from.Kind != PortTermKind.Atom)
            return false;
        if (to.Kind == PortTermKind.Atom)
            return Table.HasPlainRow(from.Name, to.Name);
        return termRows.TryGetValue(from.Name, out var rows) && rows.Contains(to);
    }

    bool IPortTermVocabulary.IsJsonSerializable(PortTerm head) =>
        ((ICapabilityClaims)this).Claims(head, WorkflowCapability.JsonSerializable);

    bool ICapabilityClaims.Claims(PortTerm head, WorkflowCapability capability) =>
        head.Kind is PortTermKind.Atom or PortTermKind.Constructor && !head.IsCollection && !head.IsMap
        && claims.TryGetValue(head.Name, out var set) && set.Contains(capability);

    /// <summary>Whether the closed <paramref name="term"/> implements <paramref name="capability"/>: a registered head
    /// implements what it claims; <c>Collection&lt;T&gt;</c> is Iterable, and Equatable or JsonSerializable when <c>T</c> is;
    /// <c>Map&lt;String,V&gt;</c> is Equatable or JsonSerializable when <c>V</c> is, and never Iterable;
    /// <c>T?</c> is JsonSerializable when <c>T</c> is and nothing else.</summary>
    /// <param name="term">A closed term; aliases and case variants are read as the names they spell.</param>
    /// <exception cref="ArgumentException">The term holds a type parameter, or names something this vocabulary does not
    /// register (see <see cref="RefusalOf"/>): membership is not answered for it, so a caller grades registration with <see cref="RefusalOf"/> first.</exception>
    public bool Implements(PortTerm term, WorkflowCapability capability)
    {
        ArgumentNullException.ThrowIfNull(term);
        return PortTermCapabilities.Implements(Registered(term), capability, this);
    }

    /// <summary>Every capability the closed <paramref name="term"/> implements, in <see cref="PortCapabilities.All"/> order.</summary>
    /// <exception cref="ArgumentException">The term holds a type parameter or names something not registered, as for <see cref="Implements"/>.</exception>
    public IReadOnlyList<WorkflowCapability> CapabilitiesOf(PortTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        var canonical = Registered(term);
        return [.. PortCapabilities.All.Where(c => PortTermCapabilities.Implements(canonical, c, this))];
    }

    /// <summary>The <c>Item</c> of <c>Iterable&lt;Item&gt;</c> for the closed <paramref name="term"/>, in canonical spelling:
    /// the argument of a non-nullable <c>Collection</c>; null when the term is not Iterable.</summary>
    /// <exception cref="ArgumentException">The term holds a type parameter or names something not registered, as for <see cref="Implements"/>.</exception>
    public PortTerm? IterableItemOf(PortTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return PortTermCapabilities.IterableItem(Registered(term));
    }

    private PortTerm Registered(PortTerm term)
    {
        var canonical = Table.Canonicalize(term);
        if (Table.RefusalOf(canonical) is { } refusal)
            throw new ArgumentException($"{refusal.Code}: {refusal.Message}; capability membership is answered only for registered terms.", nameof(term));
        return canonical;
    }

    internal string Fingerprint()
    {
        var builder = new StringBuilder(Table.Fingerprint());
        var registeredClaims = claims.Where(c => !LibraryClaims.ContainsKey(c.Key) && c.Value.Count > 0).ToList();
        var registeredForms = literals.Where(l => !LibraryClaims.ContainsKey(l.Key)).ToList();
        if (registeredClr.Count == 0 && termRows.Count == 0 && registeredClaims.Count == 0 && registeredForms.Count == 0)
            return builder.ToString();

        builder.Count(registeredClr.Count);
        foreach (var (name, type) in registeredClr.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            builder.Text(name).Text(PortTypeWideningTable.StableName(type));

        builder.Count(termRows.Count);
        foreach (var (name, rows) in termRows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Text(name);
            var texts = rows.Select(r => r.ToString()).Order(StringComparer.Ordinal).ToList();
            builder.Count(texts.Count);
            foreach (var text in texts)
                builder.Text(text);
        }

        if (registeredClaims.Count > 0)
        {
            builder.Count(registeredClaims.Count);
            foreach (var (name, set) in registeredClaims.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Text(name).Count(set.Count);
                foreach (var capability in set.Order())
                    builder.Text(PortCapabilities.NameOf(capability));
            }
        }

        if (registeredForms.Count > 0)
        {
            builder.Count(registeredForms.Count);
            foreach (var (name, form) in registeredForms.OrderBy(l => l.Key, StringComparer.OrdinalIgnoreCase))
                builder.Text(name).Text(form.GradedAs.ToString()).Text(form.Format?.ToString());
        }

        return builder.ToString();
    }
}
