using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Compilation;

internal static class FingerprintFormat
{
    public static StringBuilder Text(this StringBuilder builder, string? value) =>
        value is null
            ? builder.Append('-')
            : builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    public static StringBuilder Count(this StringBuilder builder, int count) =>
        builder.Append(count.ToString(CultureInfo.InvariantCulture)).Append('#');

    public static StringBuilder Flag(this StringBuilder builder, bool value) => builder.Append(value ? 'T' : 'F');

}

/// <summary>A SHA-256 hash over what the compiler was told — the shapes it read, the port-type vocabulary it
/// compared against, the namespaces it could ask about, and the ports resolved for it outside the graph —
/// and therefore over what can change a verdict. <see cref="BlockShape.Source"/>, display names and
/// descriptions are deliberately absent: a version bump or a reworded label that changed no shape must
/// invalidate nothing.</summary>
/// <remarks>Two properties the digest is built to have. It is <em>injective</em>: every variable-length
/// field is rendered through <see cref="FingerprintFormat"/> rather than joined with a delimiter, because
/// every delimiter available is legal inside the authored text it would separate. And it is <em>stable
/// across processes</em>: each collection is rendered element by element in a deterministic order — most
/// collections (the shapes the bind phase reached, the pinned namespace names, the environment's entries,
/// a block's raw-expression-input names) by the rendered record itself, ordinally, because nothing the
/// compiler reads depends on any of THOSE collections' own order — bind reaches a SET of shapes, a
/// namespace and a raw-expression-input name are matched by name, and two environment entries are two
/// different keyed lookups, never a sequence.
/// <para>Every other collection here IS read positionally by the compiler, so none of them sort at all —
/// each renders in exactly the DECLARED order it was given, count-prefixed, the same rule <see
/// cref="BlockShape.SubCanvasNames"/> has always rendered under: a block's own
/// input and output ports, its configuration fields, a sub-canvas's own port lists, and a resolved
/// signature's input and output ports. The compiler reports diagnostics in declared order and a stored diagnostic list is never recomputed, so which of two ports came first is a verdict input. Hashing declared order everywhere the compiler reads it positionally costs one extra recompile when a plugin merely reorders its own ports with no other change: over-invalidation, never a stale verdict.</para>
/// Port-type names are hashed as spelled: a case variant of a registered name grades as <c>port-type-alias</c>
/// (<c>IsCanonical</c> is ordinal), though lookup ignores case.</remarks>
public static class DependencyFingerprint
{
    /// <summary>The compiler a stored verdict came from: a number, bumped when a change to a RELEASED compiler can
    /// move a verdict or a digest a stored row still keys on with the same rules, then <c>/</c> and a digest of the
    /// <see cref="CompilationRules"/> ids, so a rule added or removed always moves it and a verdict from a compiler
    /// with other rules is never answered from. Deliberately not a <c>const</c>: a const is baked into each
    /// referencing assembly when <em>that</em> assembly compiles, so a host that upgraded this package would keep
    /// keying on the version it was built against, which is the one thing this member exists to prevent.</summary>
    public static readonly string CompilerVersion = VersionOf(
        typeof(CompilationRules).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!),
        ExpressionSignatures.All);

    internal static string VersionOf(IEnumerable<string> ruleIds, IEnumerable<ExpressionSignature>? signatures = null)
    {
        var builder = new StringBuilder();
        foreach (var id in ruleIds.Order(StringComparer.Ordinal))
            builder.Text(id);
        var rows = (signatures ?? []).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        builder.Count(rows.Count);
        foreach (var row in rows)
        {
            builder.Text(row.Id).Text(row.Kind.ToString()).Text(row.Name).Count(row.Parameters.Count);
            foreach (var parameter in row.Parameters)
                builder.Text(parameter);
            builder.Text(row.Result).Count(row.Failures.Count);
            foreach (var failure in row.Failures)
                builder.Text(failure);
            builder.Flag(row.Variadic).Text(row.Capability);
        }

        return "6/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..12];
    }

    /// <summary>The dependency half of a stored verdict's key: every <paramref name="shapes"/> entry the
    /// bind phase reached, the effective <paramref name="options"/> (its widening table, namespace
    /// checks and the helper terms it was answered), and <paramref name="environment"/>'s resolved signatures. The one entry point — an earlier
    /// cut had four overloads that let a caller silently substitute <see cref="NodePortEnvironment.Empty"/>
    /// or drop the namespace checks, exactly the mistake the null refusal below exists to prevent.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null. <paramref name="environment"/> included:
    /// a caller that meant to pass one and forgot would otherwise get a digest that silently does not move
    /// when a resolved signature changes, which is the one failure this hash exists to rule out.</exception>
    public static string Compute(
        IEnumerable<BlockShape> shapes, CompilationOptions options, NodePortEnvironment environment) =>
        Compute(shapes, options, environment, CompilerVersion);

    internal static string Compute(
        IEnumerable<BlockShape> shapes, CompilationOptions options, NodePortEnvironment environment, string compilerVersion)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        return Compute(
            shapes.Select(RenderShape), options.Vocabulary.Fingerprint(),
            options.RenderedNamespaces, environment, compilerVersion, options.HelperChecks);
    }

    internal static string Compute(
        IEnumerable<string> renderedShapes, string renderedWidenings, IReadOnlyList<string> namespaces,
        NodePortEnvironment environment, string? compilerVersion = null,
        IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer>? helperChecks = null)
    {
        ArgumentNullException.ThrowIfNull(renderedShapes);
        ArgumentNullException.ThrowIfNull(renderedWidenings);
        ArgumentNullException.ThrowIfNull(namespaces);
        ArgumentNullException.ThrowIfNull(environment);

        var builder = new StringBuilder();
        builder.Text(compilerVersion ?? CompilerVersion);
        builder.Text(renderedWidenings);
        AppendSorted(builder, namespaces);
        AppendSorted(builder, renderedShapes);
        AppendSorted(builder, environment.Entries.Select(RenderEntry));
        if (helperChecks is { Count: > 0 })
            AppendSorted(builder, helperChecks.Select(RenderHelperCheck));

        return HashHex(builder);
    }

    internal static string HashHex(StringBuilder builder) =>
        Convert.ToHexStringLower(SHA256.HashData(Utf16LittleEndian(builder)));

    private static byte[] Utf16LittleEndian(StringBuilder builder)
    {
        var text = builder.ToString();
        var bytes = new byte[checked(text.Length * 2)];
        for (var i = 0; i < text.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), text[i]);

        return bytes;
    }

    private static void AppendSorted(StringBuilder builder, IEnumerable<string?> rendered)
    {
        var sorted = rendered.OrderBy(r => r, StringComparer.Ordinal).ToList();
        builder.Count(sorted.Count);
        foreach (var record in sorted)
            builder.Text(record);
    }

    private static void AppendInOrder(StringBuilder builder, IEnumerable<string?> rendered)
    {
        var list = rendered.ToList();
        builder.Count(list.Count);
        foreach (var record in list)
            builder.Text(record);
    }

    internal static string RenderShape(BlockShape shape)
    {
        var builder = new StringBuilder();
        builder.Text(shape.BlockTypeId);
        AppendPorts(builder, shape.Inputs);
        AppendPorts(builder, shape.Outputs);
        AppendInOrder(builder, shape.ConfigurationFields.Select(RenderConfigField));

        builder.Flag(shape.Flags.HasDynamicPorts)
            .Flag(shape.Flags.SupportsDeclaredOutputs)
            .Flag(shape.Flags.CanSuspend)
            .Flag(shape.Flags.OnlyDrivesItsSubCanvases)
            .Flag(shape.Flags.PortsAreResolvedElsewhere);

        AppendInOrder(builder, shape.SubCanvasNames.Select(name => RenderSubCanvas(shape, name)));
        builder.Text(shape.RunsWorkflowFrom);
        if (shape.TypeParameters.Count > 0)
            AppendInOrder(builder, shape.TypeParameters.Select(RenderTypeParameter));
        if (shape.Flags.DeclaredOutputsCollect)
            builder.Text("declaredOutputsCollect");
        return builder.ToString();
    }

    private static string RenderTypeParameter(WorkflowTypeParameter parameter)
    {
        var builder = new StringBuilder().Text(parameter.Name);
        AppendInOrder(builder, parameter.Constraints);
        return builder.ToString();
    }

    private static string RenderSubCanvas(BlockShape shape, string name)
    {
        var builder = new StringBuilder().Text(name);
        AppendPorts(builder, shape.SubCanvasInputs.TryGetValue(name, out var inputs) ? inputs : []);
        AppendPorts(builder, shape.SubCanvasOutputs.TryGetValue(name, out var outputs) ? outputs : []);
        return builder.ToString();
    }

    private static string RenderConfigField(ConfigFieldShape field)
    {
        var builder = new StringBuilder().Text(field.Name).Text(field.Type.ToString()).Flag(field.Required);
        if (field.AllowedValues is { Count: > 0 } names)
        {
            builder.Count(names.Count);
            foreach (var name in names)
                builder.Text(name);
        }

        return builder.ToString();
    }

    private static void AppendPorts(StringBuilder builder, IReadOnlyList<PortShape> ports) =>
        AppendInOrder(builder, ports.Select(RenderPort));

    private static string RenderPort(PortShape port)
    {
        var builder = new StringBuilder()
            .Text(port.Name)
            .Text(port.Type.Name)
            .Flag(port.IsRequired);
        if (port.ScopeVariables.Count > 0)
            AppendInOrder(builder, port.ScopeVariables.Select(v => new StringBuilder().Text(v.Name).Text(v.Type).ToString()).Prepend("scopeVariables"));
        if (port.SecretValues)
            builder.Text("secretValues");
        return builder.ToString();
    }

    private static string RenderHelperCheck(KeyValuePair<HelperPath, NamespacePathCheckAnswer> check)
    {
        var builder = new StringBuilder().Count(check.Key.Segments.Count);
        foreach (var segment in check.Key.Segments)
            builder.Text(segment);

        return builder.Text(check.Value.Verdict.ToString()).Text(check.Value.KeyPortType).ToString();
    }

    private static string RenderEntry(KeyValuePair<NodeKey, NodePortResolution> entry)
    {
        var builder = new StringBuilder();
        var segments = entry.Key.CanvasPath.Segments;
        builder.Count(segments.Count);
        foreach (var segment in segments)
            builder.Text(segment.NodeId).Text(segment.BranchName);

        builder.Text(entry.Key.NodeId);
        builder.Text(entry.Value.Reference).Text(entry.Value.FailureReason);
        if (entry.Value.Signature is not { } signature)
            return builder.Append('-').ToString();

        builder.Append('+');
        AppendPorts(builder, signature.Inputs);
        AppendPorts(builder, signature.Outputs);
        return builder.ToString();
    }
}
