using System.Text.Json;

namespace ReeveWorkflow.Core.Compilation;

internal sealed class StoredDocumentWalk(PortTypeNameSite listSite)
{
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = StoredDocumentLexer.MaxNesting };

    internal sealed class ReadFrame
    {
        public List<List<StoredPortTypeSite>> Nodes { get; } = [];
        public HashSet<string> NodeIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EdgeIds { get; } = new(StringComparer.Ordinal);

        public List<StoredPortTypeSite> Projections { get; } = [];
    }

    private readonly List<CanvasPathSegment> canvasPath = [];
    private PortTypeNameSite listSite = listSite;
    private string prefix = "";
    private string? nodeId;
    private List<StoredPortTypeSite>? nodeSites;
    private ReadFrame? frame;
    private int index;
    private string? connectionTarget;
    private string? mapKey;

    public StoredDocumentFault? Fault { get; private set; }

    public List<StoredPortTypeSite> Sites { get; } = [];

    public List<ReadFrame> Frames { get; } = [];

    public bool Value(JsonElement value, StoredKind kind, bool nullable, string at)
    {
        if (kind is RefusedKind refused)
            return Fail(refused.Rule, at, refused.Detail);
        if (value.ValueKind == JsonValueKind.Null)
            return nullable || Fail(StoredDocumentRules.L7, at, $"null where {Describe(kind)} is required");

        switch (kind)
        {
            case StringKind text:
                if (value.ValueKind != JsonValueKind.String)
                    return Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where a string is required");
                return !text.NonEmpty || value.GetString()!.Length > 0 || Fail(StoredDocumentRules.L7, at, "an empty string where a name is required");
            case NumberKind:
                if (value.ValueKind != JsonValueKind.Number)
                    return Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where a number is required");
                return Numbers(value, at);
            case BooleanKind:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    || Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where true or false is required");
            case AnyKind:
                return Numbers(value, at);
            case EnumKind names:
                return (value.ValueKind == JsonValueKind.String && names.Names.Contains(value.GetString()!, StringComparer.Ordinal))
                    || Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where one of {string.Join(", ", names.Names)} is required");
            case ChoiceKind names:
                return (value.ValueKind == JsonValueKind.String && names.Names.Contains(value.GetString()!, StringComparer.Ordinal))
                    || Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where one of {string.Join(", ", names.Names)} is required");
            case PortTypeKind:
                return value.ValueKind == JsonValueKind.String
                    || Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where a port type name is required");
            case ArrayKind array:
                return Array(value, array, at);
            case MapKind map:
                return Map(value, map, at);
            case ConfigKind:
                return Config(value, at);
            default:
                return Object(value, (ObjectKind)kind, at);
        }
    }

    public bool Embedded(string text, PortTypeNameSite site, string at)
    {
        var (outerPrefix, outerSite) = (prefix, listSite);
        prefix = prefix + at + "#";
        listSite = site;
        try
        {
            var bytes = StoredDocumentLexer.Lex(text, out var fault);
            if (bytes is null)
                return Fail(fault!.Rule, fault.Location, fault.Detail);

            using var document = JsonDocument.Parse(bytes, ParseOptions);
            var outerIndex = index;
            var read = Value(document.RootElement, StoredDocumentSchema.PortList, nullable: false, "");
            index = outerIndex;
            return read;
        }
        finally
        {
            (prefix, listSite) = (outerPrefix, outerSite);
        }
    }

    private bool Array(JsonElement value, ArrayKind kind, string at)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where an array is required");

        var i = 0;
        foreach (var element in value.EnumerateArray())
        {
            index = i;
            if (!Value(element, kind.Element, nullable: false, JsonPointer.Append(at, i)))
                return false;
            i++;
        }

        return true;
    }

    private bool Map(JsonElement value, MapKind kind, string at)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where an object is required");

        var typeArgumentIndex = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (kind.Role == StoredRole.TypeArguments)
            {
                if (!Value(property.Value, kind.Value, nullable: false, JsonPointer.Append(at, property.Name)))
                    return false;
                var raw = property.Value.GetString();
                var added = new StoredPortTypeSite(PortTypeNameSite.TypeArgument, prefix + JsonPointer.Append(at, property.Name), [.. canvasPath], nodeId, property.Name, typeArgumentIndex++, raw, false);
                Sites.Add(added);
                nodeSites?.Add(added);
                continue;
            }
            if (kind.Role == StoredRole.SubCanvases)
                canvasPath.Add(new CanvasPathSegment(nodeId ?? "", property.Name));
            var outerKey = mapKey;
            mapKey = property.Name;
            var read = Value(property.Value, kind.Value, nullable: false, JsonPointer.Append(at, property.Name));
            mapKey = outerKey;
            if (kind.Role == StoredRole.SubCanvases)
                canvasPath.RemoveAt(canvasPath.Count - 1);
            if (!read)
                return false;
        }

        return true;
    }

    private bool Config(JsonElement value, string at)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where an object is required");

        foreach (var property in value.EnumerateObject())
        {
            var site = property.Name switch
            {
                StoredDocumentSchema.InputPortsKey => PortTypeNameSite.DynamicInputPort,
                StoredDocumentSchema.OutputPortsKey => PortTypeNameSite.DynamicOutputPort,
                _ => (PortTypeNameSite?)null,
            };
            if (site is null)
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                    return Fail(StoredDocumentRules.L7, JsonPointer.Append(at, property.Name), "null where removing the configuration key is required");
                if (!Numbers(property.Value, JsonPointer.Append(at, property.Name)))
                    return false;
                continue;
            }

            var listAt = JsonPointer.Append(at, property.Name);
            if (property.Value.ValueKind != JsonValueKind.String)
                return Fail(StoredDocumentRules.L7, listAt, $"{Describe(property.Value)} where a port list written as a string is required");
            if (property.Value.GetString() is not { Length: > 0 } text)
                return Fail(StoredDocumentRules.L7, listAt, "an empty port list where the empty array string is required");
            if (!Embedded(text, site.Value, listAt))
                return false;
        }

        return true;
    }

    private bool Object(JsonElement value, ObjectKind kind, string at)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Fail(StoredDocumentRules.L7, at, $"{Describe(value)} where {Describe(kind)} is required");

        var entryIndex = index;
        var (outerFrame, outerNodeId, outerSites, outerTarget) = (frame, nodeId, nodeSites, connectionTarget);
        if (kind.Role == StoredRole.Connection)
            connectionTarget = value.TryGetProperty("targetNodeId", out var target) && target.ValueKind == JsonValueKind.String ? target.GetString() : null;
        if (kind.Role == StoredRole.Canvas)
        {
            frame = new ReadFrame();
            Frames.Add(frame);
        }
        else if (kind.Role == StoredRole.Node)
        {
            nodeId = value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            nodeSites = [];
            frame!.Nodes.Add(nodeSites);
        }

        try
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                var propertyAt = JsonPointer.Append(at, property.Name);
                if (kind.Exact(property.Name) is { } field)
                {
                    present.Add(field.Key);
                    if (field.RefusedRule is { } refused)
                        return Fail(refused, propertyAt, field.RefusedDetail ?? "this member is refused");
                    if (!Value(property.Value, field.Kind, field.Nullable, propertyAt))
                        return false;
                    if (((kind.Role is StoredRole.Node or StoredRole.DeclaredOutput or StoredRole.PortEntry or StoredRole.Parameter)
                            && field.Key == (kind.Role == StoredRole.Node ? "id" : "name"))
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString()!.Contains(".", StringComparison.Ordinal))
                    {
                        return Fail(StoredDocumentRules.DottedName, propertyAt,
                            $"name '{property.Value.GetString()}' contains a dot");
                    }
                    if (kind.Role == StoredRole.Node && field.Key == "passthrough" && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        var nameIndex = 0;
                        foreach (var name in property.Value.EnumerateArray())
                        {
                            if (name.ValueKind == JsonValueKind.String && name.GetString()!.Contains(".", StringComparison.Ordinal))
                                return Fail(StoredDocumentRules.DottedName, JsonPointer.Append(propertyAt, nameIndex),
                                    $"name '{name.GetString()}' contains a dot");
                            nameIndex++;
                        }
                    }
                    if (kind.Role == StoredRole.Node && field.Key == "id" && property.Value.ValueKind == JsonValueKind.String
                        && !frame!.NodeIds.Add(property.Value.GetString()!))
                    {
                        return Fail(StoredDocumentRules.DuplicateNodeId, propertyAt,
                            $"a second node with id '{property.Value.GetString()}' on this canvas");
                    }
                }
                else if (kind.IgnoringCase(property.Name) is { } variant)
                {
                    return Fail(StoredDocumentRules.L5, propertyAt, $"a key '{property.Name}' where '{variant.Key}' is meant; keys match exactly");
                }
                else
                    return Fail(StoredDocumentRules.UnknownKey, propertyAt, $"unlisted key '{property.Name}' on {kind.Name}");
            }

            foreach (var field in kind.Fields.Where(f => f.Required && !present.Contains(f.Key)))
                return Fail(StoredDocumentRules.L7, JsonPointer.Append(at, field.Key), $"{Describe(kind)} with no '{field.Key}'");

            if (kind.Role == StoredRole.Connection && value.TryGetProperty("id", out var edgeId)
                && edgeId.ValueKind == JsonValueKind.String && !frame!.EdgeIds.Add(edgeId.GetString()!))
                return Fail(StoredDocumentRules.DuplicateEdgeId, JsonPointer.Append(at, "id"),
                    $"a second connection with id '{edgeId.GetString()}' on this canvas");

            if (kind.Role is StoredRole.DeclaredOutput or StoredRole.PortEntry or StoredRole.Parameter)
                AddSite(value, kind.Role == StoredRole.DeclaredOutput ? PortTypeNameSite.DeclaredOutput : listSite, entryIndex, at);
            if (kind.Role == StoredRole.Projection && value.TryGetProperty("type", out var type))
            {
                var added = new StoredPortTypeSite(PortTypeNameSite.ProjectionType, prefix + JsonPointer.Append(at, "type"), [.. canvasPath],
                    connectionTarget, mapKey ?? "", frame!.Projections.Count, type.GetString(), false);
                Sites.Add(added);
                frame.Projections.Add(added);
            }

            return true;
        }
        finally
        {
            (frame, nodeId, nodeSites, connectionTarget) = (outerFrame, outerNodeId, outerSites, outerTarget);
        }
    }

    private bool Numbers(JsonElement value, string at)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return StoredNumber.Refusal(value.GetRawText()) is not { } refusal || Fail(StoredDocumentRules.L7, at, refusal);
            case JsonValueKind.Array:
                var i = 0;
                foreach (var element in value.EnumerateArray())
                {
                    if (!Numbers(element, JsonPointer.Append(at, i++)))
                        return false;
                }

                return true;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (!Numbers(property.Value, JsonPointer.Append(at, property.Name)))
                        return false;
                }

                return true;
            default:
                return true;
        }
    }

    private void AddSite(JsonElement entry, PortTypeNameSite site, int entryIndex, string at)
    {
        var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        var type = entry.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var required = entry.TryGetProperty("isRequired", out var r) && r.ValueKind == JsonValueKind.True;
        var added = new StoredPortTypeSite(
            site, prefix + JsonPointer.Append(at, "type"), [.. canvasPath], nodeId, name, entryIndex, type, required);
        Sites.Add(added);
        nodeSites?.Add(added);
    }

    private bool Fail(string rule, string at, string detail)
    {
        Fault = new StoredDocumentFault(rule, prefix + at, detail, nodeId, [.. canvasPath]);
        return false;
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    private static string Describe(StoredKind kind) => kind switch
    {
        ObjectKind o => "a " + o.Name,
        PortTypeKind => "a port type name",
        StringKind => "a string",
        NumberKind => "a number",
        BooleanKind => "true or false",
        EnumKind => "a policy name",
        ChoiceKind choice => choice.Noun,
        AnyKind => "a literal value",
        ArrayKind => "an array",
        _ => "an object",
    };
}
