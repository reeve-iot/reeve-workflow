using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

/// <summary>The front end: a definition and a catalogue in, a program or a list of refusals out. Pure —
/// no I/O, no DI, no clock. The phases run in the order the design document lists them.</summary>
/// <remarks>Split across three files: this one is the phase list and the phases themselves.
/// <c>WorkflowLowering.cs</c> holds <c>LowerCanvas</c> and what runs after the phases, once they agree on no
/// diagnostics. <c>WorkflowCompiler.ResilienceChecks.cs</c> holds the resilience door check, called from
/// <see cref="CheckNodes"/>'s per-node loop but otherwise unrelated to what that phase is for.</remarks>
public static partial class WorkflowCompiler
{
    internal static readonly IReadOnlyList<CompilerPhase> Phases =
    [
        new(BindPhase, Bind), new("check-structure", CheckStructure), new("check-nodes", CheckNodes), new("bind-types", BindTypes),
    ];

    private const string BindPhase = "bind";

    internal const string LowerPhase = "lower";

    /// <summary>Compiles the stored form and checks its helper paths in one asynchronous call.
    /// The model is written and read by the library's stored-document boundary.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled: checked before each
    /// phase, per node in each phase but check-structure, per wire and per step of a port trace; not while reading.</exception>
    /// <exception cref="CompilationFaultException">The helper resolver failed while checking paths in the definition.</exception>
    public static async Task<CompilationResult> CompileAsync(
        WorkflowDefinition definition,
        IBlockShapeSource shapes,
        CanvasSignature signature,
        CompilationOptions? options = null,
        NodePortEnvironment? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var document = StoredWorkflowDocument.ReadModel(definition, out var fault);
        if (document is null)
            return CompilationResult.Refused([StoredWorkflowDocument.Unreadable(fault!)]);
        options ??= new CompilationOptions();
        var paths = HelperPathsInDocument.Collect(document.Definition, options.KeyShapes);
        if (paths.Count == 0)
            return Compile(document, shapes, signature, options.WithHelperChecks(new Dictionary<HelperPath, NamespacePathCheckAnswer>()), environment, cancellationToken: cancellationToken);
        IReadOnlyDictionary<HelperPath, NamespacePathCheckAnswer> checks;
        try
        {
            checks = await options.HelperResolver.CheckAsync(paths, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CompilationFaultException(new CompilationDiagnostic(CompilationRules.CompilationFault,
                $"The compiler could not finish its 'expression resolution' phase: {ex.GetType().Name}: {ex.Message}"), ex);
        }
        var checkedOptions = options.WithHelperChecks(checks);
        return Compile(document, shapes, signature, checkedOptions, environment, cancellationToken: cancellationToken);
    }

    internal static CompilationResult Compile(
        WorkflowDefinition definition, IBlockShapeSource shapes, CanvasSignature signature,
        CompilationOptions? options = null, NodePortEnvironment? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return StoredWorkflowDocument.ReadModel(definition, out var fault) is { } document
            ? Compile(document, shapes, signature, options, environment, cancellationToken: cancellationToken)
            : CompilationResult.Refused([StoredWorkflowDocument.Unreadable(fault!)]);
    }

    internal static CompilationResult Compile(
        StoredWorkflowDocument document,
        IBlockShapeSource shapes,
        CanvasSignature signature,
        CompilationOptions? options = null,
        NodePortEnvironment? environment = null,
        IReadOnlyList<CompilerPhase>? phases = null,
        Func<CanvasFrame, CompilationContext, CompiledCanvas>? lower = null,
        CancellationToken cancellationToken = default,
        Action<CompilationContext>? inspect = null)
    {
        var context = Run(document, shapes, signature, options, environment, phases, cancellationToken);
        inspect?.Invoke(context);

        var boundShapes = DistinctByBlockType(context.Bound.Values);
        if (context.Diagnostics.Count > 0)
            return CompilationResult.Refused(context.Diagnostics, boundShapes, context.Fault);

        var canvases = new List<CompiledCanvas>();
        var at = CanvasPath.Root;
        try
        {
            foreach (var frame in CanvasWalk.Frames(context.Document.Definition))
            {
                at = frame.Path;
                canvases.Add((lower ?? LowerCanvas)(frame, context));
            }
        }
        catch (Exception exception) when (CompilationFault.Catches(exception))
        {
            var diagnostic = CompilationFault.Diagnostic(LowerPhase, at, exception, out var cause);
            return CompilationResult.Refused([diagnostic], boundShapes, cause);
        }

        return CompilationResult.Compiled(new WorkflowProgram(canvases.AsReadOnly()), boundShapes);
    }

    internal static IReadOnlyDictionary<string, BlockShape> ShapesUsedBy(WorkflowDefinition definition, IBlockShapeSource shapes)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(shapes);

        shapes = BoundaryBlockShapes.Over(shapes);
        var bound = Prebind(definition, shapes, CancellationToken.None);
        var found = new List<BlockShape>();
        foreach (var frame in CanvasWalk.Frames(DeclaredBranches.Filled(definition, node => Declared(bound, node), (of, copy) => bound[copy] = bound[of])))
            foreach (var node in frame.Canvas.Nodes)
                if ((bound.TryGetValue(node, out var known) ? known : BindNode(node, shapes)) is { } shape)
                    found.Add(shape);

        return DistinctByBlockType(found);
    }

