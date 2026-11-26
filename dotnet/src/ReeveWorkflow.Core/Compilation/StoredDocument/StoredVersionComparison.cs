using System.Globalization;
using System.Text.Json;
using ReeveWorkflow.Abstractions;
using ReeveWorkflow.Core.Execution;
using ReeveWorkflow.Core.Model;

namespace ReeveWorkflow.Core.Compilation;

internal sealed record StoredVersionTexts(
    int Number, string DefinitionJson, string? InputParametersJson, string? OutputParametersJson, string? ShownDefinitionJson = null);

internal sealed record StoredFieldReading(string Label, string Says, bool Compared);

internal sealed record StoredCollection(string Label, StoredOrder Order);

internal static class StoredVersionComparison
{
    private const string NodesKey = "nodes";
    private const string EdgesKey = "edges";
    private const string IdKey = "id";
    private const string BlockKey = "blockTypeId";
    private const string SubCanvasesKey = "subCanvases";
    private const string ConfigurationKey = "configuration";
    private const string ErrorPolicyKey = "errorPolicy";
    private const string Mapped = "connection.mappings";

    private static readonly string[] RetryKeys =
        [WorkflowGraphExecutor.ConfigRetryEnabled, WorkflowGraphExecutor.ConfigRetryCount, WorkflowGraphExecutor.ConfigRetryBackoffSeconds];

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = StoredDocumentLexer.MaxNesting };

    private delegate string Canon(JsonElement? value, StoredField field, JsonElement? owner, Reading reading);

    private sealed record Reading(IWorkflowBlocks? Blocks);

    private sealed record Special(
        string Says, Canon? Read = null, bool Structural = false, bool NotRead = false,
        IReadOnlyCollection<string>? Beneath = null)
    {
        public bool Compared => !NotRead;
    }

    private static readonly Dictionary<(string Position, string Key), Special> Specials = new()
    {
        [("canvas", NodesKey)] = new("each node matched by its id on its canvas, then compared field by field; their order as "
            + "the walk and the boundary nodes it gives", Structural: true),
        [("canvas", EdgesKey)] = new("each edge matched by what the engine reads from it, its source, target and mappings, "
            + "counted; their order as the walk it gives", Structural: true),
        [("canvas", "outputMappings")] = new("the reader refuses it (output-mappings-unread), so no two versions hold it",
            NotRead: true, Beneath: []),
        [("node", IdKey)] = new("the node's key on its canvas", Structural: true),
        [("node", BlockKey)] = new("matched ignoring case, as block lookup matches, but WorkflowInput and WorkflowOutput, "
            + "which the engine matches exactly; a node whose block differs is removed and added, and one respelled in case "
            + "alone is modified, since the run's log and error data carry the stored spelling"),
        [("node", "position")] = new("the editor's layout: neither the engine nor the compiler reads it", NotRead: true, Beneath: []),
        [("node", ConfigurationKey)] = new("per key, each value as the engine hands it to the block (JsonToValue), but for a "
            + "key beginning __, which only the editor reads, and the resilience keys, read as the pipeline ReadResilience "
            + "builds from them; null is no keys", Configuration),
        [("node", "typeArguments")] = new("each exact type-parameter binding the compiler looks up by parameter name"),
        [("node", SubCanvasesKey)] = new("the set of names, since a call to a name the node lacks fails, but for a name the "
            + "host's blocks say the node's block declares, which runs as the empty branch the editor writes and is compared "
            + "as that branch; each canvas compared as the root is", Names),
        [("node", "passthrough")] = new("as a set of names, which is how the engine and the compiler read it", NameSet),
        [("error policy", "defaultOutputJson")] = new("under ContinueWithDefault only, the one policy that reads it, as the "
            + "outputs ReadDefaultOutputs parses from it", DefaultOutput),
        [("error policy", "routeToErrorPort")] = new("neither the engine nor the compiler reads it", NotRead: true),
        [("declared output", "displayName")] = new("the editor's label: neither the engine nor the compiler reads it", NotRead: true),
        [("connection", IdKey)] = new("neither the engine nor the compiler matches an edge by its id", NotRead: true),
        [("connection", "mappings")] = new("as ReadMappings reads them, per target input in order; null and {} are none",
            Mappings, Beneath: ["mapping.type", "mapping.sourceOutputName", "mapping.literalValue", "mapping.projection",
                "projection.path", "projection.mode", "projection.type"]),
        [("parameter", "name")] = new("the parameter's key in its list", Structural: true),
        [("parameter", "displayName")] = new("the editor's label: neither the engine nor the compiler reads it", NotRead: true),
    };

    public static IReadOnlyList<StoredFieldReading> Fields { get; } = Describe();

    public static IReadOnlyList<StoredCollection> Collections { get; } = CollectionsOf();

    public static WorkflowDiffResponse Compare(StoredVersionTexts a, StoredVersionTexts b, IWorkflowBlocks? blocks = null)
    {
        var reading = new Reading(blocks);
        using var readA = Read(a, reading);
        using var readB = Read(b, reading);

        var addedNodes = new List<WorkflowDiffNode>();
        var removedNodes = new List<WorkflowDiffNode>();
        var modifiedNodes = new List<WorkflowDiffModifiedNode>();
        var addedEdges = new List<WorkflowDiffEdge>();
        var removedEdges = new List<WorkflowDiffEdge>();
        var modifiedCanvases = new List<WorkflowDiffCanvas>();

        foreach (var path in readA.Canvases.Keys.Concat(readB.Canvases.Keys.Where(k => !readA.Canvases.ContainsKey(k))))
        {
            var canvasA = readA.Canvases.GetValueOrDefault(path);
            var canvasB = readB.Canvases.GetValueOrDefault(path);
            var steps = (canvasA ?? canvasB)!.Steps;
            var matched = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in canvasA?.Nodes ?? [])
            {
                if (canvasB?.ById.GetValueOrDefault(node.Id) is not { } other || !SameBlock(node.BlockTypeId, other.BlockTypeId))
                {
                    removedNodes.Add(new(node.Id, node.BlockTypeId, "", steps));
                    continue;
                }

                matched.Add(node.Id);
                if (CompareNode(node.Element, other.Element, node.ShownElement, other.ShownElement, node.Id, steps, reading) is { } modified)
                    modifiedNodes.Add(modified);
            }

            foreach (var node in canvasB?.Nodes ?? [])
            {
                if (!matched.Contains(node.Id))
                    addedNodes.Add(new(node.Id, node.BlockTypeId, "", steps));
            }

            var pairs = new List<(int A, int B)>();
            var taken = new bool[canvasB?.Edges.Count ?? 0];
            for (var i = 0; i < (canvasA?.Edges.Count ?? 0); i++)
            {
                var edge = canvasA!.Edges[i];
                var at = Enumerable.Range(0, taken.Length).FirstOrDefault(j => !taken[j] && canvasB!.Edges[j].Canon == edge.Canon, -1);
                if (at < 0)
                {
                    removedEdges.Add(edge.Response(steps));
                    continue;
                }

                taken[at] = true;
                pairs.Add((i, at));
            }
            addedEdges.AddRange(Enumerable.Range(0, taken.Length).Where(j => !taken[j]).Select(j => canvasB!.Edges[j].Response(steps)));

            var changed = new List<string>();
            if (canvasA is not null && canvasB is not null)
                changed.AddRange(OrderChanged(canvasA, canvasB, matched, pairs));
            changed.AddRange(Changed(StoredDocumentSchema.Definition, canvasA?.Element, canvasB?.Element, reading));
            if (changed.Count > 0)
                modifiedCanvases.Add(new(steps, changed));
        }

        return new WorkflowDiffResponse(
            addedNodes, removedNodes, modifiedNodes, addedEdges, removedEdges,
            CompareParameters(readA.Inputs, readB.Inputs), modifiedCanvases, CompareParameters(readA.Outputs, readB.Outputs));
    }

    public static bool SameBlock(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal)
        || (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) && !MatchedOrdinally(a) && !MatchedOrdinally(b));

    private static bool MatchedOrdinally(string id) =>
        BoundaryBlockShapes.IsBoundary(id);

    private static IEnumerable<string> OrderChanged(CanvasRead a, CanvasRead b, HashSet<string> matched, List<(int A, int B)> pairs)
    {
        var walkA = WalkOf(a.Model, matched);
        var walkB = WalkOf(b.Model, matched);
        var read = (walkA is not null && walkB is not null && !walkA.SequenceEqual(walkB))
            || !Boundary(a, matched).SequenceEqual(Boundary(b, matched));
        if (!read)
            yield break;

        if (!a.Nodes.Select(n => n.Id).Where(matched.Contains).SequenceEqual(b.Nodes.Select(n => n.Id).Where(matched.Contains)))
            yield return NodesKey;
        if (pairs.Zip(pairs.Skip(1)).Any(p => p.First.B > p.Second.B))
            yield return EdgesKey;
    }

    private static List<string>? WalkOf(WorkflowDefinition canvas, HashSet<string> matched)
    {
        var boundary = canvas.Nodes.Where(n => MatchedOrdinally(n.BlockTypeId)).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        try
        {
            return [.. WorkflowGraphHelpers.TopologicalSort(canvas).Where(id => matched.Contains(id) && !boundary.Contains(id))];
        }
        catch (WorkflowValidationException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Boundary(CanvasRead canvas, HashSet<string> matched) =>
        canvas.Nodes.Where(n => MatchedOrdinally(n.BlockTypeId) && matched.Contains(n.Id)).Select(n => n.Id);

    private static WorkflowDiffModifiedNode? CompareNode(
        JsonElement a, JsonElement b, JsonElement shownNodeA, JsonElement shownNodeB, string id, IReadOnlyList<WorkflowDiffCanvasStep> steps, Reading reading)
    {
        var node = Kind(StoredDocumentSchema.Definition, NodesKey);
        var changed = Changed(node, a, b, reading);
        if (changed.Count == 0)
            return null;

        var configA = Property(a, ConfigurationKey);
        var configB = Property(b, ConfigurationKey);
        var shownA = Property(shownNodeA, ConfigurationKey);
        var shownB = Property(shownNodeB, ConfigurationKey);
        var (timeoutA, retryA) = Resilience(a, reading);
        var (timeoutB, retryB) = Resilience(b, reading);
        var configChanges = new Dictionary<string, WorkflowDiffValueChange>(StringComparer.Ordinal);
        foreach (var key in Keys(configA).Union(Keys(configB), StringComparer.Ordinal))
        {
            var differs = key == WorkflowGraphExecutor.ConfigTimeoutSeconds ? timeoutA != timeoutB
                : RetryKeys.Contains(key) ? retryA != retryB
                : ConfigValue(configA, key) != ConfigValue(configB, key);
            if (differs && RawValue(configA, key) != RawValue(configB, key))
                configChanges[key] = new WorkflowDiffValueChange(RawValue(shownA, key), RawValue(shownB, key));
        }

        return new WorkflowDiffModifiedNode(
            id, configChanges,
            changed.Contains(ErrorPolicyKey),
            retryA != retryB,
            steps, changed);
    }

    private static List<string> Changed(ObjectKind kind, JsonElement? a, JsonElement? b, Reading reading)
    {
        var changed = new List<string>();
        foreach (var field in kind.Fields)
        {
            var special = Specials.GetValueOrDefault((kind.Name, field.Key));
            if (special is { Structural: true } or { Compared: false })
                continue;
            if (ReadField(kind, field, a, reading) != ReadField(kind, field, b, reading))
                changed.Add(field.Key);
        }

        return changed;
    }

    private static string ReadField(ObjectKind kind, StoredField field, JsonElement? owner, Reading reading) =>
        Specials.GetValueOrDefault((kind.Name, field.Key)) switch
        {
            { Compared: false } => "",
            { Read: { } read } => read(Property(owner, field.Key), field, owner, reading),
            _ => ByKind(Property(owner, field.Key), field.Kind, reading),
        };

    private static string ByKind(JsonElement? value, StoredKind kind, Reading reading)
    {
        var present = value is { ValueKind: not JsonValueKind.Null } v ? v : (JsonElement?)null;
        return kind switch
        {
            StringKind => Quote(present?.GetString() ?? ""),
            NumberKind => (present?.GetDouble() ?? 0).ToString("R", CultureInfo.InvariantCulture),
            BooleanKind => present?.ValueKind == JsonValueKind.True ? "true" : "false",
            EnumKind e => Quote(present?.GetString() ?? Enum.GetName(e.Enum, Enum.ToObject(e.Enum, 0))!),
            PortTypeKind => PortType(present),
            AnyKind => present is { } any ? EngineValue(WorkflowGraphHelpers.JsonToValue(any)) : "null",
            ConfigKind => Configuration(present, null!, null, reading),
            ArrayKind array => "[" + string.Join(",", Ordered(Elements(present).ToList(), array, reading)) + "]",
            MapKind map => "{" + string.Join(",", Entries(present).Select(p => Quote(p.Name) + ":" + ByKind(p.Value, map.Value, reading))) + "}",
            ObjectKind obj => "{" + string.Join(",", obj.Fields.Select(f => Quote(f.Key) + ":" + ReadField(obj, f, present, reading))) + "}",
            _ => present?.GetRawText() ?? "null",
        };
    }

    private static IEnumerable<string> Ordered(List<JsonElement> elements, ArrayKind array, Reading reading)
    {
        var canons = elements.Select(e => ByKind(e, array.Element, reading)).ToList();
        switch (array.Order)
        {
            case StoredOrder.Unread:
                return canons.Order(StringComparer.Ordinal);
            case StoredOrder.Dependent dependent:
                var after = Enumerable.Range(0, elements.Count).Select(_ => new List<int>()).ToList();
                var waiting = new int[elements.Count];
                for (var i = 0; i < elements.Count; i++)
                {
                    for (var j = 0; j < i; j++)
                    {
                        if (!dependent.Depends(elements[j], elements[i]))
                            continue;
                        after[j].Add(i);
                        waiting[i]++;
                    }
                }
                var free = new SortedSet<(string Canon, int Index)>(
                    Enumerable.Range(0, elements.Count).Where(i => waiting[i] == 0).Select(i => (canons[i], i)),
                    Comparer<(string Canon, int Index)>.Create((x, y) => string.CompareOrdinal(x.Canon, y.Canon) is var c and not 0 ? c : x.Index.CompareTo(y.Index)));
                var result = new List<string>();
                while (free.Count > 0)
                {
                    var (canon, next) = free.Min;
                    free.Remove(free.Min);
                    result.Add(canon);
                    foreach (var i in after[next])
                    {
                        if (--waiting[i] == 0)
                            free.Add((canons[i], i));
                    }
                }
                return result;
            default:
                return canons;
        }
    }

    private static string PortType(JsonElement? value) => value?.GetString() is not { } text
        ? "null"
        : PortValueType.TryParse(text, out _, out _) ? "t" + Quote(text.ToUpperInvariant()) : "!" + Quote(text);

    private static string Configuration(JsonElement? value, StoredField field, JsonElement? owner, Reading reading)
    {
        var keys = "{" + string.Join(",", Keys(value).Select(key => (key, read: ConfigValue(value, key))).Where(p => p.read is not null)
            .Select(p => Quote(p.key) + ":" + p.read)) + "}";
        return owner is { } node ? keys + "|" + Resilience(node, reading) : keys;
    }

    private static string? ConfigValue(JsonElement? configuration, string key) =>
        Property(configuration, key) is not { } value
        || key.StartsWith(WorkflowGraphExecutor.DesignTimeConfigPrefix, StringComparison.Ordinal)
        || WorkflowGraphExecutor.PollyConfigKeys.Contains(key)
            ? null
            : EngineValue(WorkflowGraphHelpers.JsonToValue(value));

    private static readonly IWorkflowBlockFactory[] AnyBlock =
        [new Assumed(false, false), new Assumed(true, false), new Assumed(false, true), new Assumed(true, true)];

    private static (string Timeout, string Retry) Resilience(JsonElement node, Reading reading)
    {
        var definition = new WorkflowNodeDefinition
        {
            Id = Property(node, IdKey)?.GetString() ?? "",
            BlockTypeId = Property(node, BlockKey)?.GetString() ?? "",
            Config = Entries(Property(node, ConfigurationKey)).ToDictionary(p => p.Name, p => p.Value.Clone()),
        };
        var blocks = reading.Blocks?.Find(definition.BlockTypeId) is { } known ? [known] : AnyBlock;
        var timeout = new List<string>();
        var retry = new List<string>();
        foreach (var block in blocks)
        {
            foreach (var idempotentOnly in (bool[])[false, true])
            {
                try
                {
                    var read = WorkflowGraphExecutor.ReadResilience(
                        ConfiguredResilienceForComparison(definition), definition.Id, block, idempotentOnly,
                        defaultContainerTimeoutSeconds: null);
                    timeout.Add(read.TimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? "none");
                    retry.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{read.EffectiveRetries}/{(read.EffectiveRetries > 0 ? read.RetryBackoffSeconds : 0)}/{read.RetryCountIgnored}"));
                }
                catch (InvalidWorkflowDefinitionException refused)
                {
                    timeout.Add("!" + refused.Message);
                    retry.Add("!" + refused.Message);
                }
            }
        }

        return (string.Join(",", timeout), string.Join(",", retry));
    }

    private static ConfiguredResilience ConfiguredResilienceForComparison(WorkflowNodeDefinition node)
    {
        if (WorkflowCompiler.IsExemptFromResilienceChecks(node))
            return new ConfiguredResilience(null, null, null, null);

        var timeoutSeconds = ReadInt(node, WorkflowGraphExecutor.ConfigTimeoutSeconds);
        var retryCount = ReadInt(node, WorkflowGraphExecutor.ConfigRetryCount);
        var retryBackoffSeconds = ReadDouble(node, WorkflowGraphExecutor.ConfigRetryBackoffSeconds);
        var retryEnabled = ReadBool(node, WorkflowGraphExecutor.ConfigRetryEnabled);

        return new ConfiguredResilience(timeoutSeconds, retryEnabled, retryCount, retryBackoffSeconds);
    }

    private static int? ReadInt(WorkflowNodeDefinition node, string key) =>
        ReadConfiguredNumber(node, key, static value => value.GetInt32());

    private static double? ReadDouble(WorkflowNodeDefinition node, string key) =>
        ReadConfiguredNumber(node, key, static value => value.GetDouble());

    private static T? ReadConfiguredNumber<T>(WorkflowNodeDefinition node, string key, Func<JsonElement, T> read)
        where T : struct
    {
        if (node.Config?.TryGetValue(key, out var element) != true || element.ValueKind == JsonValueKind.Null)
            return null;

        try
        {
            return element.ValueKind == JsonValueKind.Number
                ? read(element)
                : throw new FormatException($"{element.ValueKind} is not a number");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new InvalidWorkflowDefinitionException(
                $"node '{node.Id}' has '{key}' set to a value the engine cannot read: {ex.Message}", ex);
        }
    }

    private static bool? ReadBool(WorkflowNodeDefinition node, string key)
    {
        if (node.Config?.TryGetValue(key, out var element) != true || element.ValueKind == JsonValueKind.Null)
            return null;

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidWorkflowDefinitionException(
                $"node '{node.Id}' has '{key}' set to {element.ValueKind}, which is not a boolean"),
        };
    }

    private sealed class Assumed(bool idempotent, bool container) : IWorkflowBlockFactory
    {
        public string BlockTypeId => "";
        public string DisplayName => "";
        public string Description => "";
        public string Category => "";
        public IReadOnlyCollection<BlockPortDefinition> Inputs => [];
        public IReadOnlyCollection<BlockPortDefinition> Outputs => [];
        public IReadOnlyCollection<string> SubCanvasNames => container ? ["Body"] : [];
        public bool IsIdempotent => idempotent;
        public IWorkflowBlockPlugin Create() => throw new NotSupportedException();
    }

    private static string Names(JsonElement? value, StoredField field, JsonElement? owner, Reading reading) =>
        "[" + string.Join(",", Entries(value).Select(p => p.Name)
            .Union(reading.Blocks?.Find(Property(owner, BlockKey)?.GetString() ?? "")?.SubCanvasNames ?? [], StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select(Quote)) + "]";

    private static IEnumerable<(string Name, WorkflowDefinition Canvas)> AbsentDeclared(WorkflowNodeDefinition node, Reading reading) =>
        reading.Blocks?.Find(node.BlockTypeId) is { } block
            ? DeclaredBranches.Absent(node, block.SubCanvasNames).Select(name => (name, DeclaredBranches.Empty(
                DeclaredBranches.WritesExit(node, name, block.GetSubCanvasOutputs(name).Count > 0))))
            : [];

    private static string NameSet(JsonElement? value, StoredField field, JsonElement? owner, Reading reading) =>
        "[" + string.Join(",", Elements(value).Select(e => e.GetString()!).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).Select(Quote)) + "]";

    private static string DefaultOutput(JsonElement? value, StoredField field, JsonElement? owner, Reading reading)
    {
        if (Property(owner, "policy")?.GetString() != nameof(NodeErrorPolicy.ContinueWithDefault))
            return "unread";
        var outputs = WorkflowGraphExecutor.ReadDefaultOutputs(value?.ValueKind == JsonValueKind.String ? value.Value.GetString() : null, out var unparsed);
        return unparsed is not null ? "!" + Quote(unparsed) : EngineValue(outputs.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
    }

    private static string Mappings(JsonElement? value, StoredField field, JsonElement? owner, Reading reading) =>
        value is not { ValueKind: JsonValueKind.Object } mappings
            ? "{}"
            : "{" + string.Join(",", WorkflowGraphHelpers.ReadMappings(mappings)
                .GroupBy(m => m.TargetInput, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Quote(g.Key) + ":[" + string.Join(",", g.Select(m =>
                    m.IsLiteral ? "L" + EngineValue(m.LiteralValue) : "S" + Quote(m.SourceOutput) + Projection(m.Projection))) + "]")) + "}";

    private static string Projection(WorkflowProjection? projection) => projection is null
        ? ""
        : "P(" + Quote(projection.Path) + "," + (projection.Mode is { } mode ? Quote(mode) : "null") + ","
            + (projection.Type is { } type ? Quote(type) : "null") + ")";

    private static string EngineValue(object? value) => value switch
    {
        null => "null",
        string s => "s" + Quote(s),
        long l => "l" + l.ToString(CultureInfo.InvariantCulture),
        double d => "d" + d.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        JsonElement element => "j" + EngineValue(WorkflowGraphHelpers.JsonToValue(element)),
        List<object?> list => "[" + string.Join(",", list.Select(EngineValue)) + "]",
        Dictionary<string, object?> map => "{" + string.Join(",", map.Select(p => Quote(p.Key) + ":" + EngineValue(p.Value))) + "}",
        _ => "?" + Quote(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
    };

    private static WorkflowDiffParameters CompareParameters(List<(string Name, string Canon)> a, List<(string Name, string Canon)> b)
    {
        static Dictionary<string, string> ByName(List<(string Name, string Canon)> list) => list
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(",", g.Select(p => p.Canon)), StringComparer.Ordinal);

        var (byA, byB) = (ByName(a), ByName(b));
        return new(
            [.. byB.Keys.Where(k => !byA.ContainsKey(k))],
            [.. byA.Keys.Where(k => !byB.ContainsKey(k))],
            [.. byA.Keys.Where(k => byB.TryGetValue(k, out var other) && other != byA[k])]);
    }

    private static VersionRead Read(StoredVersionTexts version, Reading reading)
    {
        var model = StoredWorkflowDocument.Read(version.DefinitionJson, out var fault)?.Definition
            ?? throw new InvalidWorkflowDefinitionException($"version {version.Number} cannot be read: {fault!.Describe()}.");

        var document = JsonDocument.Parse(version.DefinitionJson, ParseOptions);
        var shown = version.ShownDefinitionJson is { } text && text != version.DefinitionJson ? JsonDocument.Parse(text, ParseOptions) : null;
        try
        {
            var read = new VersionRead(document, shown,
                Parameters(version, version.InputParametersJson, PortTypeNameSite.InputParameter, "input", reading),
                Parameters(version, version.OutputParametersJson, PortTypeNameSite.OutputParameter, "output", reading));
            Walk(version, read, document.RootElement, shown?.RootElement, model, [], reading);
            return read;
        }
        catch
        {
            shown?.Dispose();
            document.Dispose();
            throw;
        }
    }

    private static void Walk(
        StoredVersionTexts version, VersionRead read, JsonElement canvas, JsonElement? shownCanvas, WorkflowDefinition model,
        IReadOnlyList<WorkflowDiffCanvasStep> steps, Reading reading)
    {
        var connection = (ObjectKind)((ArrayKind)Field(StoredDocumentSchema.Definition, EdgesKey).Kind).Element;
        var here = new CanvasRead(canvas, model, steps);
        read.Canvases.Add(PathKey(steps), here);

        using var models = model.Nodes.GetEnumerator();
        var shownNodes = Elements(Property(shownCanvas, NodesKey)).ToList();
        var nodeIndex = 0;
        foreach (var node in Elements(Property(canvas, NodesKey)))
        {
            var id = Property(node, IdKey)?.GetString() ?? "";
            var shownNode = nodeIndex < shownNodes.Count ? shownNodes[nodeIndex]
                : read.Masked ? throw new InvalidOperationException($"The shown text of canvas '{PathKey(steps)}' holds fewer nodes than the stored text.") : node;
            nodeIndex++;
            var nodeRead = new NodeRead(id, Property(node, BlockKey)?.GetString() ?? "", node, shownNode, models.MoveNext() ? models.Current : throw new InvalidOperationException("The reader bound fewer nodes than the text holds."));
            if (!here.ById.TryAdd(id, nodeRead))
                throw new InvalidWorkflowDefinitionException($"version {version.Number} has two nodes with the id '{id}' on one canvas.");
            here.Nodes.Add(nodeRead);
        }

        var shownEdges = Elements(Property(shownCanvas, EdgesKey)).ToList();
        var edgeIndex = 0;
        foreach (var edge in Elements(Property(canvas, EdgesKey)))
        {
            var shownEdge = edgeIndex < shownEdges.Count ? shownEdges[edgeIndex]
                : read.Masked ? throw new InvalidOperationException($"The shown text of canvas '{PathKey(steps)}' holds fewer edges than the stored text.") : edge;
            edgeIndex++;
            here.Edges.Add(new EdgeRead(
                Property(edge, IdKey)?.GetString() ?? "", Property(edge, "sourceNodeId")?.GetString() ?? "",
                Property(edge, "targetNodeId")?.GetString() ?? "",
                Property(shownEdge, "mappings") is { ValueKind: not JsonValueKind.Null } mappings ? mappings.GetRawText() : "[]",
                ByKind(edge, connection, reading)));
        }

        foreach (var node in here.Nodes)
        {
            foreach (var sub in Entries(Property(node.Element, SubCanvasesKey)))
                Walk(version, read, sub.Value, Property(Property(node.ShownElement, SubCanvasesKey), sub.Name), node.Model.SubCanvases![sub.Name], [.. steps, new WorkflowDiffCanvasStep(node.Id, sub.Name)], reading);
            foreach (var (name, empty) in AbsentDeclared(node.Model, reading))
            {
                using var written = JsonDocument.Parse(StoredWorkflowDocument.Write(empty, out _)!, ParseOptions);
                Walk(version, read, written.RootElement.Clone(), null, empty, [.. steps, new WorkflowDiffCanvasStep(node.Id, name)], reading);
            }
        }
    }

    private static List<(string Name, string Canon)> Parameters(
        StoredVersionTexts version, string? json, PortTypeNameSite site, string label, Reading reading)
    {
        var sites = StoredWorkflowDocument.ReadParameters(json, site, out var fault)
            ?? throw new InvalidWorkflowDefinitionException($"version {version.Number}'s {label} parameters cannot be read: {fault!.Describe()}.");
        if (sites.Count == 0)
            return [];

        var parameter = (ObjectKind)StoredDocumentSchema.ParameterList.Element;
        using var list = JsonDocument.Parse(json!, ParseOptions);
        return [.. sites.Select(entry => (entry.PortName, ByKind(list.RootElement[entry.Index], parameter, reading)))];
    }

    private static List<StoredFieldReading> Describe()
    {
        var fields = new List<StoredFieldReading>();
        var seen = new HashSet<ObjectKind>(ReferenceEqualityComparer.Instance);

        void Visit(StoredKind kind, (string Label, IReadOnlyCollection<string>? Reads, bool Compared)? under)
        {
            switch (kind)
            {
                case ObjectKind obj when seen.Add(obj):
                    foreach (var field in obj.Fields)
                    {
                        var label = $"{obj.Name}.{field.Key}";
                        var special = Specials.GetValueOrDefault((obj.Name, field.Key));
                        var reading = special is not null ? new StoredFieldReading(label, special.Says, special.Compared)
                            : under is { } u ? (u.Reads is null || u.Reads.Contains(label)) && u.Compared
                                ? new StoredFieldReading(label, $"read as part of {u.Label}", true)
                                : new StoredFieldReading(label, $"not read by {u.Label}'s reading", false)
                            : new StoredFieldReading(label, "by its kind", true);
                        fields.Add(reading);
                        if (under is null && special is { Beneath: { } beneath })
                            Visit(field.Kind, (label, beneath, special.Compared));
                        else
                            Visit(field.Kind, under);
                    }
                    break;
                case ArrayKind array:
                    Visit(array.Element, under);
                    break;
                case MapKind map:
                    Visit(map.Value, under);
                    break;
            }
        }

        Visit(StoredDocumentSchema.Definition, null);
        Visit(StoredDocumentSchema.ParameterList, null);
        Visit(StoredDocumentSchema.PortList, ($"node.{ConfigurationKey}, the text of the value that holds the list", null, true));
        return fields;
    }

    private static List<StoredCollection> CollectionsOf()
    {
        var collections = new List<StoredCollection>();
        foreach (var obj in StoredDocumentSchema.Objects(StoredDocumentSchema.Definition))
        {
            foreach (var field in obj.Fields)
            {
                var label = $"{obj.Name}.{field.Key}";
                switch (field.Kind)
                {
                    case ArrayKind array:
                        collections.Add(new(label, array.Order));
                        break;
                    case MapKind map:
                        collections.Add(new(label, map.Order));
                        break;
                    case ConfigKind config:
                        collections.Add(new(label, config.Order));
                        break;
                }
            }
        }

        collections.Add(new("parameter list", StoredDocumentSchema.ParameterList.Order));
        collections.Add(new("port list", StoredDocumentSchema.PortList.Order));
        return collections;
    }

    private static StoredField Field(ObjectKind kind, string key) =>
        kind.Exact(key) ?? throw new InvalidOperationException($"The kind table has no '{key}' under '{kind.Name}'.");

    private static ObjectKind Kind(ObjectKind owner, string key) => (ObjectKind)((ArrayKind)Field(owner, key).Kind).Element;

    private static JsonElement? Property(JsonElement? element, string key) =>
        element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var value) ? value : null;

    private static IEnumerable<JsonElement> Elements(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    private static IEnumerable<JsonProperty> Entries(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } obj ? obj.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal) : [];

    private static IEnumerable<string> Keys(JsonElement? value) => Entries(value).Select(p => p.Name);

    private static string? RawValue(JsonElement? configuration, string key) => Property(configuration, key)?.GetRawText();

    private static string Quote(string text) => JsonSerializer.Serialize(text);

    private static string PathKey(IReadOnlyList<WorkflowDiffCanvasStep> steps) =>
        string.Join("/", steps.Select(s => Quote(s.NodeId) + ":" + Quote(s.SubCanvas)));

    private sealed class VersionRead(
        JsonDocument document, JsonDocument? shown, List<(string Name, string Canon)> inputs, List<(string Name, string Canon)> outputs) : IDisposable
    {
        public Dictionary<string, CanvasRead> Canvases { get; } = new(StringComparer.Ordinal);

        public bool Masked { get; } = shown is not null;

        public List<(string Name, string Canon)> Inputs { get; } = inputs;

        public List<(string Name, string Canon)> Outputs { get; } = outputs;

        public void Dispose()
        {
            shown?.Dispose();
            document.Dispose();
        }
    }

    private sealed class CanvasRead(JsonElement element, WorkflowDefinition model, IReadOnlyList<WorkflowDiffCanvasStep> steps)
    {
        public JsonElement Element { get; } = element;

        public WorkflowDefinition Model { get; } = model;

        public IReadOnlyList<WorkflowDiffCanvasStep> Steps { get; } = steps;

        public List<NodeRead> Nodes { get; } = [];

        public Dictionary<string, NodeRead> ById { get; } = new(StringComparer.Ordinal);

        public List<EdgeRead> Edges { get; } = [];
    }

    private sealed record NodeRead(string Id, string BlockTypeId, JsonElement Element, JsonElement ShownElement, WorkflowNodeDefinition Model);

    private sealed record EdgeRead(string Id, string SourceNodeId, string TargetNodeId, string MappingsJson, string Canon)
    {
        public WorkflowDiffEdge Response(IReadOnlyList<WorkflowDiffCanvasStep> steps) => new(Id, SourceNodeId, TargetNodeId, MappingsJson, steps);
    }
}
