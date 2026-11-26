using System.Collections.Frozen;
using System.Collections.Immutable;
using ReeveWorkflow.Abstractions;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>One port as the compiler sees it. The declared type and whether it must be fed are what a
/// verdict turns on; the display name is not here because rewording one must invalidate nothing.
/// <c>AllowedValues</c> is dropped too: it is an editor affordance that no compiler rule reads today, and
/// the day one does, it enters both this record and the fingerprint in the same commit.
/// <see cref="DependencyFingerprint"/> hashes <see cref="Type"/> with its exact spelling.</summary>
public sealed record PortShape(string Name, PortValueType Type, bool IsRequired)
{
    private readonly PortValueType type = Validated(Type);
    private readonly IReadOnlyList<ExpressionScopeVariable> scopeVariables = [];

    /// <summary>The variables the input's expression reads while its block evaluates it, in declared order, as the declaration
    /// states them: names and declaration terms, hashed with the port. Empty for any port but an <c>Expression&lt;T&gt;</c> input
    /// that declares some. A copy: a caller's list mutated afterwards moves nothing here.</summary>
    public IReadOnlyList<ExpressionScopeVariable> ScopeVariables
    {
        get => scopeVariables;
        init => scopeVariables = [.. value ?? []];
    }

    /// <summary>The port's <see cref="BlockPortDefinition.SecretValues"/>. Hashed: the declaration rules grade it, so a change
    /// can flip a stored verdict.</summary>
    public bool SecretValues { get; init; }

    /// <summary>An explicit accessor, as on <see cref="BlockPortDefinition.Type"/>, so a <c>with</c> expression
    /// cannot set <c>default(PortValueType)</c> either.</summary>
    public PortValueType Type
    {
        get => type;
        init => type = Validated(value);
    }

    private static PortValueType Validated(PortValueType type) => type.IsDefault
        ? throw new ArgumentException("A port's type cannot be default(PortValueType).", nameof(Type))
        : type;
}

/// <summary>One configuration field as the compiler sees it. <c>DefaultValue</c> and <c>Description</c> are
/// dropped because nothing in the engine applies them — a node's own config is what
/// <c>WorkflowGraphExecutor.MergeConfigIntoInputs</c> merges, never the schema default.</summary>
/// <remarks><c>AllowedValues</c> is what an Enum field accepts, in declared order; the fingerprint hashes it.</remarks>
public sealed record ConfigFieldShape(string Name, ConfigFieldType Type, bool Required,
    IReadOnlyList<string>? AllowedValues = null);

/// <summary>The behavioural flags a graph can be refused over.</summary>
public readonly record struct BlockFlags(
    bool HasDynamicPorts,
    bool SupportsDeclaredOutputs,
    bool CanSuspend,
    bool OnlyDrivesItsSubCanvases,
    bool PortsAreResolvedElsewhere,
    bool DeclaredOutputsCollect = false);

/// <summary>The compiler's entire view of a block type, and therefore what the dependency fingerprint
/// hashes to decide whether a stored verdict still applies — except <see cref="Source"/>, carried here for
/// diagnostics and recorded against a verdict, never hashed: a plugin re-published under a new version with
/// no shape change must invalidate nothing. One declaration answers both "what did the compiler read" and
/// "what do we hash" so the two cannot drift on everything the hash does cover.</summary>
/// <remarks>The collections are read in the factory's declaration order. A port or a config field is looked
/// up by name. This type's own constructor guarantees <see cref="SubCanvasOutputs"/> never holds a name absent from
/// <see cref="SubCanvasNames"/>, so a lookup can never read a sub-canvas the digest did not also walk. A
/// duplicate NAME within one list is resolved the same way everywhere (<see cref="WorkflowCompiler"/>): first-declared
/// wins. <see
/// cref="DependencyFingerprint"/> renders every port list and configuration-field list in that same
/// declared order and never sorts any of them: a required, unfed port or field is reported as
/// a diagnostic in declared order whether or not any two of them share a name, and a stored diagnostic list
/// is never recomputed, so which one came first is itself a verdict input the digest must see.
/// <para>Every collection this type is given is snapshotted into a genuinely immutable, ordinal,
/// order-preserving form at construction — a caller's <c>List</c> mutated after the call, a dictionary or
/// set built with a case-insensitive or hash-randomised comparer, or a caller downcasting what this type
/// handed back, cannot move what this shape answers, because nothing here still holds the caller's own
/// collection and nothing exposed here can be mutated in place. <see cref="SubCanvasInputs"/>,
/// <see cref="SubCanvasOutputs"/> and <see cref="SubCanvasNames"/> have no <c>init</c> accessor at all —
/// only the constructor sets them, together, so a <c>with</c> can never change one without the other two
/// silently going stale. <see cref="SubCanvasNames"/> is REQUIRED whenever either map is non-empty: this
/// type never derives a declaration order from a map's own enumeration, because nothing promises that order
/// means anything (an <c>ImmutableDictionary</c>'s is hash-seeded per process). The constructor refuses
/// (<see cref="ArgumentException"/>) unless <see cref="SubCanvasNames"/> has no nulls, no ordinal duplicates,
/// and both maps' key sets are exactly <see cref="SubCanvasNames"/>, ordinally — a name declared on only one
/// side still needs an entry, with an empty port list, on the other.</para>
/// Equality on this record is reference equality on its list members, not structural: two shapes built from
/// the same unchanged factory compare unequal. The fingerprint is the sole authority on whether a shape
/// changed; nothing here should be compared with <c>==</c> for that question.</remarks>
public sealed record BlockShape
{
    public string BlockTypeId { get; }

