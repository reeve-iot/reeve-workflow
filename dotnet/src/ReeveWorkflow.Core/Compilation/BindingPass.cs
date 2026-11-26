using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.JsonPath;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal enum PortState
{
    Typed,
    Unbound,
    Refused,
}

internal sealed record EffectivePort(
    string Name, PortState State, PortTerm? Term, bool IsRequired, IReadOnlyList<string> UnboundParameters, string? Declared = null)
{
    public static EffectivePort Typed(string name, PortTerm term, bool isRequired, string? declared = null) =>
        new(name, PortState.Typed, term, isRequired, [], declared);

    public static EffectivePort Unbound(string name, IReadOnlyList<string> parameters, bool isRequired, string? declared = null) =>
        new(name, PortState.Unbound, null, isRequired, parameters, declared);

    public static EffectivePort Refused(string name, bool isRequired, string? declared = null) =>
        new(name, PortState.Refused, null, isRequired, [], declared);

    public PortValueType Type => PortValueType.Of(Term!.ToString());
}

internal sealed record NodeTypes(
    IReadOnlyList<EffectivePort> Inputs, IReadOnlyList<EffectivePort>? Outputs, IReadOnlyDictionary<string, PortTerm?> Bindings,
    IReadOnlyDictionary<string, TypedExpression> Expressions,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, PortTerm>> ScopeVariables,
    IReadOnlyDictionary<string, PortTerm?> Projections)
{
    public IEnumerable<EffectivePort> RequiredInputs => Inputs.Where(p => p.IsRequired);
}

internal sealed class FrameTypes(IReadOnlyList<EffectivePort>? inputs, IReadOnlyList<FrameOutput>? outputs)
{
    public IReadOnlyList<EffectivePort>? Inputs { get; } = inputs;

    public IReadOnlyList<FrameOutput>? Outputs { get; } = outputs;

    public List<DeferredSite> Deferred { get; } = [];
}

internal sealed record FrameOutput(EffectivePort Port, PortTerm? Deferred = null);

internal sealed record DeferredSite(CanvasPath Path, string NodeId, string Port, BindingSite Site, string? BlockSource);

internal enum SiteKind
{
    Wire,
    Literal,
    Config,
    Expression,
    Projection,
}

internal sealed record BindingSite(
    SiteKind Kind, PortTerm? Term = null, object? Value = null, string? SourceNode = null, string? SourceOutput = null,
    ProjectionSite? Projection = null);

internal sealed record ProjectionSite(JsonPathQuery Query, bool JsonText, PortTerm? Type, bool IntoWorkflowOutput);

internal sealed class BindingPass(CompilationContext context, HashSet<CanvasPath> ambiguous)
{
    private readonly WorkflowTypeVocabulary vocabulary = context.Options.Vocabulary;

    public void Visit(CanvasFrame frame)
    {
        if (context.Visited.Contains(frame.Path) || ambiguous.Contains(frame.Path))
            return;

        if (context.Signatures.TryGetValue(frame.Path, out var root))
        {
            VisitCanvas(frame, new FrameTypes(
                [.. root.Inputs.Select(p => Closed(p.Name, p.Type.Name, TermSeam.Stored, TermPosition.Input, p.IsRequired))],
                [.. root.Outputs.Select(p => new FrameOutput(Closed(p.Name, p.Type.Name, TermSeam.Stored, TermPosition.Output, p.IsRequired)))]));
        }
        else
        {
            VisitCanvas(frame, new FrameTypes(null, null));
        }
    }

    private void VisitCanvas(CanvasFrame frame, FrameTypes types)
    {
        var outer = context.FaultAt;
        context.FaultAt = frame.Path;
        VisitNodes(frame, types);
        context.FaultAt = outer;
    }

