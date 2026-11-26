using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ReeveWorkflow.Core.JsonPath;

internal sealed class JsonPathQuery
{
    private readonly CompiledQuery _program;

    private JsonPathQuery(string text, CompiledQuery program)
    {
        Text = text;
        _program = program;
    }

    public static JsonPathQuery Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new JsonPathQuery(text, JsonPathParser.Parse(text));
    }

    public static bool TryParse(
        string text,
        [NotNullWhen(true)] out JsonPathQuery? query,
        [NotNullWhen(false)] out JsonPathSyntaxException? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            query = new JsonPathQuery(text, JsonPathParser.Parse(text));
            error = null;
            return true;
        }
        catch (JsonPathSyntaxException exception)
        {
            query = null;
            error = exception;
            return false;
        }
    }

    public string Text { get; }

    public bool IsSingular => _program.IsSingular;

    public IReadOnlyList<JsonElement> Evaluate(JsonElement root)
    {
        var sink = new ElementListSink(new List<JsonElement>());
        if (root.ValueKind != JsonValueKind.Undefined)
            _program.Run(root, new EvaluationContext(trackPaths: false), ref sink);
        return sink.Items;
    }

    public IReadOnlyList<(JsonElement Value, string Path)> EvaluateWithPaths(JsonElement root)
    {
        var sink = new PathListSink(new List<(JsonElement, string)>());
        if (root.ValueKind != JsonValueKind.Undefined)
            _program.Run(root, new EvaluationContext(trackPaths: true), ref sink);
        return sink.Items;
    }

    public override string ToString() => Text;
}