    public IReadOnlyList<PortShape> Inputs { get; }

    public IReadOnlyList<PortShape> Outputs { get; }

    public IReadOnlyList<ConfigFieldShape> ConfigurationFields { get; }

    public BlockFlags Flags { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<PortShape>> SubCanvasInputs { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<PortShape>> SubCanvasOutputs { get; }

    /// <summary>Where this block came from, in the host's own vocabulary. Carried for diagnostics only —
    /// never hashed, see the type's own remarks.</summary>
    public string? Source { get; }

    public string? RunsWorkflowFrom { get; }

    /// <summary>The type parameters the port types of this block may name, in declaration order. Empty for a block that is
    /// not generic.</summary>
    public IReadOnlyList<WorkflowTypeParameter> TypeParameters { get; }

    internal string? DisplayName { get; }

    internal BlockShape WithBlockTypeId(string blockTypeId) => new(
        blockTypeId, Inputs, Outputs, ConfigurationFields, Flags, SubCanvasInputs, SubCanvasOutputs,
        Source, RunsWorkflowFrom, SubCanvasNames, TypeParameters, DisplayName);

    /// <summary>Every sub-canvas name this block declares, in DECLARATION order. Through <see cref="From"/>
    /// this is the factory's own <c>SubCanvasNames</c> order, frozen at the moment the shape was built; a
    /// caller building a shape directly must state it explicitly whenever either port map is non-empty — see
    /// the type's own remarks for why nothing here ever derives it from a map. This order is what relation
    /// resolution walks, so it is stored once here rather than recomputed from a
    /// dictionary whose own enumeration order nothing promises stays put.</summary>
    public IReadOnlyList<string> SubCanvasNames { get; }

    internal BlockShape(
        string BlockTypeId,
        IReadOnlyList<PortShape> Inputs,
        IReadOnlyList<PortShape> Outputs,
        IReadOnlyList<ConfigFieldShape> ConfigurationFields,
        BlockFlags Flags,
        IReadOnlyDictionary<string, IReadOnlyList<PortShape>> SubCanvasInputs,
        IReadOnlyDictionary<string, IReadOnlyList<PortShape>> SubCanvasOutputs,
        string? Source,
        string? RunsWorkflowFrom = null,
        IReadOnlyList<string>? SubCanvasNames = null,
        IReadOnlyList<WorkflowTypeParameter>? TypeParameters = null,
        string? DisplayName = null)
    {
        ArgumentNullException.ThrowIfNull(BlockTypeId);
        ArgumentNullException.ThrowIfNull(Inputs);
        ArgumentNullException.ThrowIfNull(Outputs);
        ArgumentNullException.ThrowIfNull(ConfigurationFields);
        ArgumentNullException.ThrowIfNull(SubCanvasInputs);
        ArgumentNullException.ThrowIfNull(SubCanvasOutputs);

        this.BlockTypeId = BlockTypeId;
        this.Inputs = Inputs.ToImmutableArray();
        this.Outputs = Outputs.ToImmutableArray();
        this.ConfigurationFields = ConfigurationFields.ToImmutableArray();
        this.Flags = Flags;
        this.Source = Source;
        this.RunsWorkflowFrom = RunsWorkflowFrom;
        this.DisplayName = DisplayName;
        this.TypeParameters = [.. (TypeParameters ?? []).Select(p => new WorkflowTypeParameter(p.Name, [.. p.Constraints ?? []]))];

        this.SubCanvasNames = SubCanvasNames switch
        {
            null when SubCanvasInputs.Count == 0 && SubCanvasOutputs.Count == 0 => [],
            null => throw new ArgumentException(
                $"Block type '{BlockTypeId}' gives a non-empty SubCanvasInputs or SubCanvasOutputs but no "
                + "SubCanvasNames; an order nobody declared cannot be derived from either map.",
                nameof(SubCanvasNames)),
            { } names => ValidateSubCanvasNames(names, BlockTypeId),
        };
        this.SubCanvasInputs = ValidateAndSnapshotPortMap(
            SubCanvasInputs, this.SubCanvasNames, nameof(SubCanvasInputs), BlockTypeId);
        this.SubCanvasOutputs = ValidateAndSnapshotPortMap(
            SubCanvasOutputs, this.SubCanvasNames, nameof(SubCanvasOutputs), BlockTypeId);
    }

    /// <summary>Reads a factory's declared shape. Call this wherever the compiler needs to bind a node to
    /// its block or a fingerprint needs to hash what that block currently declares.</summary>
    public static BlockShape From(IWorkflowBlockFactory factory)
    {
        var shape = Snapshot(factory);
        if (shape.FirstDottedPortName() is { } dotted)
            throw new ArgumentException($"Block type '{shape.BlockTypeId}' declares name '{dotted}' containing a dot (rule dotted-name).", nameof(factory));
        BlockDeclarationRules.Refuse(shape);
        foreach (var field in factory.ConfigurationSchema)
            EnumFieldRules.Refuse(shape.BlockTypeId, field);
        return shape;
    }

    /// <summary>Reads a block a plugin declares, when the plugin loads, after its types are registered. It is <see cref="From"/>
    /// with two differences: a port type naming <c>Any</c> or <c>Float</c>, which are not port types, is refused first with
    /// <c>port-type-alias</c>, naming the block and the port. Then each port term is graded against the vocabulary <paramref name="registry"/> holds now, as a
    /// stored term is: a host alias spelling is <c>port-type-alias</c> and a type nobody has registered is
    /// <c>port-type-unregistered</c>. A type registered later, or by a plugin not yet loaded, is unregistered now: the block
    /// is not held. A host declines the plugin's load on any refusal.</summary>
    /// <param name="registry">The registry the plugin's own types were registered in.</param>
    /// <exception cref="ArgumentException">The declaration is refused; the message begins with the rule id.</exception>
    public static BlockShape FromPlugin(IWorkflowBlockFactory factory, IWorkflowTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        BlockDeclarationRules.RefuseReservedNames(Snapshot(factory));
        var shape = From(factory);
        BlockDeclarationRules.RefuseVocabulary(shape, registry.Current.Table);
        return shape;
    }

    internal string? FirstDottedPortName() => Inputs.Select(p => p.Name).Concat(Outputs.Select(p => p.Name))
        .Concat(SubCanvasNames.SelectMany(name => SubCanvasInputs[name].Select(p => p.Name)
            .Concat(SubCanvasOutputs[name].Select(p => p.Name))))
        .FirstOrDefault(name => name.Contains(".", StringComparison.Ordinal));

    internal static BlockShape Snapshot(IWorkflowBlockFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var blockTypeId = factory.BlockTypeId;
        var names = (IReadOnlyList<string>)[.. factory.SubCanvasNames];
        var inputs = factory.Inputs.ToArray();
        var outputs = factory.Outputs.ToArray();
        var subCanvasInputs = SubCanvasPortMap(blockTypeId, names, factory.GetSubCanvasInputs);
        var subCanvasOutputs = SubCanvasPortMap(blockTypeId, names, factory.GetSubCanvasOutputs);
        var inputShapes = inputs.Select(Shape).ToArray();
        var outputShapes = outputs.Select(Shape).ToArray();
        return new BlockShape(
            blockTypeId,
            inputShapes,
            outputShapes,
            [.. factory.ConfigurationSchema.Select(f => new ConfigFieldShape(f.Name, f.Type, f.Required, f.AllowedValues?.ToImmutableArray()))],
            new BlockFlags(
                factory.HasDynamicPorts,
                factory.SupportsDeclaredOutputs,
                factory.CanSuspend,
                factory.OnlyDrivesItsSubCanvases,
                factory.PortsAreResolvedElsewhere,
                factory.DeclaredOutputsCollect),
            subCanvasInputs,
            subCanvasOutputs,
            factory.Source,
            factory.RunsWorkflowFrom,
            names,
            [.. factory.TypeParameters],
            factory.DisplayName);
    }

    private static PortShape Shape(BlockPortDefinition port) =>
        new(port.Name, port.Type, port.IsRequired) { ScopeVariables = port.ScopeVariables, SecretValues = port.SecretValues };

    private static ImmutableArray<string> ValidateSubCanvasNames(IReadOnlyList<string> names, string blockTypeId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name is null)
            {
                throw new ArgumentException(
                    $"Block type '{blockTypeId}' has a null entry in SubCanvasNames.", "SubCanvasNames");
            }

            if (!seen.Add(name))
            {
                throw new ArgumentException(
                    $"Block type '{blockTypeId}' declares the sub-canvas name '{name}' more than once in "
                    + "SubCanvasNames.",
                    "SubCanvasNames");
            }
        }

        return [.. names];
    }

