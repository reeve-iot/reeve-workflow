using System.Collections.Immutable;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>What a canvas takes and what it returns — a subprogram's parameter list and result. At the root
/// it is the version's declared parameters; in a sub-canvas it is the enclosing node's own contract.</summary>
/// <remarks>Equality here is reference equality on <see cref="Inputs"/>/<see cref="Outputs"/>, like
/// <see cref="BlockShape"/>: two signatures each holding a freshly built empty list compare unequal. Assert
/// on the lists' own contents, never with <c>==</c> or <c>Be(CanvasSignature.Empty)</c>.
/// A <c>WorkflowInput</c> node's own inputs come from its block shape, not the signature, so nothing checks
/// whether a required workflow parameter was actually wired to anything.
/// <para>Like <see cref="BlockShape"/>, both members snapshot into a fixed copy on every write — the
/// constructor and a <c>with</c> expression alike, since <c>init</c> runs either way: a signature filed
/// into a <see cref="NodePortEnvironment"/> is read once by the compiler and, separately, by
/// <see cref="DependencyFingerprint"/> — a caller holding a mutable <c>List&lt;PortShape&gt;</c> and
/// mutating it between those two reads, or a <c>with</c> site further downstream, must not be able to make
/// them disagree.</para></remarks>
public sealed record CanvasSignature
{
    private readonly IReadOnlyList<PortShape> inputsValue;
    private readonly IReadOnlyList<PortShape> outputsValue;
    private readonly ImmutableArray<UntypedPort> untypedInputsValue = [];
    private readonly ImmutableArray<UntypedPort> untypedOutputsValue = [];

    public IReadOnlyList<PortShape> Inputs
    {
        get => inputsValue;
        init => inputsValue = Snapshot(value);
    }

    public IReadOnlyList<PortShape> Outputs
    {
        get => outputsValue;
        init => outputsValue = Snapshot(value);
    }

    internal IReadOnlyList<UntypedPort> UntypedInputs
    {
        get => untypedInputsValue;
        init => untypedInputsValue = [.. value];
    }

    internal IReadOnlyList<UntypedPort> UntypedOutputs
    {
        get => untypedOutputsValue;
        init => untypedOutputsValue = [.. value];
    }

    /// <summary>A signature as a host hands it over: a compile's root parameters, or a resolver's answer. Nothing is graded
    /// here; a compile grades each term where it reads it, as a stored term or as a resolver's answer.</summary>
    public CanvasSignature(IReadOnlyList<PortShape> inputs, IReadOnlyList<PortShape> outputs)
    {
        Inputs = inputs;
        Outputs = outputs;
    }

    private static IReadOnlyList<PortShape> Snapshot(IReadOnlyList<PortShape> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.ToImmutableArray();
    }

    /// <summary>No inputs, no outputs — what a caller with no signature to give must pass explicitly:
    /// <see cref="WorkflowCompiler.CompileAsync(WorkflowDefinition, IBlockShapeSource, CanvasSignature, CompilationOptions?, NodePortEnvironment?, CancellationToken)"/> takes no default, so a compile with genuinely no parameters
    /// says so rather than one call site silently choosing it for everyone.</summary>
    public static CanvasSignature Empty { get; } = new([], []);

    /// <summary>A sub-canvas's own signature: the branch's declared inputs (<paramref name="shape"/>'s
    /// <c>SubCanvasInputs[subCanvasName]</c>) union <paramref name="forwarded"/> — the names the container
    /// forwards by <c>Passthrough</c>, already typed by <see cref="EffectivePorts"/>'s own ladder — with a
    /// name collision won by the declared port, never the forwarded one. Outputs are the branch's declared
    /// outputs plus any raw key a parent output port projects out of it via
    /// <see cref="SubCanvasOutputPortDefinition.Sources"/>: <c>InlineSubCanvasExecutor</c>'s convergence
    /// projection, typed here ahead of the run rather than left to resolve at it.</summary>
    public static CanvasSignature ForSubCanvas(
        WorkflowNodeDefinition node, BlockShape shape, string subCanvasName, IReadOnlyList<PortShape> forwarded) =>
        ForSubCanvas(node, shape, subCanvasName, new PortSide(forwarded, []));