    private void VisitNodes(CanvasFrame frame, FrameTypes types)
    {
        context.Visited.Add(frame.Path);
        context.Frames[frame.Path] = types;
        if (!context.Order.TryGetValue(frame.Path, out var order))
        {
            foreach (var output in (types.Outputs ?? []).Where(o => o.Deferred is not null))
                types.Deferred.Add(new DeferredSite(frame.Path, string.Empty, output.Port.Name, new BindingSite(SiteKind.Wire), null));
            return;
        }

        var nodesById = context.NodesById(frame.Canvas);
        var scope = CompilationScope.Over(order, nodesById);
        context.Scopes[frame.Path] = scope;
        foreach (var node in frame.Canvas.Nodes.Where(n => n.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId))
            scope.Publish(node.Id, types.Inputs);

        foreach (var nodeId in order)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            if (nodesById.TryGetValue(nodeId, out var node))
                VisitNode(frame, node, scope, types, nodesById);
        }
    }

    private sealed record InputDecl(
        string Name, PortTerm? Declared, bool IsRequired, EffectivePort? Given = null, PortTerm? Deferred = null,
        bool IsExpression = false, IReadOnlyList<ExpressionScopeVariable>? ScopeVariables = null, string? Text = null);

    private void VisitNode(
        CanvasFrame frame, WorkflowNodeDefinition node, CompilationScope scope, FrameTypes frameTypes,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        var key = new NodeKey(frame.Path, node.Id);
        if (!context.Bound.TryGetValue(key, out var shape))
            return;

        var path = frame.Path.Segments;
        var isOutputNode = node.BlockTypeId == WorkflowGraphHelpers.WorkflowOutputBlockTypeId;
        var isInputNode = node.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId;
        var boundaryUnknown = (isOutputNode && frameTypes.Outputs is null) || (isInputNode && frameTypes.Inputs is null);
        var unresolved = context.PortsUnresolved.Contains(key) || boundaryUnknown;
        var checks = WorkflowCompiler.IsSigned(frame.Path, context);

        var decls = InputDecls(frame, node, shape, frameTypes);
        var configSites = ConfigSites.Of(frame, node, shape, context);
        var all = EffectivePorts.MappingsInto(frame.Canvas, node.Id).ToList();
        var mappings = all.Where(m => context.Bound.ContainsKey(new NodeKey(frame.Path, m.SourceNodeId))).ToList();
        var last = new Dictionary<string, (string Source, WorkflowMappingDefinition Mapping)>(StringComparer.Ordinal);
        foreach (var (source, mapping) in all)
            last[mapping.TargetInput] = (source, mapping);

        var sites = new Dictionary<string, List<BindingSite>>(StringComparer.Ordinal);
        void AddSite(string input, BindingSite site)
        {
            if (!sites.TryGetValue(input, out var list))
                sites[input] = list = [];
            list.Add(site);
        }

        var wireRefused = new HashSet<string>(StringComparer.Ordinal);
        var projections = new Dictionary<string, PortTerm?>(StringComparer.Ordinal);
        foreach (var (sourceId, mapping) in mappings)
        {
            var input = mapping.TargetInput;
            projections.Remove(input);
            if (mapping.IsLiteral)
            {
                if (checks && mapping.Projection is not null)
                    RefuseProjection(frame, node, shape, input, $"The literal mapping into input '{input}' of {Named(frame, node.Id)} holds a projection; only a wire from a Json port takes one.");
                if (!WorkflowCompiler.IsExpressionText(mapping.LiteralValue as string))
                    AddSite(input, new BindingSite(SiteKind.Literal, Value: mapping.LiteralValue));
                continue;
            }

            var projected = mapping.Projection is { } stored
                ? ReadProjection(frame, node, shape, decls, sourceId, mapping, stored, isOutputNode, checks)
                : null;
            if (mapping.Projection is not null && projected is null)
            {
                wireRefused.Add(input);
                continue;
            }

            if (SourcePort(frame, sourceId, mapping.SourceOutput, nodesById) is not { } port)
            {
                wireRefused.Add(input);
                continue;
            }

            switch (port.State)
            {
                case PortState.Typed when ProjectionTerms.IsJson(port.Term!) != (projected is not null):
                    wireRefused.Add(input);
                    if (checks)
                        RefuseProjection(frame, node, shape, input, projected is null
                            ? $"Output '{mapping.SourceOutput}' of {Named(frame, sourceId)} carries {port.Term}, so the wire into input '{input}' of {Named(frame, node.Id)} needs a projection."
                            : $"Output '{mapping.SourceOutput}' of {Named(frame, sourceId)} carries {port.Term}, not Json, so the wire into input '{input}' of {Named(frame, node.Id)} takes no projection.");
                    break;
                case PortState.Typed when projected is not null:
                    projections[input] = projected.Type;
                    AddSite(input, new BindingSite(SiteKind.Projection, port.Term, SourceNode: sourceId, SourceOutput: mapping.SourceOutput, Projection: projected));
                    break;
                case PortState.Typed:
                    AddSite(input, new BindingSite(SiteKind.Wire, port.Term, SourceNode: sourceId, SourceOutput: mapping.SourceOutput));
                    break;
                case PortState.Unbound:
                    wireRefused.Add(input);
                    if (checks)
                        context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.TypeParameterUnbound,
                        $"Output '{mapping.SourceOutput}' of {Named(frame, sourceId)} has a type naming the type parameter '{port.UnboundParameters[0]}', "
                        + $"which nothing binds, so input '{input}' of {Named(frame, node.Id)} cannot read it.",
                        node.Id, input, path, shape.Source));
                    break;
                default:
                    wireRefused.Add(input);
                    break;
            }
        }

        foreach (var (sourceId, mapping) in checks ? mappings : [])
        {
            if (!mapping.IsLiteral)
                ReportUnknownSourceOutput(frame, sourceId, mapping.SourceOutput, nodesById);
        }

        var expressions = new Dictionary<string, TypedExpression?>(StringComparer.Ordinal);
        var scoped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (target, source) in WorkflowCompiler.ExpressionSources(frame.Canvas, node))
        {
            if (decls.FirstOrDefault(d => d.Name == target) is { IsExpression: true, ScopeVariables.Count: > 0 })
            {
                scoped[target] = source;
                continue;
            }

            expressions[target] = ExpressionResolution.Type(
                node.Id, Named(frame, node.Id), path, source, scope, frameTypes.Inputs, context.Options, context.Diagnostics);
        }

        foreach (var decl in decls)
        {
            if (expressions.TryGetValue(decl.Name, out var result))
                AddSite(decl.Name, new BindingSite(SiteKind.Expression, result?.Term));
            if (configSites.IsSite(decl.Name) && ConfigValue(node, shape, decl.Name) is { } config)
                AddSite(decl.Name, new BindingSite(SiteKind.Config, Value: config));
        }

        var bodyBound = BlockDeclarationRules.BodyBound(shape);
        var states = new Dictionary<string, ParameterState>(StringComparer.Ordinal);
        var evidence = new List<ParameterEvidence>();
        foreach (var decl in decls.Where(d => d.Declared is { ContainsParameter: true }))
        {
            foreach (var site in sites.GetValueOrDefault(decl.Name) ?? [])
                Match(decl.Declared!, site, evidence);
        }

        var refusedArguments = TypeArguments(node, shape, evidence);
        var tainted = new HashSet<string>(decls.Where(d => d.Declared is not null && wireRefused.Contains(d.Name))
            .SelectMany(d => TypeParameterBinding.Parameters(d.Declared!)), StringComparer.Ordinal);
        foreach (var parameter in shape.TypeParameters.Where(p => !bodyBound.Contains(p.Name)))
            states[parameter.Name] = Resolve(frame, node, shape, parameter, evidence, refusedArguments, tainted);

        var inputs = new List<EffectivePort>();
        foreach (var decl in decls)
            inputs.Add(decl.Given ?? (decl.Declared is { } declared
                ? Substituted(decl.Name, declared, decl.IsRequired, states)
                : EffectivePort.Refused(decl.Name, decl.IsRequired, decl.Text ?? string.Empty)));
        var scopeTerms = new Dictionary<string, IReadOnlyDictionary<string, PortTerm>>(StringComparer.Ordinal);
        foreach (var (target, source) in scoped)
        {
            var decl = decls.First(d => d.Name == target);
            var variables = new Dictionary<string, EffectivePort>(StringComparer.Ordinal);
            foreach (var declared in decl.ScopeVariables!)
            {
                variables[declared.Name] = Read(declared.Type, TermSeam.Declaration, TermPosition.Input) is { } term
                    ? Substituted(declared.Name, term, false, states)
                    : EffectivePort.Refused(declared.Name, false);
            }

            scopeTerms[target] = variables.Where(v => v.Value.State == PortState.Typed).ToDictionary(v => v.Key, v => v.Value.Term!, StringComparer.Ordinal);
            expressions[target] = ExpressionResolution.Type(
                node.Id, Named(frame, node.Id), path, source, scope, frameTypes.Inputs, context.Options, context.Diagnostics,
                name => variables.TryGetValue(name, out var variable)
                    ? ExpressionResolution.Port(variable, optional: false, vocabulary, $"scope variable '{name}' of input '{target}'")
                    : ReadLookup.Missing($"input '{target}' of {Named(frame, node.Id)} declares no scope variable '{name}'"));
            AddSite(target, new BindingSite(SiteKind.Expression, expressions[target]?.Term));
        }

        var claimed = new HashSet<string>(inputs.Select(p => p.Name), StringComparer.Ordinal);
        foreach (var name in node.Passthrough ?? [])
        {
            if (claimed.Add(name) && PassthroughInput(frame, node, shape, name, last, expressions, nodesById, checks) is { } port)
                inputs.Add(port);
        }

        foreach (var name in checks ? (node.Passthrough ?? []).Distinct() : [])
        {
            if (shape.Inputs.Any(p => p.Name == name && p.SecretValues))
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.SecretInputPassthrough,
                    $"Input '{name}' of node {Named(frame, node.Id)} holds secret values and cannot be forwarded by passthrough.", node.Id, name, path, shape.Source));
        }

        foreach (var port in checks ? shape.Inputs.Where(p => p.SecretValues) : [])
        {
            foreach (var site in sites.GetValueOrDefault(port.Name) ?? [])
            {
                if (site.Kind is SiteKind.Config or SiteKind.Literal && InvalidKeyRow(site.Value) is { } row)
                    context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.SecretInputKeyInvalid,
                        $"Input '{port.Name}' of node {Named(frame, node.Id)} has {FieldNames.Naming(row.Key, row.Row)} that is not a field name; a name is a token.",
                        node.Id, port.Name, path, shape.Source));
            }
        }

        var bodies = VisitBodies(frame, node, shape, states, inputs, bodyBound);
        var deferred = bodies.SelectMany(b => b.Types.Deferred.Select(site => (b.Path, site))).ToList();
        if (bodyBound.Count > 0)
        {
            var bodyEvidence = evidence.Where(e => e.Kind == EvidenceKind.Exact && bodyBound.Contains(e.Parameter)).ToList();
            foreach (var (_, site) in deferred)
            {
                if (DeferredTerm(bodies, site) is not { } term)
                    continue;
                if (site.Site.Kind == SiteKind.Wire && site.Site.Term is null)
                    tainted.UnionWith(TypeParameterBinding.Parameters(term));
                else
                    Match(term, site.Site, bodyEvidence);
            }

            foreach (var parameter in shape.TypeParameters.Where(p => bodyBound.Contains(p.Name)))
                states[parameter.Name] = Resolve(frame, node, shape, parameter, bodyEvidence, refusedArguments, tainted);
            foreach (var body in bodies)
            {
                context.Signatures[body.Path] = Signature(body.Types, states);
                context.BodyOutputs[body.Path] = [.. (body.Types.Outputs ?? []).Where(o => o.Deferred is not null)
                    .Select(o => Substituted(o.Port.Name, o.Deferred!, o.Port.IsRequired, states))];
            }
        }

        foreach (var (_, site) in deferred.Where(d => checks && (d.site.Site.Kind != SiteKind.Wire || d.site.Site.Term is not null)))
        {
            if (DeferredTerm(bodies, site) is { } term && Substituted(site.Port, term, false, states) is { State: PortState.Typed } target)
                Grade(site.Path, site.NodeId, site.Port, target, site.Site, site.BlockSource, configField: null);
        }

        var outputs = boundaryUnknown ? null : Outputs(frame, node, shape, frameTypes, states, inputs);
        if (checks)
        {
            foreach (var decl in decls)
            {
                var port = inputs.First(p => p.Name == decl.Name);
                var own = (sites.GetValueOrDefault(decl.Name) ?? [])
                    .Where(s => s.Kind == SiteKind.Config ? configSites.GradedAsSite(decl.Name) : !unresolved).ToList();
                if (decl.Deferred is not null)
                {
                    frameTypes.Deferred.AddRange(own.Select(site => new DeferredSite(frame.Path, node.Id, decl.Name, site, shape.Source)));
                    if (wireRefused.Contains(decl.Name))
                        frameTypes.Deferred.Add(new DeferredSite(frame.Path, node.Id, decl.Name, new BindingSite(SiteKind.Wire), shape.Source));
                    continue;
                }

                var field = shape.ConfigurationFields.FirstOrDefault(f => f.Name == decl.Name);
                if (ExpressionInputTerm(decl) is { } evaluated)
                {
                    GradeExpressionInput(frame.Path, node.Id, decl.Name, evaluated, own, shape.Source);
                    continue;
                }

                if (port.State == PortState.Typed)
                {
                    foreach (var site in own)
                        Grade(frame.Path, node.Id, decl.Name, port, site, shape.Source, field);
                }
                else if (port.State == PortState.Unbound)
                {
                    var awaiting = false;
                    foreach (var site in own.Where(NeedsBinding))
                    {
                        if (AwaitsBinding(decl.Declared!, site))
                            awaiting = true;
                        else
                            ReportShapeMismatch(frame.Path, node.Id, decl.Name, decl.Declared!, site, shape.Source);
                    }

                    if (awaiting)
                    {
                        context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.TypeParameterUnbound,
                            $"Input '{decl.Name}' of {Named(frame, node.Id)} needs the type parameter '{port.UnboundParameters[0]}', which nothing binds; "
                            + "a type argument binds it.",
                            node.Id, decl.Name, path, shape.Source));
                    }
                }
            }
        }

        if (checks && !unresolved)
        {
            foreach (var port in inputs.Where(p => p.State == PortState.Typed && decls.All(d => d.Name != p.Name)))
            {
                foreach (var site in (sites.GetValueOrDefault(port.Name) ?? []).Where(s => s.Kind == SiteKind.Projection))
                    Grade(frame.Path, node.Id, port.Name, port, site, shape.Source, configField: null);
            }

            foreach (var (_, mapping) in mappings)
            {
                if (inputs.All(p => p.Name != mapping.TargetInput))
                {
                    context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.WirePortUnknown,
                        $"Node {Named(frame, node.Id)} has no input '{mapping.TargetInput}'.", node.Id, mapping.TargetInput, path, shape.Source));
                }
            }

            ReportDefaultOutputs(frame, node, shape, outputs!);
        }

        var trees = expressions.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!, StringComparer.Ordinal);
        var nodeTypes = new NodeTypes(inputs, outputs, states.ToDictionary(s => s.Key, s => s.Value.Term, StringComparer.Ordinal), trees, scopeTerms, projections);
        context.Types[key] = nodeTypes;
        if (checks)
        {
            GradeConfigExpressions(frame, node, shape, inputs, expressions);
            GradePassthroughConfig(frame, node, shape, inputs, decls, configSites, expressions);
            ReportUnfed(frame, node, shape, nodeTypes, configSites, nodesById);
        }
        if (!isInputNode)
            scope.Publish(node.Id, unresolved ? null : outputs);
    }

    private List<InputDecl> InputDecls(CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, FrameTypes frameTypes)
    {
        var decls = new List<InputDecl>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        if (node.BlockTypeId == WorkflowGraphHelpers.WorkflowOutputBlockTypeId)
        {
            foreach (var output in frameTypes.Outputs ?? [])
            {
                if (claimed.Add(output.Port.Name))
                    decls.Add(new InputDecl(output.Port.Name, output.Port.Term, output.Port.IsRequired, output.Port, output.Deferred));
            }

            return decls;
        }

        foreach (var port in shape.Inputs)
        {
            if (claimed.Add(port.Name))
                decls.Add(new InputDecl(port.Name, Read(port.Type.Name, TermSeam.Declaration, TermPosition.Input), port.IsRequired, IsExpression: port.Type.IsExpression, ScopeVariables: port.ScopeVariables, Text: port.Type.Name));
        }

        if (shape.Flags.HasDynamicPorts)
        {
            foreach (var entry in StoredWorkflowDocument.PortList(node, StoredDocumentSchema.InputPortsKey))
            {
                if (claimed.Add(entry.PortName))
                    decls.Add(new InputDecl(entry.PortName, entry.Error is null ? Read(entry.RawType, TermSeam.Stored, TermPosition.Input) : null, entry.IsRequired,
                        IsExpression: entry.Error is null && PortValueType.Of(entry.RawType!).IsExpression, Text: entry.RawType));
            }
        }

        if (Resolution(frame, node, shape) is { } resolved)
        {
            foreach (var port in resolved.Inputs)
            {
                if (claimed.Add(port.Name))
                    decls.Add(new InputDecl(port.Name, Read(port.Type.Name, TermSeam.Resolver, TermPosition.Input), port.IsRequired, IsExpression: port.Type.IsExpression, Text: port.Type.Name));
            }
        }

        return decls;
    }

    private PortTerm? Read(string? text, TermSeam seam, TermPosition position) =>
        PortTermGrading.Grade(text, seam, position, context.Options.Vocabulary.Table, out _)?.Term;

    private EffectivePort Closed(string name, string? text, TermSeam seam, TermPosition position, bool isRequired) =>
        Read(text, seam, position) is { ContainsParameter: false } term
            ? EffectivePort.Typed(name, term, isRequired, text)
            : EffectivePort.Refused(name, isRequired, text ?? string.Empty);

    private EffectivePort DeclaredOutput(string name, string? text, bool collects)
    {
        if (!collects)
            return Closed(name, text, TermSeam.Stored, TermPosition.Output, false);
        return Read(text, TermSeam.Stored, TermPosition.Output) is { ContainsParameter: false } term && !HoldsCollectedJson(true, text)
            ? EffectivePort.Typed(name, PortTerm.CollectionOf(term.AsNullable()), false, PortTerm.CollectionOf(term.AsNullable()).ToString())
            : EffectivePort.Refused(name, false, text ?? string.Empty);
    }

    internal static bool HoldsCollectedJson(bool collects, string? rawType) =>
        collects && rawType is not null && PortTerm.TryParse(rawType, PortTermGrammar.Document, out var term, out _) && WorkflowTypeRecord.HoldsJson(term);

    private CanvasSignature? Resolution(CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape) =>
        context.PortsUnresolved.Contains(new NodeKey(frame.Path, node.Id))
            ? null
            : EffectivePorts.TrustworthyResolution(frame, node, shape, context.Environment, context.NodesById(frame.Canvas));

    private EffectivePort? SourcePort(
        CanvasFrame frame, string sourceId, string output, IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        if (!nodesById.ContainsKey(sourceId))
            return null;
        var key = new NodeKey(frame.Path, sourceId);
        if (context.PortsUnresolved.Contains(key) || !context.Types.TryGetValue(key, out var types) || types.Outputs is null)
            return null;
        return types.Outputs.FirstOrDefault(p => p.Name == output);
    }

    private bool NamesNoOutput(
        CanvasFrame frame, string sourceId, string output, IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        var key = new NodeKey(frame.Path, sourceId);
        return nodesById.ContainsKey(sourceId) && context.Bound.ContainsKey(key) && !context.PortsUnresolved.Contains(key)
            && context.Types.TryGetValue(key, out var types) && types.Outputs is not null && types.Outputs.All(p => p.Name != output);
    }

    private void ReportUnknownSourceOutput(
        CanvasFrame frame, string sourceId, string output, IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        if (!NamesNoOutput(frame, sourceId, output, nodesById))
            return;
        context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.WirePortUnknown,
            $"Node {Named(frame, sourceId)} has no output '{output}'.", sourceId, output, frame.Path.Segments, context.Bound[new NodeKey(frame.Path, sourceId)].Source));
    }

    private static (string Key, int Row)? InvalidKeyRow(object? value)
    {
        var element = value as JsonElement? ?? JsonSerializer.SerializeToElement(value);
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var row = 0;
        foreach (var member in element.EnumerateObject())
        {
            row++;
            if (FieldNames.FirstNonToken(member.Name) >= 0)
                return (member.Name, row);
        }

        return null;
    }

    private static JsonElement? ConfigValue(WorkflowNodeDefinition node, BlockShape shape, string input)
    {
        if (!WorkflowCompiler.MergeWouldForwardConfigKey(node, input) || !node.Config!.TryGetValue(input, out var raw))
            return null;
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || (raw.ValueKind == JsonValueKind.String && WorkflowCompiler.IsExpressionText(raw.GetString())))
            return null;
        if (raw.ValueKind == JsonValueKind.String && raw.GetString() == string.Empty
            && shape.ConfigurationFields.Any(f => f.Name == input && f.Type == ConfigFieldType.Enum))
            return null;
        return raw;
    }

    private void Match(PortTerm declared, BindingSite site, List<ParameterEvidence> into)
    {
        switch (site.Kind)
        {
            case SiteKind.Wire:
                TypeParameterBinding.Match(declared, site.Term!, into, vocabulary);
                break;
            case SiteKind.Expression when site.Term is not null:
                TypeParameterBinding.Match(declared, site.Term, into, vocabulary);
                break;
            case SiteKind.Literal or SiteKind.Config:
                TypeParameterBinding.MatchLiteral(declared, site.Value, into, vocabulary);
                break;
            case SiteKind.Projection when site.Projection!.Type is { } type:
                TypeParameterBinding.Match(declared, type, into, vocabulary, exactMode: true);
                break;
        }
    }

    private ProjectionSite? ReadProjection(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, List<InputDecl> decls, string sourceId,
        WorkflowMappingDefinition mapping, WorkflowProjection stored, bool isOutputNode, bool checks)
    {
        var input = mapping.TargetInput;
        var wire = $"The projection on the wire from output '{mapping.SourceOutput}' of {Named(frame, sourceId)} into input '{input}' of {Named(frame, node.Id)}";
        if (!JsonPathQuery.TryParse(stored.Path, out var query, out var error))
        {
            if (checks)
                RefuseProjection(frame, node, shape, input, error!.IsProjectionRestriction
                    ? error.Message
                    : $"{wire} has the path '{stored.Path}', which is not an RFC 9535 query: {error.Message}");
            return null;
        }

        if (stored.Type is null)
            return new ProjectionSite(query!, stored.Mode is not null, null, isOutputNode);
        if (decls.FirstOrDefault(d => d.Name == input) is { } decl && (decl.Declared ?? decl.Deferred) is { } declared
            && !declared.ContainsParameter)
        {
            if (checks)
                RefuseProjection(frame, node, shape, input, $"{wire} states the type '{stored.Type}', but the input's term {declared} names no type parameter, so the projection takes that term.");
            return null;
        }

        return Read(stored.Type, TermSeam.Stored, TermPosition.Input) is { } type
            ? new ProjectionSite(query!, stored.Mode is not null, type, isOutputNode)
            : null;
    }

    private void RefuseProjection(CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, string input, string message) =>
        context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.JsonProjectionInvalid, message, node.Id, input, frame.Path.Segments, shape.Source));

    private HashSet<string> TypeArguments(WorkflowNodeDefinition node, BlockShape shape, List<ParameterEvidence> evidence)
    {
        var refused = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, text) in node.TypeArguments ?? new Dictionary<string, string>())
        {
            if (!shape.TypeParameters.Any(p => p.Name == name))
                continue;
            if (Read(text, TermSeam.Stored, TermPosition.Input) is { } term)
                evidence.Add(new ParameterEvidence(name, EvidenceKind.Exact, term));
            else
                refused.Add(name);
        }

        return refused;
    }

    private readonly record struct ParameterState(PortState State, PortTerm? Term);

    private ParameterState Resolve(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, WorkflowTypeParameter parameter,
        List<ParameterEvidence> evidence, HashSet<string> refusedArguments, HashSet<string> tainted)
    {
        if (refusedArguments.Contains(parameter.Name))
            return new ParameterState(PortState.Refused, null);

        var own = evidence.Where(e => e.Parameter == parameter.Name).ToList();

        var resolution = TypeParameterBinding.Resolve(own, vocabulary);
        var path = frame.Path.Segments;
        switch (resolution.Kind)
        {
            case ParameterResolutionKind.Unbound:
                return new ParameterState(tainted.Contains(parameter.Name) ? PortState.Refused : PortState.Unbound, null);
            case ParameterResolutionKind.Conflict:
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.TypeParameterConflict,
                    $"Type parameter '{parameter.Name}' of node {Named(frame, node.Id)} has no single binding: {Conflict(own)}.",
                    node.Id, parameter.Name, path, shape.Source));
                return new ParameterState(PortState.Refused, null);
        }

        var bound = resolution.Term!;
        switch (TypeParameterBinding.Refusal(parameter, bound, vocabulary))
        {
            case null:
                return new ParameterState(PortState.Typed, bound);
            case CompilationRules.PortTypeJsonPosition:
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.PortTypeJsonPosition,
                    $"Type parameter '{parameter.Name}' of node {Named(frame, node.Id)} is bound to {bound}, which contains Json; no binding holds Json.",
                    node.Id, parameter.Name, path, shape.Source));
                return new ParameterState(PortState.Refused, null);
            default:
                var missing = parameter.Constraints.First(c => PortCapabilities.TryParse(c, out var capability) && !vocabulary.Implements(bound, capability));
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.TypeParameterConstraint,
                    $"Type parameter '{parameter.Name}' of node {Named(frame, node.Id)} is bound to {bound}, which does not implement {missing}.",
                    node.Id, parameter.Name, path, shape.Source));
                return new ParameterState(PortState.Refused, null);
        }
    }

    private static string Conflict(List<ParameterEvidence> evidence)
    {
        var exact = evidence.Where(e => e.Kind == EvidenceKind.Exact).Select(e => e.Term.ToString()).Distinct().ToList();
        return exact.Count > 1
            ? $"its exact bindings {string.Join(" and ", exact)} differ"
            : $"the terms {string.Join(", ", evidence.Select(e => e.Term.ToString()).Distinct())} have no least upper bound";
    }

    private static EffectivePort Substituted(string name, PortTerm declared, bool isRequired, IReadOnlyDictionary<string, ParameterState> states)
    {
        var named = TypeParameterBinding.Parameters(declared).Distinct(StringComparer.Ordinal).ToList();
        var text = declared.ToString();
        if (named.Any(n => !states.TryGetValue(n, out var s) || s.State == PortState.Refused))
            return EffectivePort.Refused(name, isRequired, text);
        var unbound = named.Where(n => states[n].State == PortState.Unbound).ToList();
        return unbound.Count > 0
            ? EffectivePort.Unbound(name, unbound, isRequired, text)
            : EffectivePort.Typed(name, TypeParameterBinding.Substitute(declared, n => states[n].Term)!, isRequired, text);
    }

    private EffectivePort? PassthroughInput(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, string name,
        Dictionary<string, (string Source, WorkflowMappingDefinition Mapping)> last, Dictionary<string, TypedExpression?> expressions,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById, bool checks)
    {
        if (last.TryGetValue(name, out var fed))
        {
            if (fed.Mapping.IsLiteral)
            {
                if (WorkflowCompiler.IsExpressionText(fed.Mapping.LiteralValue as string))
                    return Expressed(name, expressions);
                var edgeChecked = context.Bound.ContainsKey(new NodeKey(frame.Path, fed.Source));
                return Untermed(frame, node, shape, name, fed.Mapping.LiteralValue, checks && edgeChecked);
            }

            if (nodesById.ContainsKey(fed.Source) && !NamesNoOutput(frame, fed.Source, fed.Mapping.SourceOutput, nodesById))
            {
                if (SourcePort(frame, fed.Source, fed.Mapping.SourceOutput, nodesById) is not { State: PortState.Typed } port
                    || ProjectionTerms.IsJson(port.Term!) != (fed.Mapping.Projection is not null))
                    return EffectivePort.Refused(name, false);
                return fed.Mapping.Projection is { } projection ? Projected(frame, node, shape, name, projection, checks) : EffectivePort.Typed(name, port.Term!, false);
            }
        }

        if (!WorkflowCompiler.MergeWouldForwardConfigKey(node, name) || !node.Config!.TryGetValue(name, out var raw))
            return null;
        if (raw.ValueKind == JsonValueKind.String && WorkflowCompiler.IsExpressionText(raw.GetString()))
            return Expressed(name, expressions);
        return Untermed(frame, node, shape, name, raw, checks);
    }

    private EffectivePort Projected(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, string name, WorkflowProjection projection, bool checks)
    {
        if (projection.Type is null)
        {
            if (checks)
            {
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.PortTypeUnresolved,
                    $"Port '{name}' of node {Named(frame, node.Id)} is fed by a projection that states no type, so the name it passes on has none.",
                    node.Id, name, frame.Path.Segments, shape.Source));
            }

            return EffectivePort.Refused(name, false);
        }

        return Read(projection.Type, TermSeam.Stored, TermPosition.Input) is { } term
            ? EffectivePort.Typed(name, term, false)
            : EffectivePort.Refused(name, false);
    }

    private static EffectivePort Expressed(string name, Dictionary<string, TypedExpression?> expressions) =>
        expressions.GetValueOrDefault(name) is { } tree ? EffectivePort.Typed(name, tree.Term, false) : EffectivePort.Refused(name, false);

    private EffectivePort Untermed(CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, string name, object? value, bool checks)
    {
        if (TypeParameterBinding.LiteralTerm(value, vocabulary) is { Kind: not PortTermKind.Null } term)
            return EffectivePort.Typed(name, term, false);
        if (checks)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.PortTypeUnresolved,
                $"Port '{name}' of node {Named(frame, node.Id)} is fed a value that has no type ({Describe(value)}), so the name it passes on has none.",
                node.Id, name, frame.Path.Segments, shape.Source));
        }

        return EffectivePort.Refused(name, false);
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "null",
        JsonElement { ValueKind: JsonValueKind.Object } or IDictionary<string, object?> => "an object",
        _ => "an array that is empty or whose elements have no common type",
    };

    private sealed record Body(CanvasPath Path, FrameTypes Types);

    private List<Body> VisitBodies(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, Dictionary<string, ParameterState> states,
        List<EffectivePort> inputs, HashSet<string> bodyBound)
    {
        var bodies = new List<Body>();
        foreach (var name in shape.SubCanvasNames)
        {
            if (node.SubCanvases is null || !node.SubCanvases.TryGetValue(name, out var canvas))
                continue;
            var child = frame.Child(node.Id, name, canvas);
            if (ambiguous.Contains(child.Path) || context.Visited.Contains(child.Path))
                continue;

            var bodyInputs = new List<EffectivePort>();
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in shape.SubCanvasInputs.GetValueOrDefault(name) ?? [])
            {
                if (claimed.Add(port.Name))
                    bodyInputs.Add(Read(port.Type.Name, TermSeam.Declaration, TermPosition.Input) is { } term ? Substituted(port.Name, term, port.IsRequired, states) : EffectivePort.Refused(port.Name, port.IsRequired, port.Type.Name));
            }

            foreach (var forwarded in node.Passthrough ?? [])
            {
                if (inputs.FirstOrDefault(p => p.Name == forwarded) is { } port && claimed.Add(forwarded))
                    bodyInputs.Add(port with { IsRequired = false });
            }

            var outputs = new List<FrameOutput>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in shape.SubCanvasOutputs.GetValueOrDefault(name) ?? [])
            {
                if (!taken.Add(port.Name))
                    continue;
                var term = Read(port.Type.Name, TermSeam.Declaration, TermPosition.Output);
                outputs.Add(term is not null && TypeParameterBinding.Parameters(term).Any(bodyBound.Contains)
                    ? new FrameOutput(EffectivePort.Refused(port.Name, port.IsRequired, term.ToString()), term)
                    : new FrameOutput(term is null ? EffectivePort.Refused(port.Name, port.IsRequired, port.Type.Name) : Substituted(port.Name, term, port.IsRequired, states)));
            }

            foreach (var declared in node.OutputPorts ?? [])
            {
                if (declared.Sources is null || !declared.Sources.TryGetValue(name, out var rawKey) || !taken.Add(rawKey))
                    continue;
                outputs.Add(new FrameOutput(Closed(rawKey, declared.Type, TermSeam.Stored, TermPosition.Output, false)));
            }

            var types = new FrameTypes(bodyInputs, outputs);
            context.Signatures[child.Path] = Signature(types, states);
            VisitCanvas(child, types);
            bodies.Add(new Body(child.Path, types));
        }

        return bodies;
    }

    private static PortTerm? DeferredTerm(List<Body> bodies, DeferredSite site) =>
        bodies.FirstOrDefault(b => b.Path.Equals(site.Path))?.Types.Outputs?.FirstOrDefault(o => o.Port.Name == site.Port)?.Deferred;

    private static CanvasSignature Signature(FrameTypes types, IReadOnlyDictionary<string, ParameterState> states)
    {
        var outputs = (types.Outputs ?? []).Select(o => o.Deferred is { } term ? Substituted(o.Port.Name, term, o.Port.IsRequired, states) : o.Port).ToList();
        var inputs = types.Inputs ?? [];
        static PortShape Shape(EffectivePort p) => new(p.Name, p.Type, p.IsRequired);
        static UntypedPort Untyped(EffectivePort p) => new(p.Name, p.IsRequired);
        return new CanvasSignature(
            [.. inputs.Where(p => p.State == PortState.Typed).Select(Shape)],
            [.. outputs.Where(p => p.State == PortState.Typed).Select(Shape)])
        {
            UntypedInputs = [.. inputs.Where(p => p.State != PortState.Typed).Select(Untyped)],
            UntypedOutputs = [.. outputs.Where(p => p.State != PortState.Typed).Select(Untyped)],
        };
    }

    private List<EffectivePort> Outputs(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, FrameTypes frameTypes,
        Dictionary<string, ParameterState> states, List<EffectivePort> inputs)
    {
        var outputs = new List<EffectivePort>();
        if (node.BlockTypeId == WorkflowGraphHelpers.WorkflowInputBlockTypeId)
        {
            outputs.AddRange(frameTypes.Inputs ?? []);
        }
        else if (shape.Flags.PortsAreResolvedElsewhere)
        {
            if (Resolution(frame, node, shape) is { } resolved)
                outputs.AddRange(resolved.Outputs.Select(p => Closed(p.Name, p.Type.Name, TermSeam.Resolver, TermPosition.Output, false)));
        }
        else
        {
            foreach (var port in shape.Outputs)
                outputs.Add(Read(port.Type.Name, TermSeam.Declaration, TermPosition.Output) is { } term ? Substituted(port.Name, term, false, states) : EffectivePort.Refused(port.Name, false, port.Type.Name));
            if (shape.Flags.HasDynamicPorts)
            {
                foreach (var entry in StoredWorkflowDocument.PortList(node, StoredDocumentSchema.OutputPortsKey))
                    outputs.Add(Closed(entry.PortName, entry.RawType, TermSeam.Stored, TermPosition.Output, false));
            }
        }

        foreach (var declared in node.OutputPorts ?? [])
        {
            outputs.Add(DeclaredOutput(declared.Name, declared.Type, shape.Flags.DeclaredOutputsCollect));
        }

        if (node.ErrorPolicy?.Policy == NodeErrorPolicy.EmitError && !WorkflowCompiler.IsExemptFromResilienceChecks(node)
            && outputs.All(p => p.Name != EffectivePorts.ErrorOutputName))
            outputs.Add(EffectivePort.Typed(EffectivePorts.ErrorOutputName, PortTerm.Atom("Json"), false, "Json"));

        foreach (var name in node.Passthrough ?? [])
        {
            if (outputs.All(p => p.Name != name) && inputs.FirstOrDefault(p => p.Name == name) is { } input)
                outputs.Add(input with { IsRequired = false });
        }

        return outputs;
    }

    private string Named(CanvasFrame frame, string nodeId) => context.NodeName(frame.Canvas, nodeId);

    private string Named(CanvasPath at, string? nodeId) => context.NodeName(at, nodeId ?? string.Empty);

    private static bool NeedsBinding(BindingSite site) => site.Kind switch
    {
        SiteKind.Wire or SiteKind.Expression => site.Term is not null,
        _ => true,
    };

    private bool AwaitsBinding(PortTerm declared, BindingSite site)
    {
        var probe = new List<ParameterEvidence>();
        Match(declared, site, probe);
        if (probe.Count > 0 || site is { Kind: SiteKind.Expression, Term.Kind: PortTermKind.Null } or { Kind: SiteKind.Projection, Projection.Type: null })
            return true;
        return site.Kind is SiteKind.Literal or SiteKind.Config
            && TypeParameterBinding.LiteralTerm(site.Value, vocabulary) is null or { Kind: PortTermKind.Null }
            && TypeParameterBinding.MayWaitForBinding(site.Value, declared);
    }

    private void ReportShapeMismatch(CanvasPath at, string nodeId, string input, PortTerm declared, BindingSite site, string? blockSource)
    {
        var path = at.Segments;
        context.Diagnostics.Add(site.Kind switch
        {
            SiteKind.Wire => new CompilationDiagnostic(CompilationRules.PortTypeMismatch,
                $"Output '{site.SourceOutput}' of {Named(at, site.SourceNode)} carries {site.Term}, which input '{input}' of {Named(at, nodeId)} "
                + $"cannot take ({declared}) for any binding.",
                nodeId, input, path, blockSource),
            SiteKind.Expression => new CompilationDiagnostic(CompilationRules.ExpressionResultTypeMismatch,
                $"Expression for input '{input}' of {Named(at, nodeId)} yields {site.Term}, which the input cannot take ({declared}) for any binding.",
                nodeId, input, path, blockSource),
            SiteKind.Projection => new CompilationDiagnostic(CompilationRules.PortTypeMismatch,
                $"The projection from output '{site.SourceOutput}' of {Named(at, site.SourceNode)} delivers {site.Projection!.Type}, which input '{input}' "
                + $"of {Named(at, nodeId)} cannot take ({declared}) for any binding.",
                nodeId, input, path, blockSource),
            _ => new CompilationDiagnostic(CompilationRules.LiteralPortValueInvalid,
                $"{(site.Kind == SiteKind.Config ? "Configuration value" : "Literal")} for input '{input}' of {Named(at, nodeId)} "
                + $"cannot be read as {declared} for any binding.",
                nodeId, input, path, blockSource),
        });
    }

    private void Grade(CanvasPath at, string nodeId, string input, EffectivePort port, BindingSite site, string? blockSource, ConfigFieldShape? configField)
    {
        var path = at.Segments;
        switch (site.Kind)
        {
            case SiteKind.Expression when site.Term is not null && !ResultFits(site.Term, port.Term!):
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.ExpressionResultTypeMismatch,
                    $"Expression for input '{input}' of {Named(at, nodeId)} yields {site.Term}, which the input cannot take ({port.Term}).",
                    nodeId, input, path, blockSource));
                break;
            case SiteKind.Projection:
                GradeProjection(at, nodeId, input, port.Term!, site, blockSource);
                break;
            case SiteKind.Wire when !vocabulary.IsAssignable(site.Term!, port.Term!):
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.PortTypeMismatch,
                    $"Output '{site.SourceOutput}' of {Named(at, site.SourceNode)} carries {site.Term}, which input "
                    + $"'{input}' of {Named(at, nodeId)} cannot take ({port.Term}).",
                    nodeId, input, path, blockSource));
                break;
            case SiteKind.Literal:
                if (!vocabulary.IsLiteralOf(site.Value, port.Type))
                {
                    context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.LiteralPortValueInvalid,
                        $"Literal for input '{input}' of {Named(at, nodeId)} cannot be read as {port.Term}.", nodeId, input, path, blockSource));
                }
                else if (configField is { Type: ConfigFieldType.Enum } enumField)
                {
                    var text = site.Value switch { string s => s, JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(), _ => null };
                    var allowed = enumField.AllowedValues ?? [];
                    if (text != string.Empty && (text is null || !allowed.Contains(text, StringComparer.Ordinal)))
                    {
                        context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.LiteralPortValueInvalid,
                            $"Literal for input '{input}' of {Named(at, nodeId)} must be a string equal to one of "
                            + $"{string.Join(", ", allowed.Select(name => "'" + name + "'"))}.",
                            nodeId, input, path, blockSource));
                    }
                }

                break;
            case SiteKind.Config when configField?.Type != ConfigFieldType.Enum
                && site.Value is JsonElement { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } raw
                && !vocabulary.IsLiteralOf(raw, port.Type):
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.LiteralPortValueInvalid,
                    configField is null
                        ? $"Configuration value for input '{input}' of {Named(at, nodeId)} cannot be read as {port.Term}."
                        : $"Configuration field '{input}' of {Named(at, nodeId)} cannot be read as {port.Term}.",
                    nodeId, input, path, blockSource));
                break;
        }
    }

    private void GradeProjection(CanvasPath at, string nodeId, string input, PortTerm target, BindingSite site, string? blockSource)
    {
        var projection = site.Projection!;
        var term = projection.Type ?? target;
        var into = $"The projection from output '{site.SourceOutput}' of {Named(at, site.SourceNode)} into input '{input}' of {Named(at, nodeId)}";
        var refusal = ProjectionTerms.Refusal(term, projection.Query, projection.JsonText, projection.IntoWorkflowOutput, vocabulary);
        if (refusal is not null)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.JsonProjectionInvalid, $"{into} {refusal}", nodeId, input, at.Segments, blockSource));
            return;
        }

        if (projection.Type is not null && !vocabulary.IsAssignable(term, target))
        {
            context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.PortTypeMismatch,
                $"{into} delivers {term}, which the input cannot take ({target}).", nodeId, input, at.Segments, blockSource));
        }
    }

    private static PortTerm? ExpressionInputTerm(InputDecl decl) =>
        decl.IsExpression ? decl.Declared?.Arguments[0] : null;

    private bool ResultFits(PortTerm result, PortTerm target) => vocabulary.IsAssignable(result, target);

    private void GradeExpressionInput(CanvasPath at, string nodeId, string input, PortTerm result, List<BindingSite> sites, string? blockSource)
    {
        var path = at.Segments;
        foreach (var site in sites)
        {
            switch (site.Kind)
            {
                case SiteKind.Wire or SiteKind.Projection:
                    context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.PortTypeMismatch,
                        $"Output '{site.SourceOutput}' of {Named(at, site.SourceNode)} is wired into input '{input}' of {Named(at, nodeId)}, which holds an "
                        + $"expression (Expression<{result}>) and takes no wire.",
                        nodeId, input, path, blockSource));
                    break;
                case SiteKind.Literal or SiteKind.Config:
                    context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.LiteralPortValueInvalid,
                        $"{(site.Kind == SiteKind.Config ? "Configuration value" : "Literal")} for input '{input}' of {Named(at, nodeId)} must be "
                        + $"an expr: text (Expression<{result}>).",
                        nodeId, input, path, blockSource));
                    break;
                case SiteKind.Expression when site.Term is not null && !ResultFits(site.Term, result):
                    context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.ExpressionResultTypeMismatch,
                        $"Expression for input '{input}' of {Named(at, nodeId)} yields {site.Term}, which is not assignable to {result}.",
                        nodeId, input, path, blockSource));
                    break;
            }
        }
    }

    private void GradeConfigExpressions(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, List<EffectivePort> inputs, Dictionary<string, TypedExpression?> expressions)
    {
        foreach (var (key, tree) in expressions)
        {
            if (tree is null || inputs.Any(p => p.Name == key)
                || shape.ConfigurationFields.FirstOrDefault(f => f.Name == key) is not { } field)
                continue;
            var term = PortTerm.Atom(field.Type switch
            {
                ConfigFieldType.Int => "Int",
                ConfigFieldType.Number => "Number",
                ConfigFieldType.Boolean => "Boolean",
                _ => "String",
            });
            if (!ResultFits(tree.Term, term))
            {
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.ExpressionResultTypeMismatch,
                    $"Expression for configuration field '{key}' of {Named(frame, node.Id)} yields {tree.Term}, which the field cannot take ({term}).",
                    node.Id, key, frame.Path.Segments, shape.Source));
            }
        }
    }

    private void ReportDefaultOutputs(CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, List<EffectivePort> outputs)
    {
        if (node.ErrorPolicy is not { Policy: NodeErrorPolicy.ContinueWithDefault, DefaultOutputJson: { } json }
            || string.IsNullOrWhiteSpace(json))
            return;
        Dictionary<string, JsonElement> keys;
        string? unreadable = null;
        try
        {
            using var document = JsonMembers.Parse(System.Text.Encoding.UTF8.GetBytes(json));
            keys = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : [];
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                unreadable = "it is not a JSON object";
        }
        catch (JsonRepeatException repeat)
        {
            keys = [];
            unreadable = repeat.Repeat.ToString();
        }
        catch (JsonException)
        {
            keys = [];
            unreadable = "it is not JSON";
        }

        if (unreadable is not null)
        {
            context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.LiteralPortValueInvalid,
                $"The default output text of {Named(frame, node.Id)} cannot be read: {unreadable}.", node.Id, null, frame.Path.Segments, shape.Source));
            return;
        }

        foreach (var (name, value) in keys)
        {
            if (outputs.All(p => p.Name != name))
            {
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.WirePortUnknown,
                    $"Node {Named(frame, node.Id)} has no output '{name}' for its default output to give", node.Id, name, frame.Path.Segments, shape.Source));
            }
            else if (outputs.FirstOrDefault(p => p.Name == name) is { State: PortState.Unbound } port)
            {
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.TypeParameterUnbound,
                    $"The default output '{name}' of node {Named(frame, node.Id)} has a type naming the type parameter '{port.UnboundParameters[0]}', which nothing binds.",
                    node.Id, name, frame.Path.Segments, shape.Source));
            }
            else if (outputs.FirstOrDefault(p => p.Name == name) is { State: PortState.Typed } typed && !vocabulary.IsLiteralOf(value, typed.Type))
            {
                context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.LiteralPortValueInvalid,
                    $"Default output '{name}' of {Named(frame, node.Id)} cannot be read as {typed.Term}.", node.Id, name, frame.Path.Segments, shape.Source));
            }
        }
    }

    private void GradePassthroughConfig(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, List<EffectivePort> inputs, List<InputDecl> decls,
        ConfigSites configSites, Dictionary<string, TypedExpression?> expressions)
    {
        foreach (var port in inputs.Where(p => p.State == PortState.Typed && decls.All(d => d.Name != p.Name)))
        {
            if (!configSites.GradedAsSite(port.Name))
                continue;
            var field = shape.ConfigurationFields.FirstOrDefault(f => f.Name == port.Name);
            if (ConfigValue(node, shape, port.Name) is { } config)
                Grade(frame.Path, node.Id, port.Name, port, new BindingSite(SiteKind.Config, Value: config), shape.Source, field);
            else if (expressions.GetValueOrDefault(port.Name) is { } tree)
                Grade(frame.Path, node.Id, port.Name, port, new BindingSite(SiteKind.Expression, tree.Term), shape.Source, field);
        }
    }

    private void ReportUnfed(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape, NodeTypes types, ConfigSites configSites,
        IReadOnlyDictionary<string, WorkflowNodeDefinition> nodesById)
    {
        var key = new NodeKey(frame.Path, node.Id);
        var wired = WorkflowCompiler.WiredInputNames(frame.Canvas, node.Id, nodesById);
        foreach (var port in types.RequiredInputs)
        {
            if (context.PortsUnresolved.Contains(key) && port.Name == shape.RunsWorkflowFrom)
                continue;
            if (wired.Contains(port.Name) || configSites.Feeds(port.Name))
                continue;
            context.Diagnostics.Add(new CompilationDiagnostic(CompilationRules.RequiredInputUnfed,
                $"Input '{port.Name}' of node {Named(frame, node.Id)} is fed by neither a connection nor a configuration value.",
                node.Id, port.Name, frame.Path.Segments, shape.Source));
        }
    }
}
