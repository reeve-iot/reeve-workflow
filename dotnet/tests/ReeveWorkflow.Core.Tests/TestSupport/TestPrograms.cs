using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Compilation;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Tests.TestSupport;

internal static class TestPrograms
{
    public static CompiledWalk Untyped { get; } =
        CompiledWalk.Root(new WorkflowProgram([new CompiledCanvas(CanvasPath.Root, CanvasSignature.Empty, [], [])]));

    public static CompiledWalk Walk(
        WorkflowDefinition root,
        IWorkflowBlocks blocks,
        IReadOnlyDictionary<string, string>? terms = null,
        IReadOnlyDictionary<string, string>? parameters = null,
        IEnumerable<WorkflowExpressionNamespace>? namespaces = null,
        IHelperResolver? helper = null,
        WorkflowTypeVocabulary? vocabulary = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? nodeTypes = null)
    {
        vocabulary ??= WorkflowTypeVocabulary.Builtin;
        root = DeclaredBranches.Filled(root, node => blocks.Find(node.BlockTypeId) is { SubCanvasNames.Count: > 0 } block
            ? new DeclaredBranches.Declared(block.SubCanvasNames, name => block.GetSubCanvasOutputs(name).Count > 0)
            : null);
        var canvases = new List<CompiledCanvas>();
        Lower(CanvasPath.Root, root, blocks, terms, parameters, namespaces?.ToArray() ?? [], helper, vocabulary, nodeTypes, canvases, []);
        return CompiledWalk.Root(new WorkflowProgram(canvases));
    }

    public static Task<Dictionary<string, IReadOnlyDictionary<string, object?>>> ExecuteCompiledAsync(
        this WorkflowGraphExecutor executor,
        CompiledWalk compiled,
        Dictionary<string, IReadOnlyDictionary<string, object?>> executedOutputs,
        Guid workflowId,
        Guid executionId,
        Guid userId,
        IServiceProvider services,
        List<WorkflowExecutionLogEntry> logEntries,
        List<WorkflowExecutionStep> executionSteps,
        WorkflowTraceMode traceMode,
        bool debugMode,
        int? iterationIndex,
        string? callStackJson,
        int depth,
        HashSet<string> visitedNodeIds,
        bool isInsideTryCatch,
        IReadOnlyDictionary<string, object?>? inputParametersForRun,
        Guid? triggerIdForRun,
        TriggerEnvelope? triggerEnvelopeForRun,
        CancellationToken cancellationToken,
        CanvasSignature? parameterSignature = null) =>
        executor.ExecuteGraphAsync(
            executedOutputs, workflowId, executionId, userId, services, logEntries, executionSteps, traceMode,
            debugMode, iterationIndex, callStackJson, depth, visitedNodeIds, isInsideTryCatch, inputParametersForRun,
            triggerIdForRun, triggerEnvelopeForRun, cancellationToken, debugHook: null, debugCounter: null, stateExecutionId: null,
            mayParkSleep: false, parameterSignature: parameterSignature, compiled: compiled);

    private static void Lower(
        CanvasPath path, WorkflowDefinition canvas, IWorkflowBlocks blocks, IReadOnlyDictionary<string, string>? terms,
        IReadOnlyDictionary<string, string>? parameters, WorkflowExpressionNamespace[] namespaces, IHelperResolver? helper,
        WorkflowTypeVocabulary vocabulary, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? nodeTypes,
        List<CompiledCanvas> canvases, IEnumerable<string> inputNames)
    {
        var builder = new ExpressionTypingScope.Builder()
            .Vocabulary(vocabulary)
            .Previous((node, output) => Previous(canvas, blocks, terms, vocabulary, node, output))
            .Parameters(name => parameters is not null && parameters.TryGetValue(name, out var term)
                ? ExpressionResolution.Lookup(PortValueType.Of(term), optional: false, vocabulary)
                : ReadLookup.Missing($"no parameter '{name}'"));
        if (helper is not null)
        {
            var paths = new HashSet<HelperPath>();
            var keyShapes = ExpressionKeyShapes.For(namespaces);
            foreach (var source in canvas.Nodes.SelectMany(n => WorkflowCompiler.ExpressionSources(canvas, n).Select(e => e.Source)))
                try
                {
                    foreach (var token in new ExpressionLexer(source).Tokenize().Where(t => t.Kind == TokenKind.Variable))
                        if (ExpressionPath.TrySplit(token.Text, keyShapes, out var parts, out _) && parts.Namespace == "helper") paths.Add(new HelperPath(parts.Key));
                }
                catch (ExpressionSyntaxException) { }
            var answers = helper.CheckAsync(paths, CancellationToken.None).GetAwaiter().GetResult();
            builder.Helper(answers);
        }
        foreach (var registration in namespaces)
            builder.Namespace(registration);
        var scope = builder.Build();

        var nodes = new List<CompiledNode>();
        var nodeOrder = WorkflowGraphHelpers.TopologicalSort(canvas);
        var nodesById = canvas.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var nodeId in nodeOrder)
        {
            var node = nodesById[nodeId];
            var expressions = new Dictionary<string, CompiledExpression>(StringComparer.Ordinal);
            foreach (var (target, source) in WorkflowCompiler.ExpressionSources(canvas, node))
            {
                var typed = ExpressionTyper.Type(source, scope);
                if (typed.Refusal is { } refusal)
                    throw new InvalidOperationException($"node '{node.Id}' input '{target}': {refusal.Message} ({refusal.RuleId})");
                expressions[target] = new CompiledExpression(ExpressionSource.Prefix + source,
                    typed.Tree ?? throw new InvalidOperationException($"node '{node.Id}' input '{target}' reads a value with no term"));
            }

            var branches = new Dictionary<string, CanvasPath>(StringComparer.Ordinal);
            foreach (var (branch, body) in node.SubCanvases ?? [])
            {
                var child = path.Child(node.Id, branch);
                branches[branch] = child;
                var declared = (blocks.Find(node.BlockTypeId)?.GetSubCanvasInputs(branch) ?? []).Select(p => p.Name).Concat(node.Passthrough ?? []);
                Lower(child, body, blocks, terms, parameters, namespaces, helper, vocabulary, nodeTypes, canvases, declared);
            }

            var shape = blocks.Find(node.BlockTypeId) is { } factory
                ? BlockShape.Snapshot(factory).WithBlockTypeId(node.BlockTypeId)
                : BoundaryBlockShapes.For(node.BlockTypeId)
                    ?? new BlockShape(node.BlockTypeId, [], [], [], default,
                        new Dictionary<string, IReadOnlyList<PortShape>>(),
                        new Dictionary<string, IReadOnlyList<PortShape>>(), Source: null);
            var incoming = (canvas.GetConnections() ?? []).Where(connection => connection.TargetNodeId == node.Id)
                .Select(connection => new CompiledEdge(connection.SourceNodeId ?? string.Empty,
                    connection.Mappings is { } mappings ? [.. WorkflowGraphHelpers.ReadMappings(mappings)] : []))
                .ToArray();
            var inputs = WorkflowGraphExecutor.MergeConfigBeforeCompile(
                new Dictionary<string, object?>(), node.Config ?? new Dictionary<string, JsonElement>());
            nodes.Add(new CompiledNode(node.Id, shape, inputs, expressions, incoming, branches,
                WorkflowCompiler.ConfiguredResilienceOf(node),
                node.ErrorPolicy ?? new NodeErrorConfig(NodeErrorPolicy.FailWorkflow),
                node.Passthrough ?? [], WorkflowCompiler.LowerOutputPorts(node))
            {
                InputNames = [.. inputs.Keys],
                RequiredInputs = RequiredInputs(node, blocks),
                OutputNames = OutputNames(node, blocks),
                Types = ClosedTypes(nodeTypes?.GetValueOrDefault(node.Id), vocabulary),
            });
        }