    internal static CanvasSignature ForSubCanvas(
        WorkflowNodeDefinition node, BlockShape shape, string subCanvasName, PortSide forwarded)
    {
        var declaredInputs = shape.SubCanvasInputs.TryGetValue(subCanvasName, out var d) ? d : [];
        var claimed = new HashSet<string>(declaredInputs.Select(p => p.Name), StringComparer.Ordinal);
        var inputs = new List<PortShape>(declaredInputs);
        inputs.AddRange(forwarded.Typed.Where(p => claimed.Add(p.Name)));
        var untypedInputs = forwarded.Untyped.Where(p => claimed.Add(p.Name)).ToList();

        var (outputs, untypedOutputs) = SubCanvasOutputs(node, shape, subCanvasName);
        return new CanvasSignature(inputs, outputs) { UntypedInputs = untypedInputs, UntypedOutputs = untypedOutputs };
    }

    private static (List<PortShape> Typed, List<UntypedPort> Untyped) SubCanvasOutputs(
        WorkflowNodeDefinition node, BlockShape shape, string subCanvasName)
    {
        var declared = shape.SubCanvasOutputs.TryGetValue(subCanvasName, out var d) ? d : [];
        var claimed = new HashSet<string>(declared.Select(p => p.Name), StringComparer.Ordinal);
        var typed = new List<PortShape>(declared);
        var untyped = new List<UntypedPort>();
        foreach (var port in node.OutputPorts ?? [])
        {
            if (port.Sources == null || !port.Sources.TryGetValue(subCanvasName, out var rawKey) || !claimed.Add(rawKey))
                continue;

            if (PortValueType.TryParse(port.Type, out var type, out _))
                typed.Add(new PortShape(rawKey, type, false));
            else
                untyped.Add(new UntypedPort(rawKey, false));
        }

        return (typed, untyped);
    }

    /// <summary>Reads a workflow's declared parameters from its stored <c>InputParametersJson</c>/
    /// <c>OutputParametersJson</c> through <see cref="StoredWorkflowDocument.ReadParameters"/>. False with one
    /// <see cref="CompilationRules.SignatureUnreadable"/> (or the reader rule's own code) per list refused, and one
    /// <see cref="CompilationRules.PortTypeMalformed"/> per parameter of a readable list whose type does not
    /// parse; <paramref name="signature"/> is then null, never <see cref="Empty"/>.</summary>
    public static bool TryRead(
        string? inputParametersJson, string? outputParametersJson,
        out CanvasSignature? signature, out IReadOnlyList<CompilationDiagnostic> diagnostics)
    {
        var read = ReadParameters(inputParametersJson, outputParametersJson);
        diagnostics = [.. read.Unreadable, .. read.Sites.Where(s => s.Error is not null).Select(s => PortTypeNameSites.Diagnostic(s, blockSource: null))];
        signature = diagnostics.Count > 0
            ? null
            : new CanvasSignature(
                [.. read.Sites.Where(s => s.Site == PortTypeNameSite.InputParameter).Select(Port)],
                [.. read.Sites.Where(s => s.Site == PortTypeNameSite.OutputParameter).Select(Port)]);
        return signature is not null;
    }

    internal static (IReadOnlyList<StoredPortTypeSite> Sites, IReadOnlyList<CompilationDiagnostic> Unreadable) ReadParameters(
        string? inputParametersJson, string? outputParametersJson)
    {
        var sites = new List<StoredPortTypeSite>();
        var unreadable = new List<CompilationDiagnostic>();
        foreach (var (json, site, label) in (ReadOnlySpan<(string?, PortTypeNameSite, string)>)
                 [(inputParametersJson, PortTypeNameSite.InputParameter, "input"), (outputParametersJson, PortTypeNameSite.OutputParameter, "output")])
        {
            if (StoredWorkflowDocument.ReadParameters(json, site, out var fault) is { } read)
                sites.AddRange(read);
            else
                unreadable.Add(new CompilationDiagnostic(
                    StoredWorkflowDocument.FindingRule(fault!, CompilationRules.SignatureUnreadable), $"The workflow's declared {label} parameters cannot be read: {fault!.Describe()}."));
        }

        return (sites, unreadable);
    }

    private static PortShape Port(StoredPortTypeSite site) =>
        new(site.PortName, PortValueType.Of(site.RawType!), site.IsRequired);
}
