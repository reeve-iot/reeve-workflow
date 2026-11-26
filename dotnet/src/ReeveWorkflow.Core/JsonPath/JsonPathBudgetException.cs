namespace ReeveWorkflow.Core.JsonPath;

internal sealed class JsonPathBudgetException(string reason) : Exception($"The JSONPath query {reason}.")
{
    public string Reason { get; } = reason;
}
