using System.Collections.Immutable;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>Where the port type vocabulary lives: the library's built-in atoms, the records a host registered at
/// startup and the records plugins register and withdraw while the process runs. Every change is atomic and
/// moves <see cref="Generation"/>; the compile service keys what it remembers on it.</summary>
/// <remarks><para>A record references only types the library, the host or its own owner registered: a widening row's
/// target, and an allowed argument of a constructor. A reference to any other type refuses the record, so a reference
/// never outlives the type it names: the host's types never leave, and <see cref="Unregister"/> removes all of an
/// owner's records at once.</para></remarks>
public interface IWorkflowTypeRegistry
{
    /// <summary>Registers <paramref name="records"/> under <paramref name="owner"/>. One refused record refuses the
    /// call: nothing applies, the generation stays, and <see cref="WorkflowTypeRegistrationResult.Refusals"/> names each
    /// record with <c>port-type-name-clash</c>, <c>port-type-registration-invalid</c> or <c>capability-claim-invalid</c>. A plugin loader declines the load.</summary>
    /// <param name="owner"><c>plugin:&lt;id&gt;</c>; a host registers at startup through <c>Seams.PortTypes</c>.</param>
    /// <param name="records">Atom and constructor records. A row or an allowed argument may name a type registered in the
    /// same call or before it, owned by the library, the host or <paramref name="owner"/>; it is judged against the state
    /// this call would make.</param>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is not <c>plugin:</c> followed by an id.</exception>
    WorkflowTypeRegistrationResult Register(string owner, IReadOnlyCollection<WorkflowTypeRegistrationRecord> records);

    /// <summary>Removes every record <paramref name="owner"/> registered. A stored term naming a removed type is
    /// <c>port-type-unregistered</c> at its next compile.</summary>
    /// <returns>False, with the generation unchanged, when the owner registered nothing.</returns>
    bool Unregister(string owner);

    /// <summary>Moves by one with each accepted registration and each unregistration; 0 until the first.</summary>
    long Generation { get; }

    /// <summary>The vocabulary at the current <see cref="Generation"/>. Hold the instance for one compile.</summary>
    WorkflowTypeVocabulary Current { get; }
}

/// <summary>The library's <see cref="IWorkflowTypeRegistry"/>.</summary>
public sealed class WorkflowTypeRegistry : IWorkflowTypeRegistry
{
    private const string HostOwner = "host";
    private const string PluginPrefix = "plugin:";

    private readonly object gate = new();
    private ImmutableList<(string Owner, WorkflowTypeRegistrationRecord Record)> entries;
    private WorkflowTypeVocabulary current;

    /// <summary>A registry holding the library's built-in atoms and <c>Collection</c>, and the records a host
    /// registers at startup, as generation 0.</summary>
    /// <param name="hostRecords">The host's atoms and constructors; none for a host that adds no type.</param>
    /// <exception cref="WorkflowTypeRegistrationException">A record is refused: a host defect, so startup stops.</exception>
    public WorkflowTypeRegistry(IEnumerable<WorkflowTypeRegistrationRecord>? hostRecords = null)
    {
        entries = [.. (hostRecords ?? []).Select(r => (HostOwner, r ?? throw new ArgumentNullException(nameof(hostRecords))))];
        var refusals = new List<WorkflowRegistrationRefusal>();
        current = Build(entries, generation: 0, refusals) ?? throw new WorkflowTypeRegistrationException(refusals);
    }

    /// <inheritdoc />
    public long Generation => Current.Generation;

    /// <inheritdoc />
    public WorkflowTypeVocabulary Current => Volatile.Read(ref current);

    /// <inheritdoc />
    public WorkflowTypeRegistrationResult Register(string owner, IReadOnlyCollection<WorkflowTypeRegistrationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(records);
        if (!owner.StartsWith(PluginPrefix, StringComparison.Ordinal) || owner.Length == PluginPrefix.Length)
            throw new ArgumentException($"A run-time registration is owned by '{PluginPrefix}<id>', not '{owner}'.", nameof(owner));
        foreach (var record in records)
            ArgumentNullException.ThrowIfNull(record, nameof(records));

        lock (gate)
        {
            var proposed = entries.AddRange(records.Select(r => (owner, r)));
            var refusals = new List<WorkflowRegistrationRefusal>();
            var built = Build(proposed, current.Generation + 1, refusals);
            if (built is null)
                return new WorkflowTypeRegistrationResult(refusals, current.Generation);
            entries = proposed;
            Volatile.Write(ref current, built);
            return new WorkflowTypeRegistrationResult([], built.Generation);
        }
    }

    /// <inheritdoc />
    public bool Unregister(string owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.StartsWith(PluginPrefix, StringComparison.Ordinal) || owner.Length == PluginPrefix.Length)
            throw new ArgumentException($"A run-time registration is owned by '{PluginPrefix}<id>', not '{owner}'.", nameof(owner));