    private static FrozenDictionary<string, IReadOnlyList<PortShape>> ValidateAndSnapshotPortMap(
        IReadOnlyDictionary<string, IReadOnlyList<PortShape>> source,
        IReadOnlyList<string> subCanvasNames,
        string paramName,
        string blockTypeId)
    {
        var map = new Dictionary<string, IReadOnlyList<PortShape>>(StringComparer.Ordinal);
        foreach (var (name, ports) in source)
            map[name] = ports.ToImmutableArray();

        var declared = new HashSet<string>(subCanvasNames, StringComparer.Ordinal);
        foreach (var name in map.Keys)
        {
            if (!declared.Contains(name))
            {
                throw new ArgumentException(
                    $"Block type '{blockTypeId}' has '{name}' in {paramName} but SubCanvasNames does not "
                    + "declare it; a sub-canvas map may only carry names SubCanvasNames also lists.",
                    paramName);
            }
        }

        foreach (var name in subCanvasNames)
        {
            if (!map.ContainsKey(name))
            {
                throw new ArgumentException(
                    $"Block type '{blockTypeId}' declares the sub-canvas name '{name}' in SubCanvasNames but "
                    + $"{paramName} has no entry for it; every declared name needs one, even an empty list.",
                    paramName);
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PortShape>> SubCanvasPortMap(
        string blockTypeId, IReadOnlyList<string> names, Func<string, IReadOnlyCollection<BlockPortDefinition>> ports)
    {
        var map = new Dictionary<string, IReadOnlyList<PortShape>>();
        foreach (var name in names)
        {
            var declared = (IReadOnlyList<PortShape>)
                [.. ports(name).Select(Shape)];
            if (!map.TryAdd(name, declared))
            {
                throw new InvalidOperationException(
                    $"Block type '{blockTypeId}' declares the sub-canvas name '{name}' more than "
                    + "once in SubCanvasNames; a signature cannot pick one of two declarations for it.");
            }
        }

        return map;
    }
}

/// <summary>The rule ids a block registration is refused under when a configuration field's allowed names are
/// malformed. Named like <c>dotted-name</c>: the message carries the id so a host can tell the cases apart.</summary>
public static class EnumFieldRules
{
    public const string NoValues = "enum-field-no-values";
    public const string DuplicateValue = "enum-field-duplicate-value";
    public const string BlankValue = "enum-field-blank-value";
    public const string ValuesOnNonEnum = "enum-values-on-non-enum-field";
    public const string DefaultNotAllowed = "enum-field-default-not-allowed";

    internal static void Refuse(string blockTypeId, BlockConfigFieldDefinition field)
    {
        var where = $"Block type '{blockTypeId}' configuration field '{field.Name}'";
        if (field.Type != ConfigFieldType.Enum)
        {
            if (field.AllowedValues is not null)
                throw new ArgumentException($"{where} is not an Enum field and declares AllowedValues (rule {ValuesOnNonEnum}).", "factory");
            return;
        }

        if (field.AllowedValues is not { Count: > 0 } names)
            throw new ArgumentException($"{where} is an Enum field and declares no AllowedValues (rule {NoValues}).", "factory");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException($"{where} declares an empty or whitespace-only allowed value (rule {BlankValue}).", "factory");
            if (!seen.Add(name))
                throw new ArgumentException($"{where} declares the allowed value '{name}' more than once, compared ordinally (rule {DuplicateValue}).", "factory");
        }

        if (field.DefaultValue is not null && !seen.Contains(field.DefaultValue))
            throw new ArgumentException(
                $"{where} has the default '{field.DefaultValue}', which is not one of its AllowedValues (rule {DefaultNotAllowed}).", "factory");
    }
}