        var inputTypes = inputNames.Distinct().ToDictionary(name => name, _ => (ClosedPortType?)null);
        canvases.Add(new CompiledCanvas(path, CanvasSignature.Empty, nodeOrder, nodes)
        {
            InputTypes = inputTypes,
            OutputNodeOrder = canvas.Nodes
                .Where(node => node.BlockTypeId == WorkflowGraphHelpers.WorkflowOutputBlockTypeId)
                .Select(node => node.Id).ToArray(),
        });
    }

    private static IReadOnlyDictionary<string, ClosedPortType> ClosedTypes(
        IReadOnlyDictionary<string, string>? terms, WorkflowTypeVocabulary vocabulary) =>
        (terms ?? new Dictionary<string, string>()).ToDictionary(
            t => t.Key,
            t => vocabulary.ClosedTypeOf(PortTerm.Parse(t.Value, PortTermGrammar.Document))
                ?? throw new InvalidOperationException($"'{t.Value}' is not a closed term of the vocabulary."));

    private static HashSet<string> OutputNames(WorkflowNodeDefinition node, IWorkflowBlocks blocks)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (blocks.Find(node.BlockTypeId) is { } factory)
        {
            names.UnionWith(factory.Outputs.Select(p => p.Name));
            if (factory.HasDynamicPorts)
                names.UnionWith(StoredWorkflowDocument.PortList(node, StoredDocumentSchema.OutputPortsKey).Select(p => p.PortName));
        }

        names.UnionWith((node.OutputPorts ?? []).Select(p => p.Name));
        names.UnionWith(node.Passthrough ?? []);
        return names;
    }

    private static IReadOnlyList<string> RequiredInputs(WorkflowNodeDefinition node, IWorkflowBlocks blocks)
    {
        if (blocks.Find(node.BlockTypeId) is not { } factory)
            return [];
        var required = factory.Inputs.Where(p => p.IsRequired).Select(p => p.Name).ToList();
        if (factory.HasDynamicPorts)
            required.AddRange(StoredWorkflowDocument.PortList(node, StoredDocumentSchema.InputPortsKey).Where(p => p.IsRequired).Select(p => p.PortName));
        return required;
    }

    private static ReadLookup Previous(
        WorkflowDefinition canvas, IWorkflowBlocks blocks, IReadOnlyDictionary<string, string>? terms,
        WorkflowTypeVocabulary vocabulary, string nodeId, string output)
    {
        if (terms is not null && terms.TryGetValue($"{nodeId}.{output}", out var named))
            return ExpressionResolution.Lookup(PortValueType.Of(named), optional: false, vocabulary);

        var node = canvas.Nodes.FirstOrDefault(n => n.Id == nodeId);
        var declared = node?.OutputPorts?.FirstOrDefault(p => p.Name == output)?.Type
            ?? (node is null ? null : blocks.Find(node.BlockTypeId)?.Outputs.FirstOrDefault(p => p.Name == output)?.Type.Name);
        return declared is null
            ? ReadLookup.Missing($"node '{nodeId}' publishes no '{output}' this test types")
            : ExpressionResolution.Lookup(PortValueType.Of(declared), optional: false, vocabulary);
    }
}
