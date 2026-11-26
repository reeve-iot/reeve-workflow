using System.Collections.Immutable;
using System.Text;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>Which port types may feed which. The library seeds the built-ins it owns, a host adds its own as records
/// registered in a <see cref="WorkflowTypeRegistry"/>, and what applies is the union — no host restates what the library knows.</summary>
/// <remarks>The built-ins here are the twin of widensTo and aliases in packages/core/src/portTypes/builtins.ts.
/// Works on <see cref="PortTypeTerm"/>, not text: a <see cref="PortValueType"/> that exists at all already
/// parsed under the one grammar, so nothing here re-validates a name — only <see cref="PortValueType.Of"/>
/// does. WideningConformanceTests compares the two tables row for row, and AssignabilityAgreementTests the two
/// predicates over the built-in spellings.</remarks>
public sealed class PortTypeWideningTable
{
    private readonly ImmutableDictionary<PortTypeTerm, ImmutableHashSet<PortTypeTerm>> widensTo;

    private readonly ImmutableDictionary<string, PortTypeTerm> aliases;

    private readonly ImmutableHashSet<PortTypeTerm> declared;

    private readonly ImmutableHashSet<string> spellings;

    private readonly ImmutableDictionary<string, RegisteredConstructor> constructors;

    private PortTypeWideningTable(
        ImmutableDictionary<PortTypeTerm, ImmutableHashSet<PortTypeTerm>> widensTo,
        ImmutableDictionary<string, PortTypeTerm> aliases,
        ImmutableHashSet<PortTypeTerm> declared,
        ImmutableHashSet<string> spellings,
        ImmutableDictionary<string, RegisteredConstructor> constructors)
    {
        this.widensTo = widensTo;
        this.aliases = aliases;
        this.declared = declared;
        this.spellings = spellings;
        this.constructors = constructors;
    }

    private sealed record RegisteredConstructor(WorkflowTypeConstructorRecord Record, IReadOnlyList<PortTerm> Arguments);

    private static ImmutableHashSet<string> Written(ImmutableHashSet<string> spellings, params PortTypeTerm[] terms) =>
        spellings.Union(terms.Select(t => t.Element));

    private static PortTypeTerm Atom(string name) => new(0, name);