    internal static CompilationContext Run(
        WorkflowDefinition definition,
        IBlockShapeSource shapes,
        CanvasSignature signature,
        CompilationOptions? options = null,
        NodePortEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (StoredWorkflowDocument.ReadModel(definition, out var fault) is { } document)
            return Run(document, shapes, signature, options, environment);

        var refused = new CompilationContext(StoredWorkflowDocument.Empty, shapes, options ?? new CompilationOptions(), environment ?? NodePortEnvironment.Empty);
        refused.Diagnostics.Add(StoredWorkflowDocument.Unreadable(fault!));
        return refused;
    }

    internal static CompilationContext Run(
        StoredWorkflowDocument document,
        IBlockShapeSource shapes,
        CanvasSignature signature,
        CompilationOptions? options = null,
        NodePortEnvironment? environment = null,
        IReadOnlyList<CompilerPhase>? phases = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(signature);

        options = (options ?? new CompilationOptions()).Pinned();
        if (!options.HelperChecksFilled && HelperPathsInDocument.Collect(document.Definition, options.KeyShapes).Count > 0)
            throw new InvalidOperationException("Helper paths must be checked asynchronously before the synchronous compiler core runs.");
        Dictionary<WorkflowNodeDefinition, BlockShape?> bound;
        try
        {
            bound = Prebind(document.Definition, BoundaryBlockShapes.Over(shapes), cancellation);
        }
        catch (Exception exception) when (CompilationFault.Catches(exception))
        {
            var faulted = new CompilationContext(
                document, shapes, options ?? new CompilationOptions(), environment ?? NodePortEnvironment.Empty, cancellation);
            faulted.Diagnostics.Add(CompilationFault.Diagnostic(BindPhase, CanvasPath.Root, exception, out var cause));
            faulted.Fault = cause;
            return faulted;
        }

        document = document.WithDeclaredBranches(node => Declared(bound, node), (of, copy) => bound[copy] = bound[of]);
        var context = new CompilationContext(
            document, shapes, options ?? new CompilationOptions(), environment ?? NodePortEnvironment.Empty, cancellation, bound);

        context.Signatures[CanvasPath.Root] = signature;

        context.Ambiguous.UnionWith(AmbiguousPaths(document.Definition));
        foreach (var phase in phases ?? Phases)
        {
            foreach (var frame in CanvasWalk.Frames(document.Definition))
            {
                if (context.Ambiguous.Contains(frame.Path))
                    continue;

                context.Cancellation.ThrowIfCancellationRequested();
                try
                {
                    context.FaultAt = null;
                    phase.Run(frame, context);
                }
                catch (Exception exception) when (CompilationFault.Catches(exception))
                {
                    context.Diagnostics.Add(CompilationFault.Diagnostic(phase.Name, context.FaultAt ?? frame.Path, exception, out var cause));
                    context.Fault = cause;
                    return context;
                }
            }
        }

        return context;
    }

