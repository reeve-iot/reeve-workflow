using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReeveWorkflow.Core.Hosting;

namespace ReeveWorkflow.Core.Execution;

public static class PayloadSanitizer
{
    private const int MaxSanitizeDepth = 64;
    private const int MaxJsonParseDepth = 128;
    private const string DepthLimitMarker = "[unserializable]";
    private static readonly JsonSerializerOptions SanitizerJsonOptions = new() { MaxDepth = MaxJsonParseDepth };
    private const string MaskedValue = "***";

    public static string? SerializeAndSanitize(IReadOnlyDictionary<string, object?> data, int maxValueBytes)
        => SanitizeToObject(data, maxValueBytes)?.ToJsonString();

    public static JsonObject? SanitizeToObject(IReadOnlyDictionary<string, object?> data, int maxValueBytes)
    {
        if (data.Count == 0)
            return null;

        try
        {
            var node = new JsonObject();
            foreach (var kvp in data)
            {
                var value = IsSensitiveKey(kvp.Key)
                    ? JsonValue.Create(MaskedValue)
                    : SerializeValue(kvp.Value, maxValueBytes);
                node[ShownKey(kvp.Key)] = value;
            }
            return node;
        }
        catch (Exception ex) when (ReeveWorkflow.Core.Expressions.ExpressionJsonValue.Unwritable(ex))
        {
            return null;
        }
    }

    private static bool IsSensitiveKey(string key) => WorkflowSensitiveNames.Matches(key);

    private static string ShownKey(string key) => IsSensitiveKey(key) ? CutAtColon(key) : key;

    internal static string CutAtColon(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 ? key[..(colon + 1)] + MaskedValue : key;
    }

    private static JsonNode? SerializeValue(object? value, int maxValueBytes)
    {
        if (value is null)
            return null;
        if (value is PresentedValue presented)
            return presented.ToNode(maxValueBytes);

        try
        {
            var json = JsonSerializer.SerializeToNode(value, SanitizerJsonOptions);
            if (json is not null)
                WalkAndMask(json, 0);
            if (json is JsonValue jv && jv.TryGetValue<string>(out var str))
            {
                if (str.Length > maxValueBytes)
                    return JsonValue.Create(str[..maxValueBytes] + "...[truncated]");
                return json;
            }

            var raw = json?.ToJsonString() ?? "null";
            if (raw.Length > maxValueBytes * 4)
                return JsonValue.Create($"...[truncated, {raw.Length} bytes]");

            return json;
        }
        catch (Exception ex) when (ReeveWorkflow.Core.Expressions.ExpressionJsonValue.Unwritable(ex))
        {
            return JsonValue.Create("[unserializable]");
        }
    }

    /// <summary>Redacts a full HTTP capture for the workflow debug path: headers by name, the URL query, and
    /// sensitive keys at any depth in a nested JSON body. Null when nothing was supplied.</summary>
    /// <remarks>Masks by the sensitive-name rule only: a protected form under another header name is recorded as given.</remarks>
    public static string? SanitizeHttp(
        string? url, IEnumerable<KeyValuePair<string, string>>? headers, string? body, int maxValueBytes)
    {
        if (url is null && headers is null && body is null) return null;
        try
        {
            var root = new JsonObject();
            if (url is not null) root["url"] = JsonValue.Create(StripQuery(url));
            if (headers is not null)
            {
                var headerObj = new JsonObject();
                foreach (var (name, value) in headers)
                    headerObj[ShownKey(name)] = JsonValue.Create(IsSensitiveKey(name) ? MaskedValue : value);
                root["headers"] = headerObj;
            }
            if (body is not null) root["body"] = SanitizeBody(body, maxValueBytes);
            return root.ToJsonString();
        }
        catch { return null; }
    }

    private static string StripQuery(string url)
    {
        var queryIndex = url.IndexOf('?', StringComparison.Ordinal);
        var trimmed = queryIndex >= 0 ? url[..queryIndex] : url;
        var fragmentIndex = trimmed.IndexOf('#', StringComparison.Ordinal);
        return fragmentIndex >= 0 ? trimmed[..fragmentIndex] : trimmed;
    }

    private static JsonNode? SanitizeBody(string body, int maxValueBytes)
    {
        JsonNode? parsed = null;
        try
        {
            parsed = JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { MaxDepth = MaxJsonParseDepth });
        }
        catch
        {
            if (HasNestingBeyond(body, MaxJsonParseDepth))
                return JsonValue.Create(DepthLimitMarker);
        }
        if (parsed is null)
            return body.Length > maxValueBytes ? JsonValue.Create(body[..maxValueBytes] + "...[truncated]") : JsonValue.Create(body);
        WalkAndMask(parsed, 0);
        return parsed;
    }

    private static void WalkAndMask(JsonNode node, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                var renamed = false;
                foreach (var key in obj.Select(kvp => kvp.Key).ToArray())
                {
                    if (IsSensitiveKey(key))
                    {
                        obj[key] = JsonValue.Create(MaskedValue);
                        renamed |= ShownKey(key) != key;
                    }
                    else if (obj[key] is JsonNode child)
                    {
                        if (depth + 1 >= MaxSanitizeDepth) obj[key] = JsonValue.Create(DepthLimitMarker);
                        else WalkAndMask(child, depth + 1);
                    }
                }
                if (renamed)
                {
                    var entries = obj.ToArray();
                    obj.Clear();
                    foreach (var (name, value) in entries)
                        obj[ShownKey(name)] = value;
                }
                break;
            case JsonArray arr:
                for (var index = 0; index < arr.Count; index++)
                {
                    if (arr[index] is not JsonNode child) continue;
                    if (depth + 1 >= MaxSanitizeDepth) arr[index] = JsonValue.Create(DepthLimitMarker);
                    else WalkAndMask(child, depth + 1);
                }
                break;
        }
    }

    private static bool HasNestingBeyond(string text, int maximumDepth)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        foreach (var character in text)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }

            if (character == '"') inString = true;
            else if (character == '{' || character == '[')
            {
                if (++depth > maximumDepth) return true;
            }
            else if (character == '}' || character == ']') depth--;
        }
        return false;
    }

    internal sealed class PresentedValue(IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        internal JsonNode ToNode(int maxValueBytes)
        {
            var node = new JsonObject();
            foreach (var (key, text) in entries)
                node[key] = JsonValue.Create(text.Length > maxValueBytes ? text[..maxValueBytes] + "...[truncated]" : text);
            return node;
        }
    }
}
