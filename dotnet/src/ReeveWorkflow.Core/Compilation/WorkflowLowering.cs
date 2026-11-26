using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Expressions;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

public static partial class WorkflowCompiler
{
    private static CompiledCanvas LowerCanvas(CanvasFrame frame, CompilationContext context)
    {
        var order = context.Order[frame.Path];
        var nodesById = context.NodesById(frame.Canvas);

        var nodes = new List<CompiledNode>(order.Count);
        foreach (var nodeId in order)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var node = nodesById[nodeId];
            var shape = context.Bound[new NodeKey(frame.Path, nodeId)].WithBlockTypeId(node.BlockTypeId);
            nodes.Add(LowerNode(frame, node, shape, context.Types[new NodeKey(frame.Path, nodeId)], context));
        }

        var signature = context.Signatures[frame.Path];
        var inputTypes = new Dictionary<string, ClosedPortType?>(StringComparer.Ordinal);
        foreach (var port in signature.Inputs)
            inputTypes.TryAdd(port.Name, ClosedTypeOf(port.Type, context.Options.Vocabulary));
        foreach (var port in signature.UntypedInputs)
            inputTypes.TryAdd(port.Name, null);
        var outputTypes = new Dictionary<string, ClosedPortType?>(StringComparer.Ordinal);
        foreach (var port in signature.Outputs)
            outputTypes.TryAdd(port.Name, ClosedTypeOf(port.Type, context.Options.Vocabulary));
        foreach (var port in signature.UntypedOutputs)
            outputTypes.TryAdd(port.Name, null);
        return new CompiledCanvas(frame.Path, signature, new List<string>(order).AsReadOnly(), nodes.AsReadOnly())
        {
            InputTypes = new ReadOnlyDictionary<string, ClosedPortType?>(inputTypes),
            OutputTypes = new ReadOnlyDictionary<string, ClosedPortType?>(outputTypes),
            OutputNodeOrder = frame.Canvas.Nodes
                .Where(node => node.BlockTypeId == WorkflowGraphHelpers.WorkflowOutputBlockTypeId)
                .Select(node => node.Id).ToArray(),
        };
    }

    private static CompiledNode LowerNode(
        CanvasFrame frame, WorkflowNodeDefinition node, BlockShape shape,
        NodeTypes types, CompilationContext context)
    {
        var vocabulary = context.Options.Vocabulary;
        var inputTerms = InputTerms(frame, node, types, context);
        var inputTypes = ClosedTypes(inputTerms, vocabulary);
        var inputs = CoercedConfig(node, shape, inputTerms, vocabulary);

        var expressions = new Dictionary<string, CompiledExpression>(StringComparer.Ordinal);
        foreach (var (targetInput, source) in ExpressionSources(frame.Canvas, node))
        {
            var tree = types.Expressions.GetValueOrDefault(targetInput)
                ?? throw new InvalidOperationException($"The expression on input '{targetInput}' of node '{node.Id}' has no typed tree.");
            expressions[targetInput] = new CompiledExpression(ExpressionSource.Prefix + source, tree)
            {
                Conversion = ExpressionTarget(shape, inputTerms, targetInput) is { } target
                    && vocabulary.IsAssignable(tree.Term, target) ? vocabulary.ConversionOf(tree.Term, target) : null,
            };
        }

        var incoming = (frame.Canvas.GetConnections() ?? [])
            .Where(connection => connection.TargetNodeId == node.Id)
            .Select(connection => LowerEdge(frame, connection.SourceNodeId,
                connection.Mappings is { } mappings ? [.. WorkflowGraphHelpers.ReadMappings(mappings)] : [],
                inputTerms, inputTypes, context))
            .ToList().AsReadOnly();

        var subCanvases = new Dictionary<string, CanvasPath>(StringComparer.Ordinal);
        foreach (var (name, subCanvas) in node.SubCanvases ?? [])
            subCanvases[name] = frame.Child(node.Id, name, subCanvas).Path;

        var resilience = ConfiguredResilienceOf(node);

        var errorPolicy = node.ErrorPolicy ?? DefaultErrorPolicy;
        var passthrough = node.Passthrough is { Count: > 0 } list
            ? (IReadOnlyList<string>)new List<string>(list).AsReadOnly()
            : [];

        var outputTerms = new Dictionary<string, PortTerm>(StringComparer.Ordinal);
        foreach (var port in (types.Outputs ?? []).Where(p => p.State == PortState.Typed))
            outputTerms.TryAdd(port.Name, port.Term!);
        var outputTypes = ClosedTypes(outputTerms, vocabulary);
        var typeArguments = ClosedTypes(
            types.Bindings.Where(b => b.Value is not null).ToDictionary(b => b.Key, b => b.Value!, StringComparer.Ordinal), vocabulary);

        var view = new Dictionary<string, ClosedPortType>(outputTypes, StringComparer.Ordinal);
        foreach (var (name, type) in inputTypes)
            view.TryAdd(name, type);
        foreach (var (branch, path) in subCanvases)
        {
            if (!context.Signatures.TryGetValue(path, out var signature))
                continue;
            foreach (var port in signature.Inputs.Concat(signature.Outputs))
            {
                if (ClosedTypeOf(port.Type, vocabulary) is { } type)
                    view.TryAdd($"{branch}.{port.Name}", type);
            }
        }

        foreach (var (name, type) in typeArguments)
            view[$"${name}"] = type;

        return new CompiledNode(
            node.Id, shape,
            new ReadOnlyDictionary<string, object?>(inputs),
            new ReadOnlyDictionary<string, CompiledExpression>(expressions),
            incoming,
            new ReadOnlyDictionary<string, CanvasPath>(subCanvases),
            resilience, errorPolicy, passthrough, LowerOutputPorts(node))
        {
            InputNames = [.. inputs.Keys],
            InputTypes = inputTypes,
            OutputTypes = outputTypes,
            OutputNames = (types.Outputs ?? []).Where(p => p.State == PortState.Typed).Select(p => p.Name).ToHashSet(StringComparer.Ordinal),
            TypeArguments = typeArguments,
            ScopeVariables = types.ScopeVariables.ToDictionary(
                i => i.Key, i => ClosedTypes(i.Value, vocabulary), StringComparer.Ordinal),
            Types = new ReadOnlyDictionary<string, ClosedPortType>(view),
            RequiredInputs = [.. types.RequiredInputs.Select(p => p.Name)],
            DroppedOutputs = [.. (types.Outputs ?? []).Where(p => p.State == PortState.Unbound).Select(p => p.Name)],
            DefaultOutputs = DefaultOutputs(errorPolicy, outputTerms, vocabulary),
        };
    }

    private static Dictionary<string, PortTerm> InputTerms(
        CanvasFrame frame, WorkflowNodeDefinition node, NodeTypes types, CompilationContext context)
    {
        var terms = new Dictionary<string, PortTerm>(StringComparer.Ordinal);
        if (node.BlockTypeId == WorkflowGraphHelpers.WorkflowOutputBlockTypeId)
        {
            foreach (var port in context.Signatures.TryGetValue(frame.Path, out var signature) ? signature.Outputs : [])
            {
                if (TermOf(port.Type) is { } term)
                    terms.TryAdd(port.Name, term);
            }

            return terms;
        }

        foreach (var port in types.Inputs.Where(p => p.State == PortState.Typed))
            terms.TryAdd(port.Name, port.Term!);
        return terms;
    }

    private static PortTerm? TermOf(PortValueType type) =>
        PortTerm.TryParse(type.Name, PortTermGrammar.Document, out var term, out _) ? term : null;

    private static ClosedPortType? ClosedTypeOf(PortValueType type, WorkflowTypeVocabulary vocabulary) =>
        TermOf(type) is { } term ? vocabulary.ClosedTypeOf(term) : null;

    private static IReadOnlyDictionary<string, ClosedPortType> ClosedTypes(
        IReadOnlyDictionary<string, PortTerm> terms, WorkflowTypeVocabulary vocabulary)
    {
        var types = new Dictionary<string, ClosedPortType>(StringComparer.Ordinal);
        foreach (var (name, term) in terms)
        {
            if (vocabulary.ClosedTypeOf(term) is { } type)
                types[name] = type;
        }

        return new ReadOnlyDictionary<string, ClosedPortType>(types);
    }

    private static PortTerm? ExpressionTarget(BlockShape shape, Dictionary<string, PortTerm> inputTerms, string key)
    {
        if (inputTerms.TryGetValue(key, out var input))
            return input is { Kind: PortTermKind.Constructor, Name: "Expression" } ? input.Arguments[0] : input;
        return shape.Inputs.Any(p => p.Name == key) ? null : FieldTerm(shape, key);
    }

    internal static PortTerm? FieldTerm(BlockShape shape, string key) =>
        shape.ConfigurationFields.FirstOrDefault(f => f.Name == key)?.Type switch
        {
            null => null,
            ConfigFieldType.Int => PortTerm.Atom("Int"),
            ConfigFieldType.Number => PortTerm.Atom("Number"),
            ConfigFieldType.Boolean => PortTerm.Atom("Boolean"),
            _ => PortTerm.Atom("String"),
        };

    private static Dictionary<string, object?> CoercedConfig(
        WorkflowNodeDefinition node, BlockShape shape, Dictionary<string, PortTerm> inputTerms, WorkflowTypeVocabulary vocabulary)
    {
        var merged = WorkflowGraphExecutor.MergeConfigBeforeCompile(EmptyMergedInputs, node.Config ?? EmptyConfig);
        foreach (var key in merged.Keys.ToList())
        {
            var raw = node.Config![key];
            if (raw.ValueKind == JsonValueKind.String && IsExpressionText(raw.GetString()))
                continue;
            if (raw.ValueKind == JsonValueKind.String && raw.GetString() == string.Empty
                && shape.ConfigurationFields.Any(f => f.Name == key && f.Type == ConfigFieldType.Enum))
            {
                merged.Remove(key);
                continue;
            }

            var term = inputTerms.TryGetValue(key, out var input) ? input
                : shape.Inputs.Any(p => p.Name == key) ? null : FieldTerm(shape, key);
            if (term is not null && Coerced(raw, term, vocabulary) is { } value)
                merged[key] = value.Value;
        }

        return merged;
    }

    private static CompiledEdge LowerEdge(
        CanvasFrame frame, string sourceId, IReadOnlyList<WorkflowMappingDefinition> mappings, Dictionary<string, PortTerm> inputTerms,
        IReadOnlyDictionary<string, ClosedPortType> inputTypes, CompilationContext context)
    {
        var vocabulary = context.Options.Vocabulary;
        var conversions = new Dictionary<string, PortConversion>(StringComparer.Ordinal);
        var literals = new Dictionary<string, object?>(StringComparer.Ordinal);
        var projections = new Dictionary<string, CompiledProjection>(StringComparer.Ordinal);
        var source = context.Types.GetValueOrDefault(new NodeKey(frame.Path, sourceId))?.Outputs;
        foreach (var mapping in mappings)
        {
            if (!inputTerms.TryGetValue(mapping.TargetInput, out var target))
                continue;
            if (mapping.IsLiteral)
            {
                if (inputTypes.ContainsKey(mapping.TargetInput) && !IsExpressionText(mapping.LiteralValue as string)
                    && Coerced(JsonSerializer.SerializeToElement(mapping.LiteralValue), target, vocabulary) is { } value)
                    literals[mapping.TargetInput] = value.Value;
                continue;
            }

            if (mapping.Projection is { } stored)
            {
                if (Projection(stored, target, vocabulary) is { } projection)
                    projections[mapping.TargetInput] = projection;
                continue;
            }

            if (source?.FirstOrDefault(p => p.Name == mapping.SourceOutput) is { State: PortState.Typed, Term: { } from }
                && vocabulary.IsAssignable(from, target) && vocabulary.ConversionOf(from, target) is { } conversion)
                conversions[mapping.TargetInput] = conversion;
        }

        return new CompiledEdge(sourceId, new List<WorkflowMappingDefinition>(mappings).AsReadOnly())
        {
            Conversions = new ReadOnlyDictionary<string, PortConversion>(conversions),
            Literals = new ReadOnlyDictionary<string, object?>(literals),
            Projections = new ReadOnlyDictionary<string, CompiledProjection>(projections),
        };
    }

    private static CompiledProjection? Projection(WorkflowProjection stored, PortTerm target, WorkflowTypeVocabulary vocabulary)
    {
        if (!JsonPath.JsonPathQuery.TryParse(stored.Path, out var query, out _))
            return null;
        PortTerm? term = target;
        if (stored.Type is not null && !PortTerm.TryParse(stored.Type, PortTermGrammar.Document, out term, out _))
            return null;
        if (vocabulary.ClosedTypeOf(term!) is not { } closed)
            return null;
        var conversion = vocabulary.ConversionOf(term!, target);
        return new CompiledProjection(query, stored.Mode == WorkflowProjection.JsonTextMode, closed, conversion, vocabulary);
    }

    private static StrongBox<object?>? Coerced(JsonElement raw, PortTerm term, WorkflowTypeVocabulary vocabulary)
    {
        if (vocabulary.ClosedTypeOf(term) is not { } type || !vocabulary.IsLiteralOf(raw, PortValueType.Of(type.Term.ToString())))
            return null;
        return new StrongBox<object?>(Coerce(raw, type, vocabulary));
    }

    internal static object? Coerce(JsonElement raw, ClosedPortType type, WorkflowTypeVocabulary vocabulary)
    {
        if (!Opens(type, raw))
            return CoerceLevel(raw, type, vocabulary);
        var open = new Stack<(IEnumerator<KeyValuePair<string?, JsonElement>> Items, ClosedPortType Type, List<KeyValuePair<string, object?>> Built)>();
        open.Push((Members(raw), type, []));
        while (true)
        {
            var (items, level, built) = open.Peek();
            if (!items.MoveNext())
            {
                open.Pop();
                var rebuilt = level.ValueType is null ? level.CreateList(built.Select(m => m.Value)) : level.CreateMap(built);
                if (open.Count == 0)
                    return rebuilt;
                var parent = open.Peek();
                parent.Built.Add(new(parent.Items.Current.Key!, rebuilt));
                continue;
            }

            var (key, current) = items.Current;
            var element = level.ElementType ?? level.ValueType!;
            if (Opens(element, current))
                open.Push((Members(current), element, []));
            else
                built.Add(new(key!, CoerceLevel(current, element, vocabulary)));
        }
    }

    private static bool Opens(ClosedPortType type, JsonElement raw) =>
        type.ElementType is not null && raw.ValueKind == JsonValueKind.Array || type.ValueType is not null && raw.ValueKind == JsonValueKind.Object;

    private static IEnumerator<KeyValuePair<string?, JsonElement>> Members(JsonElement container) => container.ValueKind == JsonValueKind.Array
        ? container.EnumerateArray().Select(item => new KeyValuePair<string?, JsonElement>(null, item)).GetEnumerator()
        : container.EnumerateObject().Select(property => new KeyValuePair<string?, JsonElement>(property.Name, property.Value)).GetEnumerator();

    private static object? CoerceLevel(JsonElement raw, ClosedPortType type, WorkflowTypeVocabulary vocabulary)
    {
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return type.Term.Kind != PortTermKind.Atom ? Read(raw, type, vocabulary) : type.Term.Name switch
        {
            "Int" => raw.TryGetInt64(out var whole) ? whole : (long)raw.GetDouble(),
            "Number" => raw.GetDouble(),
            "String" => raw.GetString(),
            "Boolean" => raw.GetBoolean(),
            "Json" => raw.Clone(),
            _ => Read(raw, type, vocabulary),
        };
    }

    private static object? Read(JsonElement raw, ClosedPortType type, WorkflowTypeVocabulary vocabulary)
    {
        var info = type.JsonTypeInfo ?? vocabulary.JsonTypeInfos.GetValueOrDefault(type.Term.Name);
        if (info is null)
            throw new InvalidOperationException($"A literal of '{type.Term}' cannot be lowered: no JsonTypeInfo reads it.");
        return ClosedPortType.TryReadWith(raw, info, info.Type, out var value)
            ? value
            : throw new InvalidOperationException($"A literal of '{type.Term}' was graded as readable and does not read.");
    }

    private static IReadOnlyDictionary<string, object?>? DefaultOutputs(
        NodeErrorConfig policy, IReadOnlyDictionary<string, PortTerm> outputTerms, WorkflowTypeVocabulary vocabulary)
    {
        if (policy is not { Policy: NodeErrorPolicy.ContinueWithDefault, DefaultOutputJson: var json })
            return null;
        var outputs = WorkflowGraphExecutor.ReadDefaultOutputs(json, out var unparsed);
        if (unparsed is not null)
            return null;
        if (outputs.Count > 0)
        {
            using var document = JsonDocument.Parse(json!);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (outputTerms.TryGetValue(property.Name, out var term) && Coerced(property.Value, term, vocabulary) is { } value)
                    outputs[property.Name] = value.Value;
            }
        }

        return new ReadOnlyDictionary<string, object?>(outputs);
    }

    private static readonly NodeErrorConfig DefaultErrorPolicy = new(NodeErrorPolicy.FailWorkflow);

    internal static IReadOnlyList<CompiledOutputPort> LowerOutputPorts(WorkflowNodeDefinition node)
    {
        if (node.OutputPorts is not { Count: > 0 } declared)
            return [];

        var ports = new List<CompiledOutputPort>(declared.Count);
        foreach (var port in declared)
        {
            var sources = port.Sources is { Count: > 0 } s
                ? (IReadOnlyDictionary<string, string>)new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(s, StringComparer.Ordinal))
                : ReadOnlyDictionary<string, string>.Empty;
            ports.Add(new CompiledOutputPort(port.Name, sources));
        }

        return ports.AsReadOnly();
    }

    internal static ConfiguredResilience ConfiguredResilienceOf(WorkflowNodeDefinition node) =>
        IsExemptFromResilienceChecks(node)
            ? new ConfiguredResilience(null, null, null, null)
            : new ConfiguredResilience(
                ConfiguredResilienceInt(node, WorkflowGraphExecutor.ConfigTimeoutSeconds),
                ConfiguredResilienceBool(node, WorkflowGraphExecutor.ConfigRetryEnabled),
                ConfiguredResilienceInt(node, WorkflowGraphExecutor.ConfigRetryCount),
                ConfiguredResilienceDouble(node, WorkflowGraphExecutor.ConfigRetryBackoffSeconds));

    private static int? ConfiguredResilienceInt(WorkflowNodeDefinition node, string key) =>
        IsConfiguredResilienceValue(node, key, out var element) ? element.GetInt32() : null;

    private static double? ConfiguredResilienceDouble(WorkflowNodeDefinition node, string key) =>
        IsConfiguredResilienceValue(node, key, out var element) ? element.GetDouble() : null;

    private static bool? ConfiguredResilienceBool(WorkflowNodeDefinition node, string key) =>
        IsConfiguredResilienceValue(node, key, out var element) ? element.ValueKind == JsonValueKind.True : null;

    private static readonly IReadOnlyDictionary<string, object?> EmptyMergedInputs =
        ReadOnlyDictionary<string, object?>.Empty;
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyConfig =
        ReadOnlyDictionary<string, JsonElement>.Empty;
}