        lock (gate)
        {
            var remaining = entries.RemoveAll(e => e.Owner == owner);
            if (remaining.Count == entries.Count)
                return false;
            var built = Build(remaining, current.Generation + 1, [])
                ?? throw new InvalidOperationException("Removing records cannot make the rest invalid.");
            entries = remaining;
            Volatile.Write(ref current, built);
            return true;
        }
    }

    private static WorkflowTypeVocabulary? Build(
        IReadOnlyList<(string Owner, WorkflowTypeRegistrationRecord Record)> entries, long generation,
        List<WorkflowRegistrationRefusal> refusals)
    {
        var table = PortTypeWideningTable.Builtin;
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var atomTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, clr) in WorkflowTypeVocabulary.LibraryTypes)
        {
            owners[name] = WorkflowTypeVocabulary.Library;
            atomTypes[name] = clr;
            taken.Add(name);
        }
        foreach (var known in table.KnownTypes)
        {
            taken.Add(known.Name);
            foreach (var alias in table.AliasesOf(known))
                taken.Add(alias.Name);
        }
        taken.Add(PortTypeGrammar.Keyword);

        var registeredClr = new List<(string, Type)>();
        var claims = WorkflowTypeVocabulary.NewClaims();
        var literals = WorkflowTypeVocabulary.NewLiterals();
        var accepted = new List<(string Owner, WorkflowTypeRecord Record)>();

        void Refuse(string code, string name, string message) =>
            refusals.Add(new WorkflowRegistrationRefusal(code, name, message));

        foreach (var entry in entries)
        {
            var own = entry.Record switch
            {
                WorkflowTypeRecord atom => atom.ValidateRules(),
                WorkflowTypeConstructorRecord constructor => constructor.ValidateRules(),
                _ => [new WorkflowRegistrationRefusal(
                    WorkflowRegistrationCodes.PortTypeRegistrationInvalid, "", $"Unknown registration record {entry.Record.GetType().Name}.")],
            };
            if (own.Count == 0)
                own = entry.Record switch
                {
                    WorkflowTypeRecord atom => atom.ValidateClaims(),
                    WorkflowTypeConstructorRecord constructor => constructor.ValidateClaims(),
                    _ => [],
                };
            refusals.AddRange(own);
        }

        if (refusals.Count > 0)
            return null;

        foreach (var entry in entries)
        {
            var spellings = entry.Record switch
            {
                WorkflowTypeRecord atom => atom.Aliases.Prepend(atom.Name),
                WorkflowTypeConstructorRecord constructor => [constructor.Name],
                _ => [],
            };
            var subject = entry.Record is WorkflowTypeRecord ? "Port type" : "Port type constructor";
            var name = entry.Record is WorkflowTypeRecord a ? a.Name : ((WorkflowTypeConstructorRecord)entry.Record).Name;
            foreach (var spelling in spellings)
            {
                if (!taken.Add(spelling))
                    Refuse(WorkflowRegistrationCodes.PortTypeNameClash, name,
                        $"{subject} '{name}' cannot be registered: the name '{spelling}' is already registered.");
            }
        }

        if (refusals.Count > 0)
            return null;

        foreach (var (owner, record) in entries.Where(e => e.Record is WorkflowTypeRecord))
        {
            var atom = (WorkflowTypeRecord)record;
            try
            {
                table = table.Declaring(PortValueType.Of(atom.Name));
                foreach (var alias in atom.Aliases)
                    table = table.WithAlias(PortValueType.Of(alias), PortValueType.Of(atom.Name));
            }
            catch (ArgumentException exception)
            {
                Refuse(CodeOf(exception), atom.Name, exception.Message);
                continue;
            }

            owners[atom.Name] = owner;
            atomTypes[atom.Name] = atom.ClrType;
            claims[atom.Name] = new HashSet<WorkflowCapability>(atom.Claims ?? new HashSet<WorkflowCapability>());
            if (atom.Literal is { } atomForm)
                literals[atom.Name] = atomForm;
            registeredClr.Add((atom.Name, atom.ClrType));
            accepted.Add((owner, atom));
        }

        var constructorEntries = new List<(string Owner, WorkflowTypeConstructorRecord Record, IReadOnlyList<PortTerm> Args)>();
        foreach (var (owner, record) in entries.Where(e => e.Record is WorkflowTypeConstructorRecord))
        {
            var constructor = (WorkflowTypeConstructorRecord)record;
            try
            {
                table = table.WithConstructor(constructor);
            }
            catch (ArgumentException exception)
            {
                Refuse(CodeOf(exception), constructor.Name, exception.Message);
                continue;
            }

            owners[constructor.Name] = owner;
            claims[constructor.Name] = new HashSet<WorkflowCapability>(constructor.Claims ?? new HashSet<WorkflowCapability>());
            if (constructor.Literal is { } constructorForm)
                literals[constructor.Name] = constructorForm;
            constructorEntries.Add((owner, constructor, constructor.ReadAllowedArguments()));
        }

        if (refusals.Count > 0)
            return null;

        foreach (var (owner, constructor, args) in constructorEntries)
        {
            for (var i = 0; i < args.Count; i++)
            {
                var written = constructor.AllowedArguments[i];
                var why = table.RefusalOf(args[i]) is { } unregistered ? $"leads nowhere: {unregistered.Message}"
                    : table.NonCanonicalSpelling(args[i]) is { } spelling ? $"is not canonical: {spelling}"
                    : ForeignOwner(args[i], owner, owners) is { } foreign ? OwnedElsewhere(foreign)
                    : null;
                if (why is not null)
                    Refuse(WorkflowRegistrationCodes.PortTypeRegistrationInvalid, constructor.Name,
                        $"Port type '{constructor.Name}' cannot be registered: its allowed argument '{written}' {why}.");
            }
        }

        if (refusals.Count > 0)
            return null;

        var termRows = new Dictionary<string, ImmutableList<PortTerm>>(StringComparer.OrdinalIgnoreCase);
        var probe = new WorkflowTypeVocabulary(generation, table, owners, atomTypes, termRows, registeredClr, claims, literals);
        foreach (var (owner, atom) in accepted)
        {
            var heads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in atom.ReadWidensTo())
            {
                var why = RowRefusal(atom, owner, target, table, probe, owners, heads);
                if (why is not null)
                {
                    Refuse(WorkflowRegistrationCodes.PortTypeRegistrationInvalid, atom.Name,
                        $"Port type '{atom.Name}' cannot be registered: widening row '{target}' {why}.");
                    continue;
                }

                var canonical = table.Canonicalize(target);
                if (canonical.Kind == PortTermKind.Atom)
                    table = table.With(PortValueType.Of(atom.Name), [PortValueType.Of(canonical.Name)]);
                else
                    termRows[atom.Name] = termRows.GetValueOrDefault(atom.Name, []).Add(canonical);
            }
        }

        if (refusals.Count > 0)
            return null;

        var serializers = new Dictionary<string, System.Text.Json.Serialization.Metadata.JsonTypeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, atom) in accepted)
        {
            if (atom.JsonTypeInfo is { } info)
                serializers[atom.Name] = info;
        }

        foreach (var (_, constructor, _) in constructorEntries)
        {
            if (constructor.JsonTypeInfo is { } info)
                serializers[constructor.Name] = info;
        }

        return new WorkflowTypeVocabulary(generation, table, owners, atomTypes, termRows, registeredClr, claims, literals) { JsonTypeInfos = serializers };
    }

    private static string OwnedElsewhere(string foreign) =>
        $"is owned by {foreign}; a record references only library types, host types and types of its own owner";

    private static string? ForeignOwner(PortTerm term, string owner, Dictionary<string, string> owners)
    {
        for (var node = term; node is not null; node = node.LastArgument)
        {
            if (node.Kind is PortTermKind.Atom or PortTermKind.Constructor && !node.IsCollection
                && owners.TryGetValue(node.Name, out var held)
                && held != WorkflowTypeVocabulary.Library && held != HostOwner && held != owner)
                return held;
        }

        return null;
    }

    private static string? RowRefusal(
        WorkflowTypeRecord source, string owner, PortTerm target, PortTypeWideningTable table, WorkflowTypeVocabulary probe,
        Dictionary<string, string> owners, HashSet<string> heads)
    {
        if (table.RefusalOf(target) is { } unregistered)
            return $"leads nowhere: {unregistered.Message}";
        if (ForeignOwner(target, owner, owners) is { } foreign)
            return OwnedElsewhere(foreign);
        var canonical = table.Canonicalize(target);
        if (canonical.Kind == PortTermKind.Atom
            && WorkflowTypeVocabulary.LibraryTypes.Any(l => string.Equals(l.Name, canonical.Name, StringComparison.OrdinalIgnoreCase)))
            return "leads into a library type, which no host row may do";
        if (canonical.Kind == PortTermKind.Atom && string.Equals(canonical.Name, source.Name, StringComparison.OrdinalIgnoreCase))
            return "leads to the type itself";
        if (table.NonCanonicalSpelling(target) is { } spelling)
            return $"is not spelled canonically: {spelling}";
        if (probe.ClrTypeOf(canonical) is not { } clr)
            return "has no CLR type";
        if (!clr.IsAssignableFrom(source.ClrType))
            return $"leads to {clr.Name}, which {source.ClrType.Name} is not reference-assignable to";
        if (canonical.Kind == PortTermKind.Constructor && !heads.Add(canonical.Name))
            return $"is a second row into the constructor '{canonical.Name}'";
        return null;
    }

    private static string CodeOf(ArgumentException exception) =>
        exception.Message.StartsWith(WorkflowRegistrationCodes.PortTypeNameClash, StringComparison.Ordinal)
            ? WorkflowRegistrationCodes.PortTypeNameClash
            : WorkflowRegistrationCodes.PortTypeRegistrationInvalid;
}
