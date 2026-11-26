namespace ReeveWorkflow.Core.JsonPath;

internal sealed class JsonPathSyntaxException : Exception
{
    public JsonPathSyntaxException(string reason, int position)
        : base($"Invalid JSONPath query at offset {position}: {reason}.")
    {
        Reason = reason;
        Position = position;
    }

    public JsonPathSyntaxException(string path, string unsupported, int position)
        : base($"the path '{path}' uses {unsupported}; a projection path takes only child segments with name, index, wildcard and slice selectors")
    {
        Reason = unsupported;
        Position = position;
        Unsupported = unsupported;
    }

    public int Position { get; }

    public string Reason { get; }

    public bool IsProjectionRestriction => Unsupported is not null;

    public string? Unsupported { get; }
}