    public static PortTypeWideningTable Builtin { get; } = new(
        ImmutableDictionary<PortTypeTerm, ImmutableHashSet<PortTypeTerm>>.Empty
            .Add(Atom("Int"), [Atom("Number")]),
        ImmutableDictionary.Create<string, PortTypeTerm>(StringComparer.OrdinalIgnoreCase)
            .Add("string", Atom("String"))
            .Add("number", Atom("Number"))
            .Add("int", Atom("Int"))
            .Add("boolean", Atom("Boolean"))
            .Add("bool", Atom("Boolean"))
            .Add("datetime", Atom("DateTime"))
            .Add("json", Atom("Json")),
        [
            Atom("String"), Atom("Number"), Atom("Int"), Atom("Boolean"), Atom("DateTime"), Atom("Json"),
        ],
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "String", "Number", "Int", "Boolean", "DateTime", "Json"),
        ImmutableDictionary.Create<string, RegisteredConstructor>(StringComparer.OrdinalIgnoreCase));

    internal bool TryGetAliasCanonical(string spelling, out string canonical)
    {
        if (aliases.TryGetValue(spelling, out var target))
        {
            canonical = target.Element;
            return true;
        }
        canonical = "";
        return false;
    }

    internal PortTypeWideningTable Declaring(PortValueType type)
    {
        RefuseDefault(type, nameof(type));
        var term = PortTypeTerm.From(type);
        if (term.Depth != 0)
            throw new ArgumentException(
                $"Port type '{type.Name}' cannot be declared: only a plain name can be declared, and a collection of " +
                "it is read through its element.", nameof(type));
        RefuseReservedName(type.Name, "Port type", nameof(type));
        RefuseConstructorName(term.Element, "Port type", nameof(type));
        return new PortTypeWideningTable(
            widensTo, aliases, DeclareOne(declared, aliases, term, nameof(type)), Written(spellings, term), constructors);
    }

    internal PortTypeWideningTable With(PortValueType from, IReadOnlyCollection<PortValueType> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        RefuseDefault(from, nameof(from));
        foreach (var target in targets)
            RefuseDefault(target, nameof(targets));

        var fromTerm = PortTypeTerm.From(from);
        if (fromTerm.Depth != 0)
            throw new ArgumentException(
                $"Port type '{from.Name}' cannot widen: {PlainWidening}", nameof(from));
        var targetTerms = targets.Select(PortTypeTerm.From).ToList();
        foreach (var (target, term) in targets.Zip(targetTerms))
            if (term.Depth != 0)
                throw new ArgumentException(
                    $"Port type '{from.Name}' cannot widen to '{target.Name}': {PlainWidening}", nameof(targets));
        RefuseReservedName(from.Name, "Port type", nameof(from));
        foreach (var target in targets)
            RefuseReservedName(target.Name, "Port type", nameof(targets));
        RefuseConstructorName(fromTerm.Element, "Port type", nameof(from));
        foreach (var term in targetTerms)
            RefuseConstructorName(term.Element, "Port type", nameof(targets));
        var existing = widensTo.TryGetValue(fromTerm, out var set) ? set : ImmutableHashSet<PortTypeTerm>.Empty;
        var declaredNow = targetTerms.Aggregate(
            DeclareOne(declared, aliases, fromTerm, nameof(from)),
            (acc, target) => DeclareOne(acc, aliases, target, nameof(targets)));
        return new PortTypeWideningTable(
            widensTo.SetItem(fromTerm, existing.Union(targetTerms)), aliases, declaredNow,
            Written(spellings, [fromTerm, .. targetTerms]), constructors);
    }

    internal PortTypeWideningTable WithAlias(PortValueType alias, PortValueType canonical)
    {
        RefuseDefault(alias, nameof(alias));
        RefuseDefault(canonical, nameof(canonical));

        var aliasTerm = PortTypeTerm.From(alias);
        if (aliasTerm.Depth != 0)
            throw new ArgumentException(
                $"Port type alias '{alias.Name}' cannot be registered: only a plain name can be an alias.", nameof(alias));
        RefuseReservedName(alias.Name, "Port type alias", nameof(alias));
        if (!aliasTerm.HasPlainElement)
            throw new ArgumentException(
                $"Port type alias '{alias.Name}' cannot be registered: only a plain name can be an alias.", nameof(alias));

        var target = Canon(aliases, PortTypeTerm.From(canonical));
        RefuseReservedName(canonical.Name, "Port type", nameof(canonical), alias.Name);

        if (aliases.TryGetValue(aliasTerm.Element, out var existingTarget) && !existingTarget.Equals(target))
            throw new ArgumentException(
                $"Port type alias '{alias.Name}' is claimed by both '{existingTarget.Render().Name}' and " +
                $"'{target.Render().Name}'.", nameof(alias));

        if (declared.Contains(aliasTerm) && !aliasTerm.Equals(target))
            throw new ArgumentException(
                $"Port type alias '{alias.Name}' is already declared as its own type, and cannot also alias " +
                $"'{target.Render().Name}'.", nameof(alias));

        RefuseConstructorName(aliasTerm.Element, "Port type alias", nameof(alias));
        RefuseConstructorName(target.Element, "Port type", nameof(canonical));
        var candidateAliases = aliases.SetItem(aliasTerm.Element, target);

        foreach (var existingDeclared in declared)
        {
            var recanonicalized = Canon(candidateAliases, existingDeclared);
            if (!recanonicalized.Equals(existingDeclared))
                throw new ArgumentException(
                    $"Port type alias '{alias.Name}' cannot be registered: the already-declared " +
                    $"'{existingDeclared.Render().Name}' would start canonicalising to " +
                    $"'{recanonicalized.Render().Name}'.", nameof(alias));
        }

        return new PortTypeWideningTable(
            widensTo, candidateAliases, DeclareOne(declared, candidateAliases, target, nameof(canonical)),
            Written(spellings, target), constructors);
    }

    internal PortTypeWideningTable WithConstructor(WorkflowTypeConstructorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var refusals = record.Validate();
        if (refusals.Count > 0)
            throw new ArgumentException(
                string.Join(" ", refusals.Select(r => $"{r.Code}: {r.Message}")), nameof(record));

        if (constructors.ContainsKey(record.Name))
            throw Clash("Port type constructor", record.Name, "a constructor of that name is already registered", nameof(record));
        if (IsRegistered(PortValueType.Of(record.Name)))
            throw Clash("Port type constructor", record.Name, "a port type of that name is already registered", nameof(record));

        var allowed = record.ReadAllowedArguments();
        foreach (var (text, argument) in record.AllowedArguments.Zip(allowed))
            if (NonCanonicalSpelling(argument) is { } spelling)
                throw new ArgumentException(
                    $"{WorkflowRegistrationCodes.PortTypeRegistrationInvalid}: Port type constructor '{record.Name}' cannot be " +
                    $"registered: allowed argument '{text}' is not canonical: {spelling}.", nameof(record));

        return new PortTypeWideningTable(
            widensTo, aliases, declared, spellings, constructors.Add(record.Name, new RegisteredConstructor(record, allowed)));
    }

    internal string? NonCanonicalSpelling(PortTerm term)
    {
        for (var node = term; node is not null; node = node.LastArgument)
        {
            var written = node.Kind switch
            {
                PortTermKind.Atom => Spelled(Canon(aliases, new PortTypeTerm(0, node.Name))).Element,
                PortTermKind.Constructor when !node.IsCollection && !node.IsMap => constructors.TryGetValue(node.Name, out var head) ? head.Record.Name : null,
                _ => null,
            };
            if (written is not null && !string.Equals(written, node.Name, StringComparison.Ordinal))
                return $"'{node.Name}' is spelled '{written}'";
        }

        return null;
    }

    /// <summary>The host constructors registered here, sorted by name.</summary>
    public IReadOnlyList<WorkflowTypeConstructorRecord> Constructors =>
        [.. constructors.Values.Select(c => c.Record).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The constructor registered under <paramref name="name"/>, matched ASCII case-insensitively.</summary>
    public bool TryGetConstructor(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkflowTypeConstructorRecord? record)
    {
        ArgumentNullException.ThrowIfNull(name);
        record = constructors.TryGetValue(name, out var found) ? found.Record : null;
        return record is not null;
    }

    /// <summary>Why <paramref name="term"/> is not made of registered types, or null when it is: an atom this table
    /// does not register, a constructor head no host registered, or an argument outside its head's allowed
    /// arguments. The code is <c>port-type-unregistered</c> for each; the first fault from the outside in wins.
    /// <c>Collection</c> and <c>Map</c> admit any value, and a bare type parameter argument (<c>Gauge&lt;$T&gt;</c>) is admitted
    /// here and judged when it is bound; a parameter nested in an argument is not admitted.</summary>
    /// <remarks>Iterative: the term is a chain of constructors, each continuing through its last argument, walked in a loop.</remarks>
    public PortTermRefusal? RefusalOf(PortTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        for (var node = term; node is not null; node = node.LastArgument)
        {
            switch (node.Kind)
            {
                case PortTermKind.Atom when !IsRegistered(PortValueType.Of(node.Name)):
                    return Unregistered($"'{node.Name}' is not a registered type");
                case PortTermKind.Constructor when !node.IsCollection && !node.IsMap:
                    if (!constructors.TryGetValue(node.Name, out var head))
                        return Unregistered($"'{node.Name}' is not a registered type constructor");
                    var argument = node.Arguments[0];
                    if (argument.Kind != PortTermKind.Parameter && !head.Arguments.Any(allowed => allowed.Equals(argument)))
                        return Unregistered($"'{node.Name}' does not admit the argument '{argument}'");
                    break;
            }
        }

        return null;
    }

    private static PortTermRefusal Unregistered(string message) => new(CompilationRules.PortTypeUnregistered, message);

    private static void RefuseReservedName(string written, string subject, string paramName, string? alias = null)
    {
        var reserved = ReservedPortTypeNames.In(written);
        if (reserved is not null)
            throw new ArgumentException(
                alias is null
                    ? $"{CompilationRules.PortTypeAlias}: {subject} '{written}' cannot be registered: {reserved} is a reserved name."
                    : $"{CompilationRules.PortTypeAlias}: Port type alias '{alias}' cannot resolve to '{written}': {reserved} is a reserved name.",
                paramName);
    }

    private void RefuseConstructorName(string? atom, string subject, string paramName)
    {
        if (atom is not null && constructors.ContainsKey(atom))
            throw Clash(subject, atom, "a constructor of that name is already registered", paramName);
    }

    private static ArgumentException Clash(string subject, string name, string why, string paramName) =>
        new($"{WorkflowRegistrationCodes.PortTypeNameClash}: {subject} '{name}' cannot be registered: {why}.", paramName);

    private const string PlainWidening =
        "a widening is stated between plain names, and a collection widens through its element.";

    private static void RefuseDefault(PortValueType type, string paramName)
    {
        if (type.IsDefault)
            throw new ArgumentException("A port type cannot be default(PortValueType).", paramName);
    }

    private static ImmutableHashSet<PortTypeTerm> DeclareOne(
        ImmutableHashSet<PortTypeTerm> declared,
        ImmutableDictionary<string, PortTypeTerm> aliasesForCanonicalization,
        PortTypeTerm term,
        string paramName)
    {
        if (declared.Contains(term))
            return declared;

        if (!term.HasPlainElement)
            throw new ArgumentException(
                $"Port type '{term.Render().Name}' cannot be declared: only a plain name can be declared, not a " +
                "nullable term or a constructor.", paramName);

        var canonical = Canon(aliasesForCanonicalization, term);
        if (!canonical.Equals(term))
            throw new ArgumentException(
                $"Port type '{term.Render().Name}' cannot be declared: it canonicalises to " +
                $"'{canonical.Render().Name}', a different name, so it cannot also be declared as a type of " +
                "its own.", paramName);

        foreach (var existing in declared)
            if (string.Equals(existing.Element, term.Element, StringComparison.OrdinalIgnoreCase)
                    && existing.Depth != term.Depth)
                    throw CollisionError(term, existing, paramName);

        return declared.Add(term);
    }

    private static ArgumentException CollisionError(PortTypeTerm beingDeclared, PortTypeTerm conflictsWith, string paramName) =>
        new($"Port type '{beingDeclared.Render().Name}' cannot be declared: '{conflictsWith.Render().Name}' is " +
            $"already declared, the same atom at another depth. Declare an atom at one depth only; a collection " +
            "of it is read through its element, and so is a widening stated on the element.", paramName);

    /// <summary>What <paramref name="type"/> reads as once every alias this table knows is resolved —
    /// inside a collection's element too, and a case variant of a declared name spelled as declared (a name
    /// written two ways takes an alias target's spelling, else the ordinally smallest). A name
    /// nobody declared or aliased, including a well-formed one a host never registered, comes back unchanged: this never mints a name of its own and never throws, for any
    /// non-default <see cref="PortValueType"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is <c>default(PortValueType)</c>.</exception>
    /// <remarks>One alias lookup on the term's element (composing depth if the alias target is itself
    /// collection-shaped — a target is always already canonical, by the invariant <see cref="WithAlias"/>
    /// maintains), then one render, which writes and parses the name once. Ordinally idempotent: calling this
    /// again on its own answer is always a no-op.</remarks>
    public PortValueType Canonicalize(PortValueType type) => Spelled(Canon(aliases, PortTypeTerm.From(type))).Render();

    private PortTypeTerm Spelled(PortTypeTerm term)
    {
        var map = spellingOf;
        if (map is null)
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var atom in spellings.OrderBy(a => a, StringComparer.Ordinal))
                map.TryAdd(atom, atom);
            var aliased = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var atom in aliases.Values.Select(t => t.Element)
                         .Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal))
                if (aliased.Add(atom))
                    map[atom] = atom;
            spellingOf = map;
        }

        return map.TryGetValue(term.Element, out var spelling)
            ? new PortTypeTerm(term.Depth, spelling)
            : term;
    }

    private Dictionary<string, string>? spellingOf;

    private static PortTypeTerm Canon(ImmutableDictionary<string, PortTypeTerm> aliases, PortTypeTerm term) =>
        aliases.TryGetValue(term.Element, out var target)
            ? new PortTypeTerm(term.Depth + target.Depth, target.Element)
            : term;

    internal bool HasPlainRow(string from, string to) =>
        widensTo.TryGetValue(Atom(from), out var targets) && targets.Contains(Atom(to));

    internal PortTerm Canonicalize(PortTerm term)
    {
        var chain = new List<PortTerm>();
        for (var node = term; node is not null; node = node.LastArgument)
            chain.Add(node);

        PortTerm? inner = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var node = chain[i];
            PortTerm rebuilt;
            switch (node.Kind)
            {
                case PortTermKind.Atom:
                    var spelled = Spelled(Canon(aliases, new PortTypeTerm(0, node.Name)));
                    rebuilt = PortTerm.Atom(spelled.Element);
                    for (var depth = 0; depth < spelled.Depth; depth++)
                        rebuilt = PortTerm.CollectionOf(rebuilt);
                    break;
                case PortTermKind.Constructor when !node.IsCollection && !node.IsMap:
                    rebuilt = PortTerm.Constructor(
                        constructors.TryGetValue(node.Name, out var head) ? head.Record.Name : node.Name, inner!);
                    break;
                case PortTermKind.Constructor when node.IsMap:
                    rebuilt = PortTerm.MapOf(inner!);
                    break;
                case PortTermKind.Constructor:
                    rebuilt = PortTerm.CollectionOf(inner!);
                    break;
                default:
                    rebuilt = node.Kind != PortTermKind.Null && node.IsNullable ? node.WithoutNullable() : node;
                    break;
            }

            inner = node.IsNullable && node.Kind != PortTermKind.Null ? rebuilt.AsNullable() : rebuilt;
        }

        return inner!;
    }

    /// <summary>Whether a value of <paramref name="from"/> may feed a port declared <paramref name="to"/>,
    /// under the rules docs/reference/port-types.md's "Widening and assignability" section states for this table. Never throws, for
    /// any non-default <see cref="PortValueType"/>.</summary>
    /// <remarks>Iterative: the loop peels one collection level per iteration — structural equality,
    /// a widening-row lookup, then peel both sides one level — and never builds a name.</remarks>
    public bool IsAssignable(PortValueType from, PortValueType to)
    {
        var f = Canon(aliases, PortTypeTerm.From(from));
        var t = Canon(aliases, PortTypeTerm.From(to));
        while (true)
        {
            if (f.Equals(t)) return true;
            if (widensTo.TryGetValue(f, out var targets) && targets.Contains(t)) return true;
            if (f.Depth == 0 || t.Depth == 0) return false;
            f = f.Peel();
            t = t.Peel();
        }
    }

    /// <summary>Every type this table has an opinion about — a widening source, a widening target, a
    /// <see cref="Declaring"/>'d name with no widening of its own, or an alias's own canonical type —
    /// spelled as <see cref="Canonicalize(PortValueType)"/> answers it, never by one of its aliases, and sorted so
    /// a fingerprint or the editor-facing publication reads the same order on every run.</summary>
    public IReadOnlyList<PortValueType> KnownTypes =>
        [.. widensTo.Keys.Concat(widensTo.Values.SelectMany(t => t)).Concat(declared)
            .Concat(aliases.Values)
            .Where(t => t.Depth != 0 || !aliases.TryGetValue(t.Element, out var canonical) || canonical.Equals(t))
            .Select(t => Spelled(Canon(aliases, t)).Render().Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(PortValueType.Of)];

    /// <summary>Whether a stored name is spelled the way <see cref="Canonicalize(PortValueType)"/> answers it: false for an
    /// alias or a case variant of a registered name, which the compile refuses as <c>port-type-alias</c>.</summary>
    public bool IsCanonical(PortValueType type) =>
        string.Equals(Canonicalize(type).Name, type.Name, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="type"/>, alias resolved, is made of names this table has an opinion about
    /// (<see cref="KnownTypes"/>). A well-formed name nobody registered is false: the compile refuses it as
    /// <c>port-type-unregistered</c>, and the editor still loads it unchanged.</summary>
    public bool IsRegistered(PortValueType type)
    {
        var element = Canon(aliases, PortTypeTerm.From(type)).Element;

        var atoms = registeredAtoms;
        if (atoms is null)
        {
            atoms = new HashSet<string>(
                KnownTypes.Select(PortTypeTerm.From).Select(t => t.Element),
                StringComparer.OrdinalIgnoreCase);
            registeredAtoms = atoms;
        }

        return atoms.Contains(element);
    }

    private HashSet<string>? registeredAtoms;

    /// <summary>What <paramref name="from"/> widens to directly, spelled and sorted as <see cref="KnownTypes"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="from"/> is <c>default(PortValueType)</c>.</exception>
    public IReadOnlyList<PortValueType> WideningsOf(PortValueType from)
    {
        RefuseDefault(from, nameof(from));
        return widensTo.TryGetValue(PortTypeTerm.From(from), out var targets)
            ? [.. targets.Select(t => Spelled(Canon(aliases, t)).Render())
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Every alias that canonicalises to <paramref name="canonical"/>, sorted the same way as
    /// <see cref="KnownTypes"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="canonical"/> is <c>default(PortValueType)</c>.</exception>
    public IReadOnlyList<PortValueType> AliasesOf(PortValueType canonical)
    {
        RefuseDefault(canonical, nameof(canonical));
        var term = PortTypeTerm.From(canonical);
        return [.. aliases.Where(kv => kv.Value.Equals(term)).Select(kv => Atom(kv.Key).Render())
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.Ordinal)];
    }

    internal string Fingerprint()
    {
        var builder = new StringBuilder();
        var types = KnownTypes;
        builder.Count(types.Count);
        foreach (var type in types)
        {
            builder.Text(type.Name);
            AppendNames(builder, WideningsOf(type));
            AppendNames(builder, AliasesOf(type));
        }

        if (constructors.Count > 0)
        {
            builder.Count(constructors.Count);
            foreach (var record in Constructors)
            {
                builder.Text(record.Name).Text(StableName(record.Erasure));
                var arguments = constructors[record.Name].Arguments.Select(a => a.ToString())
                    .Order(StringComparer.Ordinal).ToList();
                builder.Count(arguments.Count);
                foreach (var argument in arguments)
                    builder.Text(argument);
            }
        }

        return builder.ToString();
    }

    internal static string StableName(Type type)
    {
        if (type.HasElementType)
            return StableName(type.GetElementType()!) + (type.IsArray ? $"[{new string(',', type.GetArrayRank() - 1)}]" : type.IsByRef ? "&" : "*");
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var name = $"{definition.FullName ?? definition.Name}, {definition.Assembly.GetName().Name}";
        return type.IsGenericType && !type.IsGenericTypeDefinition
            ? $"{name}[{string.Join(",", type.GetGenericArguments().Select(StableName))}]"
            : name;
    }

    private static void AppendNames(StringBuilder builder, IReadOnlyList<PortValueType> names)
    {
        builder.Count(names.Count);
        foreach (var name in names)
            builder.Text(name.Name);
    }
}

/// <summary>Why a term is refused by a vocabulary: the compiler's rule id and a sentence naming the type.</summary>
public sealed record PortTermRefusal(string Code, string Message);