    private static HashSet<CanvasPath> AmbiguousPaths(WorkflowDefinition definition)
    {
        var paths = CanvasWalk.Frames(definition).Select(f => f.Path).ToList();
        var shared = paths.CountBy(p => p).Where(c => c.Value > 1).Select(c => c.Key).ToHashSet();
        var ambiguous = new HashSet<CanvasPath>();
        foreach (var path in paths)
        {
            if (shared.Contains(path) || (!path.IsRoot && ambiguous.Contains(path.Parent)))
                ambiguous.Add(path);
        }

        return ambiguous;
    }

    private static void BindTypes(CanvasFrame frame, CompilationContext context) =>
        new BindingPass(context, context.Ambiguous).Visit(frame);

    internal static IEnumerable<(string TargetInput, string Source)> ExpressionSources(WorkflowDefinition canvas, WorkflowNodeDefinition node)
    {
        var literalValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var entry in EffectivePorts.MappingsInto(canvas, node.Id))
        {
            if (entry.Mapping.IsLiteral)
                literalValues[entry.Mapping.TargetInput] = entry.Mapping.LiteralValue as string;
        }

        if (node.Config is not null)
        {
            foreach (var (key, value) in node.Config)
            {
                if (!MergeWouldForwardConfigKey(node, key) || literalValues.ContainsKey(key))
                    continue;

                if (value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } text
                    && ExpressionSource.IsExpression(text))
                {
                    yield return (key, ExpressionSource.Strip(text));
                }
            }
        }

        foreach (var targetInput in literalValues.Keys.Order(StringComparer.Ordinal))
        {
            if (literalValues[targetInput] is { } literal
                && ExpressionSource.IsExpression(literal))
            {
                yield return (targetInput, ExpressionSource.Strip(literal));
            }
        }
    }

    private static void ReportParameterVocabulary(CanvasSignature signature, CompilationContext context)
    {
        foreach (var (ports, side) in ((IReadOnlyList<PortShape>, PortTypeNameSite)[])
                 [(signature.Inputs, PortTypeNameSite.InputParameter), (signature.Outputs, PortTypeNameSite.OutputParameter)])
        {
            for (var i = 0; i < ports.Count; i++)
            {
                var site = new StoredPortTypeSite(side, "", [], null, ports[i].Name, i, ports[i].Type.Name, ports[i].IsRequired);
                if (site.Error is not null)
                    context.Diagnostics.Add(PortTypeNameSites.Diagnostic(site, blockSource: null));
                else if (PortTypeNameSites.Graded(site, context.Options.Vocabulary.Table, blockSource: null) is { } diagnostic)
                    context.Diagnostics.Add(diagnostic);
            }
        }
    }

    private static void Bind(CanvasFrame frame, CompilationContext context)
    {
        if (frame.Path.IsRoot && context.Signatures.TryGetValue(CanvasPath.Root, out var parameters))
            ReportParameterVocabulary(parameters, context);

        foreach (var node in frame.Canvas.Nodes)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var shape = context.Prebound.TryGetValue(node, out var known)
                ? known
                : CompilationFault.AtNode(frame.Path.Segments, node.Id, () => BindNode(node, context.Shapes));
            foreach (var site in context.Document.SitesOf(node))
            {
                if (site.Error is not null)
                    context.Diagnostics.Add(PortTypeNameSites.Diagnostic(site, shape?.Source));
                else if (PortTypeNameSites.Graded(site, context.Options.Vocabulary.Table, shape?.Source) is { } refused)
                    context.Diagnostics.Add(refused);
                else if (site.Site == PortTypeNameSite.DeclaredOutput
                         && BindingPass.HoldsCollectedJson(shape?.Flags.DeclaredOutputsCollect == true, site.RawType))
                    context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.PortTypeJsonPosition,
                        $"Port type '{site.PortName}' contains Json, which is not allowed here.",
                        node.Id, site.PortName, frame.Path.Segments, shape?.Source));
            }

            if (shape is null)
            {
                context.Diagnostics.Add(new CompilationDiagnostic(
                    CompilationRules.BlockTypeUnknown,
                    $"Block type '{node.BlockTypeId}' is not registered.",
                    node.Id, CanvasPath: frame.Path.Segments));
                continue;
            }

            ReportDeclaredVocabulary(frame, node, shape, context);

            if (node.TypeArguments is { } typeArguments)
                foreach (var name in typeArguments.Keys)
                    if (!shape.TypeParameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal)))
                        context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.TypeArgumentUnknown,
                            $"Type argument '{name}' names no type parameter of block '{shape.BlockTypeId}'.",
                            node.Id, name, frame.Path.Segments, shape.Source));

            if (EffectivePorts.TrustworthyResolution(
                    frame, node, shape, context.Environment, context.NodesById(frame.Canvas)) is { } resolvedSignature
                && resolvedSignature.Inputs.Select(p => p.Name).Concat(resolvedSignature.Outputs.Select(p => p.Name))
                    .FirstOrDefault(name => name.Contains(".", StringComparison.Ordinal)) is { } dottedPort)
            {
                context.Diagnostics.Add(new CompilationDiagnostic(
                    CompilationRules.DottedName,
                    $"The resolved node signature declares name '{dottedPort}' containing a dot.",
                    node.Id, CanvasPath: frame.Path.Segments, BlockSource: shape.Source));
                continue;
            }

            context.Bound[new NodeKey(frame.Path, node.Id)] = shape;
        }
    }

    private static void ReportDeclaredVocabulary(CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context)
    {
        var table = context.Options.Vocabulary.Table;
        if (!context.DeclaredFaults.TryGetValue(shape, out var faults))
            context.DeclaredFaults[shape] = faults = [.. BlockDeclarationRules.Ungraded(shape, table)];

        foreach (var fault in faults)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(
                fault.Rule, $"Block '{shape.BlockTypeId}' {fault.Where} has the type '{fault.Term}'{fault.What}.",
                node.Id, fault.PortName, frame.Path.Segments, shape.Source));
        }
    }

    private static Dictionary<WorkflowNodeDefinition, BlockShape?> Prebind(
        WorkflowDefinition definition, IBlockShapeSource shapes, CancellationToken cancellation)
    {
        var bound = new Dictionary<WorkflowNodeDefinition, BlockShape?>(ReferenceEqualityComparer.Instance);
        foreach (var frame in CanvasWalk.Frames(definition))
        {
            foreach (var node in frame.Canvas.Nodes)
            {
                cancellation.ThrowIfCancellationRequested();
                bound[node] = CompilationFault.AtNode(frame.Path.Segments, node.Id, () => BindNode(node, shapes));
            }
        }

        return bound;
    }

    private static DeclaredBranches.Declared? Declared(Dictionary<WorkflowNodeDefinition, BlockShape?> bound, WorkflowNodeDefinition node) =>
        bound.GetValueOrDefault(node) is { SubCanvasNames.Count: > 0 } shape
            ? new(shape.SubCanvasNames, name => shape.SubCanvasOutputs.TryGetValue(name, out var ports) && ports.Count > 0)
            : null;

    private static BlockShape? BindNode(WorkflowNodeDefinition node, IBlockShapeSource shapes) => shapes.Find(node.BlockTypeId);

    private static void CheckStructure(CanvasFrame frame, CompilationContext context)
    {
        CheckEdgeEndpoints(frame, context);
        CheckNodeLogBounds(frame, context);
        var seenPairs = new HashSet<(string, string)>();
        if (frame.Canvas.GetConnections()?.FirstOrDefault(e => !seenPairs.Add((e.SourceNodeId, e.TargetNodeId))) is { } repeated)
            context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.DuplicateEdgePair,
                $"More than one connection joins '{repeated.SourceNodeId}' to '{repeated.TargetNodeId}'.",
                CanvasPath: frame.Path.Segments));

        if (DuplicateNodeId(frame.Canvas) is { } duplicateId)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(
                CompilationRules.DuplicateNodeId,
                $"Workflow definition contains a duplicate node id '{duplicateId}'.",
                duplicateId, CanvasPath: frame.Path.Segments));
            return;
        }

        try
        {
            if (frame.Path.IsRoot)
                WorkflowGraphHelpers.ValidateRootCanvas(frame.Canvas);
            else
                WorkflowGraphHelpers.ValidateNoDuplicateInputMappings(frame.Canvas);
        }
        catch (WorkflowValidationException ex)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(
                ClassifyStructure(ex.Message), ex.Message, CanvasPath: frame.Path.Segments));
        }

        try
        {
            context.Order[frame.Path] = WorkflowGraphHelpers.TopologicalSort(frame.Canvas);
        }
        catch (WorkflowValidationException ex)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(
                ClassifyStructure(ex.Message), ex.Message, CanvasPath: frame.Path.Segments));
        }
    }

    private static void CheckNodeLogBounds(CanvasFrame frame, CompilationContext context)
    {
        const int limit = WorkflowGraphHelpers.NodeLogTextLimit;

        foreach (var node in frame.Canvas.Nodes)
        {
            context.Cancellation.ThrowIfCancellationRequested();

            if (node.Id.Length > limit)
            {
                context.Diagnostics.Add(new CompilationDiagnostic(
                    CompilationRules.NodeIdTooLong,
                    $"Node id '{node.Id}' is {node.Id.Length} characters long, and the node log's NodeId column "
                    + $"holds {limit}: the run writes every node it enters there, so shorten the node's id.",
                    node.Id, CanvasPath: frame.Path.Segments));
            }

            if (node.BlockTypeId.Length > limit)
            {
                context.Diagnostics.Add(new CompilationDiagnostic(
                    CompilationRules.BlockTypeIdTooLong,
                    $"Node '{node.Id}' has block type '{node.BlockTypeId}', whose id is "
                    + $"{node.BlockTypeId.Length} characters long, and the node log's NodeType column holds "
                    + $"{limit}: the run writes every block type it enters there, so the block type's id must fit.",
                    node.Id, CanvasPath: frame.Path.Segments));
            }
        }
    }

    private static void CheckEdgeEndpoints(CanvasFrame frame, CompilationContext context)
    {
        var nodesById = context.NodesById(frame.Canvas);

        foreach (var connection in frame.Canvas.GetConnections() ?? [])
        {
            if (!nodesById.ContainsKey(connection.SourceNodeId))
            {
                context.Diagnostics.Add(new CompilationDiagnostic(
                    CompilationRules.EdgeEndpointUnknown,
                    $"Connection '{connection.Id}' names source node '{connection.SourceNodeId}', which does not exist on this canvas.",
                    connection.SourceNodeId, CanvasPath: frame.Path.Segments));
            }

            if (!nodesById.ContainsKey(connection.TargetNodeId))
            {
                context.Diagnostics.Add(new CompilationDiagnostic(
                    CompilationRules.EdgeEndpointUnknown,
                    $"Connection '{connection.Id}' names target node '{connection.TargetNodeId}', which does not exist on this canvas.",
                    connection.TargetNodeId, CanvasPath: frame.Path.Segments));
            }
        }
    }

    private static void CheckNodes(CanvasFrame frame, CompilationContext context)
    {
        var nodesById = context.NodesById(frame.Canvas);

        foreach (var node in frame.Canvas.Nodes)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var nodeKey = new NodeKey(frame.Path, node.Id);
            if (!context.Bound.TryGetValue(nodeKey, out var shape))
                continue;

            if (PortTypeNameSites.HasMalformedStoredPortType(node, shape))
                context.PortsUnresolved.Add(nodeKey);

            if (shape.Flags.PortsAreResolvedElsewhere)
            {
                if (shape.RunsWorkflowFrom is { } referenceInput
                    && EffectivePorts.IsReferenceInputMapped(frame.Canvas, node.Id, referenceInput))
                {
                    context.Diagnostics.Add(new CompilationDiagnostic(
                        CompilationRules.ReferenceInputMapped,
                        $"Node {context.NodeName(frame.Canvas, node.Id)} selects its callee through '{referenceInput}' by a fixed "
                        + "configuration value only; a mapping into it — literal or wired — is refused.",
                        node.Id, CanvasPath: frame.Path.Segments, BlockSource: shape.Source));
                    context.PortsUnresolved.Add(nodeKey);
                }
                else if (RefuseUnresolvedPorts(frame, node, shape, context, nodesById) is { } unresolved)
                {
                    context.Diagnostics.Add(unresolved);
                    context.PortsUnresolved.Add(nodeKey);
                }
                else if (RefuseAnsweredTerms(frame, node, shape, context, nodesById))
                {
                    context.PortsUnresolved.Add(nodeKey);
                }
            }

            foreach (var field in shape.ConfigurationFields.Where(f => f.Required))
            {
                if (!IsConfigValueFilled(node, field.Name))
                {
                    context.Diagnostics.Add(new CompilationDiagnostic(
                        CompilationRules.RequiredConfigMissing,
                        $"Configuration field '{field.Name}' of node {context.NodeName(frame.Canvas, node.Id)} is required but has no value.",
                        node.Id, field.Name, frame.Path.Segments, shape.Source));
                }
            }

            var configSites = ConfigSites.Of(frame, node, shape, context);
            foreach (var field in shape.ConfigurationFields)
            {
                if (node.Config is null || !node.Config.TryGetValue(field.Name, out var raw)
                    || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    || IsExpressionText(raw.ValueKind == JsonValueKind.String ? raw.GetString() : null))
                    continue;
                if (field.Type == ConfigFieldType.Enum && raw.ValueKind == JsonValueKind.String && raw.GetString() == string.Empty)
                    continue;
                if (field.Type == ConfigFieldType.Enum)
                {
                    var allowed = field.AllowedValues ?? [];
                    if (raw.ValueKind != JsonValueKind.String || !allowed.Contains(raw.GetString(), StringComparer.Ordinal))
                    {
                        context.Diagnostics.Add(new CompilationDiagnostic(
                            CompilationRules.LiteralPortValueInvalid,
                            $"Configuration field '{field.Name}' of {context.NodeName(frame.Canvas, node.Id)} must be a string equal to one of "
                            + $"{string.Join(", ", allowed.Select(name => "'" + name + "'"))}; found {(raw.ValueKind == JsonValueKind.String ? "'" + raw.GetString() + "'" : raw.ValueKind.ToString())}.",
                            node.Id, field.Name, frame.Path.Segments, shape.Source));
                    }

                    continue;
                }

                if (configSites.GradedAsSite(field.Name))
                    continue;
                var type = field.Type switch
                {
                    ConfigFieldType.Int => PortValueType.Int,
                    ConfigFieldType.Number => PortValueType.Number,
                    ConfigFieldType.Boolean => PortValueType.Boolean,
                    _ => PortValueType.String,
                };
                if (!context.Options.Vocabulary.IsLiteralOf(raw, type))
                {
                    context.Diagnostics.Add(new CompilationDiagnostic(
                        CompilationRules.LiteralPortValueInvalid,
                        $"Configuration field '{field.Name}' of {context.NodeName(frame.Canvas, node.Id)} cannot be read as {type.Name}.",
                        node.Id, field.Name, frame.Path.Segments, shape.Source));
                }
            }

            CheckResilienceConfig(frame, node, shape, context);
            CheckErrorPolicy(frame, node, shape, context);

            CheckUndeclaredPortShapes(frame, node, shape, context);

            foreach (var declared in node.OutputPorts ?? [])
            {
                if (shape.Outputs.Any(o => string.Equals(o.Name, declared.Name, StringComparison.Ordinal)))
                {
                    context.Diagnostics.Add(new CompilationDiagnostic(
                        CompilationRules.DeclaredOutputShadowsStatic,
                        $"Declared output '{declared.Name}' on node {context.NodeName(frame.Canvas, node.Id)} shadows a static output of the block.",
                        node.Id, declared.Name, frame.Path.Segments, shape.Source));
                }
            }

            foreach (var name in IsSigned(frame.Path, context) ? node.SubCanvases?.Keys ?? Enumerable.Empty<string>() : [])
            {
                if (!shape.SubCanvasNames.Contains(name, StringComparer.Ordinal))
                {
                    context.Diagnostics.Add(new CompilationDiagnostic(
                        CompilationRules.SubCanvasNameUnknown,
                        $"Block type '{shape.BlockTypeId}' does not declare a sub-canvas named '{name}'.",
                        node.Id, CanvasPath: frame.Path.Segments, BlockSource: shape.Source));
                }
            }
        }
    }

    internal static bool IsSigned(CanvasPath path, CompilationContext context) =>
        path.IsRoot
        || (IsSigned(path.Parent, context)
            && context.Bound.TryGetValue(new NodeKey(path.Parent, path.Segments[^1].NodeId), out var owner)
            && owner.SubCanvasNames.Contains(path.Segments[^1].BranchName, StringComparer.Ordinal));

    private static void CheckUndeclaredPortShapes(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context)
    {
        if (!shape.Flags.HasDynamicPorts)
        {
            foreach (var key in (string[])[StoredDocumentSchema.InputPortsKey, StoredDocumentSchema.OutputPortsKey])
            {
                if (StoredWorkflowDocument.PortList(node, key).Count == 0)
                    continue;

                context.Diagnostics.Add(new CompilationDiagnostic(
                    CompilationRules.PortListWithoutDynamicPorts,
                    $"Node {context.NodeName(frame.Canvas, node.Id)} holds a port list under configuration '{key}', and block type '{shape.BlockTypeId}' declares no dynamic ports.",
                    node.Id, key, frame.Path.Segments, shape.Source));
            }
        }

        if (node.OutputPorts is not { Count: > 0 } declared)
            return;

        if (!shape.Flags.SupportsDeclaredOutputs)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(
                CompilationRules.DeclaredOutputsUnsupported,
                $"Node {context.NodeName(frame.Canvas, node.Id)} declares outputs, and block type '{shape.BlockTypeId}' supports none.",
                node.Id, CanvasPath: frame.Path.Segments, BlockSource: shape.Source));
            return;
        }

        foreach (var port in declared)
        {
            foreach (var branch in (IEnumerable<string>?)port.Sources?.Keys ?? [])
            {
                if (shape.SubCanvasNames.Contains(branch, StringComparer.Ordinal))
                    continue;

                context.Diagnostics.Add(new CompilationDiagnostic(
                    CompilationRules.DeclaredOutputSourceUnknown,
                    $"Declared output '{port.Name}' on node {context.NodeName(frame.Canvas, node.Id)} takes a source from '{branch}', which is no sub-canvas of block type '{shape.BlockTypeId}'.",
                    node.Id, port.Name, frame.Path.Segments, shape.Source));
            }
        }
    }

    private static CompilationDiagnostic? RefuseUnresolvedPorts(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        var reason = EffectivePorts.UnresolvedReason(frame, node, shape, context.Environment, nodesById);
        var message = (reason, shape.RunsWorkflowFrom) switch
        {
            (null, _) => null,
            (EffectivePorts.PortsUnresolvedReason.NoEffectiveReference, var referenceInput) =>
                $"Node {context.NodeName(frame.Canvas, node.Id)} names no callee a resolver could ever answer for; its reference input "
                + $"'{referenceInput}' is not configured with a non-empty string.",
            _ => UnresolvedResolutionMessage(frame, node, context),
        };

        return message is null
            ? null
            : new CompilationDiagnostic(
                CompilationRules.NodePortsUnresolved, message, node.Id, CanvasPath: frame.Path.Segments, BlockSource: shape.Source);
    }

    private static bool RefuseAnsweredTerms(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, CompilationContext context,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        if (EffectivePorts.TrustworthyResolution(frame, node, shape, context.Environment, nodesById) is not { } answer)
            return false;
        var refused = false;
        foreach (var (ports, position, side) in ((IReadOnlyList<PortShape>, TermPosition, string)[])
                 [(answer.Inputs, TermPosition.Input, "Input"), (answer.Outputs, TermPosition.Output, "Output")])
        {
            foreach (var port in ports)
            {
                if (PortTermGrading.Grade(port.Type.Name, TermSeam.Resolver, position, context.Options.Vocabulary.Table, out var refusal) is not null
                    || refusal is not { } why)
                    continue;
                refused = true;
                context.Diagnostics.Add(new CompilationDiagnostic(why.Rule,
                    $"{side} '{port.Name}' of node {context.NodeName(frame.Canvas, node.Id)}, as the resolver for block type '{shape.BlockTypeId}' answers it, {why.What}.",
                    node.Id, port.Name, frame.Path.Segments, shape.Source));
            }
        }

        if (refused)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.NodePortsUnresolved,
                $"Node {context.NodeName(frame.Canvas, node.Id)} has no ports the compiler can trust: the resolver for block type '{shape.BlockTypeId}' answers a type it refuses.",
                node.Id, CanvasPath: frame.Path.Segments, BlockSource: shape.Source));
        }

        return refused;
    }

    private static string UnresolvedResolutionMessage(CanvasFrame frame, WorkflowNodeDefinition node, CompilationContext context)
    {
        var resolution = context.Environment.For(frame.Path, node.Id);
        return resolution is { FailureReason: { } failure }
            ? $"Node {context.NodeName(frame.Canvas, node.Id)} has ports a resolver claimed for '{node.BlockTypeId}' and could not describe: {failure}"
            : $"Node {context.NodeName(frame.Canvas, node.Id)} has ports that only a resolver can describe, and none answered for it.";
    }

    internal static HashSet<string> WiredInputNames(
        WorkflowDefinition canvas, string nodeId, IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in EffectivePorts.MappingsInto(canvas, nodeId))
        {
            if (entry.Mapping.IsLiteral || nodesById.ContainsKey(entry.SourceNodeId))
                names.Add(entry.Mapping.TargetInput);
        }

        return names;
    }

    internal static bool MergeWouldForwardConfigKey(WorkflowNodeDefinition node, string portName) =>
        node.Config?.ContainsKey(portName) == true
        && !WorkflowGraphExecutor.PollyConfigKeys.Contains(portName)
        && !portName.StartsWith(WorkflowGraphExecutor.DesignTimeConfigPrefix, StringComparison.Ordinal);

    internal static bool IsExpressionText(string? text) =>
        ExpressionSource.IsExpression(text);

    internal static bool IsConfigValueFilled(WorkflowNodeDefinition node, string fieldName) =>
        node.Config is not null
        && node.Config.TryGetValue(fieldName, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
        && (value.ValueKind != JsonValueKind.String || value.GetString() != string.Empty);

    internal static bool Sorts(WorkflowDefinition canvas)
    {
        if (DuplicateNodeId(canvas) is not null)
            return false;

        try
        {
            WorkflowGraphHelpers.TopologicalSort(canvas);
            return true;
        }
        catch (WorkflowValidationException)
        {
            return false;
        }
    }

    private static string? DuplicateNodeId(WorkflowDefinition canvas)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in canvas.Nodes)
        {
            if (!seen.Add(node.Id))
                return node.Id;
        }

        return null;
    }

    private static string ClassifyStructure(string message) => message switch
    {
        var m when m.Contains("contains a cycle") => CompilationRules.Cycle,
        var m when m.Contains("has no start node") => CompilationRules.NoStartNode,
        var m when m.Contains("cannot have incoming connections") => CompilationRules.InputHasIncoming,
        var m when m.Contains("no incoming connection") => CompilationRules.ExtraRoot,
        var m when m.Contains("not reachable") => CompilationRules.Unreachable,
        var m when m.Contains("multiple parents") => CompilationRules.DuplicateInputMapping,
        _ => CompilationRules.Structure,
    };

    private static IReadOnlyDictionary<string, BlockShape> DistinctByBlockType(IEnumerable<BlockShape> shapes)
    {
        var byId = new Dictionary<string, BlockShape>(StringComparer.OrdinalIgnoreCase);
        foreach (var shape in shapes)
            byId[shape.BlockTypeId] = shape;
        return byId;
    }
}

internal sealed record CompilerPhase(string Name, Action<CanvasFrame, CompilationContext> Run);
